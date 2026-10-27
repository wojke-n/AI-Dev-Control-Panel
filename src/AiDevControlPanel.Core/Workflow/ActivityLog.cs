using System.Collections.Concurrent;
using System.Globalization;
using AiDevControlPanel.Core.Claude;

namespace AiDevControlPanel.Core.Workflow;

public sealed record ActivityEntry(DateTimeOffset Time, string Text);

/// <summary>Progress of the AI per project, TSA and issue; kept in memory and appended to logs\&lt;key&gt;.log.</summary>
public sealed class ActivityLog(AppPaths paths, TimeProvider time)
{
    private const int MaxEntries = 500;
    private const int MaxTextLength = 2000;

    private readonly ConcurrentDictionary<string, List<ActivityEntry>> _entries = new();

    public event Action<string, ActivityEntry>? EntryAdded;

    public static string ProjectKey(int id) => $"project-{id}";

    public static string TsaKey(int id) => $"tsa-{id}";

    public static string IssueKey(int id) => $"issue-{id}";

    public void Add(string key, string text)
    {
        if (text.Length > MaxTextLength)
        {
            text = text[..MaxTextLength] + "…";
        }

        var entry = new ActivityEntry(time.GetLocalNow(), text);
        var list = _entries.GetOrAdd(key, _ => []);
        lock (list)
        {
            list.Add(entry);
            if (list.Count > MaxEntries)
            {
                list.RemoveRange(0, list.Count - MaxEntries);
            }

            try
            {
                Directory.CreateDirectory(paths.Logs);
                File.AppendAllText(
                    Path.Combine(paths.Logs, key + ".log"),
                    $"{entry.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {text.ReplaceLineEndings(" ")}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never break the workflow.
            }
        }

        EntryAdded?.Invoke(key, entry);
    }

    public void Add(string key, ClaudeEvent claudeEvent)
    {
        var text = claudeEvent switch
        {
            SessionStartedEvent s => $"Session {s.SessionId} started ({s.Model}, {s.PermissionMode})",
            AssistantTextEvent a => a.Text,
            ToolUseEvent t => string.IsNullOrEmpty(t.Summary) ? $"[{t.ToolName}]" : $"[{t.ToolName}] {t.Summary}",
            RateLimitEvent { Info.IsRejected: true } => "Usage limit reached",
            ResultEvent r => r.IsError ? $"Finished with error ({r.Subtype}): {r.ResultText}" : "Finished",
            _ => null,
        };
        if (text is not null)
        {
            Add(key, text);
        }
    }

    public IReadOnlyList<ActivityEntry> Get(string key)
    {
        if (!_entries.TryGetValue(key, out var list))
        {
            return [];
        }

        lock (list)
        {
            return [.. list];
        }
    }
}
