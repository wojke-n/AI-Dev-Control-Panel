using System.Text.Json.Nodes;
using AiDevControlPanel.Core.Domain;
using AiDevControlPanel.Core.Infrastructure;

namespace AiDevControlPanel.Core.GitHub;

public sealed class GitHubException(string message) : Exception(message);

public sealed record RepoInfo(string NameWithOwner, string DefaultBranch, bool IsPublic);

public sealed record GitHubIssueRef(int Number, long Id, string Url);

public sealed record ExistingIssue(int Number, string Title, string State, IReadOnlyList<string> Labels);

public sealed record IssueComment(string Author, DateTimeOffset CreatedAt, string Body);

public sealed record IssueDetails(
    int Number,
    string Title,
    string Body,
    string State,
    string Url,
    IReadOnlyList<string> Labels,
    IReadOnlyList<IssueComment> Comments);

public interface IGitHubService
{
    Task<bool> IsAuthenticatedAsync(CancellationToken ct = default);

    /// <summary>Repository the local clone at <paramref name="localPath"/> belongs to.</summary>
    Task<RepoInfo> GetRepoInfoAsync(string localPath, CancellationToken ct = default);

    /// <summary>Creates the labels for the issue types if they are missing.</summary>
    Task EnsureLabelsAsync(string repo, CancellationToken ct = default);

    Task<IReadOnlyList<ExistingIssue>> ListIssuesAsync(string repo, CancellationToken ct = default);

    Task<GitHubIssueRef> CreateIssueAsync(string repo, string title, string body, IssueType type, CancellationToken ct = default);

    Task AddSubIssueAsync(string repo, int parentNumber, long childId, CancellationToken ct = default);

    Task AddBlockedByAsync(string repo, int number, long blockerId, CancellationToken ct = default);

    Task CommentAsync(string repo, int number, string body, CancellationToken ct = default);

    Task<IssueDetails> GetIssueAsync(string repo, int number, CancellationToken ct = default);

    Task UpdateIssueBodyAsync(string repo, int number, string body, CancellationToken ct = default);

    Task CloseIssueAsync(string repo, int number, CancellationToken ct = default);
}

/// <summary>GitHub access through the gh CLI; request bodies go through stdin to avoid quoting issues.</summary>
public sealed class GitHubService(IProcessRunner runner) : IGitHubService
{
    private static readonly (string Name, string Color, string Description)[] Labels =
    [
        ("enhancement", "a2eeef", "New feature or request"),
        ("bug", "d73a4a", "Something isn't working"),
        ("security", "b60205", "Security vulnerability report"),
    ];

    public static string LabelFor(IssueType type) => type switch
    {
        IssueType.Bug => "bug",
        IssueType.Security => "security",
        _ => "enhancement",
    };

    public async Task<bool> IsAuthenticatedAsync(CancellationToken ct = default) =>
        (await runner.RunAsync(new ProcessSpec("gh", ["auth", "status"]), ct)).Success;

    public async Task<RepoInfo> GetRepoInfoAsync(string localPath, CancellationToken ct = default)
    {
        var json = await GhAsync(ct, localPath, null, "repo", "view", "--json", "nameWithOwner,defaultBranchRef,visibility");
        var node = JsonNode.Parse(json)!;
        return new RepoInfo(
            node["nameWithOwner"]!.GetValue<string>(),
            node["defaultBranchRef"]?["name"]?.GetValue<string>() ?? "main",
            string.Equals(node["visibility"]?.GetValue<string>(), "PUBLIC", StringComparison.OrdinalIgnoreCase));
    }

    public async Task EnsureLabelsAsync(string repo, CancellationToken ct = default)
    {
        var json = await GhAsync(ct, null, null, "label", "list", "-R", repo, "--limit", "500", "--json", "name");
        var existing = JsonNode.Parse(json)!.AsArray()
            .Select(n => n!["name"]!.GetValue<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, color, description) in Labels.Where(l => !existing.Contains(l.Name)))
        {
            await GhAsync(ct, null, null, "label", "create", name, "-R", repo, "--color", color, "--description", description);
        }
    }

    public async Task<IReadOnlyList<ExistingIssue>> ListIssuesAsync(string repo, CancellationToken ct = default)
    {
        var json = await GhAsync(ct, null, null, "issue", "list", "-R", repo, "--state", "all", "--limit", "1000", "--json", "number,title,state,labels");
        return JsonNode.Parse(json)!.AsArray()
            .Select(n => new ExistingIssue(
                n!["number"]!.GetValue<int>(),
                n["title"]!.GetValue<string>(),
                n["state"]!.GetValue<string>(),
                LabelNames(n["labels"])))
            .ToList();
    }

    public async Task<GitHubIssueRef> CreateIssueAsync(string repo, string title, string body, IssueType type, CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["title"] = title,
            ["body"] = body,
            ["labels"] = new JsonArray(LabelFor(type)),
        };
        var json = await ApiAsync(ct, $"repos/{repo}/issues", "POST", request);
        var node = JsonNode.Parse(json)!;
        return new GitHubIssueRef(node["number"]!.GetValue<int>(), node["id"]!.GetValue<long>(), node["html_url"]!.GetValue<string>());
    }

    public Task AddSubIssueAsync(string repo, int parentNumber, long childId, CancellationToken ct = default) =>
        ApiAsync(ct, $"repos/{repo}/issues/{parentNumber}/sub_issues", "POST", new JsonObject { ["sub_issue_id"] = childId });

    public Task AddBlockedByAsync(string repo, int number, long blockerId, CancellationToken ct = default) =>
        ApiAsync(ct, $"repos/{repo}/issues/{number}/dependencies/blocked_by", "POST", new JsonObject { ["issue_id"] = blockerId });

    public Task CommentAsync(string repo, int number, string body, CancellationToken ct = default) =>
        ApiAsync(ct, $"repos/{repo}/issues/{number}/comments", "POST", new JsonObject { ["body"] = body });

    public async Task<IssueDetails> GetIssueAsync(string repo, int number, CancellationToken ct = default)
    {
        var json = await GhAsync(ct, null, null, "issue", "view", number.ToString(), "-R", repo, "--json", "number,title,body,state,url,labels,comments");
        var node = JsonNode.Parse(json)!;
        var comments = node["comments"]!.AsArray()
            .Select(c => new IssueComment(
                c!["author"]?["login"]?.GetValue<string>() ?? "?",
                DateTimeOffset.Parse(c["createdAt"]!.GetValue<string>()),
                c["body"]?.GetValue<string>() ?? ""))
            .ToList();
        return new IssueDetails(
            node["number"]!.GetValue<int>(),
            node["title"]!.GetValue<string>(),
            node["body"]?.GetValue<string>() ?? "",
            node["state"]!.GetValue<string>(),
            node["url"]!.GetValue<string>(),
            LabelNames(node["labels"]),
            comments);
    }

    public Task UpdateIssueBodyAsync(string repo, int number, string body, CancellationToken ct = default) =>
        ApiAsync(ct, $"repos/{repo}/issues/{number}", "PATCH", new JsonObject { ["body"] = body });

    public Task CloseIssueAsync(string repo, int number, CancellationToken ct = default) =>
        ApiAsync(ct, $"repos/{repo}/issues/{number}", "PATCH", new JsonObject { ["state"] = "closed", ["state_reason"] = "completed" });

    private static List<string> LabelNames(JsonNode? labels) =>
        labels?.AsArray().Select(l => l!["name"]!.GetValue<string>()).ToList() ?? [];

    private Task<string> ApiAsync(CancellationToken ct, string endpoint, string method, JsonObject body) =>
        GhAsync(ct, null, body.ToJsonString(), "api", endpoint, "--method", method, "--input", "-");

    private async Task<string> GhAsync(CancellationToken ct, string? workingDirectory, string? stdin, params string[] args)
    {
        var result = await runner.RunAsync(new ProcessSpec("gh", args, workingDirectory, stdin), ct);
        if (!result.Success)
        {
            throw new GitHubException($"gh {string.Join(' ', args.Take(2))} failed: {result.ErrorText}");
        }

        return result.StandardOutput;
    }
}
