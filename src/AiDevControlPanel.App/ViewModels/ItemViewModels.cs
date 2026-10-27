using AiDevControlPanel.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiDevControlPanel.App.ViewModels;

/// <summary>Common state of the entries in the Project, TSA and Issue panels.</summary>
public abstract partial class ListItemViewModel(int id, int projectId) : ObservableObject
{
    public int Id { get; } = id;

    public int ProjectId { get; } = projectId;

    [ObservableProperty]
    public partial StateColor Color { get; set; }

    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    /// <summary>Drives the live filter of the TSA and Issue panels.</summary>
    [ObservableProperty]
    public partial bool IsProjectChecked { get; set; }
}

public sealed partial class ProjectItemViewModel(int id, Action<ProjectItemViewModel> checkedChanged) : ListItemViewModel(id, id)
{
    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string? ToolTip { get; set; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => checkedChanged(this);
}

public sealed partial class TsaItemViewModel(int id, int projectId) : ListItemViewModel(id, projectId)
{
    [ObservableProperty]
    public partial string DisplayTitle { get; set; }

    /// <summary>The refiner has not created a title yet; the panel shows the start of the text instead.</summary>
    [ObservableProperty]
    public partial bool IsTitlePending { get; set; }
}

public sealed partial class IssueItemViewModel(int id, int projectId) : ListItemViewModel(id, projectId)
{
    public int? TsaId { get; set; }

    [ObservableProperty]
    public partial int Number { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial int Priority { get; set; }

    [ObservableProperty]
    public partial bool IsInterrupted { get; set; }

    [ObservableProperty]
    public partial string? ToolTip { get; set; }
}
