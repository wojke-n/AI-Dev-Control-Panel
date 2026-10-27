using AiDevControlPanel.Core.Claude;
using AiDevControlPanel.Core.Persistence;
using AiDevControlPanel.Core.Usage;

namespace AiDevControlPanel.Core.Workflow;

/// <summary>Runs Claude for a TSA or issue: applies model/effort, logs progress and persists the session id early.</summary>
public sealed class ClaudeSessionRunner(
    IClaudeRunner claude,
    ISettingsStore settings,
    ActivityLog log,
    UsageService usage,
    TimeProvider time)
{
    public static readonly IReadOnlyList<string> NoInteractiveTools = ["AskUserQuestion", "ExitPlanMode"];

    public async Task<ClaudeRunResult> RunAsync(
        string logKey,
        ClaudeRunRequest request,
        string? knownSessionId,
        Func<string, Task> persistSessionId,
        CancellationToken ct)
    {
        var current = settings.Current;
        request = request with
        {
            Model = AppSettings.CliValue(current.Model),
            Effort = AppSettings.CliValue(current.Effort),
        };
        log.Add(logKey, $"▶ {request.Name} ({request.PermissionMode} mode)");

        Task? persist = null;
        var result = await claude.RunAsync(request, e =>
        {
            log.Add(logKey, e);
            switch (e)
            {
                case SessionStartedEvent started when started.SessionId.Length > 0 && started.SessionId != knownSessionId:
                    knownSessionId = started.SessionId;
                    persist = persistSessionId(started.SessionId);
                    break;
                case RateLimitEvent rateLimit:
                    usage.ReportRateLimit(rateLimit.Info);
                    break;
            }
        }, ct);

        if (persist is not null)
        {
            await persist;
        }

        if (!result.Success)
        {
            log.Add(logKey, result.UsageLimitReached ? "⏸ Usage limit reached" : $"✖ {result.ErrorMessage}");
        }

        return result;
    }

    /// <summary>When the interrupted work can continue: the reported reset time, else the usage window that is full.</summary>
    public DateTimeOffset ResumeTime(ClaudeRunResult result)
    {
        if (result.LimitResetsAt is { } reset)
        {
            return reset;
        }

        var snapshot = usage.Current;
        if (snapshot is not null)
        {
            return snapshot.Weekly.Percent >= 100 ? snapshot.Weekly.ResetsAt : snapshot.Session.ResetsAt;
        }

        return time.GetUtcNow().AddMinutes(30);
    }

    /// <summary>The session to resume does not exist (e.g. the CLI never created it).</summary>
    public static bool IsMissingSession(ClaudeRunResult result) =>
        result.ErrorMessage?.Contains("No conversation found", StringComparison.OrdinalIgnoreCase) == true;
}
