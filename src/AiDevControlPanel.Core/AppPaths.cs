namespace AiDevControlPanel.Core;

/// <summary>Locations of the app's own data (database, settings, logs, issue worktrees).</summary>
public sealed class AppPaths(string root)
{
    /// <summary>%LOCALAPPDATA%\AI-Dev-Control-Panel, or the folder in ADCP_DATA_DIR (for development).</summary>
    public static AppPaths Default { get; } = new(
        Environment.GetEnvironmentVariable("ADCP_DATA_DIR") is { Length: > 0 } dataDir
            ? Path.GetFullPath(dataDir)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Dev-Control-Panel"));

    public string Root { get; } = root;

    public string Database => Path.Combine(Root, "app.db");

    public string Settings => Path.Combine(Root, "settings.json");

    public string Logs => Path.Combine(Root, "logs");

    public string Worktrees => Path.Combine(Root, "worktrees");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Worktrees);
    }
}
