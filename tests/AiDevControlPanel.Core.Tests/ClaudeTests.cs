using AiDevControlPanel.Core.Claude;
using AiDevControlPanel.Core.Infrastructure;

namespace AiDevControlPanel.Core.Tests;

public class StreamJsonParserTests
{
    // Shapes recorded from claude 2.1.311 with --output-format stream-json --verbose.
    private const string InitLine = """{"type":"system","subtype":"init","cwd":"C:\\repo","session_id":"8107d5aa","model":"claude-haiku-5-5","permissionMode":"plan"}""";
    private const string ToolUseLine = """{"type":"assistant","message":{"content":[{"type":"text","text":"Reading the file."},{"type":"tool_use","name":"Read","input":{"file_path":"C:\\repo\\calc.py"}}]},"session_id":"8107d5aa"}""";
    private const string RateLimitLine = """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1793110200,"rateLimitType":"five_hour","unifiedWindows":{"five_hour":{"utilization":0.05,"resetsAt":1793110200,"status":"allowed"},"seven_day":{"utilization":0.01,"resetsAt":1793379600,"status":"allowed"}}},"session_id":"8107d5aa"}""";
    private const string ResultLine = """{"type":"result","subtype":"success","is_error":false,"session_id":"8107d5aa","result":"{}","structured_output":{"status":"plan","plan":"1. Do it"},"total_cost_usd":0.11}""";

    [Fact]
    public void ParsesInit()
    {
        var e = Assert.IsType<SessionStartedEvent>(Assert.Single(StreamJsonParser.Parse(InitLine)));
        Assert.Equal("8107d5aa", e.SessionId);
        Assert.Equal("plan", e.PermissionMode);
    }

    [Fact]
    public void ParsesAssistantTextAndToolUse()
    {
        var events = StreamJsonParser.Parse(ToolUseLine);

        Assert.Equal("Reading the file.", Assert.IsType<AssistantTextEvent>(events[0]).Text);
        var tool = Assert.IsType<ToolUseEvent>(events[1]);
        Assert.Equal("Read", tool.ToolName);
        Assert.Equal(@"C:\repo\calc.py", tool.Summary);
    }

    [Fact]
    public void ParsesRateLimitAsPercent()
    {
        var e = Assert.IsType<RateLimitEvent>(Assert.Single(StreamJsonParser.Parse(RateLimitLine)));

        Assert.False(e.Info.IsRejected);
        Assert.Equal(5, e.Info.Session!.Percent, precision: 6);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793379600), e.Info.Weekly!.ResetsAt);
    }

    [Fact]
    public void ParsesResultWithStructuredOutput()
    {
        var e = Assert.IsType<ResultEvent>(Assert.Single(StreamJsonParser.Parse(ResultLine)));

        Assert.False(e.IsError);
        Assert.Equal("plan", e.StructuredOutput!.Value.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{broken")]
    [InlineData("""{"type":"user","message":{}}""")]
    public void IgnoresOtherLines(string line)
    {
        Assert.Empty(StreamJsonParser.Parse(line));
    }
}

public class ClaudeCliRunnerTests
{
    [Fact]
    public void BuildArguments_ForNewPlanSession()
    {
        var args = ClaudeCliRunner.BuildArguments(new ClaudeRunRequest
        {
            WorkingDirectory = ".",
            Prompt = "x",
            PermissionMode = "plan",
            JsonSchema = "{}",
            NewSessionId = "abc",
            Model = "opus",
            Effort = "high",
            DisallowedTools = ["AskUserQuestion", "ExitPlanMode"],
            Name = "ADCP #1 plan",
        });

        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "plan", "--permission-prompts", "none",
             "--model", "opus", "--effort", "high", "--json-schema", "{}", "--disallowedTools", "AskUserQuestion,ExitPlanMode",
             "--session-id", "abc", "-n", "ADCP #1 plan"],
            args);
    }

    [Fact]
    public void BuildArguments_ResumeWinsAndDefaultsAreOmitted()
    {
        var args = ClaudeCliRunner.BuildArguments(new ClaudeRunRequest
        {
            WorkingDirectory = ".",
            Prompt = "x",
            PermissionMode = "auto",
            ResumeSessionId = "old",
            NewSessionId = "new",
        });

        Assert.Contains("--resume", args);
        Assert.DoesNotContain("--session-id", args);
        Assert.DoesNotContain("--model", args);
        Assert.DoesNotContain("--effort", args);
    }

    [Fact]
    public async Task RunAsync_ReturnsStructuredOutputAndSession()
    {
        var runner = new ClaudeCliRunner(new ScriptedProcessRunner(0,
            """{"type":"system","subtype":"init","session_id":"s1"}""",
            """{"type":"result","subtype":"success","is_error":false,"session_id":"s1","structured_output":{"status":"done"}}"""));

        var result = await runner.RunAsync(new ClaudeRunRequest { WorkingDirectory = ".", Prompt = "x", PermissionMode = "plan" }, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("s1", result.SessionId);
        Assert.Equal("done", result.StructuredOutput!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RunAsync_DetectsRejectedRateLimit()
    {
        var runner = new ClaudeCliRunner(new ScriptedProcessRunner(1,
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1793110200,"rateLimitType":"five_hour"}}""",
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"You've hit your limit"}"""));

        var result = await runner.RunAsync(new ClaudeRunRequest { WorkingDirectory = ".", Prompt = "x", PermissionMode = "auto" }, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.UsageLimitReached);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793110200), result.LimitResetsAt);
    }

    [Fact]
    public async Task RunAsync_DetectsLimitMessageWithEpoch()
    {
        var runner = new ClaudeCliRunner(new ScriptedProcessRunner(1,
            """{"type":"result","subtype":"success","is_error":true,"result":"Claude AI usage limit reached|1793110200"}"""));

        var result = await runner.RunAsync(new ClaudeRunRequest { WorkingDirectory = ".", Prompt = "x", PermissionMode = "auto" }, null, CancellationToken.None);

        Assert.True(result.UsageLimitReached);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793110200), result.LimitResetsAt);
    }

    [Fact]
    public async Task RunAsync_OtherErrorsAreNotUsageLimits()
    {
        var runner = new ClaudeCliRunner(new ScriptedProcessRunner(1,
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Something broke"}"""));

        var result = await runner.RunAsync(new ClaudeRunRequest { WorkingDirectory = ".", Prompt = "x", PermissionMode = "auto" }, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.UsageLimitReached);
        Assert.Equal("Something broke", result.ErrorMessage);
    }

    private sealed class ScriptedProcessRunner(int exitCode, params string[] lines) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
        {
            foreach (var line in lines)
            {
                spec.OnOutputLine?.Invoke(line);
            }

            return Task.FromResult(new ProcessResult(exitCode, string.Join('\n', lines), ""));
        }
    }
}

public class PromptTemplatesTests
{
    [Theory]
    [InlineData(PromptTemplates.Refiner)]
    [InlineData(PromptTemplates.Planner)]
    [InlineData(PromptTemplates.Executor)]
    public void SchemasAreEmbeddedAndMinified(string name)
    {
        var schema = PromptTemplates.Schema(name);

        Assert.StartsWith("{\"type\":\"object\"", schema);
        Assert.DoesNotContain('\n', schema);
    }

    [Fact]
    public void RenderReplacesPlaceholders()
    {
        var prompt = PromptTemplates.Render(PromptTemplates.Answers, new Dictionary<string, string> { ["Answers"] = "Q: a\nA: b {{Answers}}" });

        Assert.Contains("Q: a\nA: b {{Answers}}", prompt);
    }

    [Fact]
    public void RenderRequiresAllPlaceholders()
    {
        Assert.Throws<KeyNotFoundException>(() => PromptTemplates.Render(PromptTemplates.Planner));
    }
}
