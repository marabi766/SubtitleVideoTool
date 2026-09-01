namespace SubtitleVideoTool.Core.Tests;

public class YtDlpOutputTests
{
    [Fact]
    public void ReadsADestinationLine()
    {
        var line = YtDlpOutput.Parse(@"[download] Destination: D:\Videos\Sample Title [dQw4w9WgXcQ].f137.mp4");

        Assert.Equal(YtDlpLineKind.Destination, line.Kind);
        Assert.Equal(@"D:\Videos\Sample Title [dQw4w9WgXcQ].f137.mp4", line.Destination);
        Assert.True(line.IsDownloadStart);
    }

    [Fact]
    public void ReadsAnAlreadyDownloadedLine()
    {
        var line = YtDlpOutput.Parse(@"[download] D:\Videos\Sample.mp4 has already been downloaded");

        Assert.Equal(YtDlpLineKind.AlreadyDownloaded, line.Kind);
        Assert.True(line.IsDownloadStart);
    }

    [Fact]
    public void ReadsTheOpeningProgressLineWithUnknownFigures()
    {
        var line = YtDlpOutput.Parse("[download]   0.0% of  123.45MiB at  Unknown B/s ETA Unknown");

        Assert.Equal(YtDlpLineKind.Progress, line.Kind);
        Assert.Equal(0d, line.Percent);
        Assert.Equal("123.45MiB", line.TotalText);
        Assert.Equal("UnknownB/s", line.SpeedText);
        Assert.Equal("Unknown", line.EtaText);
    }

    [Fact]
    public void ReadsAMidDownloadLineIncludingFragmentAndEstimate()
    {
        var line = YtDlpOutput.Parse("[download]  42.3% of ~ 500.00MiB at    1.23MiB/s ETA 05:09 (frag 12/100)");

        Assert.Equal(42.3d, line.Percent);
        Assert.Equal("500.00MiB", line.TotalText);
        Assert.Equal("1.23MiB/s", line.SpeedText);
        Assert.Equal("05:09", line.EtaText);
        Assert.Equal("12/100", line.Fragment);
    }

    [Fact]
    public void ReadsTheClosingLine()
    {
        var line = YtDlpOutput.Parse("[download] 100% of  123.45MiB in 00:12");

        Assert.Equal(100d, line.Percent);
    }

    [Theory]
    [InlineData(@"[Merger] Merging formats into ""D:\Videos\Sample.mp4""", YtDlpLineKind.Merging)]
    [InlineData("[SubtitlesConvertor] Converting subtitles", YtDlpLineKind.Subtitle)]
    [InlineData("[ExtractAudio] Destination: a.m4a", YtDlpLineKind.PostProcess)]
    [InlineData("[info] Downloading 1 format(s): 137+140", YtDlpLineKind.Info)]
    [InlineData("ERROR: [youtube] abc: Video unavailable", YtDlpLineKind.Diagnostic)]
    [InlineData("WARNING: something", YtDlpLineKind.Diagnostic)]
    [InlineData("random text", YtDlpLineKind.Other)]
    [InlineData("   ", YtDlpLineKind.Empty)]
    public void ClassifiesTheOtherLineShapes(string text, YtDlpLineKind expected)
    {
        Assert.Equal(expected, YtDlpOutput.Parse(text).Kind);
    }

    [Fact]
    public void ADebugLineIsNeverMistakenForProgress()
    {
        // Otherwise the bar would jump on text that has nothing to do with the download.
        var line = YtDlpOutput.Parse("[debug] 100% something");

        Assert.Equal(YtDlpLineKind.Other, line.Kind);
        Assert.Null(line.Percent);
    }

    [Theory]
    [InlineData(YtDlpLineKind.Progress, true)]
    [InlineData(YtDlpLineKind.Destination, true)]
    [InlineData(YtDlpLineKind.AlreadyDownloaded, true)]
    [InlineData(YtDlpLineKind.Info, false)]
    [InlineData(YtDlpLineKind.Merging, false)]
    [InlineData(YtDlpLineKind.Other, false)]
    public void OnlyRealTransferCountsAsTheDownloadHavingStarted(YtDlpLineKind kind, bool expected)
    {
        Assert.Equal(expected, new YtDlpLine { Kind = kind }.IsDownloadStart);
    }

    [Fact]
    public void PercentagesAreClampedToTheBarsRange()
    {
        Assert.Equal(100d, YtDlpOutput.Parse("[download] 105% of 1.00MiB").Percent);
    }

    [Fact]
    public void InvisibleMarksArePulledOutBeforeParsing()
    {
        var line = YtDlpOutput.Parse("\u200e[download]  50.0% of  10.00MiB");

        Assert.Equal(YtDlpLineKind.Progress, line.Kind);
        Assert.Equal(50d, line.Percent);
    }

    [Fact]
    public void TheOriginalTextIsPreservedForTheLog()
    {
        const string raw = "[download]  42.3% of ~ 500.00MiB at    1.23MiB/s ETA 05:09";

        Assert.Equal(raw, YtDlpOutput.Parse(raw).Text);
    }
}
