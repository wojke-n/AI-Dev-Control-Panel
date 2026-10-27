namespace AiDevControlPanel.Core.Domain;

/// <summary>Textual Software Artifact: free text that the refiner turns into GitHub issues.</summary>
public sealed class Tsa
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Short descriptive title created by the refiner; null until refined.</summary>
    public string? Title { get; set; }

    public TsaState State { get; set; }

    public string? SessionId { get; set; }

    /// <summary>A refiner run started and has not finished; the next run continues the session.</summary>
    public bool RunInProgress { get; set; }

    /// <summary>JSON array of <see cref="Workflow.Question"/> while <see cref="TsaState.AwaitingAnswer"/>.</summary>
    public string? PendingQuestions { get; set; }

    /// <summary>The user's answers, consumed by the next refiner run.</summary>
    public string? PendingAnswer { get; set; }

    /// <summary>Refiner result whose issues are not all created yet (security confirmation or retry).</summary>
    public string? PendingRefinement { get; set; }

    public bool AwaitingSecurityConfirmation { get; set; }

    /// <summary>Set when the refiner hit the usage limit.</summary>
    public DateTimeOffset? ResumeAt { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
