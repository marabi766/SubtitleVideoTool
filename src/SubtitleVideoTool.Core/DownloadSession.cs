using System.Globalization;
using System.Text.RegularExpressions;

namespace SubtitleVideoTool.Core;

/// <summary>The rail across the top of the state card. It never changes shape.</summary>
public enum RailStep
{
    Prepare,
    Connect,
    Download,
    Process,
    Done,
}

public enum DownloadStage
{
    Idle,
    Preparing,
    Started,
    Downloading,
    PostProcessing,
    Retrying,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// Everything the state card shows at one moment. The window binds to this and
/// nothing else, so every state is the same anatomy with different values.
/// </summary>
public sealed record DownloadProgress
{
    public DownloadStage Stage { get; init; } = DownloadStage.Idle;
    public RailStep Step { get; init; } = RailStep.Prepare;
    public string Headline { get; init; } = string.Empty;
    public string Badge { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;

    public double? Percent { get; init; }
    public int Part { get; init; }
    public int PartCount { get; init; }

    public string PercentText { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
    public string SpeedText { get; init; } = string.Empty;
    public string EtaText { get; init; } = string.Empty;

    /// <summary>Shown instead of the figures row when there are no figures.</summary>
    public string FiguresPlaceholder { get; init; } = string.Empty;

    public bool HasFigures => PercentText.Length > 0 || SizeText.Length > 0;
}

public sealed record DownloadOutcome
{
    public bool Succeeded { get; init; }
    public bool Cancelled { get; init; }
    public string? VideoPath { get; init; }
    public string? SubtitlePath { get; init; }
    public TimeSpan Elapsed { get; init; }
    public EnglishSubtitle Subtitle { get; init; } = new();
    public FailureDescription? Failure { get; init; }
    public string RawOutput { get; init; } = string.Empty;
}

/// <summary>
/// Runs one download from start to finish, including the automatic retries, and
/// reports the design's states as it goes.
/// </summary>
public sealed partial class DownloadSession(ToolSet tools)
{
    [GeneratedRegex(@"Downloading\s+\d+\s+format\(s\):\s*(?<ids>\S+)")]
    private static partial Regex FormatListPattern();

    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".webm", ".mov", ".m4v"];

    /// <summary>
    /// Asks YouTube what the video has. A bot check is answered the same way the
    /// download answers it — by asking again as a different player client —
    /// because otherwise the window dead-ends on a link the download itself
    /// would have managed.
    /// </summary>
    public async Task<VideoInfo> ProbeAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        var ytDlp = tools.PathOf(ToolSet.YtDlp)
            ?? throw new ProbeException("yt-dlp is not available, so the link cannot be checked.");

        var lastOutput = string.Empty;

        foreach (var playerClients in ProbeAttempts(request))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var json = await ToolProcess
                    .ReadAllAsync(
                        ytDlp,
                        YtDlpArguments.ForProbe(request with { PlayerClients = playerClients }, tools),
                        cancellationToken)
                    .ConfigureAwait(false);

                return VideoProbe.Parse(json) with
                {
                    QualityLimitedByFallback = playerClients == YtDlpArguments.QualityLimitedPlayerClients,
                };
            }
            catch (ToolFailedException failure)
            {
                lastOutput = failure.Output;

                // Only the refusals another player client can get past are worth
                // a second ask. A private video stays private however we ask.
                if (DownloadFailure.Classify(failure.Output) is not (FailureKind.BotCheck or FailureKind.LoginRequired))
                {
                    throw new ProbeFailedException(failure.Output);
                }
            }
        }

        // Carry yt-dlp's own words so the caller can classify this exactly the
        // way it classifies a failed download.
        throw new ProbeFailedException(lastOutput);
    }

    /// <summary>
    /// The default client first, then the alternatives — but only when nobody is
    /// signed in, since a signed-in request is not the one YouTube bot-checks.
    /// </summary>
    private static IEnumerable<string?> ProbeAttempts(DownloadRequest request)
    {
        yield return null;

        if (request.CookieSource != CookieSource.None)
        {
            yield break;
        }

        foreach (var clients in YtDlpArguments.FallbackPlayerClients)
        {
            yield return clients;
        }
    }

    public async Task<DownloadOutcome> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress> progress,
        Action<string>? onLogLine,
        CancellationToken cancellationToken)
    {
        var ytDlp = tools.PathOf(ToolSet.YtDlp);
        if (ytDlp is null)
        {
            return new DownloadOutcome
            {
                Failure = DownloadFailure.Describe(FailureKind.FfmpegMissing) with
                {
                    Headline = "yt-dlp won't start",
                    Explanation =
                        @"The file tools\yt-dlp.exe is missing or Windows refuses to run it — usually a blocked " +
                        "download or antivirus quarantine. Downloading is unavailable until it works.",
                },
            };
        }

        // Anonymous downloads are the ones YouTube answers with a bot check, so
        // the alternative player clients are only worth queueing for those.
        var attempts = new List<string?> { null };
        if (request.CookieSource == CookieSource.None)
        {
            attempts.AddRange(YtDlpArguments.FallbackPlayerClients);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var lastOutput = string.Empty;
        var attemptNumber = 0;

        foreach (var playerClients in attempts)
        {
            attemptNumber++;
            cancellationToken.ThrowIfCancellationRequested();

            if (attemptNumber > 1)
            {
                progress.Report(new DownloadProgress
                {
                    Stage = DownloadStage.Retrying,
                    Step = RailStep.Connect,
                    Headline = "Trying a different method",
                    Badge = $"attempt {attemptNumber} of {attempts.Count}",
                    Detail =
                        "YouTube turned down the first request. The app is asking again as a different player — " +
                        "this is normal and usually works.",
                    FiguresPlaceholder = "Nothing downloaded yet — no progress is lost",
                });
            }
            else
            {
                progress.Report(new DownloadProgress
                {
                    Stage = DownloadStage.Preparing,
                    Step = RailStep.Prepare,
                    Headline = "Preparing the download",
                    Detail = "Reading the link and asking YouTube what's available. This usually takes a few seconds.",
                    FiguresPlaceholder = "No figures yet",
                });
            }

            var tracker = new ProgressTracker(progress);
            var attemptRequest = request with { PlayerClients = playerClients };

            ToolResult result;
            try
            {
                result = await ToolProcess.RunAsync(
                    ytDlp,
                    YtDlpArguments.ForDownload(attemptRequest, tools),
                    new Progress<YtDlpLine>(tracker.Observe),
                    onLogLine,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new DownloadOutcome { Cancelled = true, Elapsed = DateTimeOffset.UtcNow - startedAt };
            }

            lastOutput = result.Output;

            if (result.Succeeded)
            {
                return Complete(request, startedAt, tracker, result.Output);
            }

            // yt-dlp reports a non-zero exit for anything that went wrong,
            // including a subtitle track it could not fetch after the video was
            // already saved. The video is what was asked for, so if one landed
            // this counts as done rather than as a failure that throws it away.
            if (Complete(request, startedAt, tracker, result.Output) is { Succeeded: true } salvaged)
            {
                return salvaged;
            }

            var kind = DownloadFailure.Classify(result.Output);
            var worthRetrying = kind is FailureKind.BotCheck or FailureKind.LoginRequired;
            if (!worthRetrying || attemptNumber == attempts.Count)
            {
                return new DownloadOutcome
                {
                    Failure = DownloadFailure.Describe(kind, BuildContext(request, attemptNumber)),
                    RawOutput = result.Output,
                    Elapsed = DateTimeOffset.UtcNow - startedAt,
                };
            }
        }

        return new DownloadOutcome
        {
            Failure = DownloadFailure.Describe(lastOutput, BuildContext(request, attemptNumber)),
            RawOutput = lastOutput,
            Elapsed = DateTimeOffset.UtcNow - startedAt,
        };
    }

    private FailureContext BuildContext(DownloadRequest request, int attemptNumber)
    {
        var context = new FailureContext
        {
            AttemptCount = attemptNumber,
            BrowserName = request.CookieSource is CookieSource.Firefox or CookieSource.Edge
                or CookieSource.Chrome or CookieSource.Brave
                ? YtDlpArguments.BrowserDisplayName(request.CookieSource)
                : null,
            FfmpegPath = tools[ToolSet.Ffmpeg].Path,
        };

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(request.OutputFolder));
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                context = context with
                {
                    DriveLetter = root.TrimEnd('\\'),
                    FreeBytes = drive.AvailableFreeSpace,
                };
            }
        }
        catch (Exception)
        {
            // A card without the number still reads correctly.
        }

        return context;
    }

    /// <summary>
    /// Finds what actually landed. yt-dlp names files from the video title, so
    /// the only reliable identification is "written into the output folder while
    /// this download was running".
    /// </summary>
    private static DownloadOutcome Complete(
        DownloadRequest request,
        DateTimeOffset startedAt,
        ProgressTracker tracker,
        string output)
    {
        var since = startedAt.AddSeconds(-5).UtcDateTime;
        var produced = new List<FileInfo>();

        try
        {
            produced = new DirectoryInfo(request.OutputFolder)
                .GetFiles()
                .Where(file => file.LastWriteTimeUtc >= since)
                .ToList();
        }
        catch (Exception)
        {
            // The success card degrades to "finished" without file details.
        }

        var video = produced
            .Where(file => VideoExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        var subtitle = produced
            .Where(file => file.Extension.Equals(".srt", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        return new DownloadOutcome
        {
            Succeeded = video is not null,
            VideoPath = video?.FullName,
            SubtitlePath = subtitle?.FullName,
            Elapsed = DateTimeOffset.UtcNow - startedAt,
            RawOutput = output,
            Failure = video is null
                ? new FailureDescription
                {
                    Kind = FailureKind.Unknown,
                    Headline = "The download finished but no video appeared",
                    Explanation =
                        "yt-dlp ended without an error, yet there is no new video file in the output folder. " +
                        "Check that the folder is writable and try again.",
                    PrimaryAction = FailureAction.ChooseAnotherFolder,
                    PrimaryActionLabel = "Choose another folder",
                    SecondaryAction = FailureAction.Retry,
                    SecondaryActionLabel = "Try again",
                }
                : null,
        };
    }

    /// <summary>
    /// Turns the line stream into the states the design draws. Kept separate so
    /// the rules about what counts as "started" live in one place.
    /// </summary>
    private sealed class ProgressTracker(IProgress<DownloadProgress> progress)
    {
        private int part;
        private int partCount = 1;
        private bool announced;
        private string destination = string.Empty;

        public void Observe(YtDlpLine line)
        {
            switch (line.Kind)
            {
                case YtDlpLineKind.Info when FormatListPattern().Match(line.Text) is { Success: true } match:
                    // "Downloading 1 format(s): 137+140" — the plus is what makes
                    // this a two-part download, so the bar can say so honestly.
                    partCount = match.Groups["ids"].Value.Split('+').Length;
                    break;

                case YtDlpLineKind.Destination or YtDlpLineKind.AlreadyDownloaded:
                    part++;
                    destination = Path.GetFileName(line.Destination);
                    Announce();
                    break;

                case YtDlpLineKind.Progress:
                    if (part == 0)
                    {
                        part = 1;
                    }

                    Announce();
                    Report(line);
                    break;

                case YtDlpLineKind.Merging:
                    progress.Report(new DownloadProgress
                    {
                        Stage = DownloadStage.PostProcessing,
                        Step = RailStep.Process,
                        Headline = "Merging video and audio",
                        Detail = "Then the subtitle track is converted to .srt. Nothing left to download.",
                        FiguresPlaceholder = "No percentage available for this step",
                    });
                    break;

                case YtDlpLineKind.Subtitle:
                    progress.Report(new DownloadProgress
                    {
                        Stage = DownloadStage.PostProcessing,
                        Step = RailStep.Process,
                        Headline = "Converting the subtitle track",
                        Detail = "Nothing left to download.",
                        FiguresPlaceholder = "No percentage available for this step",
                    });
                    break;
            }
        }

        /// <summary>
        /// The earliest proof yt-dlp got past YouTube's checks and is really
        /// moving bytes. The design gives this its own state.
        /// </summary>
        private void Announce()
        {
            if (announced)
            {
                return;
            }

            announced = true;
            progress.Report(new DownloadProgress
            {
                Stage = DownloadStage.Started,
                Step = RailStep.Connect,
                Headline = "Download started",
                Detail = destination.Length > 0
                    ? $"YouTube accepted the request. Saving as {destination}"
                    : "YouTube accepted the request.",
                FiguresPlaceholder = partCount > 1
                    ? "Two parts to fetch — video, then audio"
                    : "Fetching one file",
            });
        }

        private void Report(YtDlpLine line)
        {
            var percent = line.Percent;
            progress.Report(new DownloadProgress
            {
                Stage = DownloadStage.Downloading,
                Step = RailStep.Download,
                Headline = partCount > 1 && part >= 2 ? "Downloading audio" : "Downloading video",
                Badge = partCount > 1 ? $"part {Math.Min(part, partCount)} of {partCount}" : string.Empty,
                Detail = partCount > 1
                    ? "Video and audio arrive separately, then get merged."
                    : "Fetching the file.",
                Percent = percent,
                Part = part,
                PartCount = partCount,
                PercentText = percent is { } value
                    ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + " %"
                    : string.Empty,
                SizeText = FormatSize(line, percent),
                SpeedText = Readable(line.SpeedText),
                EtaText = FormatEta(line.EtaText),
            });
        }

        private static string FormatSize(YtDlpLine line, double? percent)
        {
            if (line.TotalText.Length == 0)
            {
                return string.Empty;
            }

            var total = Readable(line.TotalText);
            if (total.Length == 0)
            {
                return string.Empty;
            }

            return percent is { } value && TryParseSize(line.TotalText) is { } totalBytes
                ? $"{DownloadFailure.FormatSize((long)(totalBytes * value / 100))} of {DownloadFailure.FormatSize(totalBytes)}"
                : total;
        }

        private static string FormatEta(string eta)
        {
            if (Readable(eta).Length == 0)
            {
                return string.Empty;
            }

            var parts = eta.Split(':');
            if (parts.Length is 2 && int.TryParse(parts[0], out var minutes) && int.TryParse(parts[1], out var seconds))
            {
                if (minutes == 0)
                {
                    return $"{seconds} sec left";
                }

                return $"{minutes} min left";
            }

            return eta + " left";
        }

        private static string Readable(string value) =>
            value.Length == 0 || value.Contains("Unknown", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : value;

        /// <summary>"123.45MiB" to bytes. yt-dlp always uses binary units here.</summary>
        private static long? TryParseSize(string text)
        {
            var match = Regex.Match(text, @"^(?<value>[\d.]+)(?<unit>[KMGT])?i?B$", RegexOptions.IgnoreCase);
            if (!match.Success ||
                !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            var multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
            {
                "K" => 1024d,
                "M" => 1024d * 1024,
                "G" => 1024d * 1024 * 1024,
                "T" => 1024d * 1024 * 1024 * 1024,
                _ => 1d,
            };

            return (long)(value * multiplier);
        }
    }
}

/// <summary>The probe reached yt-dlp, and yt-dlp refused. Carries its words.</summary>
public sealed class ProbeFailedException(string output)
    : Exception("YouTube would not say what this video contains.")
{
    public string Output { get; } = output;
}
