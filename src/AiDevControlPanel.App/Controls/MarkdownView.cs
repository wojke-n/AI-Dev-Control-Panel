using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MdXaml;

namespace AiDevControlPanel.App.Controls;

/// <summary>Read-only Markdown that grows with its content, so several can share one outer ScrollViewer.</summary>
public sealed class MarkdownView : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty AssetPathRootProperty = DependencyProperty.Register(
        nameof(AssetPathRoot), typeof(string), typeof(MarkdownView), new PropertyMetadata(null, OnContentChanged));

    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        CommandBindings.Add(new CommandBinding(NavigationCommands.GoToPage, OnNavigate));
    }

    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public string? AssetPathRoot
    {
        get => (string?)GetValue(AssetPathRootProperty);
        set => SetValue(AssetPathRootProperty, value);
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MarkdownView)d;
        var engine = new Markdown
        {
            DocumentStyle = MarkdownStyle.GithubLike,
            HyperlinkCommand = NavigationCommands.GoToPage,
            DisabledLazyLoad = true,
        };
        if (!string.IsNullOrEmpty(view.AssetPathRoot))
        {
            engine.AssetPathRoot = view.AssetPathRoot;
        }

        var document = engine.Transform(view.Markdown ?? "");
        document.PagePadding = new Thickness(0);
        view.Document = document;
    }

    private static void OnNavigate(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }
}
