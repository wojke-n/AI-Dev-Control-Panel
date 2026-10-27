using System.Text.Json;

namespace AiDevControlPanel.Core.Persistence;

public sealed record AppSettings
{
    public const string DefaultOption = "Default";

    public bool AutostartPlanning { get; init; } = true;

    public bool AutostartExecution { get; init; } = true;

    /// <summary>Model alias or id, or <see cref="DefaultOption"/> for the CLI default.</summary>
    public string Model { get; init; } = DefaultOption;

    /// <summary>Effort level, or <see cref="DefaultOption"/> for the CLI default.</summary>
    public string Effort { get; init; } = DefaultOption;

    /// <summary>The value to pass to the CLI, or null for "Default".</summary>
    public static string? CliValue(string option) =>
        string.IsNullOrWhiteSpace(option) || option.Equals(DefaultOption, StringComparison.OrdinalIgnoreCase) ? null : option.Trim();
}

public interface ISettingsStore
{
    AppSettings Current { get; }

    event EventHandler? Changed;

    void Update(Func<AppSettings, AppSettings> change);
}

public sealed class SettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly Lock _lock = new();

    public SettingsStore(AppPaths paths)
    {
        _path = paths.Settings;
        Current = Load(_path);
    }

    public AppSettings Current { get; private set; }

    public event EventHandler? Changed;

    public void Update(Func<AppSettings, AppSettings> change)
    {
        lock (_lock)
        {
            Current = change(Current);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Json.Options));
            File.Move(temp, _path, overwrite: true);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json.Options) ?? new() : new();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }
}
