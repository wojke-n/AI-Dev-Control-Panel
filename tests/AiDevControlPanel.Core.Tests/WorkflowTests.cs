using AiDevControlPanel.Core.Domain;
using AiDevControlPanel.Core.Persistence;
using AiDevControlPanel.Core.Workflow;

namespace AiDevControlPanel.Core.Tests;

public class RefinerTests
{
    private const string RefinedJson = """
        {"status":"done","tsaTitle":"Login and export","issues":[
          {"ref":"P","type":"feature","title":"Export","body":"Export data","priority":3},
          {"ref":"P1","parentRef":"P","type":"feature","title":"CSV export","body":"CSV","priority":3},
          {"ref":"P2","parentRef":"P","type":"feature","title":"PDF export","body":"PDF","priority":4,"blockedBy":["P1"]},
          {"ref":"B","type":"bug","title":"Login fails","body":"Fix it","priority":4}
        ]}
        """;

    [Fact]
    public async Task RefinerCreatesIssuesWithSubIssuesAndDependencies()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var project = await env.AddProjectAsync();
        var tsa = await env.Refiner.AddTsaAsync(project.Id, "We need export and login is broken");
        env.Claude.EnqueueOutput(RefinedJson);

        await env.Refiner.RunAsync(tsa.Id, CancellationToken.None);

        var snapshot = await env.Repo.LoadSnapshotAsync();
        var refined = snapshot.Tsas.Single();
        Assert.Equal(TsaState.Refined, refined.State);
        Assert.Equal("Login and export", refined.Title);

        Assert.Equal(["Export", "CSV export", "PDF export", "Login fails"], env.GitHub.Created.Select(c => c.Title));
        Assert.Equal(2, env.GitHub.SubIssues.Count);
        Assert.Contains("Blocked by #2", env.GitHub.Created[2].Body);

        var byTitle = snapshot.Issues.ToDictionary(i => i.Title);
        Assert.True(byTitle["Export"].IsContainer);
        Assert.Equal(IssueState.Blocked, byTitle["Export"].State);
        Assert.Equal(IssueState.Waiting, byTitle["CSV export"].State);
        Assert.Equal(IssueState.Blocked, byTitle["PDF export"].State);
        Assert.Equal(2, byTitle["PDF export"].Priority);
        Assert.Equal(4, byTitle["Login fails"].Priority);
        Assert.Equal(IssueType.Bug, byTitle["Login fails"].Type);

        var request = Assert.Single(env.Claude.Requests);
        Assert.Equal("plan", request.PermissionMode);
        Assert.Contains("We need export and login is broken", request.Prompt);
    }

    [Fact]
    public async Task RefinerQuestionsAreAnsweredInTheSameSession()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var project = await env.AddProjectAsync();
        var tsa = await env.Refiner.AddTsaAsync(project.Id, "Make it better");
        env.Claude.EnqueueOutput("""{"status":"questions","questions":[{"question":"Better how?","options":[{"label":"Faster"},{"label":"Nicer"}]}]}""");

        await env.Refiner.RunAsync(tsa.Id, CancellationToken.None);

        var waiting = (await env.Repo.GetTsaAsync(tsa.Id))!;
        Assert.Equal(TsaState.AwaitingAnswer, waiting.State);
        Assert.Equal("Better how?", Questions.Deserialize(waiting.PendingQuestions).Single().Text);

        await env.Refiner.SubmitAnswerAsync(tsa.Id, [new QuestionAnswer("Better how?", "Faster")]);
        env.Claude.EnqueueOutput("""{"status":"done","tsaTitle":"Speed","issues":[{"ref":"A","type":"feature","title":"Cache","body":"b","priority":3}]}""");
        await env.Refiner.RunAsync(tsa.Id, CancellationToken.None);

        Assert.Equal(TsaState.Refined, (await env.Repo.GetTsaAsync(tsa.Id))!.State);
        var resume = env.Claude.Requests[1];
        Assert.Equal(env.Claude.Requests[0].NewSessionId, resume.ResumeSessionId);
        Assert.Contains("A: Faster", resume.Prompt);
    }

    [Fact]
    public async Task SecurityIssuesInPublicReposNeedConfirmation()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var project = await env.AddProjectAsync(isPublic: true);
        var tsa = await env.Refiner.AddTsaAsync(project.Id, "XSS in the search");
        env.Claude.EnqueueOutput("""
            {"status":"done","tsaTitle":"XSS","issues":[
              {"ref":"S","type":"security","title":"XSS","body":"b","priority":4},
              {"ref":"F","type":"feature","title":"Escape helper","body":"b","priority":3,"blockedBy":["S"]}]}
            """);

        await env.Refiner.RunAsync(tsa.Id, CancellationToken.None);
        Assert.Empty(env.GitHub.Created);
        Assert.True((await env.Repo.GetTsaAsync(tsa.Id))!.AwaitingSecurityConfirmation);

        await env.Refiner.SubmitAnswerAsync(tsa.Id, [new QuestionAnswer("q", RefinerService.SkipSecurity)]);
        await env.Refiner.RunAsync(tsa.Id, CancellationToken.None);

        Assert.Equal(["Escape helper"], env.GitHub.Created.Select(c => c.Title));
        Assert.Equal(TsaState.Refined, (await env.Repo.GetTsaAsync(tsa.Id))!.State);
        Assert.Single(env.Claude.Requests);
    }

    [Fact]
    public void TopologicalOrder_PutsParentsAndBlockersFirst()
    {
        RefinedIssue Item(string r, string? parent = null, params string[] blockedBy) => new(r, parent, "feature", r, "", 3, blockedBy);

        var order = RefinerService.TopologicalOrder([Item("C", "A", "B"), Item("A"), Item("B")]);

        Assert.Equal(["A", "B", "C"], order.Select(i => i.Ref));
    }

    [Fact]
    public void TopologicalOrder_ToleratesParentBlockedByChild()
    {
        RefinedIssue Item(string r, string? parent = null, params string[] blockedBy) => new(r, parent, "feature", r, "", 3, blockedBy);

        var order = RefinerService.TopologicalOrder([Item("P", null, "C"), Item("C", "P")]);

        Assert.Equal(["P", "C"], order.Select(i => i.Ref));
    }

    [Fact]
    public void Validate_RejectsParentCyclesAndUnknownParents()
    {
        RefinedIssue Item(string r, string? parent) => new(r, parent, "feature", r, "", 3, null);

        Assert.Throws<InvalidOperationException>(() => RefinerService.Validate([Item("A", "B"), Item("B", "A")]));
        Assert.Throws<InvalidOperationException>(() => RefinerService.Validate([Item("A", "X")]));
        Assert.Throws<InvalidOperationException>(() => RefinerService.Validate([Item("A", null), Item("A", null)]));
    }
}

public class IssueWorkflowTests
{
    private const string PlanJson = """{"status":"plan","plan":"1. Change the code"}""";
    private const string ReportJson = """{"status":"done","report":"Changed the code.","commitMessage":"Change the code"}""";

    [Fact]
    public async Task PlanningThenExecutionEndsInReview()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var project = await env.AddProjectAsync();
        var issue = await env.AddIssueAsync(project.Id, 7);
        env.Claude.EnqueueOutput(PlanJson);
        env.Claude.EnqueueOutput(ReportJson);

        await env.Issues.RunPlanningAsync(issue.Id, CancellationToken.None);
        var planned = (await env.Repo.GetIssueAsync(issue.Id))!;
        Assert.Equal(IssueState.Executing, planned.State);
        Assert.Equal("1. Change the code", planned.CurrentPlan);
        Assert.NotNull(planned.WorktreePath);
        Assert.StartsWith("## Implementation plan (iteration 1)", env.GitHub.Comments.Single().Body);

        await env.Issues.RunExecutionAsync(issue.Id, CancellationToken.None);
        var reviewed = (await env.Repo.GetIssueAsync(issue.Id))!;
        Assert.Equal(IssueState.Review, reviewed.State);
        Assert.Equal(5, reviewed.Priority);
        Assert.Contains("commit-all Change the code", env.Git.Calls);
        Assert.StartsWith("## Completion report", env.GitHub.Comments[1].Body);

        var (planRequest, executeRequest) = (env.Claude.Requests[0], env.Claude.Requests[1]);
        Assert.Equal("plan", planRequest.PermissionMode);
        Assert.Equal(["AskUserQuestion", "ExitPlanMode"], planRequest.DisallowedTools);
        Assert.Equal("auto", executeRequest.PermissionMode);
        Assert.Equal(planRequest.NewSessionId, executeRequest.ResumeSessionId);
        Assert.Equal(planned.WorktreePath, executeRequest.WorkingDirectory);
    }

    [Fact]
    public async Task WithoutAutostartThePlanWaitsForTheUser()
    {
        await using var env = await TestEnvironment.CreateAsync();
        env.Settings.Update(s => s with { AutostartExecution = false });
        var project = await env.AddProjectAsync();
        var issue = await env.AddIssueAsync(project.Id, 1);
        env.Claude.EnqueueOutput(PlanJson);

        await env.Issues.RunPlanningAsync(issue.Id, CancellationToken.None);
        Assert.Equal(IssueState.PlanReady, (await env.Repo.GetIssueAsync(issue.Id))!.State);

        await env.Issues.StartExecutionAsync(issue.Id);
        Assert.Equal(IssueState.Executing, (await env.Repo.GetIssueAsync(issue.Id))!.State);
    }

    [Fact]
    public async Task PlannerQuestionsResumeTheSessionWithTheAnswers()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var project = await env.AddProjectAsync();
        var issue = await env.AddIssueAsync(project.Id, 1);
        env.Claude.EnqueueOutput("""{"status":"questions","questions":[{"question":"Which DB?","options":[{"label":"SQLite"}]}]}""");
        env.Claude.EnqueueOutput(PlanJson);

        await env.Issues.RunPlanningAsync(issue.Id, CancellationToken.None);
        Assert.Equal(IssueState.AwaitingAnswer, (await env.Repo.GetIssueAsync(issue.Id))!.State);

        await env.Issues.SubmitAnswerAsync(issue.Id, [new QuestionAnswer("Which DB?", "SQLite")]);
        Assert.Equal(IssueState.Planning, (await env.Repo.GetIssueAsync(issue.Id))!.State);
        await env.Issues.RunPlanningAsync(issue.Id, CancellationToken.None);

        Assert.Equal(IssueState.Executing, (await env.Repo.GetIssueAsync(issue.Id))!.State);
        Assert.Contains("A: SQLite", env.Claude.Requests[1].Prompt);
        Assert.Equal(env.Claude.Requests[0].NewSessionId, env.Claude.Requests[1].ResumeSessionId);
    }

    [Fact]
    public async Task AcceptMergesPushesClosesAndUnblocksDependents()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var project = await env.AddProjectAsync();
        var parent = await env.AddIssueAsync(project.Id, 1, IssueState.Blocked, i => i.IsContainer = true);
        var issue = await env.AddIssueAsync(project.Id, 2, IssueState.Waiting, i => i.ParentIssueId = parent.Id);
        var dependent = await env.AddIssueAsync(project.Id, 3, IssueState.Blocked);
        await env.Repo.AddDependencyAsync(dependent.Id, issue.Id);
        env.Claude.EnqueueOutput(PlanJson);
        env.Claude.EnqueueOutput(ReportJson);
        await env.Issues.RunPlanningAsync(issue.Id, CancellationToken.None);
        await env.Issues.RunExecutionAsync(issue.Id, CancellationToken.None);
        var worktree = (await env.Repo.GetIssueAsync(issue.Id))!.WorktreePath!;

        await env.Issues.AcceptAsync(issue.Id);

        var snapshot = await env.Repo.LoadSnapshotAsync();
        Assert.Equal(IssueState.Done, snapshot.Issues.Single(i => i.Id == issue.Id).State);
        Assert.Equal(IssueState.Waiting, snapshot.Issues.Single(i => i.Id == dependent.Id).State);
        Assert.Equal(IssueState.Done, snapshot.Issues.Single(i => i.Id == parent.Id).State);
        Assert.Equal([2, 1], env.GitHub.Closed);
        Assert.Contains("push", env.Git.Calls);
        Assert.False(Directory.Exists(worktree));
    }

    [Fact]
    public async Task AcceptRequiresTheDefaultBranch()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var project = await env.AddProjectAsync();
        var issue = await env.AddIssueAsync(project.Id, 1, IssueState.Review);
        env.Git.CurrentBranch = "feature";

        await env.Issues.AcceptAsync(issue.Id);

        var failed = (await env.Repo.GetIssueAsync(issue.Id))!;
        Assert.Equal(IssueState.Error, failed.State);
        Assert.Equal(IssueState.Merging, failed.ErrorPhase);
        Assert.Contains("'main'", failed.LastError);
    }

    [Fact]
    public async Task AdjustSendsFeedbackToThePlanner()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var project = await env.AddProjectAsync();
        var issue = await env.AddIssueAsync(project.Id, 1);
        env.Claude.EnqueueOutput(PlanJson);
        env.Claude.EnqueueOutput(ReportJson);
        env.Claude.EnqueueOutput("""{"status":"plan","plan":"2. Rename it"}""");
        await env.Issues.RunPlanningAsync(issue.Id, CancellationToken.None);
        await env.Issues.RunExecutionAsync(issue.Id, CancellationToken.None);

        await env.Issues.RequestAdjustmentAsync(issue.Id, "Please rename the method.");
        var adjusted = (await env.Repo.GetIssueAsync(issue.Id))!;
        Assert.Equal(IssueState.AdjustRequested, adjusted.State);
        Assert.Equal("## Change request\n\nPlease rename the method.", env.GitHub.Comments.Last().Body);

        await env.Issues.RunPlanningAsync(issue.Id, CancellationToken.None);
        var replanned = (await env.Repo.GetIssueAsync(issue.Id))!;
        Assert.Equal(IssueState.Executing, replanned.State);
        Assert.Equal(2, replanned.PlanIteration);
        Assert.Null(replanned.PendingFeedback);
        Assert.Contains("Please rename the method.", env.Claude.Requests[2].Prompt);
        Assert.Equal("plan", env.Claude.Requests[2].PermissionMode);
        Assert.Equal(env.Claude.Requests[0].NewSessionId, env.Claude.Requests[2].ResumeSessionId);
    }

    [Fact]
    public async Task RejectDiscardsProgressAndRestartUsesTheEditedPlan()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var project = await env.AddProjectAsync();
        var issue = await env.AddIssueAsync(project.Id, 1);
        env.Claude.EnqueueOutput(PlanJson);
        env.Claude.EnqueueOutput(ReportJson);
        env.Claude.EnqueueOutput(ReportJson);
        await env.Issues.RunPlanningAsync(issue.Id, CancellationToken.None);
        await env.Issues.RunExecutionAsync(issue.Id, CancellationToken.None);
        var worktree = (await env.Repo.GetIssueAsync(issue.Id))!.WorktreePath!;

        await env.Issues.RejectAsync(issue.Id);
        var rejected = (await env.Repo.GetIssueAsync(issue.Id))!;
        Assert.Equal(IssueState.Rejected, rejected.State);
        Assert.Null(rejected.SessionId);
        Assert.False(Directory.Exists(worktree));

        await env.Issues.RestartWithPlanAsync(issue.Id, "Better plan", newBody: null);
        Assert.Equal(IssueState.Executing, (await env.Repo.GetIssueAsync(issue.Id))!.State);
        await env.Issues.RunExecutionAsync(issue.Id, CancellationToken.None);

        var restart = env.Claude.Requests[2];
        Assert.Null(restart.ResumeSessionId);
        Assert.NotNull(restart.NewSessionId);
        Assert.Contains("Better plan", restart.Prompt);
        Assert.Equal(IssueState.Review, (await env.Repo.GetIssueAsync(issue.Id))!.State);
    }

    [Fact]
    public async Task UsageLimitInterruptsAndTheNextRunContinues()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var project = await env.AddProjectAsync();
        var issue = await env.AddIssueAsync(project.Id, 1, IssueState.Waiting, i => i.IsPrioritized = true);
        var reset = env.Time.GetUtcNow().AddHours(2);
        env.Claude.Enqueue(_ => FakeClaudeRunner.UsageLimit(reset));
        env.Claude.EnqueueOutput(PlanJson);

        await env.Issues.RunPlanningAsync(issue.Id, CancellationToken.None);
        var interrupted = (await env.Repo.GetIssueAsync(issue.Id))!;
        Assert.Equal(IssueState.Planning, interrupted.State);
        Assert.True(interrupted.IsInterrupted);
        Assert.Equal(8, interrupted.Priority);
        Assert.Equal(reset, interrupted.ResumeAt);
        Assert.Equal(reset, JobSelector.PausedUntil(await env.Repo.LoadSnapshotAsync(), env.Time.GetUtcNow()));

        await env.Issues.RunPlanningAsync(issue.Id, CancellationToken.None);
        var resumed = (await env.Repo.GetIssueAsync(issue.Id))!;
        Assert.False(resumed.IsInterrupted);
        Assert.Equal(IssueState.Executing, resumed.State);
        Assert.Contains("interrupted", env.Claude.Requests[1].Prompt);
        Assert.Equal(env.Claude.Requests[0].NewSessionId, env.Claude.Requests[1].ResumeSessionId);
    }

    [Fact]
    public async Task FailuresCanBeRetried()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var project = await env.AddProjectAsync();
        var issue = await env.AddIssueAsync(project.Id, 1);
        env.Claude.Enqueue(_ => new ClaudeRunResultBuilder().Failed("boom"));

        await env.Issues.RunPlanningAsync(issue.Id, CancellationToken.None);
        var failed = (await env.Repo.GetIssueAsync(issue.Id))!;
        Assert.Equal(IssueState.Error, failed.State);
        Assert.Equal("boom", failed.LastError);

        await env.Issues.RetryAsync(issue.Id);
        Assert.Equal(IssueState.Planning, (await env.Repo.GetIssueAsync(issue.Id))!.State);
    }

    private sealed class ClaudeRunResultBuilder
    {
        public Claude.ClaudeRunResult Failed(string message) => new(false, null, null, message, false, null, message, null);
    }
}

public class JobSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly Project Project = new() { Id = 1, OnboardingState = OnboardingState.Ready };

    private static Issue Issue(int number, IssueState state, int priority, Action<Issue>? configure = null)
    {
        var issue = new Issue { Id = number, ProjectId = 1, Number = number, State = state, Priority = priority };
        configure?.Invoke(issue);
        return issue;
    }

    private static ScheduledJob? Select(AppSnapshot snapshot, bool autostartPlanning = true) =>
        JobSelector.Select(snapshot, Project, new AppSettings { AutostartPlanning = autostartPlanning }, Now);

    [Fact]
    public void OnboardingComesFirst()
    {
        var project = new Project { Id = 1, OnboardingState = OnboardingState.Initializing };

        Assert.Equal(JobKind.Onboard, JobSelector.Select(AppSnapshot.Empty, project, new AppSettings(), Now)!.Kind);
    }

    [Fact]
    public void InterruptedThenStartedThenTsasThenNewIssues()
    {
        var tsa = new Tsa { Id = 5, ProjectId = 1, State = TsaState.Waiting };
        var waiting = Issue(1, IssueState.Waiting, 4);
        var started = Issue(2, IssueState.AdjustRequested, 5);
        var interrupted = Issue(3, IssueState.Executing, 7, i => { i.IsInterrupted = true; i.ResumeAt = Now.AddMinutes(-1); });

        Assert.Equal(new ScheduledJob(JobKind.Execute, 1, IssueId: 3), Select(new AppSnapshot([Project], [tsa], [waiting, started, interrupted], [])));
        Assert.Equal(new ScheduledJob(JobKind.Plan, 1, IssueId: 2), Select(new AppSnapshot([Project], [tsa], [waiting, started], [])));
        Assert.Equal(new ScheduledJob(JobKind.Refine, 1, TsaId: 5), Select(new AppSnapshot([Project], [tsa], [waiting], [])));
        Assert.Equal(new ScheduledJob(JobKind.Plan, 1, IssueId: 1), Select(new AppSnapshot([Project], [], [waiting], [])));
    }

    [Fact]
    public void HigherPriorityThenLowerNumberWins()
    {
        var snapshot = new AppSnapshot([Project], [], [Issue(9, IssueState.Waiting, 3), Issue(8, IssueState.Waiting, 4), Issue(7, IssueState.Waiting, 4)], []);

        Assert.Equal(7, Select(snapshot)!.IssueId);
    }

    [Fact]
    public void SkipsBlockedOnHoldContainersAndHumanStates()
    {
        var snapshot = new AppSnapshot([Project], [], [
            Issue(1, IssueState.Blocked, 2),
            Issue(2, IssueState.Waiting, 0, i => i.OnHold = true),
            Issue(3, IssueState.Planning, 5, i => i.IsContainer = true),
            Issue(4, IssueState.Review, 6),
            Issue(5, IssueState.AwaitingAnswer, 6),
        ], []);

        Assert.Null(Select(snapshot));
    }

    [Fact]
    public void NewIssuesOnlyWithAutostartPlanning()
    {
        var snapshot = new AppSnapshot([Project], [], [Issue(1, IssueState.Waiting, 3)], []);

        Assert.Null(Select(snapshot, autostartPlanning: false));
    }

    [Fact]
    public void PausesWhileTheUsageLimitIsActive()
    {
        var other = new Project { Id = 2, OnboardingState = OnboardingState.Ready };
        var snapshot = new AppSnapshot([Project, other], [], [
            Issue(1, IssueState.Planning, 7, i => { i.IsInterrupted = true; i.ResumeAt = Now.AddMinutes(5); }),
            Issue(2, IssueState.Waiting, 3, i => i.ProjectId = 2),
        ], []);

        Assert.Null(JobSelector.Select(snapshot, other, new AppSettings(), Now));
        Assert.NotNull(JobSelector.Select(snapshot, other, new AppSettings(), Now.AddMinutes(6)));
    }
}
