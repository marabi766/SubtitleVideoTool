namespace SubtitleVideoTool.Core.Tests;

/// <summary>
/// The cases a full pass over the running app turned up. Each one is a thing
/// the window actually did wrong in front of a user, not a hypothetical.
/// </summary>
public class AuditRegressionTests
{
    // ------------------------------------------------- the quality dropdown

    [Fact]
    public void TheAutomaticQualityEntryIsNamedRatherThanCalledZeroP()
    {
        // Height 0 is the "let yt-dlp choose" row at the top of the list. It
        // read "0p" in the dropdown, which is not a quality anybody can pick.
        Assert.Equal("Best available", new QualityOption { Height = 0 }.Label);
    }

    [Theory]
    [InlineData(720, null, "720p")]
    [InlineData(1080, 30, "1080p")]
    [InlineData(1080, 60, "1080p 60 fps")]
    public void RealHeightsStillReadAsBefore(int height, int? fps, string expected)
    {
        Assert.Equal(expected, new QualityOption { Height = height, Fps = fps }.Label);
    }

    // ------------------------------------------------ the subtitle language

    [Theory]
    [InlineData("en", 0)]
    [InlineData("en-US", 1)]
    [InlineData("en-GB", 1)]
    [InlineData("en-419", 1)]
    [InlineData("en-orig", 2)]
    public void EnglishTracksAreRankedBestFirst(string code, int expected)
    {
        Assert.Equal(expected, VideoProbe.RankEnglishCode(code));
    }

    [Theory]
    [InlineData("en-ja")]
    [InlineData("en-de-DE")]
    [InlineData("en-pt-BR")]
    [InlineData("en-es-419")]
    public void RoundTripTranslationsRankBelowEveryRealEnglishTrack(string code)
    {
        // "English from Japanese" is the English track translated out and back.
        // Asking for these is what fetched a dozen files and drew a 429 that
        // took the video download down with it.
        Assert.Equal(3, VideoProbe.RankEnglishCode(code));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ja")]
    [InlineData("english")]
    public void NonEnglishCodesAreNotOffered(string code)
    {
        Assert.Equal(int.MaxValue, VideoProbe.RankEnglishCode(code));
    }

    [Fact]
    public void TheProbeNamesTheOneTrackItFound()
    {
        var info = VideoProbe.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "TestData", "probe-rickroll.json")));

        Assert.Equal(EnglishSubtitleKind.HumanWritten, info.Subtitle.Kind);
        Assert.Equal("en", info.Subtitle.LanguageCode);
    }

    [Fact]
    public void TheDownloadAsksForThatOneTrackAndNoPattern()
    {
        var request = Request() with { SubtitleLanguage = "en-GB" };

        Assert.Equal("en-GB", YtDlpArguments.SubtitleLanguageOf(request));
        Assert.Equal(["--sub-langs", "en-GB"], Pair(request, "--sub-langs"));
    }

    [Fact]
    public void WithoutAProbeItAsksForPlainEnglishRatherThanEveryEnglishLikeKey()
    {
        Assert.Equal("en", YtDlpArguments.SubtitleLanguageOf(Request()));
        Assert.DoesNotContain("en.*", YtDlpArguments.SubtitleLanguages);
    }

    // ---------------------------------------------------- the probe retries

    [Fact]
    public void TheProbeCarriesThePlayerClientItWasAskedFor()
    {
        // The download recovers from a bot check by asking again as a different
        // player. The probe took the same list but dropped it on the floor, so
        // the window dead-ended on links the download itself could manage.
        var arguments = YtDlpArguments.ForProbe(
            Request() with { PlayerClients = "tv_simply,web_embedded" },
            Tools());

        Assert.Equal(["--extractor-args", "youtube:player_client=tv_simply,web_embedded"],
            Pair(arguments, "--extractor-args"));
    }

    [Fact]
    public void APlainProbeStillPassesNoExtractorArguments()
    {
        Assert.DoesNotContain("--extractor-args", YtDlpArguments.ForProbe(Request(), Tools()));
    }

    // ------------------------------------------------------------- helpers

    private static DownloadRequest Request() => new()
    {
        Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
        OutputFolder = Path.GetTempPath(),
    };

    /// <summary>A tool set pointed at nothing, so no bundled path leaks in.</summary>
    private static ToolSet Tools() => new(Path.Combine(Path.GetTempPath(), "svt-tests-no-tools"));

    private static string[] Pair(DownloadRequest request, string flag) =>
        Pair(YtDlpArguments.ForDownload(request, Tools()), flag);

    private static string[] Pair(IReadOnlyList<string> arguments, string flag)
    {
        var at = arguments.ToList().IndexOf(flag);
        return at < 0 || at + 1 >= arguments.Count ? [] : [arguments[at], arguments[at + 1]];
    }
}

/// <summary>
/// Compressing to a size the user chose. The tab promised a limit and then
/// went past it, because the sound was budgeted before the picture.
/// </summary>
public class CompressionBudgetTests
{
    private static readonly TimeSpan Rickroll = TimeSpan.FromSeconds(213);

    [Fact]
    public void ATightLimitBuysCheaperAudioRatherThanBlowingTheBudget()
    {
        // 4 MB over 3.5 minutes is about 151 kbps all in. Spending 128 of it on
        // sound left 23 for the picture, which the old floor then raised back to
        // 200 — and the result came out at 8.4 MB against a 4 MB limit.
        var audio = MediaSession.AudioBitrateKbps(4L * 1024 * 1024, Rickroll);

        // 48 is the floor: below it the sound is not worth keeping, so a budget
        // too small even for a quarter of that still gets 48 and the picture
        // takes the squeeze. What must not happen is the old fixed 128.
        Assert.Equal(48, audio);
        Assert.True(audio < 128);
    }

    [Fact]
    public void AComfortableLimitStillGetsFullQualityAudio()
    {
        Assert.Equal(128, MediaSession.AudioBitrateKbps(120L * 1024 * 1024, Rickroll));
    }

    [Fact]
    public void NoLimitAtAllGetsFullQualityAudio()
    {
        Assert.Equal(128, MediaSession.AudioBitrateKbps(null, Rickroll));
    }

    [Fact]
    public void TheTwoStreamsTogetherFitInsideTheLimit()
    {
        const long limit = 4L * 1024 * 1024;

        var audio = MediaSession.AudioBitrateKbps(limit, Rickroll);
        var video = MediaSession.VideoBitrateKbps(limit, Rickroll, audio);
        var predicted = (long)((video + audio) * 1000 / 8.0 * Rickroll.TotalSeconds);

        Assert.True(predicted <= limit, $"predicted {predicted} bytes against a {limit} byte limit");
    }

    [Theory]
    [InlineData(200, 1080, 480)]
    [InlineData(300, 1080, 720)]
    [InlineData(300, 480, null)]
    [InlineData(900, 1080, null)]
    public void ResolutionDropsOnlyWhenTheBitrateCannotCarryTheFrame(int videoKbps, int height, int? expected)
    {
        Assert.Equal(expected, MediaSession.TargetHeight(videoKbps, height));
    }

    [Fact]
    public void TheSmallestReachableSizeIsWhatTheWarningIsMeasuredAgainst()
    {
        var smallest = MediaSession.SmallestReachableBytes(Rickroll);

        // Under this the picture stops being worth watching, so the tab says so
        // rather than encoding for minutes and missing anyway.
        Assert.InRange(smallest, 3_500_000, 4_500_000);
        Assert.Equal(0, MediaSession.SmallestReachableBytes(TimeSpan.Zero));
    }
}
