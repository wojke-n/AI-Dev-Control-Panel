namespace AiDevControlPanel.Core.Domain;

/// <summary>
/// Internal priority (never sent to GitHub):
/// 0 on hold · 1/2 blocked · 3/4 not started · 5/6 started · 7/8 interrupted by the usage limit
/// (each pair: regular / prioritized).
/// </summary>
public static class PriorityRules
{
    public static int Compute(Issue issue)
    {
        // Finished issues keep the priority they had last.
        if (issue.State == IssueState.Done)
        {
            return issue.Priority;
        }

        if (issue.OnHold)
        {
            return 0;
        }

        var bonus = issue.IsPrioritized ? 1 : 0;
        if (issue.IsInterrupted)
        {
            return 7 + bonus;
        }

        return issue.State switch
        {
            IssueState.Blocked => 1 + bonus,
            IssueState.Waiting => 3 + bonus,
            _ => 5 + bonus,
        };
    }

    /// <summary>Maps the refiner's priority (0–4) to the flags the app keeps.</summary>
    public static (bool OnHold, bool IsPrioritized) FromRefinerPriority(int priority) => priority switch
    {
        <= 0 => (true, false),
        2 or 4 => (false, true),
        _ => (false, false),
    };
}
