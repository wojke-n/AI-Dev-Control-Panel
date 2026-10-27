using AiDevControlPanel.Core.Domain;
using AiDevControlPanel.Core.Persistence;
using AiDevControlPanel.Core.Usage;
using AiDevControlPanel.Core.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace AiDevControlPanel.Core.Tests;

/// <summary>Real SQLite repository in a temp folder plus fakes for git, gh and claude.</summary>
internal sealed class TestEnvironment : IAsyncDisposable
{
    private TestEnvironment(string root)
    {
        Paths = new AppPaths(root);
        Paths.EnsureCreated();
        Repo = new AppRepository(AppDbContext.CreateOptions(Paths.Database), Time);
        Settings = new SettingsStore(Paths);
        Log = new ActivityLog(Paths, Time);
        Usage = new UsageService(UsageProvider, Time);
        ClaudeSessions = new ClaudeSessionRunner(Claude, Settings, Log, Usage, Time);
        Blocking = new BlockingService(Repo, GitHub, Log);
        Onboarding = new ProjectOnboardingService(Repo, Git, GitHub, ClaudeSessions, Log);
        Refiner = new RefinerService(Repo, GitHub, ClaudeSessions, Blocking, Log);
        Issues = new IssueWorkflowService(Repo, Git, GitHub, ClaudeSessions, Settings, Usage, Blocking, Log, Paths);
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));

    public AppPaths Paths { get; }

    public AppRepository Repo { get; }

    public SettingsStore Settings { get; }

    public ActivityLog Log { get; }

    public FakeClaudeRunner Claude { get; } = new();

    public FakeGitService Git { get; } = new();

    public FakeGitHubService GitHub { get; } = new();

    public FakeUsageProvider UsageProvider { get; } = new();

    public UsageService Usage { get; }

    public ClaudeSessionRunner ClaudeSessions { get; }

    public BlockingService Blocking { get; }

    public ProjectOnboardingService Onboarding { get; }

    public RefinerService Refiner { get; }

    public IssueWorkflowService Issues { get; }

    public static async Task<TestEnvironment> CreateAsync()
    {
        var env = new TestEnvironment(Path.Combine(Path.GetTempPath(), "adcp-tests", Guid.NewGuid().ToString("N")));
        await env.Repo.InitializeAsync();
        return env;
    }

    public Task<Project> AddProjectAsync(bool isPublic = false)
    {
        var folder = Path.Combine(Paths.Root, "project");
        Directory.CreateDirectory(folder);
        return Repo.AddProjectAsync(new Project
        {
            Name = "project",
            LocalPath = folder,
            GitHubRepo = "owner/repo",
            DefaultBranch = "main",
            IsPublic = isPublic,
            OnboardingState = OnboardingState.Ready,
        });
    }

    public async Task<Issue> AddIssueAsync(int projectId, int number, IssueState state = IssueState.Waiting, Action<Issue>? configure = null)
    {
        var issue = new Issue { ProjectId = projectId, Number = number, GitHubId = 1000 + number, Title = $"Issue {number}", Body = "Body", State = state };
        configure?.Invoke(issue);
        return await Repo.AddIssueAsync(issue);
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Paths.Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp folder.
        }

        await Task.CompletedTask;
    }
}
