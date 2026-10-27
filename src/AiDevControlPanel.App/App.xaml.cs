using System.Net.Http;
using System.Windows;
using AiDevControlPanel.App.ViewModels;
using AiDevControlPanel.Core;
using AiDevControlPanel.Core.Claude;
using AiDevControlPanel.Core.Git;
using AiDevControlPanel.Core.GitHub;
using AiDevControlPanel.Core.Infrastructure;
using AiDevControlPanel.Core.Persistence;
using AiDevControlPanel.Core.Usage;
using AiDevControlPanel.Core.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiDevControlPanel.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, "AI-Dev-Control-Panel.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("AI-Dev-Control-Panel is already running.", "AI-Dev-Control-Panel", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            var paths = AppPaths.Default;
            paths.EnsureCreated();
            _host = BuildHost(paths);

            await _host.Services.GetRequiredService<AppRepository>().InitializeAsync();
            await _host.Services.GetRequiredService<IssueWorkflowService>().RecoverAfterRestartAsync();

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            await _host.Services.GetRequiredService<MainViewModel>().InitializeAsync();
            _host.Services.GetRequiredService<JobScheduler>().Start();
            _ = _host.Services.GetRequiredService<UsageService>().RequestRefresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The app could not start:\n\n{ex.Message}", "AI-Dev-Control-Panel", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static IHost BuildHost(AppPaths paths)
    {
        var builder = Host.CreateApplicationBuilder();
        var services = builder.Services;

        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ExecutableLocator>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<IGitHubService, GitHubService>();
        services.AddSingleton<IClaudeRunner, ClaudeCliRunner>();

        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
        services.AddSingleton<IUsageProvider, OAuthUsageProvider>();
        services.AddSingleton<UsageService>();

        services.AddSingleton(sp => new AppRepository(AppDbContext.CreateOptions(paths.Database), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IAppRepository>(sp => sp.GetRequiredService<AppRepository>());
        services.AddSingleton<ISettingsStore, SettingsStore>();

        services.AddSingleton<ActivityLog>();
        services.AddSingleton<ClaudeSessionRunner>();
        services.AddSingleton<BlockingService>();
        services.AddSingleton<ProjectOnboardingService>();
        services.AddSingleton<RefinerService>();
        services.AddSingleton<IssueWorkflowService>();
        services.AddSingleton<IJobExecutor, WorkflowJobExecutor>();
        services.AddSingleton<JobScheduler>();

        services.AddSingleton<ViewModelServices>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return builder.Build();
    }
}
