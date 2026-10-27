using System.Collections.ObjectModel;
using AiDevControlPanel.Core.Domain;
using AiDevControlPanel.Core.GitHub;
using AiDevControlPanel.Core.Persistence;
using AiDevControlPanel.Core.Workflow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiDevControlPanel.App.ViewModels;

public sealed record CommentViewModel(string Author, string Date, string Body);

public sealed partial class IssueDetailViewModel : DetailViewModel
{
    private readonly ViewModelServices _services;
    private string? _questionsJson;
    private IssueState? _lastState;
    private Issue _issue;
    private Project _project;

    public IssueDetailViewModel(ViewModelServices services, Issue issue, Project project, AppSnapshot snapshot)
    {
        _services = services;
        _issue = issue;
        _project = project;
        IssueId = issue.Id;
        Activity = new ActivityViewModel(services.Log, ActivityLog.IssueKey(issue.Id));
        Update(snapshot);
    }

    public int IssueId { get; }

    public ActivityViewModel Activity { get; }

    public ObservableCollection<CommentViewModel> Comments { get; } = [];

    public IReadOnlyList<PriorityMode> PriorityModes { get; } = [PriorityMode.OnHold, PriorityMode.Regular, PriorityMode.Prioritized];

    [ObservableProperty]
    public partial string Header { get; set; }

    [ObservableProperty]
    public partial string Meta { get; set; }

    [ObservableProperty]
    public partial string StateText { get; set; }

    [ObservableProperty]
    public partial string Body { get; set; }

    [ObservableProperty]
    public partial string? Plan { get; set; }

    [ObservableProperty]
    public partial string? Report { get; set; }

    [ObservableProperty]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    public partial string? LastError { get; set; }

    [ObservableProperty]
    public partial string? DiffStat { get; set; }

    [ObservableProperty]
    public partial string? CommentsStatus { get; set; }

    [ObservableProperty]
    public partial QuestionViewModel? Question { get; set; }

    [ObservableProperty]
    public partial PriorityMode SelectedPriorityMode { get; set; }

    [ObservableProperty]
    public partial bool CanStartPlanning { get; set; }

    [ObservableProperty]
    public partial bool CanStartExecution { get; set; }

    [ObservableProperty]
    public partial bool IsReview { get; set; }

    [ObservableProperty]
    public partial bool IsRejected { get; set; }

    [ObservableProperty]
    public partial bool IsError { get; set; }

    [ObservableProperty]
    public partial bool HasWorktree { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AdjustCommand))]
    public partial string? AdjustText { get; set; }

    [ObservableProperty]
    public partial string? EditableBody { get; set; }

    [ObservableProperty]
    public partial string? EditablePlan { get; set; }

    public override void Update(AppSnapshot snapshot)
    {
        if (snapshot.Issues.FirstOrDefault(i => i.Id == IssueId) is not { } issue ||
            snapshot.Projects.FirstOrDefault(p => p.Id == issue.ProjectId) is not { } project)
        {
            return;
        }

        _issue = issue;
        _project = project;
        Header = $"#{issue.Number} {issue.Title}";
        var parent = issue.ParentIssueId is { } parentId ? snapshot.Issues.FirstOrDefault(i => i.Id == parentId) : null;
        Meta = $"{TypeText(issue.Type)} · {project.Name} · priority {issue.Priority}" + (parent is null ? "" : $" · sub-issue of #{parent.Number}");
        StateText = IssueStateText.Of(issue);
        Body = issue.Body;
        Plan = issue.CurrentPlan;
        Report = issue.LastReport;
        Notice = issue.Notice;
        LastError = issue.State == IssueState.Error ? issue.LastError : null;
        HasWorktree = issue.WorktreePath is not null;

        var mode = issue.OnHold ? PriorityMode.OnHold : issue.IsPrioritized ? PriorityMode.Prioritized : PriorityMode.Regular;
        if (SelectedPriorityMode != mode)
        {
            SelectedPriorityMode = mode;
        }

        CanStartPlanning = issue.State == IssueState.Waiting && !issue.IsContainer;
        CanStartExecution = issue.State == IssueState.PlanReady;
        IsReview = issue.State == IssueState.Review;
        IsRejected = issue.State == IssueState.Rejected;
        IsError = issue.State == IssueState.Error;

        var questionsJson = issue.State == IssueState.AwaitingAnswer ? issue.PendingQuestions : null;
        if (questionsJson != _questionsJson)
        {
            _questionsJson = questionsJson;
            Question = questionsJson is null
                ? null
                : new QuestionViewModel(Questions.Deserialize(questionsJson), answers => _services.Issues.SubmitAnswerAsync(IssueId, answers));
        }

        if (_lastState != issue.State)
        {
            _lastState = issue.State;
            EditableBody = issue.Body;
            EditablePlan = issue.CurrentPlan;
            _ = LoadCommentsAsync();
            _ = LoadDiffStatAsync();
        }
    }

    public override void Dispose() => Activity.Dispose();

    [RelayCommand]
    private Task StartPlanningAsync() => RunActionAsync(() => _services.Issues.StartPlanningAsync(IssueId));

    [RelayCommand]
    private Task StartExecutionAsync() => RunActionAsync(() => _services.Issues.StartExecutionAsync(IssueId));

    [RelayCommand]
    private Task AcceptAsync() => RunActionAsync(() => _services.Issues.AcceptAsync(IssueId));

    [RelayCommand(CanExecute = nameof(CanAdjust))]
    private Task AdjustAsync() => RunActionAsync(async () =>
    {
        await _services.Issues.RequestAdjustmentAsync(IssueId, AdjustText!);
        AdjustText = null;
    });

    private bool CanAdjust() => !string.IsNullOrWhiteSpace(AdjustText);

    [RelayCommand]
    private Task RestartAsync()
    {
        if (!Confirm("Discard all progress of this issue (worktree, branch and session)?"))
        {
            return Task.CompletedTask;
        }

        return RunActionAsync(() => _services.Issues.RejectAsync(IssueId));
    }

    [RelayCommand]
    private Task RestartWithPlanningAsync() => RunActionAsync(() => _services.Issues.RestartWithPlanningAsync(IssueId, EditableBody));

    [RelayCommand]
    private Task RestartWithPlanAsync() => RunActionAsync(() => _services.Issues.RestartWithPlanAsync(IssueId, EditablePlan ?? "", EditableBody));

    [RelayCommand]
    private Task RetryAsync() => RunActionAsync(() => _services.Issues.RetryAsync(IssueId));

    [RelayCommand]
    private void OpenWorktree() => OpenFolder(_issue.WorktreePath);

    [RelayCommand]
    private void OpenOnGitHub() => OpenUrl($"https://github.com/{_project.GitHubRepo}/issues/{_issue.Number}");

    [RelayCommand]
    private Task ReloadCommentsAsync() => LoadCommentsAsync();

    partial void OnSelectedPriorityModeChanged(PriorityMode value)
    {
        var current = _issue.OnHold ? PriorityMode.OnHold : _issue.IsPrioritized ? PriorityMode.Prioritized : PriorityMode.Regular;
        if (value != current)
        {
            _ = RunActionAsync(() => _services.Issues.SetPriorityModeAsync(IssueId, value));
        }
    }

    private async Task LoadCommentsAsync()
    {
        CommentsStatus = "Loading comments…";
        try
        {
            var details = await _services.GitHub.GetIssueAsync(_project.GitHubRepo, _issue.Number);
            Comments.Clear();
            foreach (var comment in details.Comments)
            {
                Comments.Add(new CommentViewModel(comment.Author, comment.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), comment.Body));
            }

            CommentsStatus = Comments.Count == 0 ? "No comments yet." : null;
        }
        catch (GitHubException e)
        {
            CommentsStatus = $"The comments could not be loaded: {e.Message}";
        }
    }

    private async Task LoadDiffStatAsync()
    {
        DiffStat = _issue.State == IssueState.Review ? await _services.Issues.GetDiffStatAsync(_issue, _project) : null;
    }

    private static string TypeText(IssueType type) => type switch
    {
        IssueType.Bug => "Bug report",
        IssueType.Security => "Security vulnerability report",
        _ => "Feature request",
    };
}
