using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AiDevControlPanel.Core.Claude;

/// <summary>Prompt templates (Claude/Prompts/*.md) and JSON schemas (Claude/Schemas/*.json), embedded in the assembly.</summary>
public static partial class PromptTemplates
{
    public const string Refiner = "refiner";
    public const string Planner = "planner";
    public const string Executor = "executor";
    public const string Answers = "answers";
    public const string Feedback = "feedback";
    public const string Continue = "continue";

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex Placeholder();

    /// <summary>Loads a prompt template and replaces its {{Placeholders}}.</summary>
    public static string Render(string name, IReadOnlyDictionary<string, string>? values = null)
    {
        var template = ReadResource($"Prompts.{name}.md");
        return Placeholder().Replace(template, match =>
            values is not null && values.TryGetValue(match.Groups[1].Value, out var value)
                ? value
                : throw new KeyNotFoundException($"Prompt '{name}' needs a value for {match.Value}."));
    }

    /// <summary>The schema for the given phase, minified for the command line.</summary>
    public static string Schema(string name) => JsonNode.Parse(ReadResource($"Schemas.{name}.json"))!.ToJsonString();

    private static string ReadResource(string logicalName)
    {
        using var stream = typeof(PromptTemplates).Assembly.GetManifestResourceStream(logicalName)
                           ?? throw new InvalidOperationException($"Embedded resource '{logicalName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
