namespace SubtitleVideoTool.Core.Tests;

public class YoutubeUrlTests
{
    private const string Canonical = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    [InlineData("www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("youtu.be/dQw4w9WgXcQ")]
    public void EveryShapeOfLinkReachesTheSameVideo(string input)
    {
        Assert.Equal(Canonical, YoutubeUrl.Clean(input));
    }

    [Theory]
    [InlineData("  https://youtu.be/dQw4w9WgXcQ  ")]
    [InlineData("\"https://youtu.be/dQw4w9WgXcQ\"")]
    [InlineData("'https://youtu.be/dQw4w9WgXcQ'")]
    [InlineData("<https://youtu.be/dQw4w9WgXcQ>")]
    [InlineData("[watch this](https://youtu.be/dQw4w9WgXcQ)")]
    public void TheWrappingPeoplePasteIsStripped(string input)
    {
        Assert.Equal(Canonical, YoutubeUrl.Clean(input));
    }

    [Theory]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=AbCdEf")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&ab_channel=Rick")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&utm_source=newsletter")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL1234567890&index=3")]
    public void TrackingAndPlaylistNoiseIsDropped(string input)
    {
        Assert.Equal(Canonical, YoutubeUrl.Clean(input));
    }

    [Theory]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=42", Canonical + "&t=42")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=1h2m", Canonical + "&t=1h2m")]
    public void AStartTimeIsKeptBecauseItChangesWhatYouGet(string input, string expected)
    {
        Assert.Equal(expected, YoutubeUrl.Clean(input));
    }

    [Fact]
    public void APlaylistLinkKeepsItsPlaylist()
    {
        var cleaned = YoutubeUrl.Clean("https://www.youtube.com/playlist?list=PL1234567890&si=xyz");

        Assert.Contains("list=PL1234567890", cleaned);
        Assert.DoesNotContain("si=", cleaned);
    }

    [Fact]
    public void ANonYoutubeLinkIsHandedOverUntouched()
    {
        // yt-dlp supports far more than YouTube.
        const string vimeo = "https://vimeo.com/123456789";

        Assert.Equal(vimeo, YoutubeUrl.Clean(vimeo));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptyBoxAsksForALink(string? input)
    {
        var error = Assert.Throws<InputException>(() => YoutubeUrl.Clean(input));

        Assert.Equal("Paste a YouTube link first.", error.Message);
    }

    [Theory]
    [InlineData("not a link at all")]
    [InlineData("ftp://example.com/video.mp4")]
    [InlineData("file:///C:/Windows/System32")]
    public void SomethingThatIsNotAWebLinkIsRejected(string input)
    {
        Assert.Throws<InputException>(() => YoutubeUrl.Clean(input));
    }

    [Fact]
    public void InvisibleDirectionMarksFromCopyPasteAreRemoved()
    {
        Assert.Equal(Canonical, YoutubeUrl.Clean("\u202ahttps://youtu.be/dQw4w9WgXcQ\u202c"));
    }
}
