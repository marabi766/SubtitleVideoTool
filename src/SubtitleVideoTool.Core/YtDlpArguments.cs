namespace SubtitleVideoTool.Core;

public enum CookieSource
{
    /// <summary>"Try without signing in" — works for most public videos.</summary>
    None,

    /// <summary>"Sign in to YouTube" — a Firefox profile owned by this app alone.</summary>
    AppSignIn,

    Firefox,
    Edge,
    Chrome,
    Brave,

    /// <summary>A Netscape-format cookies.txt the user exported.</summary>
    File,
}

public sealed record DownloadRequest
{
    public required string Url { get; init; }
    public required string OutputFolder { get; init; }

    /// <summary>Null means "Best available".</summary>
    public int? Height { get; init; }

    public bool DownloadEnglishSubtitles { get; init; } = true;

    /// <summary>
    /// The one track key the probe settled on, e.g. "en". Empty falls back to
    /// <see cref="YtDlpArguments.SubtitleLanguages"/>.
    /// </summary>
    public string? SubtitleLanguage { get; init; }
    public CookieSource CookieSource { get; init; } = CookieSource.None;
    public string? CookieFilePath { get; init; }
    public string? SignInProfileFolder { get; init; }

    /// <summary>Set only on a fallback attempt, e.g. "tv_simply,web_embedded".</summary>
    public string? PlayerClients { get; init; }
}

public static class YtDlpArguments
{
    /// <summary>
    /// Player clients tried in order when the default is answered with a bot
    /// check. Kept as data so a future YouTube change is a one-line edit.
    /// </summary>
    public static readonly string[] FallbackPlayerClients =
    [
        "tv_simply,web_embedded",
        "android_vr,web_safari",
    ];

    public const string OutputTemplate = "%(title).180B [%(id)s].%(ext)s";

    /// <summary>
    /// Used only when the probe could not name a track. Deliberately just "en":
    /// the pattern "en.*" also matches YouTube's "English from Japanese"
    /// round trips, and asking for those fetches a dozen near-identical files
    /// and invites a rate limit that takes the video down with it.
    /// </summary>
    public const string SubtitleLanguages = "en";

    /// <summary>
    /// H.264 + AAC is tried first at capped heights so that
    /// --merge-output-format mp4 really produces a playable MP4 instead of
    /// silently falling back to Matroska. The generic selectors stay as
    /// fallbacks so a video without an AVC rendition still downloads.
    /// </summary>
    public static string FormatSelector(int? height) => height is not > 0
        ? "bv*+ba/b"
        : $"bv*[height<={height}][vcodec^=avc1]+ba[acodec^=mp4a]/bv*[height<={height}]+ba/b[height<={height}]/b";

    /// <summary>Arguments for the 6-8 second metadata probe. Downloads nothing.</summary>
    public static List<string> ForProbe(DownloadRequest request, ToolSet tools)
    {
        List<string> arguments =
        [
            "--ignore-config",
            "--no-warnings",
            "--no-playlist",
            "--skip-download",
            "-J",
        ];

        AddJsRuntime(arguments, tools);
        AddCookies(arguments, request);
        AddPlayerClients(arguments, request);
        arguments.Add(request.Url);
        return arguments;
    }

    public static List<string> ForDownload(DownloadRequest request, ToolSet tools)
    {
        // --ignore-config keeps a stray %APPDATA%\yt-dlp\config from changing
        // what the window says it is going to do.
        List<string> arguments =
        [
            "--ignore-config",
            "--verbose",
            "--newline",
            "--no-playlist",
            "--windows-filenames",
        ];

        if (tools.PathOf(ToolSet.Ffmpeg) is { } ffmpeg &&
            Path.GetDirectoryName(ffmpeg) is { Length: > 0 } ffmpegFolder)
        {
            arguments.AddRange(["--ffmpeg-location", ffmpegFolder]);
        }

        AddJsRuntime(arguments, tools);

        arguments.AddRange(
        [
            "--retries", "10",
            "--fragment-retries", "10",
            "--retry-sleep", "2",
            "-f", FormatSelector(request.Height),
            "--merge-output-format", "mp4",
        ]);

        if (request.DownloadEnglishSubtitles)
        {
            arguments.AddRange(
            [
                "--write-subs",
                "--write-auto-subs",
                "--sub-langs", SubtitleLanguageOf(request),
                "--sub-format", "srt/best",
                "--convert-subs", "srt",
            ]);
        }

        arguments.AddRange(["--paths", request.OutputFolder, "-o", OutputTemplate]);

        AddCookies(arguments, request);

        AddPlayerClients(arguments, request);

        arguments.Add(request.Url);
        return arguments;
    }

    /// <summary>The exact track the probe found, or "en" when it found none.</summary>
    public static string SubtitleLanguageOf(DownloadRequest request) =>
        string.IsNullOrWhiteSpace(request.SubtitleLanguage)
            ? SubtitleLanguages
            : request.SubtitleLanguage;

    private static void AddPlayerClients(List<string> arguments, DownloadRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.PlayerClients))
        {
            arguments.AddRange(["--extractor-args", "youtube:player_client=" + request.PlayerClients]);
        }
    }

    private static void AddJsRuntime(List<string> arguments, ToolSet tools)
    {
        // Deno solves the JavaScript challenges YouTube now serves.
        if (tools.PathOf(ToolSet.Deno) is { Length: > 0 } deno)
        {
            arguments.AddRange(["--js-runtimes", "deno:" + deno]);
        }
    }

    private static void AddCookies(List<string> arguments, DownloadRequest request)
    {
        switch (request.CookieSource)
        {
            case CookieSource.File when !string.IsNullOrWhiteSpace(request.CookieFilePath):
                arguments.AddRange(["--cookies", request.CookieFilePath]);
                break;

            case CookieSource.AppSignIn when !string.IsNullOrWhiteSpace(request.SignInProfileFolder):
                // Firefox is the only browser whose cookie store yt-dlp can read
                // from an arbitrary profile folder on Windows; Chromium profiles
                // are sealed with DPAPI plus app-bound encryption.
                arguments.AddRange(["--cookies-from-browser", "firefox:" + request.SignInProfileFolder]);
                break;

            case CookieSource.Firefox:
            case CookieSource.Edge:
            case CookieSource.Chrome:
            case CookieSource.Brave:
                arguments.AddRange(["--cookies-from-browser", BrowserKey(request.CookieSource)]);
                break;
        }
    }

    public static string BrowserKey(CookieSource source) => source switch
    {
        CookieSource.Firefox => "firefox",
        CookieSource.Edge => "edge",
        CookieSource.Chrome => "chrome",
        CookieSource.Brave => "brave",
        _ => string.Empty,
    };

    /// <summary>The browser's own name, for a failure card that has to say it.</summary>
    public static string BrowserDisplayName(CookieSource source) => source switch
    {
        CookieSource.Firefox => "Firefox",
        CookieSource.Edge => "Microsoft Edge",
        CookieSource.Chrome => "Chrome",
        CookieSource.Brave => "Brave",
        _ => "The browser",
    };
}
