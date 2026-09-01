namespace SubtitleVideoTool.Core.Tests;

public class DownloadFailureTests
{
    [Theory]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm you're not a bot", FailureKind.BotCheck)]
    [InlineData("ERROR: Sign in to confirm you are not a bot", FailureKind.BotCheck)]
    [InlineData("ERROR: [youtube] abc: LOGIN_REQUIRED", FailureKind.LoginRequired)]
    [InlineData("ERROR: Sign in to confirm your age", FailureKind.AgeRestricted)]
    [InlineData("ERROR: Failed to decrypt with DPAPI", FailureKind.CookiesUndecryptable)]
    [InlineData("ERROR: could not copy Chrome cookie database", FailureKind.CookiesLocked)]
    [InlineData("ERROR: ffmpeg is not installed", FailureKind.FfmpegMissing)]
    [InlineData("ERROR: Private video. Sign in if you've been granted access", FailureKind.PrivateVideo)]
    [InlineData("ERROR: The uploader has not made this video available in your country", FailureKind.GeoBlocked)]
    [InlineData("ERROR: Video unavailable", FailureKind.Unavailable)]
    [InlineData("ERROR: Requested format is not available", FailureKind.FormatUnavailable)]
    [InlineData("ERROR: unable to download: ProxyError", FailureKind.Proxy)]
    [InlineData("ERROR: [Errno 11001] getaddrinfo failed", FailureKind.Network)]
    [InlineData("OSError: [Errno 28] No space left on device", FailureKind.DiskFull)]
    [InlineData("PermissionError: [Errno 13] Permission denied", FailureKind.PermissionDenied)]
    [InlineData("ERROR: something nobody has seen before", FailureKind.Unknown)]
    [InlineData("", FailureKind.Unknown)]
    [InlineData(null, FailureKind.Unknown)]
    public void EachKnownFailureIsRecognised(string? output, FailureKind expected)
    {
        Assert.Equal(expected, DownloadFailure.Classify(output));
    }

    [Fact]
    public void ACookieProblemWinsOverTheBotCheckTextItAlsoPrints()
    {
        // yt-dlp prints both; the cookie cause is the one the user can act on.
        const string output = """
            WARNING: Failed to decrypt with DPAPI
            ERROR: Sign in to confirm you're not a bot
            """;

        Assert.Equal(FailureKind.CookiesUndecryptable, DownloadFailure.Classify(output));
    }

    [Fact]
    public void EveryKindFillsAllFourSlots()
    {
        foreach (var kind in Enum.GetValues<FailureKind>())
        {
            var description = DownloadFailure.Describe(kind);

            Assert.False(string.IsNullOrWhiteSpace(description.Headline), $"{kind} has no headline");
            Assert.False(string.IsNullOrWhiteSpace(description.Explanation), $"{kind} has no explanation");

            // A card that offers an action must label it, and one that labels an
            // action must have an action to run.
            if (description.PrimaryAction != FailureAction.None)
            {
                Assert.False(string.IsNullOrWhiteSpace(description.PrimaryActionLabel), $"{kind} has an unlabelled action");
            }

            if (!string.IsNullOrWhiteSpace(description.SecondaryActionLabel))
            {
                Assert.NotEqual(FailureAction.None, description.SecondaryAction);
            }
        }
    }

    [Fact]
    public void NoHeadlineBlamesTheUserOrLeaksToolJargon()
    {
        string[] forbidden = ["yt-dlp", "ffmpeg.exe", "exit code", "stderr", "Errno", "traceback"];

        foreach (var kind in Enum.GetValues<FailureKind>())
        {
            var headline = DownloadFailure.Describe(kind).Headline;

            foreach (var word in forbidden)
            {
                Assert.DoesNotContain(word, headline, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void TheBotCheckCardSaysItIsTheNetworkNotTheVideo()
    {
        var description = DownloadFailure.Describe("ERROR: Sign in to confirm you're not a bot", new FailureContext { AttemptCount = 3 });

        Assert.Equal("YouTube asked this download to prove it isn't a robot", description.Headline);
        Assert.Contains("3 different methods", description.Explanation);
        Assert.Contains("about the network, not about the video", description.Explanation);
        Assert.Equal(FailureAction.SignInAndRetry, description.PrimaryAction);
        Assert.Equal(FailureAction.UseBrowserCookies, description.SecondaryAction);
    }

    [Fact]
    public void TheDiskFullCardUsesRealNumbersWhenItHasThem()
    {
        var description = DownloadFailure.Describe(FailureKind.DiskFull, new FailureContext
        {
            DriveLetter = "C:",
            FreeBytes = 240L * 1024 * 1024,
            RequiredBytes = 512L * 1024 * 1024,
        });

        Assert.Equal("The disk ran out of space", description.Headline);
        Assert.Contains("Drive C: has 240 MB free", description.Explanation);
        Assert.Contains("needs about 512 MB", description.Explanation);
        Assert.Contains("partial file has been removed", description.Explanation);
    }

    [Fact]
    public void TheDiskFullCardStillReadsWithoutNumbers()
    {
        var description = DownloadFailure.Describe(FailureKind.DiskFull);

        Assert.DoesNotContain("MB free", description.Explanation);
        Assert.Contains("filled up", description.Explanation);
    }

    [Fact]
    public void TheLockedCookieCardNamesTheBrowserThatIsHoldingTheFile()
    {
        var description = DownloadFailure.Describe(FailureKind.CookiesLocked, new FailureContext { BrowserName = "Chrome" });

        Assert.Contains("Chrome is open", description.Explanation);
        Assert.Contains("Close Chrome completely", description.Explanation);
    }

    [Fact]
    public void AGoneVideoOffersNoActionBecauseNothingWouldHelp()
    {
        var description = DownloadFailure.Describe(FailureKind.Unavailable);

        Assert.Equal(FailureAction.None, description.PrimaryAction);
        Assert.Equal(FailureAction.None, description.SecondaryAction);
    }
}
