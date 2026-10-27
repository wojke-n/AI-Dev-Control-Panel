using AiDevControlPanel.Core.Claude;

namespace AiDevControlPanel.Core.Usage;

/// <summary>
/// Keeps the usage shown in the title bar. Refreshes are requested at startup and after each
/// planning/execution phase, but run at most once per <see cref="MinInterval"/>; requests in between are coalesced.
/// </summary>
public sealed class UsageService(IUsageProvider provider, TimeProvider time)
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);

    private readonly Lock _lock = new();
    private DateTimeOffset? _lastFetch;
    private bool _pending;
    private UsageSnapshot? _streamFallback;

    public UsageSnapshot? Current { get; private set; }

    public event EventHandler? Changed;

    public static TimeSpan DelayUntilNextFetch(DateTimeOffset? lastFetch, DateTimeOffset now)
    {
        if (lastFetch is null)
        {
            return TimeSpan.Zero;
        }

        var due = lastFetch.Value + MinInterval;
        return due > now ? due - now : TimeSpan.Zero;
    }

    /// <summary>Schedules a refresh; returns the task of the (possibly already pending) refresh for tests.</summary>
    public Task RequestRefresh()
    {
        TimeSpan delay;
        lock (_lock)
        {
            if (_pending)
            {
                return Task.CompletedTask;
            }

            _pending = true;
            delay = DelayUntilNextFetch(_lastFetch, time.GetUtcNow());
        }

        return RefreshAfterAsync(delay);
    }

    /// <summary>Remembers the latest utilization reported by a claude run, used when the endpoint fails.</summary>
    public void ReportRateLimit(RateLimitInfo info)
    {
        if (info.ToSnapshot() is { } snapshot)
        {
            _streamFallback = snapshot;
        }
    }

    private async Task RefreshAfterAsync(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, time);
        }

        lock (_lock)
        {
            _pending = false;
            _lastFetch = time.GetUtcNow();
        }

        var snapshot = await provider.GetUsageAsync() ?? _streamFallback;
        if (snapshot is not null)
        {
            Current = snapshot;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
