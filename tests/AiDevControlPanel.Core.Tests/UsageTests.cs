using AiDevControlPanel.Core.Usage;
using Microsoft.Extensions.Time.Testing;

namespace AiDevControlPanel.Core.Tests;

public class TitleFormatterTests
{
    [Fact]
    public void Format_MatchesSpecifiedExample()
    {
        // 2026-11-02 is a Monday.
        var usage = new UsageSnapshot(
            new UsageWindow(7.0, new DateTimeOffset(2026, 10, 27, 14, 56, 0, TimeSpan.Zero)),
            new UsageWindow(23.0, new DateTimeOffset(2026, 11, 2, 7, 38, 0, TimeSpan.Zero)));

        Assert.Equal("AI-Dev-Control-Panel - Usage: 7% (14:56) | 23% (Mo, 07:38)", TitleFormatter.Format(usage, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Format_ConvertsToLocalTimeAndRounds()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(1), "test", "test");
        var usage = new UsageSnapshot(
            new UsageWindow(6.5, new DateTimeOffset(2026, 10, 27, 23, 30, 0, TimeSpan.Zero)),
            new UsageWindow(0.4, new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero)));

        Assert.Equal("AI-Dev-Control-Panel - Usage: 7% (00:30) | 0% (So, 00:30)", TitleFormatter.Format(usage, zone));
    }

    [Fact]
    public void Format_WithoutUsage()
    {
        Assert.Equal("AI-Dev-Control-Panel - Usage: n/a", TitleFormatter.Format(null, TimeZoneInfo.Utc));
    }
}

public class UsageServiceTests
{
    private static readonly UsageSnapshot Snapshot = new(
        new UsageWindow(10, DateTimeOffset.UnixEpoch),
        new UsageWindow(20, DateTimeOffset.UnixEpoch));

    [Fact]
    public async Task FirstRefreshRunsImmediately()
    {
        var time = new FakeTimeProvider();
        var provider = new FakeUsageProvider { Snapshot = Snapshot };
        var service = new UsageService(provider, time);

        await service.RequestRefresh();

        Assert.Equal(1, provider.Calls);
        Assert.Equal(Snapshot, service.Current);
    }

    [Fact]
    public async Task RefreshesWithin60SecondsAreDeferredAndCoalesced()
    {
        var time = new FakeTimeProvider();
        var provider = new FakeUsageProvider { Snapshot = Snapshot };
        var service = new UsageService(provider, time);
        await service.RequestRefresh();

        time.Advance(TimeSpan.FromSeconds(10));
        var deferred = service.RequestRefresh();
        var coalesced = service.RequestRefresh();
        Assert.True(coalesced.IsCompleted);
        Assert.Equal(1, provider.Calls);

        time.Advance(TimeSpan.FromSeconds(49));
        Assert.Equal(1, provider.Calls);

        time.Advance(TimeSpan.FromSeconds(1));
        await deferred;
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public void DelayUntilNextFetch()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        Assert.Equal(TimeSpan.Zero, UsageService.DelayUntilNextFetch(null, now));
        Assert.Equal(TimeSpan.FromSeconds(45), UsageService.DelayUntilNextFetch(now.AddSeconds(-15), now));
        Assert.Equal(TimeSpan.Zero, UsageService.DelayUntilNextFetch(now.AddSeconds(-61), now));
    }

    [Fact]
    public void OAuthResponseIsParsed()
    {
        const string json = """
            {"five_hour":{"utilization":7.0,"resets_at":"2026-10-27T14:10:00.156366+00:00"},
             "seven_day":{"utilization":2.0,"resets_at":"2026-10-30T17:00:00.156390+00:00"},
             "seven_day_opus":null}
            """;

        var snapshot = OAuthUsageProvider.ParseResponse(json);

        Assert.NotNull(snapshot);
        Assert.Equal(7.0, snapshot.Session.Percent);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 17, 0, 0, TimeSpan.Zero), snapshot.Weekly.ResetsAt, TimeSpan.FromSeconds(1));
    }
}
