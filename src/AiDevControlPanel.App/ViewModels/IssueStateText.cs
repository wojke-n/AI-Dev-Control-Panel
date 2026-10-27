using AiDevControlPanel.Core.Domain;

namespace AiDevControlPanel.App.ViewModels;

public static class IssueStateText
{
    public static string Of(Issue issue)
    {
        if (issue.IsInterrupted && issue.ResumeAt is { } resume)
        {
            return $"Paused until {resume.ToLocalTime():HH:mm} (usage limit)";
        }

        return issue.State switch
        {
            IssueState.Waiting => "Waiting for the AI",
            IssueState.Blocked => issue.IsContainer ? "Waiting for its sub-issues" : "Blocked by other issues",
            IssueState.Planning => "Planning",
            IssueState.AwaitingAnswer => "The planner needs an answer",
            IssueState.PlanReady => "Plan ready; waiting for the start of the execution",
            IssueState.Executing => "Implementing the plan",
            IssueState.Review => "Review the result",
            IssueState.Merging => "Accepted; merging",
            IssueState.AdjustRequested => "Change requested; waiting for the planner",
            IssueState.Rejected => "Rejected; waiting for a new attempt",
            IssueState.Done => "Done",
            IssueState.Error => $"Failed during {issue.ErrorPhase?.ToString().ToLowerInvariant() ?? "processing"}",
            _ => issue.State.ToString(),
        };
    }
}
