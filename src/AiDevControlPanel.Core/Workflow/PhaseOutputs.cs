using System.Text.Json;

namespace AiDevControlPanel.Core.Workflow;

/// <summary>Structured output of the refiner (Claude/Schemas/refiner.json).</summary>
public sealed record RefinerOutput(
    string Status,
    IReadOnlyList<Question>? Questions,
    string? TsaTitle,
    IReadOnlyList<RefinedIssue>? Issues);

public sealed record RefinedIssue(
    string Ref,
    string? ParentRef,
    string Type,
    string Title,
    string Body,
    int Priority,
    IReadOnlyList<string>? BlockedBy);

/// <summary>Structured output of the planner (Claude/Schemas/planner.json).</summary>
public sealed record PlannerOutput(string Status, IReadOnlyList<Question>? Questions, string? Plan);

/// <summary>Structured output of the execution phase (Claude/Schemas/executor.json).</summary>
public sealed record ExecutorOutput(string Status, string Report, string CommitMessage);

public static class PhaseOutputs
{
    public static T? Parse<T>(JsonElement? output) where T : class
    {
        if (output is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        try
        {
            return element.Deserialize<T>(Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json.Options);

    public static T? Deserialize<T>(string? json) where T : class =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Json.Options);
}
