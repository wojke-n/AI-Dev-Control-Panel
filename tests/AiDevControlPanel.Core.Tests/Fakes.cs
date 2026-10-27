using System.Text.Json;
using AiDevControlPanel.Core.Claude;
using AiDevControlPanel.Core.Domain;
using AiDevControlPanel.Core.Git;
using AiDevControlPanel.Core.GitHub;
using AiDevControlPanel.Core.Usage;

namespace AiDevControlPanel.Core.Tests;

internal sealed class FakeClaudeRunner : IClaudeRunner
{
    private readonly Queue<Func<ClaudeRunRequest, ClaudeRunResult>> _responses = new();

    public List<ClaudeRunRequest> Requests { get; } = [];

    public void Enqueue(Func<ClaudeRunRequest, ClaudeRunResult> response) => _responses.Enqueue(response);

    public void EnqueueOutput(string structuredJson) => Enqueue(r => Success(r, structuredJson));

    public Task<ClaudeRunResult> RunAsync(ClaudeRunRequest request, Action<ClaudeEvent>? onEvent, CancellationToken ct)
    {
        Requests.Add(request);
        var sessionId = request.ResumeSessionId ?? request.NewSessionId ?? "session";
        onEvent?.Invoke(new SessionStartedEvent(sessionId, "test-model", request.PermissionMode));
        var result = _responses.Dequeue()(request);
        return Task.FromResult(result with { SessionId = result.SessionId ?? sessionId });
    }

    public static ClaudeRunResult Success(ClaudeRunRequest request, string structuredJson) =>
        new(true, null, JsonDocument.Parse(structuredJson).RootElement.Clone(), structuredJson, false, null, null, null);

    public static ClaudeRunResult UsageLimit(DateTimeOffset resetsAt) =>
        new(false, null, null, "Usage limit reached", true, resetsAt, "Usage limit reached", null);
}

internal sealed class FakeGitService : IGitService
{
    public List<string> Calls { get; } = [];

    public string CurrentBranch { get; set; } = "main";

    public bool Clean { get; set; } = true;

    public bool HasUncommittedChanges { get; set; } = true;

    public MergeOutcome NextMergeOutcome { get; set; } = MergeOutcome.Merged;

    public Task<string?> GetTopLevelAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<string?>(Directory.Exists(Path.Combine(path, ".git")) ? path : null);

    public Task<string> GetCurrentBranchAsync(string repoPath, CancellationToken ct = default) => Task.FromResult(CurrentBranch);

    public Task<bool> IsCleanAsync(string repoPath, CancellationToken ct = default) => Task.FromResult(Clean);

    public Task<bool> HasChangesAsync(string repoPath, string relativePath, CancellationToken ct = default) =>
        Task.FromResult(HasUncommittedChanges);

    public Task CommitFileAsync(string repoPath, string relativePath, string message, CancellationToken ct = default) =>
        Record($"commit-file {relativePath}");

    public Task<bool> CommitAllAsync(string repoPath, string message, CancellationToken ct = default)
    {
        Calls.Add($"commit-all {message.Split('\n')[0]}");
        return Task.FromResult(HasUncommittedChanges);
    }

    public Task AddWorktreeAsync(string repoPath, string worktreePath, string branch, string baseBranch, CancellationToken ct = default)
    {
        Directory.CreateDirectory(worktreePath);
        return Record($"worktree-add {branch}");
    }

    public Task RemoveWorktreeAsync(string repoPath, string worktreePath, CancellationToken ct = default)
    {
        if (Directory.Exists(worktreePath))
        {
            Directory.Delete(worktreePath, recursive: true);
        }

        return Record("worktree-remove");
    }

    public Task DeleteBranchAsync(string repoPath, string branch, CancellationToken ct = default) => Record($"branch-delete {branch}");

    public Task<string> DiffStatAsync(string worktreePath, string baseBranch, CancellationToken ct = default) => Task.FromResult(" 1 file changed");

    public Task PullFastForwardAsync(string repoPath, CancellationToken ct = default) => Record("pull");

    public Task<MergeOutcome> MergeAsync(string repoPath, string branch, string? message, bool noFastForward, CancellationToken ct = default)
    {
        Calls.Add($"merge {branch}");
        return Task.FromResult(NextMergeOutcome);
    }

    public Task PushAsync(string repoPath, CancellationToken ct = default) => Record("push");

    public Task<string> GetHeadShaAsync(string repoPath, CancellationToken ct = default) => Task.FromResult("abcdef1234567");

    private Task Record(string call)
    {
        Calls.Add(call);
        return Task.CompletedTask;
    }
}

internal sealed class FakeGitHubService : IGitHubService
{
    private int _nextNumber = 1;

    public List<(int Number, string Title, string Body, IssueType Type)> Created { get; } = [];

    public List<(int Number, string Body)> Comments { get; } = [];

    public List<(int Parent, long Child)> SubIssues { get; } = [];

    public List<(int Number, long Blocker)> BlockedBy { get; } = [];

    public List<int> Closed { get; } = [];

    public List<ExistingIssue> Existing { get; } = [];

    public Task<bool> IsAuthenticatedAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<RepoInfo> GetRepoInfoAsync(string localPath, CancellationToken ct = default) =>
        Task.FromResult(new RepoInfo("owner/repo", "main", IsPublic: false));

    public Task EnsureLabelsAsync(string repo, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ExistingIssue>> ListIssuesAsync(string repo, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExistingIssue>>(Existing);

    public Task<GitHubIssueRef> CreateIssueAsync(string repo, string title, string body, IssueType type, CancellationToken ct = default)
    {
        var number = _nextNumber++;
        Created.Add((number, title, body, type));
        return Task.FromResult(new GitHubIssueRef(number, 1000 + number, $"https://github.com/{repo}/issues/{number}"));
    }

    public Task AddSubIssueAsync(string repo, int parentNumber, long childId, CancellationToken ct = default)
    {
        SubIssues.Add((parentNumber, childId));
        return Task.CompletedTask;
    }

    public Task AddBlockedByAsync(string repo, int number, long blockerId, CancellationToken ct = default)
    {
        BlockedBy.Add((number, blockerId));
        return Task.CompletedTask;
    }

    public Task CommentAsync(string repo, int number, string body, CancellationToken ct = default)
    {
        Comments.Add((number, body));
        return Task.CompletedTask;
    }

    public Task<IssueDetails> GetIssueAsync(string repo, int number, CancellationToken ct = default) =>
        Task.FromResult(new IssueDetails(number, "title", "body", "OPEN", "url", [], []));

    public Task UpdateIssueBodyAsync(string repo, int number, string body, CancellationToken ct = default) => Task.CompletedTask;

    public Task CloseIssueAsync(string repo, int number, CancellationToken ct = default)
    {
        Closed.Add(number);
        return Task.CompletedTask;
    }
}

internal sealed class FakeUsageProvider : IUsageProvider
{
    public int Calls { get; private set; }

    public UsageSnapshot? Snapshot { get; set; }

    public Task<UsageSnapshot?> GetUsageAsync(CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Snapshot);
    }
}
