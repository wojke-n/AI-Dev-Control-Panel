using AiDevControlPanel.Core.Claude;
using AiDevControlPanel.Core.Domain;
using AiDevControlPanel.Core.GitHub;
using AiDevControlPanel.Core.Persistence;

namespace AiDevControlPanel.Core.Workflow;

/// <summary>Turns TSAs into GitHub issues (feature requests, bug reports, security vulnerability reports).</summary>
public sealed class RefinerService(
    IAppRepository repo,
    IGitHubService github,
    ClaudeSessionRunner claude,
    BlockingService blocking,
    ActivityLog log)
{
    public const string CreatePublicly = "Create the issues publicly";
    public const string SkipSecurity = "Skip the security issues";

    public Task<Tsa> AddTsaAsync(int projectId, string text) =>
        repo.AddTsaAsync(new Tsa { ProjectId = projectId, Text = text.Trim(), State = TsaState.Waiting });

    /// <summary>Scheduler job: runs the refiner, or finishes creating the issues of a pending refinement.</summary>
    public async Task RunAsync(int tsaId, CancellationToken ct)
    {
        var tsa = await repo.GetTsaAsync(tsaId, ct) ?? throw new InvalidOperationException($"TSA {tsaId} not found.");
        var project = await repo.GetProjectAsync(tsa.ProjectId, ct) ?? throw new InvalidOperationException($"Project {tsa.ProjectId} not found.");
        var key = ActivityLog.TsaKey(tsaId);
        try
        {
            if (tsa.PendingRefinement is not null)
            {
                await repo.UpdateTsaAsync(tsaId, t =>
                {
                    t.State = TsaState.Refining;
                    t.LastError = null;
                }, ct);
                await CreateIssuesAsync(tsaId, project, PhaseOutputs.Deserialize<RefinerOutput>(tsa.PendingRefinement)!, ct);
                return;
            }

            await RefineAsync(tsa, project, key, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Add(key, $"✖ {e.Message}");
            await repo.UpdateTsaAsync(tsaId, t =>
            {
                t.State = TsaState.Error;
                t.LastError = e.Message;
            }, CancellationToken.None);
        }
    }

    public async Task SubmitAnswerAsync(int tsaId, IReadOnlyList<QuestionAnswer> answers)
    {
        var tsa = await repo.GetTsaAsync(tsaId) ?? throw new InvalidOperationException($"TSA {tsaId} not found.");
        if (tsa.State != TsaState.AwaitingAnswer)
        {
            return;
        }

        if (!tsa.AwaitingSecurityConfirmation)
        {
            await repo.UpdateTsaAsync(tsaId, t =>
            {
                t.PendingAnswer = Questions.FormatAnswers(answers);
                t.PendingQuestions = null;
                t.State = TsaState.Waiting;
            });
            return;
        }

        var output = PhaseOutputs.Deserialize<RefinerOutput>(tsa.PendingRefinement)!;
        if (answers.FirstOrDefault()?.Answer.StartsWith(SkipSecurity, StringComparison.Ordinal) == true)
        {
            output = WithoutSecurityIssues(output);
        }

        var nothingLeft = output.Issues is not { Count: > 0 };
        await repo.UpdateTsaAsync(tsaId, t =>
        {
            t.AwaitingSecurityConfirmation = false;
            t.PendingQuestions = null;
            t.PendingRefinement = nothingLeft ? null : PhaseOutputs.Serialize(output);
            t.State = nothingLeft ? TsaState.Refined : TsaState.Waiting;
        });
    }

    public Task RetryAsync(int tsaId) => repo.UpdateTsaAsync(tsaId, t =>
    {
        t.State = TsaState.Waiting;
        t.LastError = null;
    });

    private async Task RefineAsync(Tsa tsa, Project project, string key, CancellationToken ct)
    {
        string prompt;
        var sessionId = tsa.SessionId;
        if (sessionId is not null && tsa.PendingAnswer is not null)
        {
            prompt = PromptTemplates.Render(PromptTemplates.Answers, new Dictionary<string, string> { ["Answers"] = tsa.PendingAnswer });
        }
        else if (sessionId is not null && tsa.RunInProgress)
        {
            prompt = PromptTemplates.Render(PromptTemplates.Continue);
        }
        else
        {
            sessionId = null;
            var existing = await github.ListIssuesAsync(project.GitHubRepo, ct);
            prompt = PromptTemplates.Render(PromptTemplates.Refiner, new Dictionary<string, string>
            {
                ["Repo"] = project.GitHubRepo,
                ["TsaText"] = tsa.Text,
                ["ExistingIssues"] = existing.Count == 0
                    ? "(none)"
                    : string.Join('\n', existing.Select(i => $"- #{i.Number} [{i.State.ToLowerInvariant()}] [{string.Join(", ", i.Labels)}] {i.Title}")),
            });
        }

        await repo.UpdateTsaAsync(tsa.Id, t =>
        {
            t.State = TsaState.Refining;
            t.RunInProgress = true;
            t.SessionId = sessionId;
            t.ResumeAt = null;
            t.LastError = null;
        }, ct);

        var result = await claude.RunAsync(key, new ClaudeRunRequest
        {
            WorkingDirectory = project.LocalPath,
            Prompt = prompt,
            PermissionMode = "plan",
            JsonSchema = PromptTemplates.Schema(PromptTemplates.Refiner),
            DisallowedTools = ClaudeSessionRunner.NoInteractiveTools,
            ResumeSessionId = sessionId,
            NewSessionId = sessionId is null ? Guid.NewGuid().ToString() : null,
            Name = $"ADCP TSA {tsa.Id} refine",
        }, sessionId, id => repo.UpdateTsaAsync(tsa.Id, t => t.SessionId = id, ct), ct);

        if (result.UsageLimitReached)
        {
            var resumeAt = claude.ResumeTime(result);
            await repo.UpdateTsaAsync(tsa.Id, t =>
            {
                t.State = TsaState.Waiting;
                t.ResumeAt = resumeAt;
            }, ct);
            return;
        }

        if (!result.Success)
        {
            if (ClaudeSessionRunner.IsMissingSession(result))
            {
                // Start over with a fresh session on the next run.
                await repo.UpdateTsaAsync(tsa.Id, t =>
                {
                    t.State = TsaState.Waiting;
                    t.SessionId = null;
                    t.RunInProgress = false;
                }, ct);
                return;
            }

            throw new InvalidOperationException(result.ErrorMessage);
        }

        var output = PhaseOutputs.Parse<RefinerOutput>(result.StructuredOutput)
                     ?? throw new InvalidOperationException("The refiner returned no structured output.");
        if (output.Status == "questions" && output.Questions is { Count: > 0 } questions)
        {
            await repo.UpdateTsaAsync(tsa.Id, t =>
            {
                t.State = TsaState.AwaitingAnswer;
                t.PendingQuestions = Questions.Serialize(questions);
                t.PendingAnswer = null;
                t.RunInProgress = false;
                t.Title ??= output.TsaTitle;
            }, ct);
            return;
        }

        if (output.Issues is not { Count: > 0 } issues)
        {
            throw new InvalidOperationException("The refiner returned no issues.");
        }

        Validate(issues);
        var securityCount = issues.Count(i => ParseType(i.Type) == IssueType.Security);
        var needsConfirmation = project.IsPublic && securityCount > 0;
        await repo.UpdateTsaAsync(tsa.Id, t =>
        {
            t.Title = string.IsNullOrWhiteSpace(output.TsaTitle) ? t.Title : output.TsaTitle;
            t.PendingRefinement = PhaseOutputs.Serialize(output);
            t.PendingAnswer = null;
            t.RunInProgress = false;
            if (needsConfirmation)
            {
                t.State = TsaState.AwaitingAnswer;
                t.AwaitingSecurityConfirmation = true;
                t.PendingQuestions = Questions.Serialize([SecurityQuestion(project, securityCount)]);
            }
        }, ct);

        if (!needsConfirmation)
        {
            await CreateIssuesAsync(tsa.Id, project, output, ct);
        }
    }

    private async Task CreateIssuesAsync(int tsaId, Project project, RefinerOutput output, CancellationToken ct)
    {
        var key = ActivityLog.TsaKey(tsaId);
        var items = output.Issues ?? [];
        var snapshot = await repo.LoadSnapshotAsync(ct);

        // Issues created by an earlier, interrupted attempt are reused.
        var created = snapshot.IssuesOfTsa(tsaId).Where(i => i.RefinerRef is not null).ToDictionary(i => i.RefinerRef!);

        foreach (var item in TopologicalOrder(items))
        {
            if (created.ContainsKey(item.Ref))
            {
                continue;
            }

            var blockerNumbers = (item.BlockedBy ?? [])
                .Select(b => created.TryGetValue(b, out var c) ? c.Number : ParseIssueNumber(b))
                .OfType<int>()
                .Distinct()
                .ToList();
            var body = item.Body.TrimEnd();
            if (blockerNumbers.Count > 0)
            {
                body += "\n\n" + string.Join('\n', blockerNumbers.Select(n => $"Blocked by #{n}"));
            }

            var type = ParseType(item.Type);
            var gitHubIssue = await github.CreateIssueAsync(project.GitHubRepo, item.Title, body, type, ct);
            var (onHold, prioritized) = PriorityRules.FromRefinerPriority(item.Priority);
            var parent = item.ParentRef is { } parentRef ? created[parentRef] : null;
            var issue = await repo.AddIssueAsync(new Issue
            {
                ProjectId = project.Id,
                TsaId = tsaId,
                ParentIssueId = parent?.Id,
                RefinerRef = item.Ref,
                Number = gitHubIssue.Number,
                GitHubId = gitHubIssue.Id,
                Title = item.Title,
                Body = body,
                Type = type,
                IsContainer = items.Any(o => o.ParentRef == item.Ref),
                IsPrioritized = prioritized,
                OnHold = onHold,
                State = IssueState.Waiting,
            }, ct);
            created[item.Ref] = issue;
            log.Add(key, $"Created #{issue.Number} {issue.Title}");

            if (parent is not null)
            {
                await BestEffortAsync(key, $"link #{issue.Number} as sub-issue of #{parent.Number}",
                    () => github.AddSubIssueAsync(project.GitHubRepo, parent.Number, issue.GitHubId, ct));
            }
        }

        foreach (var item in items)
        {
            var issue = created[item.Ref];
            foreach (var blockerRef in item.BlockedBy ?? [])
            {
                var blocker = created.TryGetValue(blockerRef, out var c)
                    ? c
                    : ParseIssueNumber(blockerRef) is { } number
                        ? snapshot.IssuesOf(project.Id).FirstOrDefault(i => i.Number == number)
                        : null;
                if (blocker is null || blocker.Id == issue.Id)
                {
                    continue;
                }

                await repo.AddDependencyAsync(issue.Id, blocker.Id, ct);
                await BestEffortAsync(key, $"mark #{issue.Number} as blocked by #{blocker.Number}",
                    () => github.AddBlockedByAsync(project.GitHubRepo, issue.Number, blocker.GitHubId, ct));
            }
        }

        await repo.UpdateTsaAsync(tsaId, t =>
        {
            t.State = TsaState.Refined;
            t.PendingRefinement = null;
            t.PendingQuestions = null;
            t.Title ??= output.TsaTitle;
        }, ct);
        await blocking.RecomputeAsync(project.Id, ct);
    }

    /// <summary>Parents come before their sub-issues; blockers come first where that is possible.</summary>
    internal static IReadOnlyList<RefinedIssue> TopologicalOrder(IReadOnlyList<RefinedIssue> items)
    {
        var byRef = items.ToDictionary(i => i.Ref);
        RefinedIssue? Parent(RefinedIssue item) =>
            item.ParentRef is { } parentRef && byRef.TryGetValue(parentRef, out var parent) ? parent : null;

        IEnumerable<RefinedIssue> SelfAndAncestors(RefinedIssue item)
        {
            for (var current = item; current is not null; current = Parent(current))
            {
                yield return current;
            }
        }

        foreach (var item in items)
        {
            var seen = new HashSet<string>();
            if (SelfAndAncestors(item).Take(items.Count + 1).Any(i => !seen.Add(i.Ref)))
            {
                throw new InvalidOperationException($"The refiner returned cyclic parent references ({item.Ref}).");
            }
        }

        var visiting = new HashSet<string>();
        var done = new HashSet<string>();
        var result = new List<RefinedIssue>();

        void Visit(RefinedIssue item)
        {
            if (done.Contains(item.Ref) || !visiting.Add(item.Ref))
            {
                return;
            }

            if (Parent(item) is { } parent)
            {
                Visit(parent);
            }

            // Blockers are only a preference: skip one when it would have to wait for this issue.
            foreach (var blockerRef in item.BlockedBy ?? [])
            {
                if (byRef.TryGetValue(blockerRef, out var blocker) && SelfAndAncestors(blocker).All(b => !visiting.Contains(b.Ref)))
                {
                    Visit(blocker);
                }
            }

            visiting.Remove(item.Ref);
            done.Add(item.Ref);
            result.Add(item);
        }

        foreach (var item in items)
        {
            Visit(item);
        }

        return result;
    }

    internal static void Validate(IReadOnlyList<RefinedIssue> issues)
    {
        var duplicate = issues.GroupBy(i => i.Ref).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"The refiner used the reference '{duplicate.Key}' twice.");
        }

        var refs = issues.Select(i => i.Ref).ToHashSet();
        var orphan = issues.FirstOrDefault(i => i.ParentRef is not null && !refs.Contains(i.ParentRef));
        if (orphan is not null)
        {
            throw new InvalidOperationException($"Issue '{orphan.Ref}' references the unknown parent '{orphan.ParentRef}'.");
        }

        TopologicalOrder(issues);
    }

    internal static IssueType ParseType(string type) => type.ToLowerInvariant() switch
    {
        "bug" => IssueType.Bug,
        "security" => IssueType.Security,
        _ => IssueType.Feature,
    };

    internal static int? ParseIssueNumber(string reference) =>
        reference.StartsWith('#') && int.TryParse(reference.AsSpan(1), out var number) ? number : null;

    internal static RefinerOutput WithoutSecurityIssues(RefinerOutput output)
    {
        var removed = (output.Issues ?? []).Where(i => ParseType(i.Type) == IssueType.Security).Select(i => i.Ref).ToHashSet();
        var remaining = (output.Issues ?? [])
            .Where(i => !removed.Contains(i.Ref))
            .Select(i => i with
            {
                ParentRef = i.ParentRef is not null && removed.Contains(i.ParentRef) ? null : i.ParentRef,
                BlockedBy = i.BlockedBy?.Where(b => !removed.Contains(b)).ToList(),
            })
            .ToList();
        return output with { Issues = remaining };
    }

    private static Question SecurityQuestion(Project project, int count) => new(
        $"This TSA results in {count} security vulnerability report(s), but {project.GitHubRepo} is public, so everyone could read them. Create them anyway?",
        "Security",
        [
            new QuestionOption(CreatePublicly, "Create all issues on GitHub as planned."),
            new QuestionOption(SkipSecurity, "Create only the other issues and report the vulnerabilities privately."),
        ],
        MultiSelect: false);

    private async Task BestEffortAsync(string key, string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (GitHubException e)
        {
            log.Add(key, $"Could not {what}: {e.Message}");
        }
    }
}
