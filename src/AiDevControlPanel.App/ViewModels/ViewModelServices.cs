using AiDevControlPanel.Core.GitHub;
using AiDevControlPanel.Core.Infrastructure;
using AiDevControlPanel.Core.Persistence;
using AiDevControlPanel.Core.Workflow;

namespace AiDevControlPanel.App.ViewModels;

/// <summary>The services the main-panel view models use, bundled to keep their constructors short.</summary>
public sealed class ViewModelServices(
    IAppRepository repo,
    ISettingsStore settings,
    ProjectOnboardingService onboarding,
    RefinerService refiner,
    IssueWorkflowService issues,
    IGitHubService github,
    ActivityLog log,
    JobScheduler scheduler,
    ExecutableLocator locator)
{
    public IAppRepository Repo { get; } = repo;

    public ISettingsStore Settings { get; } = settings;

    public ProjectOnboardingService Onboarding { get; } = onboarding;

    public RefinerService Refiner { get; } = refiner;

    public IssueWorkflowService Issues { get; } = issues;

    public IGitHubService GitHub { get; } = github;

    public ActivityLog Log { get; } = log;

    public JobScheduler Scheduler { get; } = scheduler;

    public ExecutableLocator Locator { get; } = locator;
}
