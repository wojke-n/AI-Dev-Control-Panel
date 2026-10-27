using System.Diagnostics;
using System.Text;

namespace AiDevControlPanel.Core.Infrastructure;

public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    string? StandardInput = null,
    Action<string>? OnOutputLine = null);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;

    /// <summary>Error text for messages: stderr if present, otherwise stdout.</summary>
    public string ErrorText => string.IsNullOrWhiteSpace(StandardError) ? StandardOutput.Trim() : StandardError.Trim();
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default);
}

/// <summary>Runs external tools (git, gh, claude) without a shell, streaming stdout line by line.</summary>
public sealed class ProcessRunner(ExecutableLocator locator) : IProcessRunner
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(locator.Resolve(spec.FileName))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (spec.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = spec.WorkingDirectory;
        }

        startInfo.Environment["PATH"] = locator.SearchPath;

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.Start();

        var stdoutTask = ReadLinesAsync(process.StandardOutput, line =>
        {
            stdout.AppendLine(line);
            spec.OnOutputLine?.Invoke(line);
        });
        var stderrTask = ReadLinesAsync(process.StandardError, line => stderr.AppendLine(line));

        try
        {
            if (spec.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(spec.StandardInput.AsMemory(), cancellationToken);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            onLine(line);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
