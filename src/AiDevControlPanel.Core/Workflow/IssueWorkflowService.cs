using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AiDevControlPanel.Core.Claude;
using AiDevControlPanel.Core.Domain;
using AiDevControlPanel.Core.Git;
using AiDevControlPanel.Core.GitHub;
using AiDevControlPanel.Core.Persistence;
using AiDevControlPanel.Core.Usage;

namespace AiDevControlPanel.Core.Workflow;

public enum PriorityMode
{
    OnHold,
    Regular,
    Prioritized,
}

/// <summary>
/// Drives an issue through planning (plan mode), execution (auto mode) and the user's review
/// (accept, adjust, restart). Each issue works in its own git worktree.
/// </summary>
public sealed partial class IssueWorkflowService(
    IAppRepository repo,
    IGitService git,
    IGitHubService github,
    ClaudeSessionRunner claude,
    ISettingsStore settings,
    UsageService usage,
    BlockingService blocking,
    ActivityLog log,
    AppPaths paths)
{
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();

    // ---- Scheduler jobs ----

    public async Task RunPlanningAsync(int issueId, CancellationToken ct)
    {
        var (issue, project) = await LoadAsync(issueId, ct);
        var key = ActivityLog.IssueKey(issueId);
        try
        {
            await StartPhaseAsync(issueId, IssueState.Planning, ct);
            var worktree = await EnsureWorktreeAsync(project, issue, ct);

            var sessionId = issue.SessionId;
            string prompt;
            if (sessionId is not null && issue.PendingAnswer is not null)
            {
                prompt = PromptTemplates.Render(PromptTemplates.Answers, new Dictionary<string, string> { ["Answers"] = issue.PendingAnswer });
            }
            else if (sessionId is not null && issue.PendingFeedback is not null)
            {
                prompt = PromptTemplates.Render(PromptTemplates.Feedback, new Dictionary<string, string>
                {
                    ["Number"] = issue.Number.ToString(CultureInfo.InvariantCulture),
                    ["Feedback"] = issue.PendingFeedback,
                });
            }
            else if (sessionId is not null && issue.RunInProgress)
            {
                prompt = PromptTemplates.Render(PromptTemplates.Continue);
            }
            else
            {
                sessionId = null;
                prompt = await PlannerPromptAsync(project, issue, ct);
            }

            var result = await RunClaudeAsync(issue, key, new ClaudeRunRequest
            {
                WorkingDirectory = worktree,
                Prompt = prompt,
                PermissionMode = "plan",
                JsonSchema = PromptTemplates.Schema(PromptTemplates.Planner),
                DisallowedTools = ClaudeSessionRunner.NoInteractiveTools,
                Name = $"ADCP #{issue.Number} plan",
            }, sessionId, ct);
            if (result is null)
            {
                return;
            }

            var output = PhaseOutputs.Parse<PlannerOutput>(result.StructuredOutput)
                         ?? throw new InvalidOperationException("The planner returned no structured output.");
            if (output.Status == "questions" && output.Questions is { Count: > 0 } questions)
            {
                await repo.UpdateIssueAsync(issueId, i =>
                {
                    i.State = IssueState.AwaitingAnswer;
                    i.PendingQuestions = Questions.Serialize(questions);
                    i.PendingAnswer = null;
                    i.RunInProgress = false;
                }, ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(output.Plan))
            {
                throw new InvalidOperationException("The planner returned neither questions nor a plan.");
            }

            var autostart = settings.Current.AutostartExecution;
            var updated = await repo.UpdateIssueAsync(issueId, i =>
            {
                i.CurrentPlan = output.Plan;
                i.PlanIteration++;
                i.PendingAnswer = null;
                i.PendingFeedback = null;
                i.PendingQuestions = null;
                i.RunInProgress = false;
                i.State = autostart ? IssueState.Executing : IssueState.PlanReady;
            }, ct);
            await CommentAsync(project, updated, $"## Implementation plan (iteration {updated.PlanIteration})\n\n{output.Plan}", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await FailAsync(issueId, IssueState.Planning, e.Message);
        }
    }

    public async Task RunExecutionAsync(int issueId, CancellationToken ct)
    {
        var (issue, project) = await LoadAsync(issueId, ct);
        var key = ActivityLog.IssueKey(issueId);
        try
        {
            if (string.IsNullOrWhiteSpace(issue.CurrentPlan))
            {
                throw new InvalidOperationException("There is no plan to execute.");
            }

            await StartPhaseAsync(issueId, IssueState.Executing, ct);
            var worktree = await EnsureWorktreeAsync(project, issue, ct);

            var prompt = issue.SessionId is not null && issue.RunInProgress
                ? PromptTemplates.Render(PromptTemplates.Continue)
                : PromptTemplates.Render(PromptTemplates.Executor, new Dictionary<string, string>
                {
                    ["Number"] = issue.Number.ToString(CultureInfo.InvariantCulture),
                    ["Repo"] = project.GitHubRepo,
                    ["Title"] = issue.Title,
                    ["Type"] = issue.Type.ToString(),
                    ["Body"] = issue.Body,
                    ["Plan"] = issue.CurrentPlan,
                });

            var result = await RunClaudeAsync(issue, key, new ClaudeRunRequest
            {
                WorkingDirectory = worktree,
                Prompt = prompt,
                PermissionMode = "auto",
                JsonSchema = PromptTemplates.Schema(PromptTemplates.Executor),
                DisallowedTools = ClaudeSessionRunner.NoInteractiveTools,
                Name = $"ADCP #{issue.Number} execute",
            }, issue.SessionId, ct);
            if (result is null)
            {
                return;
            }

            var output = PhaseOutputs.Parse<ExecutorOutput>(result.StructuredOutput)
                         ?? throw new InvalidOperationException("The execution returned no structured output.");
            var message = string.IsNullOrWhiteSpace(output.CommitMessage) ? $"Implement #{issue.Number}: {issue.Title}" : output.CommitMessage.Trim();
            var committed = await git.CommitAllAsync(worktree, $"{message}\n\nRefs #{issue.Number}", ct);
            log.Add(key, committed ? "Committed the changes locally (not pushed)." : "There were no changes to commit.");

            var updated = await repo.UpdateIssueAsync(issueId, i =>
            {
                i.LastReport = output.Report;
                i.RunInProgress = false;
                i.State = IssueState.Review;
                i.Notice = committed ? null : "The execution did not change any files.";
            }, ct);
            await CommentAsync(project, updated, $"## Completion report\n\n{output.Report}", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await FailAsync(issueId, IssueState.Executing, e.Message);
        }
    }

    // ---- User actions ----

    public Task StartPlanningAsync(int issueId) => TransitionAsync(issueId, IssueState.Waiting, i => i.State = IssueState.Planning);

    public Task StartExecutionAsync(int issueId) => TransitionAsync(issueId, IssueState.PlanReady, i => i.State = IssueState.Executing);

    public Task SubmitAnswerAsync(int issueId, IReadOnlyList<QuestionAnswer> answers) =>
        TransitionAsync(issueId, IssueState.AwaitingAnswer, i =>
        {
            i.PendingAnswer = Questions.FormatAnswers(answers);
            i.PendingQuestions = null;
            i.State = IssueState.Planning;
        });

    /// <summary>Accept: merge into the default branch, push, close the issue and unblock dependent issues.</summary>
    public async Task AcceptAsync(int issueId, CancellationToken ct = default)
    {
        var (issue, project) = await LoadAsync(issueId, ct);
        if (issue.State is not (IssueState.Review or IssueState.Error) ||
            (issue.State == IssueState.Error && issue.ErrorPhase != IssueState.Merging))
        {
            return;
        }

        var key = ActivityLog.IssueKey(issueId);
        await repo.UpdateIssueAsync(issueId, i =>
        {
            i.State = IssueState.Merging;
            i.ErrorPhase = null;
            i.LastError = null;
            i.Notice = null;
        }, ct);

        try
        {
            var defaultBranch = project.DefaultBranch;
            var current = await git.GetCurrentBranchAsync(project.LocalPath, ct);
            if (!string.Equals(current, defaultBranch, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The project folder must be on branch '{defaultBranch}' (currently '{current}').");
            }

            if (!await git.IsCleanAsync(project.LocalPath, ct))
            {
                throw new InvalidOperationException("The project folder has uncommitted changes.");
            }

            await git.PullFastForwardAsync(project.LocalPath, ct);
            string? mergedSha = null;
            if (issue.Branch is not null && issue.WorktreePath is not null && Directory.Exists(issue.WorktreePath))
            {
                if (await git.MergeAsync(issue.WorktreePath, defaultBranch, null, noFastForward: false, ct) == MergeOutcome.Conflict ||
                    await git.MergeAsync(project.LocalPath, issue.Branch, $"Merge #{issue.Number}: {issue.Title}\n\nCloses #{issue.Number}", noFastForward: true, ct) == MergeOutcome.Conflict)
                {
                    await repo.UpdateIssueAsync(issueId, i =>
                    {
                        i.State = IssueState.Review;
                        i.Notice = $"Merge conflict with '{defaultBranch}'. Resolve it in the worktree or request an adjustment.";
                    }, ct);
                    log.Add(key, "Merge conflict; the merge was aborted.");
                    return;
                }

                await git.PushAsync(project.LocalPath, ct);
                mergedSha = await git.GetHeadShaAsync(project.LocalPath, ct);
                await git.RemoveWorktreeAsync(project.LocalPath, issue.WorktreePath, ct);
                await git.DeleteBranchAsync(project.LocalPath, issue.Branch, ct);
                log.Add(key, $"Merged and pushed ({mergedSha[..Math.Min(7, mergedSha.Length)]}).");
            }

            try
            {
                await github.CommentAsync(project.GitHubRepo, issue.Number,
                    mergedSha is null ? "Accepted." : $"Accepted and merged into `{defaultBranch}` ({mergedSha}).", ct);
                await github.CloseIssueAsync(project.GitHubRepo, issue.Number, ct);
            }
            catch (GitHubException e)
            {
                log.Add(key, $"Could not close the issue on GitHub: {e.Message}");
            }

            await repo.UpdateIssueAsync(issueId, i =>
            {
                i.State = IssueState.Done;
                i.SessionId = null;
                i.RunInProgress = false;
                i.WorktreePath = null;
                i.Branch = null;
                i.PendingAnswer = null;
                i.PendingFeedback = null;
                i.PendingQuestions = null;
            }, ct);
            await blocking.RecomputeAsync(project.Id, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await FailAsync(issueId, IssueState.Merging, e.Message);
        }
    }

    /// <summary>Adjust: the change request goes to the issue as a comment and back to the planner.</summary>
    public async Task RequestAdjustmentAsync(int issueId, string feedback)
    {
        var (issue, project) = await LoadAsync(issueId, CancellationToken.None);
        if (issue.State != IssueState.Review || string.IsNullOrWhiteSpace(feedback))
        {
            return;
        }

        var updated = await repo.UpdateIssueAsync(issueId, i =>
        {
            i.PendingFeedback = feedback.Trim();
            i.Notice = null;
            i.State = IssueState.AdjustRequested;
        });
        await CommentAsync(project, updated, $"## Change request\n\n{feedback.Trim()}", CancellationToken.None);
    }

    /// <summary>Restart: discards the worktree, branch and session; the user then edits the issue or plan.</summary>
    public async Task RejectAsync(int issueId)
    {
        var (issue, project) = await LoadAsync(issueId, CancellationToken.None);
        if (issue.State is not (IssueState.Review or IssueState.Error))
        {
            return;
        }

        if (issue.WorktreePath is not null)
        {
            await git.RemoveWorktreeAsync(project.LocalPath, issue.WorktreePath);
        }

        if (issue.Branch is not null)
        {
            await git.DeleteBranchAsync(project.LocalPath, issue.Branch);
        }

        var updated = await repo.UpdateIssueAsync(issueId, i =>
        {
            i.State = IssueState.Rejected;
            i.SessionId = null;
            i.RunInProgress = false;
            i.WorktreePath = null;
            i.Branch = null;
            i.PendingAnswer = null;
            i.PendingFeedback = null;
            i.PendingQuestions = null;
            i.ErrorPhase = null;
            i.LastError = null;
            i.Notice = null;
        });
        log.Add(ActivityLog.IssueKey(issueId), "Progress discarded.");
        await CommentAsync(project, updated, "## Restart\n\nThe implementation was rejected and all progress was discarded.", CancellationToken.None);
    }

    public async Task RestartWithPlanningAsync(int issueId, string? newBody)
    {
        await UpdateBodyIfChangedAsync(issueId, newBody);
        await TransitionAsync(issueId, IssueState.Rejected, i =>
        {
            i.CurrentPlan = null;
            i.State = IssueState.Planning;
        });
    }

    public async Task RestartWithPlanAsync(int issueId, string plan, string? newBody)
    {
        if (string.IsNullOrWhiteSpace(plan))
        {
            return;
        }

        await UpdateBodyIfChangedAsync(issueId, newBody);
        var (issue, project) = await LoadAsync(issueId, CancellationToken.None);
        if (issue.State != IssueState.Rejected)
        {
            return;
        }

        var updated = await repo.UpdateIssueAsync(issueId, i =>
        {
            i.CurrentPlan = plan.Trim();
            i.PlanIteration++;
            i.State = IssueState.Executing;
        });
        await CommentAsync(project, updated, $"## Implementation plan (iteration {updated.PlanIteration}, edited)\n\n{plan.Trim()}", CancellationToken.None);
    }

    public async Task RetryAsync(int issueId)
    {
        var issue = await repo.GetIssueAsync(issueId) ?? throw new InvalidOperationException($"Issue {issueId} not found.");
        if (issue.State != IssueState.Error)
        {
            return;
        }

        if (issue.ErrorPhase == IssueState.Merging)
        {
            await AcceptAsync(issueId);
            return;
        }

        await repo.UpdateIssueAsync(issueId, i =>
        {
            i.State = i.ErrorPhase ?? IssueState.Planning;
            i.ErrorPhase = null;
            i.LastError = null;
        });
    }

    public Task SetPriorityModeAsync(int issueId, PriorityMode mode) =>
        repo.UpdateIssueAsync(issueId, i =>
        {
            i.OnHold = mode == PriorityMode.OnHold;
            i.IsPrioritized = mode == PriorityMode.Prioritized;
        });

    public async Task<string> GetDiffStatAsync(Issue issue, Project project, CancellationToken ct = default)
    {
        if (issue.WorktreePath is null || !Directory.Exists(issue.WorktreePath))
        {
            return "";
        }

        try
        {
            return (await git.DiffStatAsync(issue.WorktreePath, project.DefaultBranch, ct)).TrimEnd();
        }
        catch (GitException e)
        {
            return e.Message;
        }
    }

    /// <summary>Issues interrupted while merging cannot be resumed automatically.</summary>
    public async Task RecoverAfterRestartAsync()
    {
        var snapshot = await repo.LoadSnapshotAsync();
        foreach (var issue in snapshot.Issues.Where(i => i.State == IssueState.Merging))
        {
            await FailAsync(issue.Id, IssueState.Merging, "The app was closed while merging. Retry to finish the merge.");
        }
    }

    internal static string Slug(string text)
    {
        var slug = NonSlugCharacters().Replace(text.ToLowerInvariant(), "-").Trim('-');
        return slug.Length <= 40 ? slug : slug[..40].TrimEnd('-');
    }

    // ---- Helpers ----

    /// <summary>Runs Claude; handles usage limits and missing sessions. Returns null when the phase did not finish.</summary>
    private async Task<ClaudeRunResult?> RunClaudeAsync(Issue issue, string key, ClaudeRunRequest request, string? sessionId, CancellationToken ct)
    {
        request = request with
        {
            ResumeSessionId = sessionId,
            NewSessionId = sessionId is null ? Guid.NewGuid().ToString() : null,
        };
        await repo.UpdateIssueAsync(issue.Id, i =>
        {
            i.RunInProgress = true;
            i.SessionId = sessionId;
        }, ct);

        var result = await claude.RunAsync(key, request, sessionId, id => repo.UpdateIssueAsync(issue.Id, i => i.SessionId = id, ct), ct);
        _ = usage.RequestRefresh();

        if (result.UsageLimitReached)
        {
            var resumeAt = claude.ResumeTime(result);
            await repo.UpdateIssueAsync(issue.Id, i =>
            {
                i.IsInterrupted = true;
                i.ResumeAt = resumeAt;
            }, ct);
            log.Add(key, $"Paused until {resumeAt.ToLocalTime():HH:mm} (usage limit).");
            return null;
        }

        if (!result.Success)
        {
            if (ClaudeSessionRunner.IsMissingSession(result))
            {
                await repo.UpdateIssueAsync(issue.Id, i =>
                {
                    i.SessionId = null;
                    i.RunInProgress = false;
                }, ct);
                log.Add(key, "The session could not be resumed; the next run starts a new one.");
                return null;
            }

            throw new InvalidOperationException(result.ErrorMessage);
        }

        return result;
    }

    private Task StartPhaseAsync(int issueId, IssueState phase, CancellationToken ct) =>
        repo.UpdateIssueAsync(issueId, i =>
        {
            i.State = phase;
            i.IsInterrupted = false;
            i.ResumeAt = null;
            i.ErrorPhase = null;
            i.LastError = null;
        }, ct);

    private async Task<string> EnsureWorktreeAsync(Project project, Issue issue, CancellationToken ct)
    {
        if (issue.WorktreePath is not null && Directory.Exists(issue.WorktreePath))
        {
            return issue.WorktreePath;
        }

        var branch = issue.Branch ?? $"issue-{issue.Number}-{Slug(issue.Title)}".TrimEnd('-');
        var path = Path.Combine(paths.Worktrees, $"{Slug(project.Name)}-{project.Id}", $"issue-{issue.Number}");
        await git.AddWorktreeAsync(project.LocalPath, path, branch, project.DefaultBranch, ct);
        await repo.UpdateIssueAsync(issue.Id, i =>
        {
            i.Branch = branch;
            i.WorktreePath = path;
        }, ct);
        log.Add(ActivityLog.IssueKey(issue.Id), $"Created worktree {path} on branch {branch}.");
        issue.Branch = branch;
        issue.WorktreePath = path;
        return path;
    }

    private async Task<string> PlannerPromptAsync(Project project, Issue issue, CancellationToken ct)
    {
        var comments = new StringBuilder();
        try
        {
            var details = await github.GetIssueAsync(project.GitHubRepo, issue.Number, ct);
            foreach (var comment in details.Comments)
            {
                comments.Append("### ").Append(comment.Author).Append(", ")
                    .AppendLine(comment.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                    .AppendLine(comment.Body).AppendLine();
            }
        }
        catch (GitHubException e)
        {
            log.Add(ActivityLog.IssueKey(issue.Id), $"Could not load the comments: {e.Message}");
        }

        return PromptTemplates.Render(PromptTemplates.Planner, new Dictionary<string, string>
        {
            ["Number"] = issue.Number.ToString(CultureInfo.InvariantCulture),
            ["Repo"] = project.GitHubRepo,
            ["Title"] = issue.Title,
            ["Type"] = issue.Type.ToString(),
            ["Body"] = issue.Body,
            ["Comments"] = comments.Length == 0 ? "(none)" : comments.ToString().TrimEnd(),
        });
    }

    private async Task CommentAsync(Project project, Issue issue, string body, CancellationToken ct)
    {
        try
        {
            await github.CommentAsync(project.GitHubRepo, issue.Number, body, ct);
        }
        catch (GitHubException e)
        {
            log.Add(ActivityLog.IssueKey(issue.Id), $"Could not post the comment: {e.Message}");
            await repo.UpdateIssueAsync(issue.Id, i => i.Notice = $"Could not post a comment on GitHub: {e.Message}", ct);
        }
    }

    private async Task UpdateBodyIfChangedAsync(int issueId, string? newBody)
    {
        var (issue, project) = await LoadAsync(issueId, CancellationToken.None);
        if (newBody is null || newBody == issue.Body)
        {
            return;
        }

        await github.UpdateIssueBodyAsync(project.GitHubRepo, issue.Number, newBody);
        await repo.UpdateIssueAsync(issueId, i => i.Body = newBody);
    }

    private async Task TransitionAsync(int issueId, IssueState from, Action<Issue> change)
    {
        var issue = await repo.GetIssueAsync(issueId) ?? throw new InvalidOperationException($"Issue {issueId} not found.");
        if (issue.State == from)
        {
            await repo.UpdateIssueAsync(issueId, change);
        }
    }

    private async Task FailAsync(int issueId, IssueState phase, string message)
    {
        log.Add(ActivityLog.IssueKey(issueId), $"✖ {message}");
        await repo.UpdateIssueAsync(issueId, i =>
        {
            i.State = IssueState.Error;
            i.ErrorPhase = phase;
            i.LastError = message;
        }, CancellationToken.None);
    }

    private async Task<(Issue Issue, Project Project)> LoadAsync(int issueId, CancellationToken ct)
    {
        var issue = await repo.GetIssueAsync(issueId, ct) ?? throw new InvalidOperationException($"Issue {issueId} not found.");
        var project = await repo.GetProjectAsync(issue.ProjectId, ct) ?? throw new InvalidOperationException($"Project {issue.ProjectId} not found.");
        return (issue, project);
    }
}
