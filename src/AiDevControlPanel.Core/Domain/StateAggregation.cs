namespace AiDevControlPanel.Core.Domain;

/// <summary>Derives the color codes of issues, TSAs and projects.</summary>
public static class StateAggregation
{
    public static StateColor IssueColor(Issue issue) => issue.State switch
    {
        IssueState.Waiting => StateColor.White,
        IssueState.Blocked => StateColor.Gray,
        IssueState.Planning => StateColor.LightYellow,
        IssueState.AwaitingAnswer or IssueState.PlanReady or IssueState.Error => StateColor.LightBlue,
        IssueState.Executing => StateColor.Yellow,
        IssueState.Review => StateColor.DarkBlue,
        IssueState.Merging => StateColor.Orange,
        IssueState.AdjustRequested => StateColor.Purple,
        IssueState.Rejected => StateColor.Red,
        IssueState.Done => StateColor.Green,
        _ => StateColor.White,
    };

    /// <summary>The issue cannot progress until the user acts.</summary>
    public static bool NeedsHuman(Issue issue) => issue.State is
        IssueState.AwaitingAnswer or IssueState.PlanReady or IssueState.Review or IssueState.Rejected or IssueState.Error;

    /// <summary>The issue is being worked on (started and not finished).</summary>
    public static bool IsActive(Issue issue) => issue.State is
        IssueState.Planning or IssueState.AwaitingAnswer or IssueState.PlanReady or IssueState.Executing or
        IssueState.Review or IssueState.Merging or IssueState.AdjustRequested or IssueState.Rejected or IssueState.Error;

    public static StateColor TsaColor(Tsa tsa, IEnumerable<Issue> issuesOfTsa)
    {
        switch (tsa.State)
        {
            case TsaState.Waiting:
                return StateColor.White;
            case TsaState.Refining:
                return StateColor.Yellow;
            case TsaState.AwaitingAnswer:
            case TsaState.Error:
                return StateColor.LightBlue;
        }

        var relevant = issuesOfTsa.Where(i => !i.OnHold).ToList();
        if (relevant.Any(IsActive))
        {
            return StateColor.Orange;
        }

        return relevant.All(i => i.State == IssueState.Done) ? StateColor.Green : StateColor.Brown;
    }

    public static StateColor ProjectColor(Project project, IEnumerable<Tsa> tsas, IEnumerable<Issue> issues, bool jobRunning)
    {
        if (project.OnboardingState == OnboardingState.Failed)
        {
            return StateColor.LightBlue;
        }

        if (project.OnboardingState == OnboardingState.Initializing)
        {
            return StateColor.Yellow;
        }

        var tsaList = tsas.ToList();
        var issueList = issues.ToList();
        if (tsaList.Any(t => t.State is TsaState.AwaitingAnswer or TsaState.Error) || issueList.Any(NeedsHuman))
        {
            return StateColor.LightBlue;
        }

        if (jobRunning || issueList.Any(i => i.State == IssueState.Merging))
        {
            return StateColor.Yellow;
        }

        var allRefined = tsaList.All(t => t.State == TsaState.Refined);
        var allDone = issueList.Where(i => !i.OnHold).All(i => i.State == IssueState.Done);
        return allRefined && allDone ? StateColor.Green : StateColor.White;
    }
}
