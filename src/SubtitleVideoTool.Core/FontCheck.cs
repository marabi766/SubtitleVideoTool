using System.Text.RegularExpressions;

namespace SubtitleVideoTool.Core;

public sealed record FontVerdict
{
    /// <summary>The family the user picked.</summary>
    public required string Requested { get; init; }

    /// <summary>The font libass actually chose. Empty when the check could not run.</summary>
    public string Resolved { get; init; } = string.Empty;

    /// <summary>True when libass used the requested font rather than substituting.</summary>
    public bool Matched { get; init; }

    public bool Checked => Resolved.Length > 0;
}

/// <summary>
/// Asks libass which font it would really use.
/// </summary>
/// <remarks>
/// This exists because the substitution is otherwise silent: libass falls back
/// to Arial whenever it cannot resolve a family, and the only sign is that the
/// burnt-in subtitle looks wrong after a long encode. Two causes are common —
/// the font is one libass cannot read at all, and the font has no glyphs for
/// the subtitle's script, which is how a Latin-only font behaves on Persian
/// text. Both produce the same fallback, so both are worth catching up front.
/// </remarks>
public sealed partial class FontCheck(ToolSet tools)
{
    [GeneratedRegex(@"fontselect:\s*\((?<requested>[^,]*),[^)]*\)\s*->\s*(?<resolved>[^,]+)")]
    private static partial Regex FontSelectPattern();

    /// <summary>A line of the script the subtitle will actually be in.</summary>
    public const string PersianSample = "سلام دنیا";

    public const string LatinSample = "Hello world";

    /// <summary>
    /// Renders one throwaway frame with the requested font and reads libass's
    /// own report of what it picked. Takes well under a second.
    /// </summary>
    public async Task<FontVerdict> VerifyAsync(
        string fontName,
        string sampleText,
        CancellationToken cancellationToken = default)
    {
        var verdict = new FontVerdict { Requested = fontName };

        var ffmpeg = tools.PathOf(ToolSet.Ffmpeg);
        if (ffmpeg is null || string.IsNullOrWhiteSpace(fontName))
        {
            return verdict;
        }

        var probeFile = Path.Combine(Path.GetTempPath(), $"svt-fontcheck-{Guid.NewGuid():N}.srt");
        try
        {
            await File.WriteAllTextAsync(
                probeFile,
                $"1{Environment.NewLine}00:00:00,000 --> 00:00:01,000{Environment.NewLine}{sampleText}{Environment.NewLine}",
                new System.Text.UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            var filter =
                $"subtitles='{EscapeForFilter(probeFile)}':force_style='FontName={fontName},FontSize=40'";

            string[] arguments =
            [
                "-y",
                "-loglevel", "debug",
                "-f", "lavfi",
                "-i", "color=c=gray:s=320x120:d=1",
                "-vf", filter,
                "-frames:v", "1",
                "-f", "null",
                "-",
            ];

            var report = new System.Text.StringBuilder();
            await ToolProcess.RunAsync(
                ffmpeg,
                arguments,
                onRawLine: line => report.AppendLine(line),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // The first fontselect line for a run is libass resolving the
            // requested family against the actual subtitle text. Any line
            // after it is libass patching a single missing glyph in that same
            // family — most commonly a plain space, which many display fonts
            // (Peyda included) simply don't ship — and always resolves to a
            // fallback. Taking the last line instead of the first read that
            // harmless per-glyph patch as the family itself having failed.
            var match = FontSelectPattern().Matches(report.ToString()).FirstOrDefault();
            if (match is null)
            {
                return verdict;
            }

            var resolved = match.Groups["resolved"].Value.Trim();
            return verdict with
            {
                Resolved = resolved,
                Matched = LooksLikeSameFamily(fontName, resolved),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A check that cannot run must not block burning.
            return verdict;
        }
        finally
        {
            try
            {
                if (File.Exists(probeFile))
                {
                    File.Delete(probeFile);
                }
            }
            catch (Exception)
            {
                // A stray temp file is not worth surfacing.
            }
        }
    }

    /// <summary>
    /// libass answers with the font's PostScript name — "Segoe UI" comes back
    /// as "SegoeUI", "Times New Roman" as "TimesNewRomanPSMT" — so the
    /// comparison ignores spaces and the usual PostScript suffixes.
    /// </summary>
    public static bool LooksLikeSameFamily(string requested, string resolved)
    {
        static string Normalise(string value) =>
            new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        var wanted = Normalise(requested);
        var got = Normalise(resolved);

        if (wanted.Length == 0 || got.Length == 0)
        {
            return false;
        }

        foreach (var suffix in new[] { "psmt", "mt", "ps", "regular" })
        {
            if (got.EndsWith(suffix, StringComparison.Ordinal) && got.Length > suffix.Length)
            {
                got = got[..^suffix.Length];
            }
        }

        return got.StartsWith(wanted, StringComparison.Ordinal) ||
               wanted.StartsWith(got, StringComparison.Ordinal);
    }

    /// <summary>
    /// A path inside a quoted filter argument: backslashes, colons and quotes
    /// all have to be escaped or the filter is silently mis-parsed.
    /// </summary>
    private static string EscapeForFilter(string path) =>
        path.Replace('\\', '/').Replace("'", @"\'").Replace(":", @"\:");
}
