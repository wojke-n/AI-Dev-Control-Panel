using System.Collections.ObjectModel;
using AiDevControlPanel.Core.Workflow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiDevControlPanel.App.ViewModels;

/// <summary>Questions of the refiner or planner, shown at the top of the main panel.</summary>
public sealed partial class QuestionViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<QuestionAnswer>, Task> _submit;

    public QuestionViewModel(IReadOnlyList<Question> questions, Func<IReadOnlyList<QuestionAnswer>, Task> submit)
    {
        _submit = submit;
        Items = new ObservableCollection<QuestionItemViewModel>(questions.Select(q => new QuestionItemViewModel(q, OnAnswerChanged)));
    }

    public ObservableCollection<QuestionItemViewModel> Items { get; }

    [ObservableProperty]
    public partial bool IsSubmitting { get; set; }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        IsSubmitting = true;
        try
        {
            await _submit(Items.Select(i => new QuestionAnswer(i.Text, i.BuildAnswer())).ToList());
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private bool CanSubmit() => !IsSubmitting && Items.All(i => i.IsAnswered);

    private void OnAnswerChanged() => SubmitCommand.NotifyCanExecuteChanged();

    partial void OnIsSubmittingChanged(bool value) => SubmitCommand.NotifyCanExecuteChanged();
}

public sealed partial class QuestionItemViewModel : ObservableObject
{
    private readonly Action _changed;

    public QuestionItemViewModel(Question question, Action changed)
    {
        _changed = changed;
        Text = question.Text;
        Header = question.Header;
        MultiSelect = question.MultiSelect;
        GroupName = Guid.NewGuid().ToString("N");
        Options = new ObservableCollection<OptionViewModel>((question.Options ?? []).Select(o => new OptionViewModel(o, changed)));
    }

    public string Text { get; }

    public string? Header { get; }

    public bool MultiSelect { get; }

    public bool SingleSelect => !MultiSelect && HasOptions;

    public bool HasOptions => Options.Count > 0;

    /// <summary>Groups the radio buttons of this question.</summary>
    public string GroupName { get; }

    public ObservableCollection<OptionViewModel> Options { get; }

    public string OtherLabel => HasOptions ? "Other answer (optional):" : "Answer:";

    [ObservableProperty]
    public partial string OtherText { get; set; } = "";

    public bool IsAnswered => Options.Any(o => o.IsSelected) || !string.IsNullOrWhiteSpace(OtherText);

    public string BuildAnswer()
    {
        var parts = Options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
        if (!string.IsNullOrWhiteSpace(OtherText))
        {
            parts.Add(HasOptions ? $"Other: {OtherText.Trim()}" : OtherText.Trim());
        }

        return string.Join("; ", parts);
    }

    partial void OnOtherTextChanged(string value) => _changed();
}

public sealed partial class OptionViewModel(QuestionOption option, Action changed) : ObservableObject
{
    public string Label { get; } = option.Label;

    public string? Description { get; } = option.Description;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => changed();
}
