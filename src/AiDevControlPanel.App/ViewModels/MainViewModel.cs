using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using AiDevControlPanel.Core.Domain;
using AiDevControlPanel.Core.Persistence;
using AiDevControlPanel.Core.Usage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiDevControlPanel.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int TsaTitlePreviewLength = 60;

    private readonly ViewModelServices _services;
    private readonly UsageService _usage;
    private AppSnapshot _snapshot = AppSnapshot.Empty;
    private bool _syncing;
    private bool _syncQueued;
    private bool _selecting;

    public MainViewModel(ViewModelServices services, UsageService usage)
    {
        _services = services;
        _usage = usage;
        Title = TitleFormatter.Format(null, TimeZoneInfo.Local);
        Model = services.Settings.Current.Model;
        Effort = services.Settings.Current.Effort;

        TsaView = CreateFilteredView(Tsas);
        IssueView = CreateFilteredView(Issues);
        var issueView = (ListCollectionView)IssueView;
        issueView.SortDescriptions.Add(new SortDescription(nameof(IssueItemViewModel.Priority), ListSortDirection.Descending));
        issueView.SortDescriptions.Add(new SortDescription(nameof(IssueItemViewModel.Number), ListSortDirection.Ascending));
        issueView.IsLiveSorting = true;
        issueView.LiveSortingProperties.Add(nameof(IssueItemViewModel.Priority));
    }

    public ObservableCollection<ProjectItemViewModel> Projects { get; } = [];

    public ObservableCollection<TsaItemViewModel> Tsas { get; } = [];

    public ObservableCollection<IssueItemViewModel> Issues { get; } = [];

    /// <summary>TSAs of the checked projects.</summary>
    public ICollectionView TsaView { get; }

    /// <summary>Issues of the checked projects, highest priority first.</summary>
    public ICollectionView IssueView { get; }

    public IReadOnlyList<string> ModelOptions { get; } = [AppSettings.DefaultOption, "fable", "opus", "sonnet", "haiku"];

    public IReadOnlyList<string> EffortOptions { get; } = [AppSettings.DefaultOption, "low", "medium", "high", "xhigh", "max"];

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string? PrerequisiteWarning { get; set; }

    [ObservableProperty]
    public partial string Model { get; set; }

    [ObservableProperty]
    public partial string Effort { get; set; }

    [ObservableProperty]
    public partial DetailViewModel? CurrentView { get; set; }

    [ObservableProperty]
    public partial ProjectItemViewModel? SelectedProject { get; set; }

    [ObservableProperty]
    public partial TsaItemViewModel? SelectedTsa { get; set; }

    [ObservableProperty]
    public partial IssueItemViewModel? SelectedIssue { get; set; }

    public bool HasRunningJobs => _services.Scheduler.RunningJobs.Count > 0;

    public async Task InitializeAsync()
    {
        _services.Repo.Changed += (_, _) => QueueSync();
        _services.Scheduler.RunningJobsChanged += (_, _) => QueueSync();
        _usage.Changed += (_, _) => Dispatch(() => Title = TitleFormatter.Format(_usage.Current, TimeZoneInfo.Local));
        await SyncAsync();
        await CheckPrerequisitesAsync();
    }

    public Task StopJobsAsync() => _services.Scheduler.StopAsync();

    // ---- Action bar and panel headers ----

    [RelayCommand]
    private void OpenSettings() => ShowStandalone(new SettingsViewModel(_services));

    [RelayCommand]
    private void AddProject() => ShowStandalone(new AddProjectViewModel(_services, path => _ = SelectAfterSyncAsync(() =>
        Projects.FirstOrDefault(p => string.Equals(_snapshot.Projects.FirstOrDefault(s => s.Id == p.Id)?.LocalPath, path, StringComparison.OrdinalIgnoreCase)),
        p => SelectedProject = p)));

    [RelayCommand]
    private void AddTsa()
    {
        var choices = _snapshot.Projects
            .Where(p => p.OnboardingState == OnboardingState.Ready)
            .Select(p => new ProjectChoice(p.Id, p.Name))
            .ToList();
        var preselected = SelectedProject?.Id ?? SelectedTsa?.ProjectId ?? SelectedIssue?.ProjectId;
        ShowStandalone(new AddTsaViewModel(_services, choices, preselected, tsaId => _ = SelectAfterSyncAsync(
            () => Tsas.FirstOrDefault(t => t.Id == tsaId),
            t => SelectedTsa = t)));
    }

    [RelayCommand]
    private void CheckAll() => SetAllChecked(true);

    [RelayCommand]
    private void UncheckAll() => SetAllChecked(false);

    partial void OnModelChanged(string value)
    {
        var model = string.IsNullOrWhiteSpace(value) ? AppSettings.DefaultOption : value.Trim();
        if (model != _services.Settings.Current.Model)
        {
            _services.Settings.Update(s => s with { Model = model });
        }
    }

    partial void OnEffortChanged(string value)
    {
        if (!string.IsNullOrEmpty(value) && value != _services.Settings.Current.Effort)
        {
            _services.Settings.Update(s => s with { Effort = value });
        }
    }

    // ---- Selection: one item at a time; its associated items are highlighted ----

    partial void OnSelectedProjectChanged(ProjectItemViewModel? value)
    {
        if (value is null || _selecting)
        {
            return;
        }

        Select(() =>
        {
            SelectedTsa = null;
            SelectedIssue = null;
        });
        var project = _snapshot.Projects.FirstOrDefault(p => p.Id == value.Id);
        ShowDetail(project is null ? null : new ProjectDetailViewModel(_services, project));
        UpdateHighlights();
    }

    partial void OnSelectedTsaChanged(TsaItemViewModel? value)
    {
        if (value is null || _selecting)
        {
            return;
        }

        Select(() =>
        {
            SelectedProject = null;
            SelectedIssue = null;
        });
        ShowDetail(new TsaDetailViewModel(_services, value.Id, _snapshot));
        UpdateHighlights();
    }

    partial void OnSelectedIssueChanged(IssueItemViewModel? value)
    {
        if (value is null || _selecting)
        {
            return;
        }

        Select(() =>
        {
            SelectedProject = null;
            SelectedTsa = null;
        });
        var issue = _snapshot.Issues.FirstOrDefault(i => i.Id == value.Id);
        var project = issue is null ? null : _snapshot.Projects.FirstOrDefault(p => p.Id == issue.ProjectId);
        ShowDetail(issue is null || project is null ? null : new IssueDetailViewModel(_services, issue, project, _snapshot));
        UpdateHighlights();
    }

    private void Select(Action clearOthers)
    {
        _selecting = true;
        try
        {
            clearOthers();
        }
        finally
        {
            _selecting = false;
        }
    }

    private void ShowStandalone(DetailViewModel view)
    {
        Select(() =>
        {
            SelectedProject = null;
            SelectedTsa = null;
            SelectedIssue = null;
        });
        ShowDetail(view);
        UpdateHighlights();
    }

    private void ShowDetail(DetailViewModel? view)
    {
        CurrentView?.Dispose();
        CurrentView = view;
    }

    private void UpdateHighlights()
    {
        HashSet<int> projects = [], tsas = [], issues = [];
        if (SelectedProject is { } project)
        {
            tsas.UnionWith(Tsas.Where(t => t.ProjectId == project.Id).Select(t => t.Id));
            issues.UnionWith(Issues.Where(i => i.ProjectId == project.Id).Select(i => i.Id));
        }
        else if (SelectedTsa is { } tsa)
        {
            projects.Add(tsa.ProjectId);
            issues.UnionWith(Issues.Where(i => i.TsaId == tsa.Id).Select(i => i.Id));
        }
        else if (SelectedIssue is { } issue)
        {
            projects.Add(issue.ProjectId);
            if (issue.TsaId is { } tsaId)
            {
                tsas.Add(tsaId);
            }
        }

        foreach (var item in Projects)
        {
            item.IsHighlighted = projects.Contains(item.Id);
        }

        foreach (var item in Tsas)
        {
            item.IsHighlighted = tsas.Contains(item.Id);
        }

        foreach (var item in Issues)
        {
            item.IsHighlighted = issues.Contains(item.Id);
        }
    }

    // ---- Synchronization with the database ----

    private void QueueSync() => Dispatch(() =>
    {
        if (_syncQueued)
        {
            return;
        }

        _syncQueued = true;
        _ = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            // Coalesce bursts of changes into one reload.
            await Task.Delay(150);
            _syncQueued = false;
            await SyncAsync();
        });
    });

    private async Task SyncAsync()
    {
        _snapshot = await _services.Repo.LoadSnapshotAsync();
        var running = _services.Scheduler.RunningJobs.Select(j => j.ProjectId).ToHashSet();

        _syncing = true;
        try
        {
            Reconcile(Projects, _snapshot.Projects, p => p.Id, p => new ProjectItemViewModel(p.Id, OnProjectCheckedChanged), (vm, p) =>
            {
                vm.Name = p.Name;
                vm.IsChecked = p.IsChecked;
                vm.IsProjectChecked = p.IsChecked;
                vm.ToolTip = $"{p.LocalPath}\n{p.GitHubRepo}";
                vm.Color = StateAggregation.ProjectColor(p, _snapshot.TsasOf(p.Id), _snapshot.IssuesOf(p.Id), running.Contains(p.Id));
            });

            var checkedProjects = _snapshot.Projects.Where(p => p.IsChecked).Select(p => p.Id).ToHashSet();
            Reconcile(Tsas, _snapshot.Tsas, t => t.Id, t => new TsaItemViewModel(t.Id, t.ProjectId), (vm, t) =>
            {
                vm.IsTitlePending = string.IsNullOrWhiteSpace(t.Title);
                vm.DisplayTitle = vm.IsTitlePending ? Preview(t.Text) : t.Title!;
                vm.IsProjectChecked = checkedProjects.Contains(t.ProjectId);
                vm.Color = StateAggregation.TsaColor(t, _snapshot.IssuesOfTsa(t.Id));
            });

            Reconcile(Issues, _snapshot.Issues, i => i.Id, i => new IssueItemViewModel(i.Id, i.ProjectId), (vm, i) =>
            {
                vm.TsaId = i.TsaId;
                vm.Number = i.Number;
                vm.Title = i.Title;
                vm.Priority = i.Priority;
                vm.IsInterrupted = i.IsInterrupted;
                vm.ToolTip = IssueStateText.Of(i);
                vm.IsProjectChecked = checkedProjects.Contains(i.ProjectId);
                vm.Color = StateAggregation.IssueColor(i);
            });
        }
        finally
        {
            _syncing = false;
        }

        UpdateHighlights();
        CurrentView?.Update(_snapshot);
        OnPropertyChanged(nameof(HasRunningJobs));
    }

    private static void Reconcile<TVm, TEntity>(
        ObservableCollection<TVm> items,
        IReadOnlyList<TEntity> entities,
        Func<TEntity, int> id,
        Func<TEntity, TVm> create,
        Action<TVm, TEntity> update)
        where TVm : ListItemViewModel
    {
        var ids = entities.Select(id).ToHashSet();
        foreach (var stale in items.Where(i => !ids.Contains(i.Id)).ToList())
        {
            items.Remove(stale);
        }

        var existing = items.ToDictionary(i => i.Id);
        foreach (var entity in entities)
        {
            if (!existing.TryGetValue(id(entity), out var vm))
            {
                vm = create(entity);
                update(vm, entity);
                items.Add(vm);
            }
            else
            {
                update(vm, entity);
            }
        }
    }

    private void OnProjectCheckedChanged(ProjectItemViewModel project)
    {
        if (_syncing)
        {
            return;
        }

        project.IsProjectChecked = project.IsChecked;
        foreach (var item in Tsas.Where(t => t.ProjectId == project.Id))
        {
            item.IsProjectChecked = project.IsChecked;
        }

        foreach (var item in Issues.Where(i => i.ProjectId == project.Id))
        {
            item.IsProjectChecked = project.IsChecked;
        }

        _ = _services.Onboarding.SetCheckedAsync(project.Id, project.IsChecked);
    }

    private void SetAllChecked(bool isChecked)
    {
        foreach (var project in Projects)
        {
            project.IsChecked = isChecked;
        }
    }

    private async Task SelectAfterSyncAsync<T>(Func<T?> find, Action<T> select) where T : class
    {
        await SyncAsync();
        if (find() is { } item)
        {
            select(item);
        }
    }

    private async Task CheckPrerequisitesAsync()
    {
        var missing = new[] { "claude", "git", "gh" }.Where(tool => _services.Locator.TryResolve(tool) is null).ToList();
        if (missing.Count > 0)
        {
            PrerequisiteWarning = $"Missing tools: {string.Join(", ", missing)}. Install them and restart the app.";
            return;
        }

        if (!await _services.GitHub.IsAuthenticatedAsync())
        {
            PrerequisiteWarning = "The GitHub CLI is not logged in. Run 'gh auth login' in a terminal.";
        }
    }

    private static ICollectionView CreateFilteredView<T>(ObservableCollection<T> items) where T : ListItemViewModel
    {
        var view = new ListCollectionView(items)
        {
            Filter = item => ((ListItemViewModel)item).IsProjectChecked,
            IsLiveFiltering = true,
        };
        view.LiveFilteringProperties.Add(nameof(ListItemViewModel.IsProjectChecked));
        return view;
    }

    private static string Preview(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= TsaTitlePreviewLength ? line : line[..TsaTitlePreviewLength] + "…";
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
