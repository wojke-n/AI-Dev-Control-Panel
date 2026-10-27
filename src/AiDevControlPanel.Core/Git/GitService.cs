using AiDevControlPanel.Core.Infrastructure;

namespace AiDevControlPanel.Core.Git;

public sealed class GitException(string message) : Exception(message);

public enum MergeOutcome
{
    Merged,
    Conflict,
}

public interface IGitService
{
    /// <summary>Top-level directory of the repository containing <paramref name="path"/>, or null.</summary>
    Task<string?> GetTopLevelAsync(string path, CancellationToken ct = default);

    Task<string> GetCurrentBranchAsync(string repoPath, CancellationToken ct = default);

    /// <summary>True when tracked files have no uncommitted changes (untracked files are ignored).</summary>
    Task<bool> IsCleanAsync(string repoPath, CancellationToken ct = default);

    /// <summary>True when the path is untracked or modified.</summary>
    Task<bool> HasChangesAsync(string repoPath, string relativePath, CancellationToken ct = default);

    /// <summary>Commits only the given file (other staged changes stay untouched).</summary>
    Task CommitFileAsync(string repoPath, string relativePath, string message, CancellationToken ct = default);

    /// <summary>Stages everything and commits; returns false when there was nothing to commit.</summary>
    Task<bool> CommitAllAsync(string repoPath, string message, CancellationToken ct = default);

    Task AddWorktreeAsync(string repoPath, string worktreePath, string branch, string baseBranch, CancellationToken ct = default);

    Task RemoveWorktreeAsync(string repoPath, string worktreePath, CancellationToken ct = default);

    Task DeleteBranchAsync(string repoPath, string branch, CancellationToken ct = default);

    Task<string> DiffStatAsync(string worktreePath, string baseBranch, CancellationToken ct = default);

    Task PullFastForwardAsync(string repoPath, CancellationToken ct = default);

    /// <summary>Merges <paramref name="branch"/>; on conflict the merge is aborted.</summary>
    Task<MergeOutcome> MergeAsync(string repoPath, string branch, string? message, bool noFastForward, CancellationToken ct = default);

    Task PushAsync(string repoPath, CancellationToken ct = default);

    Task<string> GetHeadShaAsync(string repoPath, CancellationToken ct = default);
}

public sealed class GitService(IProcessRunner runner) : IGitService
{
    public async Task<string?> GetTopLevelAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        var result = await GitAsync(path, ct, "rev-parse", "--show-toplevel");
        return result.Success ? Path.GetFullPath(result.StandardOutput.Trim()) : null;
    }

    public async Task<string> GetCurrentBranchAsync(string repoPath, CancellationToken ct = default) =>
        (await GitOrThrowAsync(repoPath, ct, "rev-parse", "--abbrev-ref", "HEAD")).Trim();

    public async Task<bool> IsCleanAsync(string repoPath, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(await GitOrThrowAsync(repoPath, ct, "status", "--porcelain", "--untracked-files=no"));

    public async Task<bool> HasChangesAsync(string repoPath, string relativePath, CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(await GitOrThrowAsync(repoPath, ct, "status", "--porcelain", "--", relativePath));

    public async Task CommitFileAsync(string repoPath, string relativePath, string message, CancellationToken ct = default)
    {
        await GitOrThrowAsync(repoPath, ct, "add", "--", relativePath);
        await GitOrThrowAsync(repoPath, ct, "commit", "-m", message, "--", relativePath);
    }

    public async Task<bool> CommitAllAsync(string repoPath, string message, CancellationToken ct = default)
    {
        await GitOrThrowAsync(repoPath, ct, "add", "-A");
        if (string.IsNullOrWhiteSpace(await GitOrThrowAsync(repoPath, ct, "status", "--porcelain")))
        {
            return false;
        }

        await GitOrThrowAsync(repoPath, ct, "commit", "-m", message);
        return true;
    }

    public async Task AddWorktreeAsync(string repoPath, string worktreePath, string branch, string baseBranch, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
        var branchExists = (await GitAsync(repoPath, ct, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch)).Success;
        if (branchExists)
        {
            await GitOrThrowAsync(repoPath, ct, "worktree", "add", worktreePath, branch);
        }
        else
        {
            await GitOrThrowAsync(repoPath, ct, "worktree", "add", "-b", branch, worktreePath, baseBranch);
        }
    }

    public async Task RemoveWorktreeAsync(string repoPath, string worktreePath, CancellationToken ct = default)
    {
        if (Directory.Exists(worktreePath))
        {
            await GitAsync(repoPath, ct, "worktree", "remove", "--force", worktreePath);
        }

        await GitAsync(repoPath, ct, "worktree", "prune");
        if (Directory.Exists(worktreePath))
        {
            Directory.Delete(worktreePath, recursive: true);
        }
    }

    public async Task DeleteBranchAsync(string repoPath, string branch, CancellationToken ct = default) =>
        await GitAsync(repoPath, ct, "branch", "-D", branch);

    public Task<string> DiffStatAsync(string worktreePath, string baseBranch, CancellationToken ct = default) =>
        GitOrThrowAsync(worktreePath, ct, "diff", "--stat", baseBranch + "...HEAD");

    public async Task PullFastForwardAsync(string repoPath, CancellationToken ct = default) =>
        await GitOrThrowAsync(repoPath, ct, "pull", "--ff-only");

    public async Task<MergeOutcome> MergeAsync(string repoPath, string branch, string? message, bool noFastForward, CancellationToken ct = default)
    {
        var args = new List<string> { "merge" };
        if (noFastForward)
        {
            args.Add("--no-ff");
        }

        if (message is null)
        {
            args.Add("--no-edit");
        }
        else
        {
            args.AddRange(["-m", message]);
        }

        args.Add(branch);
        var result = await GitAsync(repoPath, ct, [.. args]);
        if (result.Success)
        {
            return MergeOutcome.Merged;
        }

        var conflict = (await GitAsync(repoPath, ct, "rev-parse", "--verify", "--quiet", "MERGE_HEAD")).Success;
        if (conflict)
        {
            await GitAsync(repoPath, ct, "merge", "--abort");
            return MergeOutcome.Conflict;
        }

        throw new GitException($"git merge {branch} failed: {result.ErrorText}");
    }

    public async Task PushAsync(string repoPath, CancellationToken ct = default) =>
        await GitOrThrowAsync(repoPath, ct, "push");

    public async Task<string> GetHeadShaAsync(string repoPath, CancellationToken ct = default) =>
        (await GitOrThrowAsync(repoPath, ct, "rev-parse", "HEAD")).Trim();

    private Task<ProcessResult> GitAsync(string workingDirectory, CancellationToken ct, params string[] args) =>
        runner.RunAsync(new ProcessSpec("git", args, workingDirectory), ct);

    private async Task<string> GitOrThrowAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        var result = await GitAsync(workingDirectory, ct, args);
        if (!result.Success)
        {
            throw new GitException($"git {string.Join(' ', args.Take(2))} failed: {result.ErrorText}");
        }

        return result.StandardOutput;
    }
}
