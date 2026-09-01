using System.Globalization;
using System.Text.RegularExpressions;

namespace SubtitleVideoTool.Core;

public enum YtDlpLineKind
{
    Empty,
    Destination,
    AlreadyDownloaded,
    Progress,
    Merging,
    Subtitle,
    PostProcess,
    Info,
    Diagnostic,
    Other,
}

/// <summary>One classified line of yt-dlp output.</summary>
public sealed record YtDlpLine
{
    public YtDlpLineKind Kind { get; init; } = YtDlpLineKind.Other;
    public double? Percent { get; init; }
    public string TotalText { get; init; } = string.Empty;
    public string SpeedText { get; init; } = string.Empty;
    public string EtaText { get; init; } = string.Empty;
    public string Fragment { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// The kinds that prove yt-dlp got past YouTube's checks and is moving
    /// bytes. Anything before this is still negotiation.
    /// </summary>
    public bool IsDownloadStart =>
        Kind is YtDlpLineKind.Progress or YtDlpLineKind.Destination or YtDlpLineKind.AlreadyDownloaded;
}

public static partial class YtDlpOutput
{
    // yt-dlp runs with --newline, so each progress update arrives as its own
    // complete line instead of a carriage-return repaint. That is what makes
    // live parsing possible at all.
    [GeneratedRegex(@"^\[download\]\s+Destination:\s*(?<path>.+?)\s*$")]
    private static partial Regex DestinationPattern();

    [GeneratedRegex(@"^\[download\]\s+(?<path>.+?)\s+has already been downloaded\s*$")]
    private static partial Regex AlreadyDownloadedPattern();

    [GeneratedRegex(@"^\[download\]\s+(?<pct>\d{1,3}(?:\.\d+)?)%")]
    private static partial Regex PercentPattern();

    // "of ~ 123.45MiB" appears when the size is only an estimate.
    [GeneratedRegex(@"\bof\s+~?\s*(?<total>[\d.]+\s*(?:[KMGTP]i?)?B)")]
    private static partial Regex TotalPattern();

    [GeneratedRegex(@"\bat\s+(?<speed>[\d.]+\s*(?:[KMGTP]i?)?B/s|Unknown\s*B/s)")]
    private static partial Regex SpeedPattern();

    [GeneratedRegex(@"\bETA\s+(?<eta>[\d:]+|Unknown)")]
    private static partial Regex EtaPattern();

    [GeneratedRegex(@"\(frag\s+(?<frag>\d+/\d+)\)")]
    private static partial Regex FragmentPattern();

    [GeneratedRegex(@"^\[Merger\]")]
    private static partial Regex MergerPattern();

    [GeneratedRegex(@"^\[SubtitlesConvertor\]|^\[info\].*subtitle", RegexOptions.IgnoreCase)]
    private static partial Regex SubtitlePattern();

    [GeneratedRegex(@"^\[(ExtractAudio|VideoConvertor|VideoRemuxer|Metadata|FixupM3u8|FixupM4a|EmbedSubtitle)\]")]
    private static partial Regex PostProcessPattern();

    [GeneratedRegex(@"^\[info\]\s")]
    private static partial Regex InfoPattern();

    [GeneratedRegex(@"^(ERROR|WARNING):", RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticPattern();

    [GeneratedRegex(@"[​-‏‪-‮⁦-⁩﻿]")]
    private static partial Regex InvisibleMarkPattern();

    public static string RemoveInvisibleMarks(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : InvisibleMarkPattern().Replace(text, string.Empty);

    public static YtDlpLine Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return new YtDlpLine { Kind = YtDlpLineKind.Empty, Text = line ?? string.Empty };
        }

        var text = RemoveInvisibleMarks(line).Trim();

        var destination = DestinationPattern().Match(text);
        if (destination.Success)
        {
            return new YtDlpLine
            {
                Kind = YtDlpLineKind.Destination,
                Destination = destination.Groups["path"].Value,
                Text = line,
            };
        }

        var already = AlreadyDownloadedPattern().Match(text);
        if (already.Success)
        {
            return new YtDlpLine
            {
                Kind = YtDlpLineKind.AlreadyDownloaded,
                Destination = already.Groups["path"].Value,
                Text = line,
            };
        }

        var percent = PercentPattern().Match(text);
        if (percent.Success)
        {
            double? value = null;
            if (double.TryParse(percent.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                value = Math.Clamp(parsed, 0, 100);
            }

            var total = TotalPattern().Match(text);
            var speed = SpeedPattern().Match(text);
            var eta = EtaPattern().Match(text);
            var fragment = FragmentPattern().Match(text);

            return new YtDlpLine
            {
                Kind = YtDlpLineKind.Progress,
                Percent = value,
                TotalText = total.Success ? Compact(total.Groups["total"].Value) : string.Empty,
                SpeedText = speed.Success ? Compact(speed.Groups["speed"].Value) : string.Empty,
                EtaText = eta.Success ? eta.Groups["eta"].Value : string.Empty,
                Fragment = fragment.Success ? fragment.Groups["frag"].Value : string.Empty,
                Text = line,
            };
        }

        var kind =
            MergerPattern().IsMatch(text) ? YtDlpLineKind.Merging
            : SubtitlePattern().IsMatch(text) ? YtDlpLineKind.Subtitle
            : PostProcessPattern().IsMatch(text) ? YtDlpLineKind.PostProcess
            : InfoPattern().IsMatch(text) ? YtDlpLineKind.Info
            : DiagnosticPattern().IsMatch(text) ? YtDlpLineKind.Diagnostic
            : YtDlpLineKind.Other;

        return new YtDlpLine { Kind = kind, Text = line };
    }

    private static string Compact(string value) => Regex.Replace(value, @"\s+", string.Empty);
}
