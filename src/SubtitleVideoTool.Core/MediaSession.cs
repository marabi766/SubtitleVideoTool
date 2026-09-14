using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SubtitleVideoTool.Core;

public sealed record MediaInfo
{
    public TimeSpan Duration { get; init; }
    public long SizeBytes { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>"512 MB · 24 min", the line under the Compress tab's size field.</summary>
    public string Summary =>
        $"Source is {DownloadFailure.FormatSize(SizeBytes)} · {(int)Math.Round(Duration.TotalMinutes)} min";
}

public sealed record BurnRequest
{
    public required string VideoPath { get; init; }
    public required string SubtitlePath { get; init; }
    public required string OutputPath { get; init; }
    public string FontName { get; init; } = "Segoe UI";
    public int FontSize { get; init; } = 18;

    /// <summary>ASS colours are &amp;HBBGGRR, which is why these are stored split.</summary>
    public (byte R, byte G, byte B) TextColour { get; init; } = (255, 255, 255);

    public (byte R, byte G, byte B) BackgroundColour { get; init; } = (0, 0, 0);

    public long? SizeLimitBytes { get; init; }
}

public sealed record CompressRequest
{
    public required string VideoPath { get; init; }
    public required string OutputPath { get; init; }
    public required long SizeLimitBytes { get; init; }
}

public sealed record MediaOutcome
{
    public bool Succeeded { get; init; }
    public bool Cancelled { get; init; }
    public string? OutputPath { get; init; }
    public long OutputBytes { get; init; }
    public bool OvershotLimit { get; init; }

    /// <summary>The limit that was asked for, so a result can be measured against it.</summary>
    public long? SizeLimitBytes { get; init; }
    public FailureDescription? Failure { get; init; }
    public string RawOutput { get; init; } = string.Empty;
}

/// <summary>
/// The FFmpeg half: reading what a file is, burning subtitles into it, and
/// hitting a size target.
/// </summary>
public sealed partial class MediaSession(ToolSet tools)
{
    [GeneratedRegex(@"time=(?<h>\d+):(?<m>\d{2}):(?<s>\d{2})(?:\.(?<cs>\d+))?")]
    private static partial Regex TimePattern();

    /// <summary>
    /// Audio bitrates worth using, best first. A fixed 128 kbps used to be
    /// spent whatever the target was, which on a tight limit left the video
    /// nothing and put the result over the size the user asked for.
    /// </summary>
    private static readonly int[] AudioLadder = [128, 96, 64, 48];

    /// <summary>Below this the video stops being worth watching at all.</summary>
    private const int MinimumVideoKbps = 100;

    public async Task<MediaInfo> InspectAsync(string path, CancellationToken cancellationToken)
    {
        var ffprobe = tools.PathOf(ToolSet.Ffprobe)
            ?? throw new InputException("FFprobe is not available, so the file cannot be inspected.");

        string[] arguments =
        [
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "format=duration,size:stream=width,height",
            "-of", "json",
            path,
        ];

        var json = await ToolProcess.ReadAllAsync(ffprobe, arguments, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var duration = TimeSpan.Zero;
        long size = 0;
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var d) &&
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            if (format.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var bytes))
            {
                size = bytes;
            }
        }

        var width = 0;
        var height = 0;
        if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
        {
            var stream = streams[0];
            width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
            height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
        }

        if (size == 0)
        {
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception)
            {
                // Leave it at zero; the summary line simply reads oddly.
            }
        }

        return new MediaInfo { Duration = duration, SizeBytes = size, Width = width, Height = height };
    }

    public async Task<MediaOutcome> BurnAsync(
        BurnRequest request,
        IProgress<double> progress,
        Action<string>? onLogLine,
        CancellationToken cancellationToken)
    {
        var ffmpeg = tools.PathOf(ToolSet.Ffmpeg);
        if (ffmpeg is null)
        {
            return Unavailable();
        }

        var info = await InspectAsync(request.VideoPath, cancellationToken).ConfigureAwait(false);
        var filter = BuildSubtitleFilter(request);
        var audioKbps = AudioBitrateKbps(request.SizeLimitBytes, info.Duration);

        List<string> arguments =
        [
            "-y",
            "-i", request.VideoPath,
            "-vf", filter,
            "-c:a", "aac",
            "-b:a", $"{audioKbps}k",
        ];

        if (request.SizeLimitBytes is > 0)
        {
            var videoKbps = VideoBitrateKbps(request.SizeLimitBytes.Value, info.Duration, audioKbps);
            arguments.AddRange(["-c:v", "libx264", "-preset", "medium", "-b:v", $"{videoKbps}k", "-maxrate", $"{videoKbps}k", "-bufsize", $"{videoKbps * 2}k"]);
        }
        else
        {
            arguments.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", "20"]);
        }

        arguments.Add(request.OutputPath);

        return await RunFfmpegAsync(ffmpeg, arguments, info.Duration, request.OutputPath, request.SizeLimitBytes, progress, onLogLine, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MediaOutcome> CompressAsync(
        CompressRequest request,
        IProgress<double> progress,
        Action<string>? onLogLine,
        CancellationToken cancellationToken)
    {
        var ffmpeg = tools.PathOf(ToolSet.Ffmpeg);
        if (ffmpeg is null)
        {
            return Unavailable();
        }

        var info = await InspectAsync(request.VideoPath, cancellationToken).ConfigureAwait(false);
        var audioKbps = AudioBitrateKbps(request.SizeLimitBytes, info.Duration);
        var videoKbps = VideoBitrateKbps(request.SizeLimitBytes, info.Duration, audioKbps);

        List<string> arguments =
        [
            "-y",
            "-i", request.VideoPath,
            "-c:v", "libx264",
            "-preset", "medium",
            "-b:v", $"{videoKbps}k",
            "-maxrate", $"{videoKbps}k",
            "-bufsize", $"{videoKbps * 2}k",
            "-c:a", "aac",
            "-b:a", $"{audioKbps}k",
        ];

        // Resolution is only lowered when the target cannot be met at the
        // current one, which is what the tab's hint promises.
        if (TargetHeight(videoKbps, info.Height) is { } height)
        {
            arguments.AddRange(["-vf", $"scale=-2:{height}"]);
        }

        arguments.Add(request.OutputPath);

        return await RunFfmpegAsync(ffmpeg, arguments, info.Duration, request.OutputPath, request.SizeLimitBytes, progress, onLogLine, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<MediaOutcome> RunFfmpegAsync(
        string ffmpeg,
        IReadOnlyList<string> arguments,
        TimeSpan duration,
        string outputPath,
        long? sizeLimit,
        IProgress<double> progress,
        Action<string>? onLogLine,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ToolProcess.RunAsync(
                ffmpeg,
                arguments,
                new Progress<YtDlpLine>(line => ReportTime(line, duration, progress)),
                onLogLine,
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                TryDelete(outputPath);
                return new MediaOutcome
                {
                    Failure = DownloadFailure.Describe(result.Output),
                    RawOutput = result.Output,
                };
            }

            var bytes = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
            return new MediaOutcome
            {
                Succeeded = true,
                OutputPath = outputPath,
                OutputBytes = bytes,
                SizeLimitBytes = sizeLimit,
                OvershotLimit = sizeLimit is > 0 && bytes > sizeLimit,
                RawOutput = result.Output,
            };
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            return new MediaOutcome { Cancelled = true };
        }
    }

    /// <summary>
    /// FFmpeg reports elapsed media time rather than a percentage, so the bar
    /// comes from time against the source's duration.
    /// </summary>
    private static void ReportTime(YtDlpLine line, TimeSpan duration, IProgress<double> progress)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var match = TimePattern().Match(line.Text);
        if (!match.Success)
        {
            return;
        }

        var elapsed = new TimeSpan(
            0,
            int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture),
            match.Groups["cs"].Success ? int.Parse(match.Groups["cs"].Value, CultureInfo.InvariantCulture) * 10 : 0);

        progress.Report(Math.Clamp(elapsed.TotalSeconds / duration.TotalSeconds * 100, 0, 100));
    }

    /// <summary>
    /// The whole budget, in kbps, once 4% is set aside for container and muxing
    /// overhead — so the result lands under the target rather than a whisker
    /// over it.
    /// </summary>
    public static double TotalBudgetKbps(long sizeLimitBytes, TimeSpan duration) =>
        duration <= TimeSpan.Zero ? 0 : sizeLimitBytes * 8 / duration.TotalSeconds / 1000 * 0.96;

    /// <summary>
    /// The best audio the budget can afford while still leaving the picture a
    /// quarter of it. A long video under a small limit gets 48 kbps rather than
    /// spending the entire budget on sound.
    /// </summary>
    public static int AudioBitrateKbps(long? sizeLimitBytes, TimeSpan duration)
    {
        if (sizeLimitBytes is not > 0)
        {
            return AudioLadder[0];
        }

        var affordable = TotalBudgetKbps(sizeLimitBytes.Value, duration) * 0.25;
        return AudioLadder.FirstOrDefault(rate => rate <= affordable, AudioLadder[^1]);
    }

    public static int VideoBitrateKbps(long sizeLimitBytes, TimeSpan duration, int audioKbps)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 1000;
        }

        return Math.Max(MinimumVideoKbps, (int)(TotalBudgetKbps(sizeLimitBytes, duration) - audioKbps));
    }

    /// <summary>
    /// The height to scale down to, or null to leave the picture alone. Below
    /// roughly these bitrates a larger frame only spends the budget on blocking.
    /// </summary>
    public static int? TargetHeight(int videoKbps, int sourceHeight) => videoKbps switch
    {
        < 250 when sourceHeight > 480 => 480,
        < 500 when sourceHeight > 720 => 720,
        _ => null,
    };

    /// <summary>
    /// The smallest this video can be made without going below what is still
    /// watchable. A limit under this cannot be met, however the encode is run.
    /// </summary>
    public static long SmallestReachableBytes(TimeSpan duration) =>
        duration <= TimeSpan.Zero
            ? 0
            : (long)((MinimumVideoKbps + AudioLadder[^1]) * 1000 / 8.0 * duration.TotalSeconds);

    /// <summary>
    /// ASS alpha runs backwards from ordinary transparency — &amp;H00 is
    /// opaque and &amp;HFF is invisible — so this is roughly 70% opaque.
    /// </summary>
    private const byte BoxFillAlpha = 0x4D;

    /// <summary>
    /// The subtitles filter takes a path inside a quoted filter string, so
    /// backslashes, colons and quotes all have to be escaped or the filter is
    /// silently mis-parsed.
    /// </summary>
    public static string BuildSubtitleFilter(BurnRequest request)
    {
        var path = request.SubtitlePath.Replace('\\', '/').Replace("'", @"\'");
        path = path.Replace(":", @"\:");

        var style =
            $"FontName={request.FontName}," +
            $"FontSize={request.FontSize}," +
            $"PrimaryColour={AssColour(request.TextColour)}," +
            // BorderStyle=3's box is stroked per glyph cluster rather than
            // drawn once for the whole line, and for some fonts — Peyda
            // included — adjacent clusters don't fully join, leaving the box
            // visibly gapped between letters and words. BorderStyle=4 draws
            // one box for the line and has no such gap; BackColour is its
            // fill, so that is where the chosen background colour goes, with
            // a little transparency so it reads as a subtitle box rather
            // than a solid card. OutlineColour is the same colour at full
            // opacity, so the thin 1px border it also draws stays a crisp
            // edge instead of a visible seam.
            $"BackColour={AssColour(request.BackgroundColour, BoxFillAlpha)}," +
            $"OutlineColour={AssColour(request.BackgroundColour)}," +
            "BorderStyle=4,Outline=1,Shadow=0";

        return $"subtitles='{path}':force_style='{style}'";
    }

    /// <summary>ASS wants &amp;HBBGGRR, the reverse of the usual RRGGBB.</summary>
    private static string AssColour((byte R, byte G, byte B) colour) =>
        $"&H{colour.B:X2}{colour.G:X2}{colour.R:X2}";

    /// <summary>ASS wants &amp;HAABBGGRR when the colour carries transparency.</summary>
    private static string AssColour((byte R, byte G, byte B) colour, byte alpha) =>
        $"&H{alpha:X2}{colour.B:X2}{colour.G:X2}{colour.R:X2}";

    private static MediaOutcome Unavailable() => new()
    {
        Failure = new FailureDescription
        {
            Kind = FailureKind.FfmpegMissing,
            Headline = "FFmpeg won't start",
            Explanation =
                @"The file tools\ffmpeg.exe is missing or Windows refuses to run it — usually a blocked download " +
                "or antivirus quarantine. Burning and compressing are unavailable until it works.",
            PrimaryAction = FailureAction.LocateFfmpeg,
            PrimaryActionLabel = "Locate FFmpeg…",
            SecondaryAction = FailureAction.CheckAgain,
            SecondaryActionLabel = "Check again",
        },
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // A half-written file we cannot remove is not worth failing over.
        }
    }
}
