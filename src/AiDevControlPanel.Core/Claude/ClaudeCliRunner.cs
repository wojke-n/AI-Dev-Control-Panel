using System.Text.Json;
using AiDevControlPanel.Core.Infrastructure;

namespace AiDevControlPanel.Core.Claude;

public sealed record ClaudeRunRequest
{
    public required string WorkingDirectory { get; init; }

    public required string Prompt { get; init; }

    /// <summary>plan, auto, acceptEdits, …</summary>
    public required string PermissionMode { get; init; }

    public string? JsonSchema { get; init; }

    public string? ResumeSessionId { get; init; }

    public string? NewSessionId { get; init; }

    /// <summary>Model alias or id; null uses the CLI default.</summary>
    public string? Model { get; init; }

    /// <summary>Effort level; null uses the CLI default.</summary>
    public string? Effort { get; init; }

    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    /// <summary>Display name of the session (shown in claude's /resume picker).</summary>
    public string? Name { get; init; }
}

public sealed record ClaudeRunResult(
    bool Success,
    string? SessionId,
    JsonElement? StructuredOutput,
    string? ResultText,
    bool UsageLimitReached,
    DateTimeOffset? LimitResetsAt,
    string? ErrorMessage,
    RateLimitInfo? LastRateLimit);

public interface IClaudeRunner
{
    Task<ClaudeRunResult> RunAsync(ClaudeRunRequest request, Action<ClaudeEvent>? onEvent, CancellationToken ct);
}

/// <summary>Runs one non-interactive Claude Code turn (<c>claude -p</c>) and parses its stream-json output.</summary>
public sealed class ClaudeCliRunner(IProcessRunner runner) : IClaudeRunner
{
    public async Task<ClaudeRunResult> RunAsync(ClaudeRunRequest request, Action<ClaudeEvent>? onEvent, CancellationToken ct)
    {
        string? sessionId = request.ResumeSessionId;
        RateLimitInfo? lastRateLimit = null;
        ResultEvent? result = null;

        void OnLine(string line)
        {
            foreach (var claudeEvent in StreamJsonParser.Parse(line))
            {
                switch (claudeEvent)
                {
                    case SessionStartedEvent started when started.SessionId.Length > 0:
                        sessionId = started.SessionId;
                        break;
                    case RateLimitEvent rateLimit:
                        lastRateLimit = rateLimit.Info;
                        break;
                    case ResultEvent r:
                        result = r;
                        sessionId = r.SessionId ?? sessionId;
                        break;
                }

                onEvent?.Invoke(claudeEvent);
            }
        }

        var spec = new ProcessSpec("claude", BuildArguments(request), request.WorkingDirectory, request.Prompt, OnLine);
        var process = await runner.RunAsync(spec, ct);

        var failureText = result?.ResultText ?? process.ErrorText;
        var limitReached = lastRateLimit?.IsRejected == true ||
                           ((result is null || result.IsError) && UsageLimitDetector.IsLimitMessage(failureText));
        var resetsAt = lastRateLimit?.IsRejected == true ? lastRateLimit.ResetsAt : UsageLimitDetector.ParseResetTime(failureText);
        var success = process.Success && result is { IsError: false } && !limitReached;

        string? error = null;
        if (!success)
        {
            error = string.IsNullOrWhiteSpace(failureText)
                ? $"claude exited with code {process.ExitCode} without a result."
                : failureText.Trim();
        }

        return new ClaudeRunResult(success, sessionId, result?.StructuredOutput, result?.ResultText, limitReached, resetsAt, error, lastRateLimit);
    }

    public static IReadOnlyList<string> BuildArguments(ClaudeRunRequest request)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--permission-mode", request.PermissionMode,
            "--permission-prompts", "none",
        };

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            args.AddRange(["--model", request.Model]);
        }

        if (!string.IsNullOrWhiteSpace(request.Effort))
        {
            args.AddRange(["--effort", request.Effort]);
        }

        if (request.JsonSchema is not null)
        {
            args.AddRange(["--json-schema", request.JsonSchema]);
        }

        if (request.DisallowedTools.Count > 0)
        {
            args.AddRange(["--disallowedTools", string.Join(',', request.DisallowedTools)]);
        }

        if (request.ResumeSessionId is not null)
        {
            args.AddRange(["--resume", request.ResumeSessionId]);
        }
        else if (request.NewSessionId is not null)
        {
            args.AddRange(["--session-id", request.NewSessionId]);
        }

        if (request.Name is not null)
        {
            args.AddRange(["-n", request.Name]);
        }

        return args;
    }
}
