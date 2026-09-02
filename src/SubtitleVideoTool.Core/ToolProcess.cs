using System.Diagnostics;
using System.Text;

namespace SubtitleVideoTool.Core;

public sealed record ToolResult
{
    public int ExitCode { get; init; }

    /// <summary>Everything the tool wrote, minus progress lines. Feeds the failure card.</summary>
    public string Output { get; init; } = string.Empty;

    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs one bundled tool and hands back its output line by line while it runs.
/// </summary>
public static class ToolProcess
{
    /// <summary>
    /// Lines are delivered through <paramref name="onLine"/>, which is an
    /// <see cref="IProgress{T}"/> so the window receives them on its own thread
    /// without any cross-thread call of its own.
    /// </summary>
    public static async Task<ToolResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IProgress<YtDlpLine>? onLine = null,
        Action<string>? onRawLine = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // yt-dlp and ffmpeg write UTF-8 to a redirected pipe; without this
            // the console code page mangles titles and hides the real error text.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        // Progress lines are deliberately kept out of this buffer: yt-dlp emits
        // several per second and they would push the line that explains a
        // failure out of the window the classifier sees.
        var captured = new List<string>();
        var captureLock = new Lock();

        void Handle(string? raw)
        {
            if (raw is null)
            {
                return;
            }

            onRawLine?.Invoke(raw);

            var line = YtDlpOutput.Parse(raw);
            if (line.Kind is not (YtDlpLineKind.Progress or YtDlpLineKind.Empty))
            {
                lock (captureLock)
                {
                    captured.Add(raw);
                    if (captured.Count > 400)
                    {
                        captured.RemoveAt(0);
                    }
                }
            }

            onLine?.Report(line);
        }

        process.OutputDataReceived += (_, e) => Handle(e.Data);
        process.ErrorDataReceived += (_, e) => Handle(e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException($"{Path.GetFileName(executable)} could not be started.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        // WaitForExitAsync returns before the redirected readers have finished;
        // the synchronous overload with no timeout is what flushes them.
        process.WaitForExit();

        lock (captureLock)
        {
            return new ToolResult
            {
                ExitCode = process.ExitCode,
                Output = string.Join(Environment.NewLine, captured),
            };
        }
    }

    /// <summary>Runs a tool that prints a document, with nothing to stream.</summary>
    public static async Task<string> ReadAllAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        var errors = new StringBuilder();

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"{Path.GetFileName(executable)} could not be started.");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        builder.Append(await stdout.ConfigureAwait(false));
        errors.Append(await stderr.ConfigureAwait(false));

        if (process.ExitCode != 0)
        {
            // The caller classifies this; it must carry the tool's own words.
            throw new ToolFailedException(process.ExitCode, errors.ToString());
        }

        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone, or gone by the time we asked.
        }
    }
}

public sealed class ToolFailedException(int exitCode, string output)
    : Exception($"The tool stopped with exit code {exitCode}.")
{
    public int ExitCode { get; } = exitCode;
    public string Output { get; } = output;
}
