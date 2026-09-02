using System.Diagnostics;
using System.Text;

namespace SubtitleVideoTool.Core;

public enum ToolState
{
    /// <summary>The file is not on disk anywhere we look.</summary>
    NotFound,

    /// <summary>The file exists but Windows refuses to start it.</summary>
    WontStart,

    Ready,
}

public sealed record ToolStatus
{
    public required string Name { get; init; }
    public string? Path { get; init; }
    public ToolState State { get; init; } = ToolState.NotFound;

    /// <summary>First line of the tool's own --version output, when it ran.</summary>
    public string Version { get; init; } = string.Empty;

    public bool IsReady => State == ToolState.Ready;

    /// <summary>The right-hand column of the design's tools card.</summary>
    public string StatusText => State switch
    {
        ToolState.Ready => Version.Length > 0 ? Version : "Ready",
        ToolState.WontStart => "Won't start",
        _ => "Not found",
    };
}

/// <summary>
/// Finds the bundled tools and proves each one actually runs.
/// </summary>
/// <remarks>
/// Existence is not enough. A truncated or quarantined tools\*.exe still passes
/// a file-exists check but Windows refuses to start it, and yt-dlp then behaves
/// as if FFmpeg were simply absent. Launching each one once with its version
/// flag is the only reliable check.
/// </remarks>
public sealed class ToolSet(string appRoot)
{
    public const string Ffmpeg = "ffmpeg";
    public const string Ffprobe = "ffprobe";
    public const string YtDlp = "yt-dlp";
    public const string Deno = "deno";

    public static readonly string[] All = [Ffmpeg, Ffprobe, YtDlp, Deno];

    private readonly Dictionary<string, ToolStatus> statuses = [];

    public string AppRoot { get; } = appRoot;

    public IReadOnlyDictionary<string, ToolStatus> Statuses => statuses;

    public ToolStatus this[string name] =>
        statuses.GetValueOrDefault(name, new ToolStatus { Name = name });

    public string? PathOf(string name) => this[name].IsReady ? this[name].Path : null;

    /// <summary>Video work needs both FFmpeg and FFprobe; downloading does not.</summary>
    public bool CanProcessVideo => this[Ffmpeg].IsReady && this[Ffprobe].IsReady;

    public bool CanDownload => this[YtDlp].IsReady;

    public int UnavailableCount => All.Count(name => !this[name].IsReady);

    /// <summary>The footer line: "All tools ready", or "1 of 4 tools unavailable".</summary>
    public string SummaryText => UnavailableCount == 0
        ? "All tools ready — ffmpeg, ffprobe, yt-dlp, deno"
        : $"{UnavailableCount} of {All.Length} tools unavailable";

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        foreach (var name in All)
        {
            statuses[name] = await InspectAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Adopts a pair the user pointed at with "Locate FFmpeg…".</summary>
    public async Task<bool> AdoptFfmpegAsync(string ffmpegPath, CancellationToken cancellationToken = default)
    {
        var folder = System.IO.Path.GetDirectoryName(ffmpegPath);
        if (folder is null)
        {
            return false;
        }

        var ffprobePath = System.IO.Path.Combine(folder, "ffprobe.exe");
        if (!File.Exists(ffprobePath))
        {
            return false;
        }

        var ffmpeg = await InspectPathAsync(Ffmpeg, ffmpegPath, cancellationToken).ConfigureAwait(false);
        var ffprobe = await InspectPathAsync(Ffprobe, ffprobePath, cancellationToken).ConfigureAwait(false);
        if (!ffmpeg.IsReady || !ffprobe.IsReady)
        {
            return false;
        }

        statuses[Ffmpeg] = ffmpeg;
        statuses[Ffprobe] = ffprobe;
        return true;
    }

    private async Task<ToolStatus> InspectAsync(string name, CancellationToken cancellationToken)
    {
        var path = Resolve(name);
        return path is null
            ? new ToolStatus { Name = name, State = ToolState.NotFound }
            : await InspectPathAsync(name, path, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ToolStatus> InspectPathAsync(string name, string path, CancellationToken cancellationToken)
    {
        var version = await ReadVersionAsync(path, VersionArgument(name), cancellationToken).ConfigureAwait(false);
        return new ToolStatus
        {
            Name = name,
            Path = path,
            State = version is null ? ToolState.WontStart : ToolState.Ready,
            Version = version ?? string.Empty,
        };
    }

    public string? Resolve(string name)
    {
        string[] candidates =
        [
            System.IO.Path.Combine(AppRoot, "tools", name + ".exe"),
            System.IO.Path.Combine(AppRoot, name + ".exe"),
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return System.IO.Path.GetFullPath(candidate);
            }
        }

        return FindOnPath(name + ".exe");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = System.IO.Path.Combine(folder.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing over.
            }
        }

        return null;
    }

    /// <summary>FFmpeg and FFprobe predate the --version convention.</summary>
    public static string VersionArgument(string name) =>
        name is Ffmpeg or Ffprobe ? "-version" : "--version";

    private static async Task<string?> ReadVersionAsync(string path, string argument, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var text = await stdout.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = await stderr.ConfigureAwait(false);
            }

            var firstLine = text
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0);

            return firstLine ?? string.Empty;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Started but never answered — as unusable as one that won't start.
            return null;
        }
        catch (Exception)
        {
            // Truncated, quarantined, or the wrong architecture.
            return null;
        }
    }
}
