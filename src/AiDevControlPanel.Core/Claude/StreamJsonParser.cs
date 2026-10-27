using System.Text.Json;
using AiDevControlPanel.Core.Usage;

namespace AiDevControlPanel.Core.Claude;

public static class StreamJsonParser
{
    private static readonly string[] SummaryKeys = ["file_path", "command", "pattern", "path", "url", "query", "description", "prompt"];

    private const int MaxSummaryLength = 160;

    /// <summary>Parses one stream-json line; unknown or malformed lines yield no events.</summary>
    public static IReadOnlyList<ClaudeEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return GetString(root, "type") switch
            {
                "system" when GetString(root, "subtype") == "init" => [ParseInit(root)],
                "assistant" => ParseAssistant(root),
                "rate_limit_event" when root.TryGetProperty("rate_limit_info", out var info) => [new RateLimitEvent(ParseRateLimit(info))],
                "result" => [ParseResult(root)],
                _ => [],
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static SessionStartedEvent ParseInit(JsonElement root) =>
        new(GetString(root, "session_id") ?? "", GetString(root, "model"), GetString(root, "permissionMode"));

    private static List<ClaudeEvent> ParseAssistant(JsonElement root)
    {
        var events = new List<ClaudeEvent>();
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return events;
        }

        foreach (var block in content.EnumerateArray())
        {
            switch (GetString(block, "type"))
            {
                case "text" when GetString(block, "text") is { Length: > 0 } text:
                    events.Add(new AssistantTextEvent(text));
                    break;
                case "tool_use":
                    var name = GetString(block, "name") ?? "?";
                    var summary = block.TryGetProperty("input", out var input) ? Summarize(input) : "";
                    events.Add(new ToolUseEvent(name, summary));
                    break;
            }
        }

        return events;
    }

    private static string Summarize(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        foreach (var key in SummaryKeys)
        {
            if (GetString(input, key) is { Length: > 0 } value)
            {
                var singleLine = value.ReplaceLineEndings(" ").Trim();
                return singleLine.Length <= MaxSummaryLength ? singleLine : singleLine[..MaxSummaryLength] + "…";
            }
        }

        return "";
    }

    internal static RateLimitInfo ParseRateLimit(JsonElement info)
    {
        UsageWindow? session = null;
        UsageWindow? weekly = null;
        if (info.TryGetProperty("unifiedWindows", out var windows) && windows.ValueKind == JsonValueKind.Object)
        {
            session = ParseWindow(windows, "five_hour");
            weekly = ParseWindow(windows, "seven_day");
        }

        return new RateLimitInfo(
            GetString(info, "status") ?? "",
            GetEpoch(info, "resetsAt"),
            GetString(info, "rateLimitType"),
            session,
            weekly);
    }

    private static UsageWindow? ParseWindow(JsonElement windows, string name)
    {
        if (!windows.TryGetProperty(name, out var window) || GetEpoch(window, "resetsAt") is not { } resetsAt ||
            !window.TryGetProperty("utilization", out var utilization) || utilization.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        // The stream reports utilization as a fraction (0.05 = 5 %).
        return new UsageWindow(utilization.GetDouble() * 100, resetsAt);
    }

    private static ResultEvent ParseResult(JsonElement root)
    {
        JsonElement? structured = root.TryGetProperty("structured_output", out var output) && output.ValueKind == JsonValueKind.Object
            ? output.Clone()
            : null;
        double? cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
        return new ResultEvent(
            root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True,
            GetString(root, "subtype") ?? "",
            GetString(root, "session_id"),
            GetString(root, "result"),
            structured,
            cost);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? GetEpoch(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(value.GetInt64())
            : null;
}
