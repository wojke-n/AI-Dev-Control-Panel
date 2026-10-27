using AiDevControlPanel.Core.Domain;
using AiDevControlPanel.Core.GitHub;
using AiDevControlPanel.Core.Persistence;

namespace AiDevControlPanel.Core.Workflow;

/// <summary>
/// Keeps Waiting/Blocked in sync with the dependencies and completes container issues
/// once all their sub-issues are done.
/// </summary>
public sealed class BlockingService(IAppRepository repo, IGitHubService github, ActivityLog log)
{
    public async Task RecomputeAsync(int projectId, CancellationToken ct = default)
    {
        bool changed;
        do
        {
            changed = false;
            var snapshot = await repo.LoadSnapshotAsync(ct);
            var project = snapshot.Projects.First(p => p.Id == projectId);
            foreach (var issue in snapshot.IssuesOf(projectId).Where(i => i.State is IssueState.Waiting or IssueState.Blocked))
            {
                if (issue.IsContainer)
                {
                    var children = snapshot.ChildrenOf(issue.Id).ToList();
                    if (children.Count > 0 && children.All(c => c.State == IssueState.Done))
                    {
                        await CompleteContainerAsync(project, issue, ct);
                        changed = true;
                    }
                    else if (issue.State != IssueState.Blocked)
                    {
                        await repo.UpdateIssueAsync(issue.Id, i => i.State = IssueState.Blocked, ct);
                    }

                    continue;
                }

                var target = snapshot.BlockersOf(issue.Id).Any(b => b.State != IssueState.Done) ? IssueState.Blocked : IssueState.Waiting;
                if (target != issue.State)
                {
                    await repo.UpdateIssueAsync(issue.Id, i => i.State = target, ct);
                }
            }
        }
        // A finished container can unblock other issues and complete its own parent.
        while (changed);
    }

    private async Task CompleteContainerAsync(Project project, Issue issue, CancellationToken ct)
    {
        try
        {
            await github.CommentAsync(project.GitHubRepo, issue.Number, "All sub-issues are done.", ct);
            await github.CloseIssueAsync(project.GitHubRepo, issue.Number, ct);
        }
        catch (GitHubException e)
        {
            log.Add(ActivityLog.IssueKey(issue.Id), $"Could not close the issue on GitHub: {e.Message}");
        }

        await repo.UpdateIssueAsync(issue.Id, i => i.State = IssueState.Done, ct);
        log.Add(ActivityLog.IssueKey(issue.Id), "All sub-issues are done.");
    }
}
