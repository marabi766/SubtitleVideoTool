using System.Text.RegularExpressions;

namespace SubtitleVideoTool.Core;

/// <summary>Raised when the user's input cannot be used as given.</summary>
public sealed class InputException(string message) : Exception(message);

public static partial class YoutubeUrl
{
    private static readonly string[] YoutubeHosts =
    [
        "youtube.com",
        "m.youtube.com",
        "music.youtube.com",
        "youtu.be",
        "youtube-nocookie.com",
    ];

    private static readonly string[] TrackingParameters =
    [
        "si", "pp", "feature", "ab_channel", "gclid", "fbclid",
    ];

    [GeneratedRegex(@"^\[[^\]]*\]\(\s*(?<url>[^)\s]+)\s*\)$")]
    private static partial Regex MarkdownLinkPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9+.\-]*://", RegexOptions.IgnoreCase)]
    private static partial Regex SchemePattern();

    [GeneratedRegex(@"^(www\.|m\.|music\.)?(youtube\.com|youtu\.be|youtube-nocookie\.com)(/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BareYoutubeHostPattern();

    [GeneratedRegex(@"^(?i)(shorts|live|embed|v)/(?<id>[^/]+)")]
    private static partial Regex PathVideoIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdPattern();

    /// <summary>
    /// Accepts what people actually paste: quoted links, markdown links, links
    /// without a scheme, youtu.be / shorts / live / embed forms, and links
    /// carrying playlist or tracking parameters.
    /// </summary>
    public static string Clean(string? text)
    {
        var cleaned = YtDlpOutput.RemoveInvisibleMarks(text).Trim();
        cleaned = cleaned.Trim('"', '\'', '<', '>', '`').Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            throw new InputException("Paste a YouTube link first.");
        }

        var markdown = MarkdownLinkPattern().Match(cleaned);
        if (markdown.Success)
        {
            cleaned = markdown.Groups["url"].Value;
        }

        if (!SchemePattern().IsMatch(cleaned) && BareYoutubeHostPattern().IsMatch(cleaned))
        {
            cleaned = "https://" + cleaned;
        }

        if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InputException("That does not look like a valid link.");
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        if (!YoutubeHosts.Contains(host))
        {
            // yt-dlp supports far more than YouTube; hand anything else over untouched.
            return uri.AbsoluteUri;
        }

        var query = ParseQuery(uri);
        var path = uri.AbsolutePath.Trim('/');

        var videoId = host switch
        {
            "youtu.be" => path.Split('/')[0],
            _ => PathVideoIdPattern().Match(path) is { Success: true } m
                ? m.Groups["id"].Value
                : query.GetValueOrDefault("v", string.Empty),
        };

        if (VideoIdPattern().IsMatch(videoId))
        {
            var result = "https://www.youtube.com/watch?v=" + videoId;
            foreach (var key in new[] { "t", "start" })
            {
                if (query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    result += "&t=" + Uri.EscapeDataString(value);
                    break;
                }
            }

            return result;
        }

        // Playlist, channel or search links stay as they are; only tracking noise goes.
        var kept = query
            .Where(pair => !TrackingParameters.Contains(pair.Key.ToLowerInvariant()))
            .Where(pair => !pair.Key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase))
            .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))
            .ToArray();

        var rebuilt = uri.GetLeftPart(UriPartial.Path);
        return kept.Length > 0 ? rebuilt + "?" + string.Join("&", kept) : rebuilt;
    }

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (uri.Query.Length <= 1)
        {
            return query;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            if (string.IsNullOrWhiteSpace(pair))
            {
                continue;
            }

            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            query.TryAdd(key, value);
        }

        return query;
    }
}
