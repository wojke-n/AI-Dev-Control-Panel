namespace AiDevControlPanel.Core.Usage;

/// <summary>Utilization of one limit window in percent (0–100) and when it resets.</summary>
public sealed record UsageWindow(double Percent, DateTimeOffset ResetsAt);

/// <summary>Current session (5 hour) and weekly utilization of the Claude subscription.</summary>
public sealed record UsageSnapshot(UsageWindow Session, UsageWindow Weekly);
