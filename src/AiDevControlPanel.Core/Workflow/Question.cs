using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDevControlPanel.Core.Workflow;

/// <summary>A question the refiner or planner asks the user (same shape as their JSON output).</summary>
public sealed record Question(
    [property: JsonPropertyName("question")] string Text,
    [property: JsonPropertyName("header")] string? Header,
    [property: JsonPropertyName("options")] IReadOnlyList<QuestionOption>? Options,
    [property: JsonPropertyName("multiSelect")] bool MultiSelect);

public sealed record QuestionOption(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("description")] string? Description);

public sealed record QuestionAnswer(string Question, string Answer);

public static class Questions
{
    public static string Serialize(IReadOnlyList<Question> questions) => JsonSerializer.Serialize(questions, Json.Options);

    public static IReadOnlyList<Question> Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<Question>>(json, Json.Options) ?? [];

    /// <summary>Formats answers for the prompt that resumes the refiner or planner.</summary>
    public static string FormatAnswers(IEnumerable<QuestionAnswer> answers)
    {
        var sb = new StringBuilder();
        foreach (var answer in answers)
        {
            sb.Append("Q: ").AppendLine(answer.Question);
            sb.Append("A: ").AppendLine(answer.Answer);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
