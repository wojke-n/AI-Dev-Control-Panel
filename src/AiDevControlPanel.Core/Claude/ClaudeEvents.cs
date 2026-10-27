using System.Text.Json;
using AiDevControlPanel.Core.Usage;

namespace AiDevControlPanel.Core.Claude;

/// <summary>Event parsed from one line of <c>claude -p --output-format stream-json</c>.</summary>
public abstract record ClaudeEvent;

public sealed record SessionStartedEvent(string SessionId, string? Model, string? PermissionMode) : ClaudeEvent;

public sealed record AssistantTextEvent(string Text) : ClaudeEvent;

public sealed record ToolUseEvent(string ToolName, string Summary) : ClaudeEvent;

public sealed record RateLimitEvent(RateLimitInfo Info) : ClaudeEvent;

public sealed record ResultEvent(
    bool IsError,
    string Subtype,
    string? SessionId,
    string? ResultText,
    JsonElement? StructuredOutput,
    double? CostUsd) : ClaudeEvent;

/// <param name="Status">"allowed", "allowed_warning" or "rejected".</param>
public sealed record RateLimitInfo(
    string Status,
    DateTimeOffset? ResetsAt,
    string? RateLimitType,
    UsageWindow? Session,
    UsageWindow? Weekly)
{
    public bool IsRejected => string.Equals(Status, "rejected", StringComparison.OrdinalIgnoreCase);

    public UsageSnapshot? ToSnapshot() => Session is not null && Weekly is not null ? new UsageSnapshot(Session, Weekly) : null;
}
