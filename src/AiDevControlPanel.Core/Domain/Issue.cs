namespace AiDevControlPanel.Core.Domain;

public sealed class Issue
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public int? TsaId { get; set; }

    public int? ParentIssueId { get; set; }

    /// <summary>Reference the refiner used for this issue; makes issue creation idempotent on retry.</summary>
    public string? RefinerRef { get; set; }

    /// <summary>GitHub issue number.</summary>
    public int Number { get; set; }

    /// <summary>GitHub database id (needed for sub-issue and dependency APIs).</summary>
    public long GitHubId { get; set; }

    public string Title { get; set; } = "";

    public string Body { get; set; } = "";

    public IssueType Type { get; set; }

    /// <summary>Parent issue with sub-issues; never executed itself.</summary>
    public bool IsContainer { get; set; }

    public bool IsPrioritized { get; set; }

    public bool OnHold { get; set; }

    public IssueState State { get; set; }

    /// <summary>Internal priority 0–8, derived by <see cref="PriorityRules"/> on every save.</summary>
    public int Priority { get; set; }

    public string? SessionId { get; set; }

    /// <summary>A planning/execution run started and has not finished; the next run continues the session.</summary>
    public bool RunInProgress { get; set; }

    public string? CurrentPlan { get; set; }

    public int PlanIteration { get; set; }

    public string? LastReport { get; set; }

    public string? PendingQuestions { get; set; }

    public string? PendingAnswer { get; set; }

    public string? PendingFeedback { get; set; }

    public string? Branch { get; set; }

    public string? WorktreePath { get; set; }

    public bool IsInterrupted { get; set; }

    public DateTimeOffset? ResumeAt { get; set; }

    public IssueState? ErrorPhase { get; set; }

    public string? LastError { get; set; }

    /// <summary>Non-fatal notice shown in the review (e.g. a merge conflict).</summary>
    public string? Notice { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class IssueDependency
{
    public int IssueId { get; set; }

    public int BlockedByIssueId { get; set; }
}
