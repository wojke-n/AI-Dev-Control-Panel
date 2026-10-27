using System.Collections.ObjectModel;
using System.IO;
using AiDevControlPanel.Core.Domain;
using AiDevControlPanel.Core.Persistence;
using AiDevControlPanel.Core.Workflow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AiDevControlPanel.App.ViewModels;

public sealed partial class AddProjectViewModel(ViewModelServices services, Action<string> added) : DetailViewModel
{
    public ObservableCollection<string> Errors { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string? Path { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool IsBusy { get; set; }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "Select the project folder" };
        if (dialog.ShowDialog() == true)
        {
            Path = dialog.FolderName;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        IsBusy = true;
        Errors.Clear();
        try
        {
            var result = await services.Onboarding.AddAsync(Path);
            foreach (var error in result.Errors)
            {
                Errors.Add(error);
            }

            if (result.IsValid)
            {
                added(result.NormalizedPath!);
            }
        }
        catch (Exception e)
        {
            Errors.Add(e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAdd() => !IsBusy && !string.IsNullOrWhiteSpace(Path);
}

public sealed partial class ProjectDetailViewModel : DetailViewModel
{
    private readonly ViewModelServices _services;

    public ProjectDetailViewModel(ViewModelServices services, Project project)
    {
        _services = services;
        ProjectId = project.Id;
        Name = project.Name;
        LocalPath = project.LocalPath;
        GitHubRepo = project.GitHubRepo;
        Activity = new ActivityViewModel(services.Log, ActivityLog.ProjectKey(project.Id));
        Readme = LoadReadme(project.LocalPath);
        Apply(project);
    }

    public int ProjectId { get; }

    public string Name { get; }

    public string LocalPath { get; }

    public string GitHubRepo { get; }

    public string Readme { get; }

    public ActivityViewModel Activity { get; }

    [ObservableProperty]
    public partial string StateText { get; set; }

    [ObservableProperty]
    public partial bool IsInitializing { get; set; }

    [ObservableProperty]
    public partial string? LastError { get; set; }

    public override void Update(AppSnapshot snapshot)
    {
        if (snapshot.Projects.FirstOrDefault(p => p.Id == ProjectId) is { } project)
        {
            Apply(project);
        }
    }

    public override void Dispose() => Activity.Dispose();

    [RelayCommand]
    private Task RetryAsync() => RunActionAsync(() => _services.Onboarding.RetryAsync(ProjectId));

    [RelayCommand]
    private void OpenFolder() => OpenFolder(LocalPath);

    [RelayCommand]
    private void OpenOnGitHub() => OpenUrl($"https://github.com/{GitHubRepo}");

    private void Apply(Project project)
    {
        IsInitializing = project.OnboardingState == OnboardingState.Initializing;
        LastError = project.OnboardingState == OnboardingState.Failed ? project.LastError ?? "Onboarding failed." : null;
        StateText = project.OnboardingState switch
        {
            OnboardingState.Initializing => "Setting up the project (/init, labels)…",
            OnboardingState.Failed => "Setup failed",
            _ => $"{project.GitHubRepo} · {(project.IsPublic ? "public" : "private")} · default branch {project.DefaultBranch}",
        };
    }

    private static string LoadReadme(string folder)
    {
        try
        {
            var file = Directory.EnumerateFiles(folder, "README*")
                .OrderBy(f => System.IO.Path.GetExtension(f).Equals(".md", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            return file is null ? "*This project has no README.*" : File.ReadAllText(file);
        }
        catch (IOException e)
        {
            return $"*The README could not be read: {e.Message}*";
        }
    }
}

public sealed partial class SettingsViewModel(ViewModelServices services) : DetailViewModel
{
    public bool AutostartPlanning
    {
        get => services.Settings.Current.AutostartPlanning;
        set
        {
            services.Settings.Update(s => s with { AutostartPlanning = value });
            OnPropertyChanged();
        }
    }

    public bool AutostartExecution
    {
        get => services.Settings.Current.AutostartExecution;
        set
        {
            services.Settings.Update(s => s with { AutostartExecution = value });
            OnPropertyChanged();
        }
    }
}
