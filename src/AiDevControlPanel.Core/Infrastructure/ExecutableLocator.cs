namespace AiDevControlPanel.Core.Infrastructure;

/// <summary>
/// Resolves executables against the current machine and user PATH, so tools installed
/// after the app (or its parent shell) started are still found.
/// </summary>
public sealed class ExecutableLocator
{
    private static readonly string[] Extensions = [".exe", ".cmd", ".bat", ""];

    public ExecutableLocator()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var parts = new[]
            {
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Path.Combine(home, ".local", "bin"),
            }
            .Where(p => !string.IsNullOrEmpty(p))
            .SelectMany(p => p!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        SearchPath = string.Join(Path.PathSeparator, parts);
    }

    /// <summary>Merged PATH that is also passed to child processes.</summary>
    public string SearchPath { get; }

    public string? TryResolve(string name)
    {
        if (Path.IsPathRooted(name))
        {
            return File.Exists(name) ? name : null;
        }

        foreach (var directory in SearchPath.Split(Path.PathSeparator))
        {
            foreach (var extension in Extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public string Resolve(string name) => TryResolve(name) ?? name;
}
