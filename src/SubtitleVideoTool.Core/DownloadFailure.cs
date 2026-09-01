using System.Text.RegularExpressions;

namespace SubtitleVideoTool.Core;

public enum FailureKind
{
    Unknown,
    BotCheck,
    LoginRequired,
    AgeRestricted,
    CookiesLocked,
    CookiesUndecryptable,
    FfmpegMissing,
    PrivateVideo,
    GeoBlocked,
    Unavailable,
    FormatUnavailable,
    Proxy,
    Network,
    DiskFull,
    PermissionDenied,
}

/// <summary>Actions a failure card can offer. The window maps these to buttons.</summary>
public enum FailureAction
{
    None,
    SignInAndRetry,
    UseBrowserCookies,
    Retry,
    ChooseAnotherFolder,
    LocateFfmpeg,
    CheckAgain,
    PickAnotherQuality,
}

/// <summary>
/// Facts the window can supply to make an explanation concrete. Everything is
/// optional: without them the explanation still reads correctly, just generally.
/// </summary>
public sealed record FailureContext
{
    public string? DriveLetter { get; init; }
    public long? FreeBytes { get; init; }
    public long? RequiredBytes { get; init; }
    public string? BrowserName { get; init; }
    public string? FfmpegPath { get; init; }
    public int AttemptCount { get; init; } = 1;
}

/// <summary>
/// The design gives every failure the same four slots: what happened, why, the
/// one action that fixes it, and the raw output folded away. This is those
/// slots, filled.
/// </summary>
public sealed record FailureDescription
{
    public required FailureKind Kind { get; init; }
    public required string Headline { get; init; }
    public required string Explanation { get; init; }
    public FailureAction PrimaryAction { get; init; } = FailureAction.Retry;
    public string PrimaryActionLabel { get; init; } = "Try again";
    public FailureAction SecondaryAction { get; init; } = FailureAction.None;
    public string SecondaryActionLabel { get; init; } = string.Empty;
}

public static partial class DownloadFailure
{
    [GeneratedRegex(@"Failed to decrypt with DPAPI|Could not decrypt.{0,30}cookie|app-bound encryption", RegexOptions.IgnoreCase)]
    private static partial Regex CookiesUndecryptablePattern();

    [GeneratedRegex(@"could not (?:find|copy|open).{0,40}cookie|unsupported browser|cookie database", RegexOptions.IgnoreCase)]
    private static partial Regex CookiesLockedPattern();

    [GeneratedRegex(@"Sign in to confirm you.{0,3}re not a bot", RegexOptions.IgnoreCase)]
    private static partial Regex BotCheckPattern();

    [GeneratedRegex(@"Sign in to confirm your age|age-restricted|inappropriate for some users", RegexOptions.IgnoreCase)]
    private static partial Regex AgeRestrictedPattern();

    [GeneratedRegex(@"LOGIN_REQUIRED|Please sign in|account cookies are no longer valid|only available to", RegexOptions.IgnoreCase)]
    private static partial Regex LoginRequiredPattern();

    [GeneratedRegex(@"ffmpeg is not installed|ffprobe and ffmpeg not found|ffmpeg not found|requested merging of multiple formats but", RegexOptions.IgnoreCase)]
    private static partial Regex FfmpegMissingPattern();

    [GeneratedRegex(@"Private video|members-only|Join this channel", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateVideoPattern();

    [GeneratedRegex(@"available in your country|blocked it in your country|geo.?restrict|not available from your location", RegexOptions.IgnoreCase)]
    private static partial Regex GeoBlockedPattern();

    [GeneratedRegex(@"Video unavailable|This video is (?:no longer|not) available|removed by the uploader", RegexOptions.IgnoreCase)]
    private static partial Regex UnavailablePattern();

    [GeneratedRegex(@"Requested format is not available", RegexOptions.IgnoreCase)]
    private static partial Regex FormatUnavailablePattern();

    [GeneratedRegex(@"ProxyError|Unable to connect to proxy|Tunnel connection failed|SOCKS", RegexOptions.IgnoreCase)]
    private static partial Regex ProxyPattern();

    [GeneratedRegex(@"timed out|Connection refused|Connection reset|getaddrinfo|Name or service not known|Network is unreachable|Remote end closed|Temporary failure in name resolution|SSLError|CERTIFICATE_VERIFY_FAILED", RegexOptions.IgnoreCase)]
    private static partial Regex NetworkPattern();

    [GeneratedRegex(@"No space left|not enough space|Errno 28", RegexOptions.IgnoreCase)]
    private static partial Regex DiskFullPattern();

    [GeneratedRegex(@"Permission denied|Errno 13|Access is denied", RegexOptions.IgnoreCase)]
    private static partial Regex PermissionPattern();

    /// <summary>
    /// Order matters. The cookie patterns run first because a cookie problem
    /// often also prints the bot-check text, and the cookie cause is the one
    /// the user can actually act on.
    /// </summary>
    public static FailureKind Classify(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return FailureKind.Unknown;
        }

        if (CookiesUndecryptablePattern().IsMatch(output)) return FailureKind.CookiesUndecryptable;
        if (CookiesLockedPattern().IsMatch(output)) return FailureKind.CookiesLocked;
        if (BotCheckPattern().IsMatch(output)) return FailureKind.BotCheck;
        if (AgeRestrictedPattern().IsMatch(output)) return FailureKind.AgeRestricted;
        if (LoginRequiredPattern().IsMatch(output)) return FailureKind.LoginRequired;
        if (FfmpegMissingPattern().IsMatch(output)) return FailureKind.FfmpegMissing;
        if (PrivateVideoPattern().IsMatch(output)) return FailureKind.PrivateVideo;
        if (GeoBlockedPattern().IsMatch(output)) return FailureKind.GeoBlocked;
        if (UnavailablePattern().IsMatch(output)) return FailureKind.Unavailable;
        if (FormatUnavailablePattern().IsMatch(output)) return FailureKind.FormatUnavailable;
        if (ProxyPattern().IsMatch(output)) return FailureKind.Proxy;
        if (NetworkPattern().IsMatch(output)) return FailureKind.Network;
        if (DiskFullPattern().IsMatch(output)) return FailureKind.DiskFull;
        if (PermissionPattern().IsMatch(output)) return FailureKind.PermissionDenied;
        return FailureKind.Unknown;
    }

    public static FailureDescription Describe(FailureKind kind, FailureContext? context = null)
    {
        context ??= new FailureContext();

        return kind switch
        {
            FailureKind.BotCheck => new FailureDescription
            {
                Kind = kind,
                Headline = "YouTube asked this download to prove it isn't a robot",
                Explanation =
                    $"Anonymous downloads from this network have been blocked. The app tried {Attempts(context.AttemptCount)} " +
                    "and YouTube declined all of them. This is about the network, not about the video — the video itself is fine.",
                PrimaryAction = FailureAction.SignInAndRetry,
                PrimaryActionLabel = "Sign in to YouTube and retry",
                SecondaryAction = FailureAction.UseBrowserCookies,
                SecondaryActionLabel = "Use a browser I'm signed into",
            },

            FailureKind.LoginRequired => new FailureDescription
            {
                Kind = kind,
                Headline = "YouTube wants you signed in for this video",
                Explanation =
                    "This video is not available to anonymous downloads. Signing in with an account that can watch it " +
                    "will let the app fetch it.",
                PrimaryAction = FailureAction.SignInAndRetry,
                PrimaryActionLabel = "Sign in to YouTube and retry",
                SecondaryAction = FailureAction.UseBrowserCookies,
                SecondaryActionLabel = "Use a browser I'm signed into",
            },

            FailureKind.AgeRestricted => new FailureDescription
            {
                Kind = kind,
                Headline = "This video is age-restricted",
                Explanation =
                    "YouTube only serves it to a signed-in account that meets the age requirement. Signing in will work " +
                    "if your account does.",
                PrimaryAction = FailureAction.SignInAndRetry,
                PrimaryActionLabel = "Sign in to YouTube and retry",
            },

            FailureKind.PrivateVideo => new FailureDescription
            {
                Kind = kind,
                Headline = "This video is private",
                Explanation =
                    "Only the owner and people they've invited can watch it. Signing in will only help if your account " +
                    "is one of them.",
                PrimaryAction = FailureAction.SignInAndRetry,
                PrimaryActionLabel = "Sign in to YouTube and retry",
            },

            FailureKind.CookiesLocked => new FailureDescription
            {
                Kind = kind,
                Headline = "Your browser's cookies are locked",
                Explanation =
                    $"{context.BrowserName ?? "The browser"} is open and won't let another program read its cookie file. " +
                    $"Close {context.BrowserName ?? "it"} completely, then try again.",
                PrimaryAction = FailureAction.Retry,
                PrimaryActionLabel = "Try again",
                SecondaryAction = FailureAction.SignInAndRetry,
                SecondaryActionLabel = "Sign in here instead",
            },

            FailureKind.CookiesUndecryptable => new FailureDescription
            {
                Kind = kind,
                Headline = "Windows wouldn't unlock your browser's cookies",
                Explanation =
                    "Chrome and Edge encrypt their cookie files so other programs can't read them. Signing in through " +
                    "this app avoids the problem entirely.",
                PrimaryAction = FailureAction.SignInAndRetry,
                PrimaryActionLabel = "Sign in to YouTube instead",
            },

            FailureKind.FfmpegMissing => new FailureDescription
            {
                Kind = kind,
                Headline = "FFmpeg won't start",
                Explanation =
                    $"The file {context.FfmpegPath ?? @"tools\ffmpeg.exe"} is there but Windows refuses to run it — " +
                    "usually a blocked download or antivirus quarantine. Video and audio can't be merged until it works.",
                PrimaryAction = FailureAction.LocateFfmpeg,
                PrimaryActionLabel = "Locate FFmpeg…",
                SecondaryAction = FailureAction.CheckAgain,
                SecondaryActionLabel = "Check again",
            },

            FailureKind.GeoBlocked => new FailureDescription
            {
                Kind = kind,
                Headline = "This video isn't available in your region",
                Explanation =
                    "YouTube blocks it for the country your connection appears to be in. A VPN in a country where it is " +
                    "available is the only thing that changes this.",
                PrimaryAction = FailureAction.Retry,
                PrimaryActionLabel = "Try again",
            },

            FailureKind.Unavailable => new FailureDescription
            {
                Kind = kind,
                Headline = "This video is gone",
                Explanation =
                    "YouTube says it has been removed or is no longer available. Nothing about the app or your " +
                    "connection will bring it back.",
                PrimaryAction = FailureAction.None,
            },

            FailureKind.FormatUnavailable => new FailureDescription
            {
                Kind = kind,
                Headline = "That quality isn't available for this video",
                Explanation =
                    "The quality you picked no longer exists on YouTube's side. Choosing another one from the list will work.",
                PrimaryAction = FailureAction.PickAnotherQuality,
                PrimaryActionLabel = "Choose another quality",
            },

            FailureKind.Proxy => new FailureDescription
            {
                Kind = kind,
                Headline = "The proxy refused the connection",
                Explanation =
                    "The app could not reach YouTube through the proxy or VPN this machine is configured to use. " +
                    "Check that it is running, then try again.",
                PrimaryAction = FailureAction.Retry,
                PrimaryActionLabel = "Try again",
            },

            FailureKind.Network => new FailureDescription
            {
                Kind = kind,
                Headline = "Couldn't reach YouTube",
                Explanation =
                    "The connection failed or timed out before the download finished. Check your internet connection, " +
                    "VPN or proxy, then try again.",
                PrimaryAction = FailureAction.Retry,
                PrimaryActionLabel = "Try again",
            },

            FailureKind.DiskFull => new FailureDescription
            {
                Kind = kind,
                Headline = "The disk ran out of space",
                Explanation = DiskFullExplanation(context),
                PrimaryAction = FailureAction.ChooseAnotherFolder,
                PrimaryActionLabel = "Choose another folder",
                SecondaryAction = FailureAction.Retry,
                SecondaryActionLabel = "Try again",
            },

            FailureKind.PermissionDenied => new FailureDescription
            {
                Kind = kind,
                Headline = "Windows wouldn't let the app write there",
                Explanation =
                    "The output folder is read-only, or another program is holding the file open. Choosing a folder " +
                    "inside your own user profile usually solves it.",
                PrimaryAction = FailureAction.ChooseAnotherFolder,
                PrimaryActionLabel = "Choose another folder",
                SecondaryAction = FailureAction.Retry,
                SecondaryActionLabel = "Try again",
            },

            _ => new FailureDescription
            {
                Kind = FailureKind.Unknown,
                Headline = "The download stopped unexpectedly",
                Explanation =
                    "yt-dlp ended with an error the app doesn't recognise. The raw output below says what it reported.",
                PrimaryAction = FailureAction.Retry,
                PrimaryActionLabel = "Try again",
            },
        };
    }

    public static FailureDescription Describe(string? output, FailureContext? context = null) =>
        Describe(Classify(output), context);

    private static string Attempts(int count) =>
        count <= 1 ? "the usual method" : $"{count} different methods";

    private static string DiskFullExplanation(FailureContext context)
    {
        var drive = string.IsNullOrWhiteSpace(context.DriveLetter) ? "The output drive" : $"Drive {context.DriveLetter}";
        if (context is { FreeBytes: > 0, RequiredBytes: > 0 })
        {
            return $"{drive} has {FormatSize(context.FreeBytes.Value)} free and this video needs about " +
                   $"{FormatSize(context.RequiredBytes.Value)}. The partial file has been removed.";
        }

        return $"{drive} filled up before the download finished. The partial file has been removed.";
    }

    /// <summary>Sizes read the way the design writes them: "512 MB", "9 MB".</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit <= 1
            ? $"{Math.Round(value)} {units[unit]}"
            : $"{(value >= 100 ? Math.Round(value) : Math.Round(value, 1))} {units[unit]}";
    }
}
