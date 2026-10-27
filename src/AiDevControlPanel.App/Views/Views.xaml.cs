using System.Collections.Specialized;
using System.Windows.Controls;

namespace AiDevControlPanel.App.Views;

public partial class QuestionView : UserControl
{
    public QuestionView() => InitializeComponent();
}

public partial class ActivityView : UserControl
{
    public ActivityView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ViewModels.ActivityViewModel old)
            {
                old.Entries.CollectionChanged -= ScrollToEnd;
            }

            if (e.NewValue is ViewModels.ActivityViewModel current)
            {
                current.Entries.CollectionChanged += ScrollToEnd;
                Scroller.ScrollToEnd();
            }
        };
    }

    private void ScrollToEnd(object? sender, NotifyCollectionChangedEventArgs e) => Scroller.ScrollToEnd();
}

public partial class AddProjectView : UserControl
{
    public AddProjectView() => InitializeComponent();
}

public partial class ProjectDetailView : UserControl
{
    public ProjectDetailView() => InitializeComponent();
}

public partial class AddTsaView : UserControl
{
    public AddTsaView() => InitializeComponent();
}

public partial class TsaDetailView : UserControl
{
    public TsaDetailView() => InitializeComponent();
}

public partial class IssueDetailView : UserControl
{
    public IssueDetailView() => InitializeComponent();
}

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
