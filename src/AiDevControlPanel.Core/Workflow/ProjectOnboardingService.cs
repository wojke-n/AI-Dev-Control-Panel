using AiDevControlPanel.Core.Claude;
using AiDevControlPanel.Core.Domain;
using AiDevControlPanel.Core.Git;
using AiDevControlPanel.Core.GitHub;
using AiDevControlPanel.Core.Persistence;

namespace AiDevControlPanel.Core.Workflow;

public sealed record OnboardingValidation(IReadOnlyList<string> Errors, string? NormalizedPath, RepoInfo? Repo)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Adds projects: validates the folder, runs /init and prepares the GitHub labels.</summary>
public sealed class ProjectOnboardingService(
    IAppRepository repo,
    IGitService git,
    IGitHubService github,
    ClaudeSessionRunner claude,
    ActivityLog log)
{
    private const string ClaudeMd = "CLAUDE.md";

    public async Task<OnboardingValidation> ValidateAsync(string? path, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(path))
        {
            return new OnboardingValidation(["Please enter the project folder."], null, null);
        }

        if (!Directory.Exists(path))
        {
            return new OnboardingValidation(["The folder does not exist."], null, null);
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var topLevel = await git.GetTopLevelAsync(normalized, ct);
        if (topLevel is null)
        {
            errors.Add("The folder is not a git repository.");
        }
        else if (!string.Equals(Path.TrimEndingDirectorySeparator(topLevel), normalized, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Please select the root folder of the repository ({topLevel}).");
        }

        if (!Directory.EnumerateFiles(normalized, "README*").Any())
        {
            errors.Add("The folder has no README.");
        }

        var snapshot = await repo.LoadSnapshotAsync(ct);
        if (snapshot.Projects.Any(p => string.Equals(p.LocalPath, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("This project has already been added.");
        }

        RepoInfo? repoInfo = null;
        if (topLevel is not null)
        {
            try
            {
                repoInfo = await github.GetRepoInfoAsync(normalized, ct);
            }
            catch (GitHubException e)
            {
                errors.Add($"The repository is not connected to GitHub: {e.Message}");
            }
        }

        return new OnboardingValidation(errors, normalized, repoInfo);
    }

    /// <summary>Adds the project; the scheduler then runs <see cref="RunOnboardingAsync"/>.</summary>
    public async Task<OnboardingValidation> AddAsync(string? path, CancellationToken ct = default)
    {
        var validation = await ValidateAsync(path, ct);
        if (!validation.IsValid)
        {
            return validation;
        }

        await repo.AddProjectAsync(new Project
        {
            Name = Path.GetFileName(validation.NormalizedPath!),
            LocalPath = validation.NormalizedPath!,
            GitHubRepo = validation.Repo!.NameWithOwner,
            DefaultBranch = validation.Repo.DefaultBranch,
            IsPublic = validation.Repo.IsPublic,
            IsChecked = true,
            OnboardingState = OnboardingState.Initializing,
        }, ct);
        return validation;
    }

    public async Task RunOnboardingAsync(int projectId, CancellationToken ct)
    {
        var project = await repo.GetProjectAsync(projectId, ct) ?? throw new InvalidOperationException($"Project {projectId} not found.");
        var key = ActivityLog.ProjectKey(projectId);
        try
        {
            if (!File.Exists(Path.Combine(project.LocalPath, ClaudeMd)))
            {
                await RunInitAsync(project, key, ct);
            }

            if (await git.HasChangesAsync(project.LocalPath, ClaudeMd, ct))
            {
                await git.CommitFileAsync(project.LocalPath, ClaudeMd, "Add CLAUDE.md", ct);
                log.Add(key, "Committed CLAUDE.md locally (not pushed).");
            }

            await github.EnsureLabelsAsync(project.GitHubRepo, ct);
            await repo.UpdateProjectAsync(projectId, p =>
            {
                p.OnboardingState = OnboardingState.Ready;
                p.LastError = null;
            }, ct);
            log.Add(key, "Project is ready.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Add(key, $"✖ {e.Message}");
            await repo.UpdateProjectAsync(projectId, p =>
            {
                p.OnboardingState = OnboardingState.Failed;
                p.LastError = e.Message;
            }, CancellationToken.None);
        }
    }

    public Task RetryAsync(int projectId) =>
        repo.UpdateProjectAsync(projectId, p =>
        {
            p.OnboardingState = OnboardingState.Initializing;
            p.LastError = null;
        });

    public Task SetCheckedAsync(int projectId, bool isChecked) =>
        repo.UpdateProjectAsync(projectId, p => p.IsChecked = isChecked);

    private async Task RunInitAsync(Project project, string key, CancellationToken ct)
    {
        var result = await claude.RunAsync(key, new ClaudeRunRequest
        {
            WorkingDirectory = project.LocalPath,
            Prompt = "/init",
            PermissionMode = "acceptEdits",
            Name = $"ADCP {project.Name} init",
        }, null, _ => Task.CompletedTask, ct);

        if (!result.Success)
        {
            throw new InvalidOperationException(result.UsageLimitReached
                ? "/init stopped at the usage limit. Retry after the reset."
                : $"/init failed: {result.ErrorMessage}");
        }

        if (!File.Exists(Path.Combine(project.LocalPath, ClaudeMd)))
        {
            throw new InvalidOperationException("/init did not create a CLAUDE.md.");
        }
    }
}
