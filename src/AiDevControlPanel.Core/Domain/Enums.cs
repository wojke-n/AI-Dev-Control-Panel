namespace AiDevControlPanel.Core.Domain;

public enum OnboardingState
{
    Initializing,
    Ready,
    Failed,
}

public enum TsaState
{
    /// <summary>Waiting for the refiner (also used when queued after an answer).</summary>
    Waiting,
    Refining,
    AwaitingAnswer,
    Refined,
    Error,
}

public enum IssueType
{
    Feature,
    Bug,
    Security,
}

public enum IssueState
{
    /// <summary>Not started, waiting for the AI.</summary>
    Waiting,
    Blocked,
    /// <summary>Planning phase started (queued or running).</summary>
    Planning,
    AwaitingAnswer,
    /// <summary>Plan finished; waits for a manual start because autostart execution is off.</summary>
    PlanReady,
    /// <summary>Execution phase started (queued or running).</summary>
    Executing,
    Review,
    Merging,
    /// <summary>Change request posted; waits for the planner to revise the plan.</summary>
    AdjustRequested,
    /// <summary>Progress discarded; waits for the user to release a new attempt.</summary>
    Rejected,
    Done,
    /// <summary>A phase failed; <see cref="Issue.ErrorPhase"/> tells which one to retry.</summary>
    Error,
}

/// <summary>Colors used by the GUI; the mapping from states lives in <see cref="StateAggregation"/>.</summary>
public enum StateColor
{
    White,
    Gray,
    LightYellow,
    LightBlue,
    Yellow,
    DarkBlue,
    Orange,
    Purple,
    Red,
    Green,
    Brown,
}
