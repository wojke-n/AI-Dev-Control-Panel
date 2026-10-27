using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AiDevControlPanel.Core.Persistence;
using AiDevControlPanel.Core.Workflow;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiDevControlPanel.App.ViewModels;

/// <summary>A view in the main panel that follows data changes.</summary>
public abstract partial class DetailViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    public partial string? ActionError { get; set; }

    public virtual void Update(AppSnapshot snapshot)
    {
    }

    public virtual void Dispose()
    {
    }

    /// <summary>Runs a user action and shows its error in the view instead of crashing.</summary>
    protected async Task RunActionAsync(Func<Task> action)
    {
        ActionError = null;
        try
        {
            await action();
        }
        catch (Exception e)
        {
            ActionError = e.Message;
        }
    }

    protected static void OpenFolder(string? path)
    {
        if (path is not null && Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
    }

    protected static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    protected static bool Confirm(string message) =>
        MessageBox.Show(message, "AI-Dev-Control-Panel", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}

/// <summary>Live activity of the AI for one project, TSA or issue.</summary>
public sealed class ActivityViewModel : IDisposable
{
    private readonly ActivityLog _log;
    private readonly string _key;

    public ActivityViewModel(ActivityLog log, string key)
    {
        _log = log;
        _key = key;
        Entries = new ObservableCollection<ActivityEntry>(log.Get(key));
        log.EntryAdded += OnEntryAdded;
    }

    public ObservableCollection<ActivityEntry> Entries { get; }

    public void Dispose() => _log.EntryAdded -= OnEntryAdded;

    private void OnEntryAdded(string key, ActivityEntry entry)
    {
        if (key == _key)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => Entries.Add(entry));
        }
    }
}
