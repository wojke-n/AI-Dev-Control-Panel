using System.Collections.ObjectModel;
using AiDevControlPanel.Core.Domain;
using AiDevControlPanel.Core.Persistence;
using AiDevControlPanel.Core.Workflow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiDevControlPanel.App.ViewModels;

public sealed record ProjectChoice(int Id, string Name);

public sealed partial class AddTsaViewModel : DetailViewModel
{
    private readonly ViewModelServices _services;
    private readonly Action<int> _added;

    public AddTsaViewModel(ViewModelServices services, IReadOnlyList<ProjectChoice> projects, int? preselectedProjectId, Action<int> added)
    {
        _services = services;
        _added = added;
        Projects = projects;
        SelectedProject = projects.FirstOrDefault(p => p.Id == preselectedProjectId) ?? projects.FirstOrDefault();
    }

    public IReadOnlyList<ProjectChoice> Projects { get; }

    public bool HasProjects => Projects.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial ProjectChoice? SelectedProject { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string? Text { get; set; }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task AddAsync() => RunActionAsync(async () =>
    {
        var tsa = await _services.Refiner.AddTsaAsync(SelectedProject!.Id, Text!);
        _added(tsa.Id);
    });

    private bool CanAdd() => SelectedProject is not null && !string.IsNullOrWhiteSpace(Text);
}

public sealed record LinkedIssue(int Number, string Title, string State);

public sealed partial class TsaDetailViewModel : DetailViewModel
{
    private readonly ViewModelServices _services;
    private string? _questionsJson;

    public TsaDetailViewModel(ViewModelServices services, int tsaId, AppSnapshot snapshot)
    {
        _services = services;
        TsaId = tsaId;
        Activity = new ActivityViewModel(services.Log, ActivityLog.TsaKey(tsaId));
        Update(snapshot);
    }

    public int TsaId { get; }

    public ActivityViewModel Activity { get; }

    public ObservableCollection<LinkedIssue> Issues { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial string ProjectName { get; set; }

    [ObservableProperty]
    public partial string StateText { get; set; }

    [ObservableProperty]
    public partial string? LastError { get; set; }

    [ObservableProperty]
    public partial QuestionViewModel? Question { get; set; }

    public override void Update(AppSnapshot snapshot)
    {
        if (snapshot.Tsas.FirstOrDefault(t => t.Id == TsaId) is not { } tsa)
        {
            return;
        }

        Title = tsa.Title ?? "(title pending)";
        Text = tsa.Text;
        ProjectName = snapshot.Projects.FirstOrDefault(p => p.Id == tsa.ProjectId)?.Name ?? "";
        LastError = tsa.State == TsaState.Error ? tsa.LastError : null;
        StateText = tsa.State switch
        {
            TsaState.Waiting when tsa.ResumeAt is { } resume => $"Paused until {resume.ToLocalTime():HH:mm} (usage limit)",
            TsaState.Waiting => "Waiting for the refiner",
            TsaState.Refining => "The refiner is working on this TSA",
            TsaState.AwaitingAnswer => "The refiner needs an answer",
            TsaState.Refined => "Issues created",
            _ => "Refining failed",
        };

        var questionsJson = tsa.State == TsaState.AwaitingAnswer ? tsa.PendingQuestions : null;
        if (questionsJson != _questionsJson)
        {
            _questionsJson = questionsJson;
            Question = questionsJson is null
                ? null
                : new QuestionViewModel(Questions.Deserialize(questionsJson), answers => _services.Refiner.SubmitAnswerAsync(TsaId, answers));
        }

        Issues.Clear();
        foreach (var issue in snapshot.IssuesOfTsa(TsaId).OrderBy(i => i.Number))
        {
            Issues.Add(new LinkedIssue(issue.Number, issue.Title, IssueStateText.Of(issue)));
        }
    }

    public override void Dispose() => Activity.Dispose();

    [RelayCommand]
    private Task RetryAsync() => RunActionAsync(() => _services.Refiner.RetryAsync(TsaId));
}
