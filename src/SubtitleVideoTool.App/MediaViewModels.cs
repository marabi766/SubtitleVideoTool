using System.IO;
using System.Windows.Media;
using SubtitleVideoTool.Core;

namespace SubtitleVideoTool.App;

/// <summary>Shared plumbing for the two FFmpeg tabs.</summary>
public abstract class MediaViewModelBase(MainViewModel shell, ToolSet tools) : ObservableObject
{
    private string videoPath = string.Empty;
    private string outputPath = string.Empty;
    private MediaOutcome? outcome;
    private FailureDescription? failure;

    protected MainViewModel Shell { get; } = shell;

    protected MediaSession Session { get; } = new(tools);

    protected ToolSet Tools { get; } = tools;

    public string VideoPath
    {
        get => videoPath;
        set
        {
            if (!Set(ref videoPath, value))
            {
                return;
            }

            if (outputPath.Length == 0 && value.Length > 0)
            {
                OutputPath = SuggestOutput(value);
            }

            OnVideoChanged();
            RaiseCanStart();
        }
    }

    public string OutputPath
    {
        get => outputPath;
        set
        {
            if (Set(ref outputPath, value))
            {
                RaiseCanStart();
            }
        }
    }

    public MediaOutcome? Outcome
    {
        get => outcome;
        protected set
        {
            if (Set(ref outcome, value))
            {
                RaiseAll(nameof(HasSucceeded), nameof(ResultText));
            }
        }
    }

    public FailureDescription? Failure
    {
        get => failure;
        protected set => Set(ref failure, value);
    }

    public bool HasSucceeded => Outcome is { Succeeded: true };

    /// <summary>
    /// An overshoot says by how much. "A little over" was the wording whether
    /// the result missed by a percent or by double, which is not something the
    /// user should have to check for themselves.
    /// </summary>
    public string ResultText => Outcome is { Succeeded: true } result
        ? result is { OvershotLimit: true, SizeLimitBytes: > 0 and var limit }
            ? $"Done — {DownloadFailure.FormatSize(result.OutputBytes)}, over the " +
              $"{DownloadFailure.FormatSize(limit)} limit you set. This video can't be made " +
              "smaller without dropping the picture below what is worth watching."
            : $"Done — {DownloadFailure.FormatSize(result.OutputBytes)}."
        : string.Empty;

    public abstract void OnToolsChanged();

    protected virtual void OnVideoChanged()
    {
    }

    protected abstract void RaiseCanStart();

    protected abstract string SuggestOutput(string source);

    protected string? PickVideo()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a video",
            Filter = "Video files|*.mp4;*.mkv;*.webm;*.mov;*.m4v;*.avi|All files|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    protected string? PickOutput(string suggested)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save as",
            Filter = "MP4 video|*.mp4|Matroska video|*.mkv|All files|*.*",
            FileName = Path.GetFileName(suggested),
            InitialDirectory = Path.GetDirectoryName(suggested) ?? string.Empty,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>Wraps a run with the shell's busy state, progress and cancellation.</summary>
    protected async Task RunAsync(Func<IProgress<double>, CancellationToken, Task<MediaOutcome>> work, string startedMessage)
    {
        Failure = null;
        Outcome = null;

        using var cancellation = new CancellationTokenSource();
        Shell.CurrentWork = cancellation;
        Shell.Busy = true;
        Shell.ActivityText = startedMessage;
        Shell.ProgressIndeterminate = true;

        var progress = new Progress<double>(percent =>
        {
            Shell.ProgressIndeterminate = false;
            Shell.ProgressValue = percent;
            Shell.ActivityText = $"{startedMessage} — {percent:0} %";
        });

        try
        {
            var result = await work(progress, cancellation.Token).ConfigureAwait(true);
            Outcome = result;

            if (result.Cancelled)
            {
                Shell.ActivityText = "Cancelled.";
            }
            else if (result.Succeeded)
            {
                Shell.ActivityText = ResultText;
                Shell.Log("Wrote " + result.OutputPath);
            }
            else
            {
                Failure = result.Failure;
                Shell.ActivityText = result.Failure?.Headline ?? "It did not finish.";
            }
        }
        catch (OperationCanceledException)
        {
            Shell.ActivityText = "Cancelled.";
        }
        catch (Exception error)
        {
            Failure = DownloadFailure.Describe(FailureKind.Unknown) with { Explanation = error.Message };
            Shell.ActivityText = "It did not finish.";
        }
        finally
        {
            Shell.Busy = false;
            Shell.CurrentWork = null;
            Shell.ProgressIndeterminate = false;
            Shell.ProgressValue = 0;
        }
    }
}

public sealed class BurnViewModel : MediaViewModelBase
{
    /// <summary>
    /// Preferred default. It is what the user asked for; whether ffmpeg can
    /// actually use it is reported by the font check rather than assumed.
    /// </summary>
    private const string PreferredFont = "Peyda";

    private const string FallbackFont = "Segoe UI";

    private readonly FontCheck fontCheck;
    private CancellationTokenSource? fontCheckWork;

    private string subtitlePath = string.Empty;
    private string fontName = FallbackFont;
    private string fontSearch = string.Empty;
    private bool hasFontMatches = true;
    private FontVerdict? fontVerdict;
    private int fontSize = 18;
    private Color textColour = Colors.White;
    private Color backgroundColour = Colors.Black;
    private bool alsoCompress;
    private int sizeLimitMb = 120;
    private string suggestedSubtitlePath = string.Empty;
    private string suggestedSubtitleNote = string.Empty;

    public BurnViewModel(MainViewModel shell, ToolSet tools) : base(shell, tools)
    {
        fontCheck = new FontCheck(tools);

        AllFonts = ReadInstalledFontFamilies();

        // Default to the preferred font when it is installed, so the common
        // case needs no choosing at all.
        fontName = AllFonts.Contains(PreferredFont, StringComparer.OrdinalIgnoreCase)
            ? PreferredFont
            : FallbackFont;

        RefreshFontList();

        BrowseVideoCommand = new RelayCommand(() => { if (PickVideo() is { } path) VideoPath = path; });
        BrowseSubtitleCommand = new RelayCommand(BrowseSubtitle);
        BrowseOutputCommand = new RelayCommand(() => { if (PickOutput(OutputPath) is { } path) OutputPath = path; });
        UseSuggestionCommand = new RelayCommand(AcceptSuggestion, () => SuggestedSubtitlePath.Length > 0);
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
    }

    public RelayCommand BrowseVideoCommand { get; }

    public RelayCommand BrowseSubtitleCommand { get; }

    public RelayCommand BrowseOutputCommand { get; }

    public RelayCommand UseSuggestionCommand { get; }

    public AsyncRelayCommand StartCommand { get; }

    public string SubtitlePath
    {
        get => subtitlePath;
        set
        {
            if (!Set(ref subtitlePath, value))
            {
                return;
            }

            // The suggestion disappears once a subtitle is picked.
            if (value.Length > 0)
            {
                SuggestedSubtitlePath = string.Empty;
            }

            RaiseCanStart();

            // A different subtitle may be in a different script, which changes
            // whether the chosen font can render it.
            _ = CheckFontAsync();
        }
    }

    /// <summary>Every font family installed, by its registered family name.</summary>
    public IReadOnlyList<string> AllFonts { get; }

    /// <summary>The filtered view the dropdown binds to.</summary>
    public BulkObservableCollection<string> Fonts { get; } = [];

    /// <summary>
    /// WPF's own <see cref="System.Windows.Media.Fonts.SystemFontFamilies"/>
    /// groups families by DirectWrite's typographic weight, which collapses a
    /// font shipped as several same-family, differently-weighted files — e.g.
    /// "Peyda Black", "Peyda Bold", "Peyda Thin" — down to a single "Peyda"
    /// entry and hides the rest entirely. GDI+'s font collection instead
    /// reports each family exactly as it is registered, which is also how
    /// FFmpeg's own font matching sees them, so a name found here is one
    /// FFmpeg can actually be asked for.
    /// </summary>
    private static string[] ReadInstalledFontFamilies()
    {
        using var installed = new System.Drawing.Text.InstalledFontCollection();
        return installed.Families
            .Select(family => family.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public string FontName
    {
        get => fontName;
        set
        {
            if (Set(ref fontName, value))
            {
                RaiseCanStart();
                _ = CheckFontAsync();
            }
        }
    }

    /// <summary>What the user has typed into the font box, used to narrow the list.</summary>
    public string FontSearch
    {
        get => fontSearch;
        set
        {
            if (Set(ref fontSearch, value))
            {
                RefreshFontList();
            }
        }
    }

    public FontVerdict? FontVerdict
    {
        get => fontVerdict;
        private set
        {
            if (Set(ref fontVerdict, value))
            {
                RaiseAll(nameof(FontWarning), nameof(HasFontWarning));
            }
        }
    }

    public bool HasFontWarning => FontVerdict is { Checked: true, Matched: false };

    /// <summary>
    /// Said plainly, because the alternative is discovering it after a long
    /// encode: the subtitle will not be in the font that was chosen.
    /// </summary>
    public string FontWarning => FontVerdict is { Checked: true, Matched: false } verdict
        ? $"FFmpeg can't use \"{verdict.Requested}\" and will substitute {verdict.Resolved}. " +
          "Either the font can't be read, or it has no letters for this subtitle's script."
        : string.Empty;

    /// <summary>True while the typed text matches at least one installed family.</summary>
    public bool HasFontMatches => hasFontMatches;

    public string FontSearchNote => hasFontMatches
        ? string.Empty
        : $"No installed font matches “{FontSearch.Trim()}”.";

    /// <summary>
    /// Narrows the list to what was typed. A needle that matches nothing leaves
    /// the whole list in place rather than an empty dropdown — the note beside
    /// the box is what says the search found nothing.
    /// </summary>
    private void RefreshFontList()
    {
        var needle = FontSearch.Trim();

        IReadOnlyList<string> matches = needle.Length == 0
            ? AllFonts
            : AllFonts.Where(name => name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)).ToArray();

        var found = matches.Count > 0;
        var shown = found ? matches : AllFonts;

        // Rewriting an identical list would reset the ComboBox's editable text
        // for nothing, so an unchanged result is left alone. When it does
        // change, it changes in one notification rather than one per item —
        // with ~1800 installed families, Clear()+Add() in a loop made every
        // keystroke visibly stall the dropdown.
        if (!Fonts.SequenceEqual(shown, StringComparer.Ordinal))
        {
            Fonts.ReplaceAll(shown);
        }

        if (hasFontMatches != found)
        {
            hasFontMatches = found;
            RaiseAll(nameof(HasFontMatches), nameof(FontSearchNote));
        }
        else if (!found)
        {
            Raise(nameof(FontSearchNote));
        }
    }

    /// <summary>
    /// Verifies against the subtitle that is actually loaded, so a Latin-only
    /// font is flagged for Persian text but not for English.
    /// </summary>
    private async Task CheckFontAsync()
    {
        fontCheckWork?.Cancel();
        fontCheckWork = new CancellationTokenSource();
        var token = fontCheckWork.Token;

        FontVerdict = null;
        if (!Tools.CanProcessVideo || FontName.Length == 0)
        {
            return;
        }

        try
        {
            // Each check starts an FFmpeg process. Arrowing down the font list
            // would otherwise start one per keystroke, so the newest choice
            // cancels the ones still waiting here.
            await Task.Delay(TimeSpan.FromMilliseconds(400), token).ConfigureAwait(true);

            var sample = ReadSubtitleSample();
            var verdict = await fontCheck.VerifyAsync(FontName, sample, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested)
            {
                FontVerdict = verdict;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection.
        }
    }

    /// <summary>
    /// A line from the chosen subtitle is the honest sample. Without one, the
    /// Persian sample is used, since that is the demanding case here.
    /// </summary>
    private string ReadSubtitleSample()
    {
        try
        {
            if (File.Exists(SubtitlePath))
            {
                var line = File.ReadLines(SubtitlePath)
                    .Select(text => text.Trim())
                    .FirstOrDefault(text =>
                        text.Length > 0 &&
                        !text.Contains("-->", StringComparison.Ordinal) &&
                        !int.TryParse(text, out _));

                if (!string.IsNullOrWhiteSpace(line))
                {
                    return line.Length > 60 ? line[..60] : line;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the sample below.
        }

        return FontCheck.PersianSample;
    }

    public int FontSize
    {
        get => fontSize;
        set => Set(ref fontSize, value);
    }

    public Color TextColour
    {
        get => textColour;
        set
        {
            if (Set(ref textColour, value))
            {
                Raise(nameof(TextBrush));
            }
        }
    }

    public Color BackgroundColour
    {
        get => backgroundColour;
        set
        {
            if (Set(ref backgroundColour, value))
            {
                Raise(nameof(BackgroundBrush));
            }
        }
    }

    public Brush TextBrush => new SolidColorBrush(TextColour);

    public Brush BackgroundBrush => new SolidColorBrush(BackgroundColour);

    public bool AlsoCompress
    {
        get => alsoCompress;
        set => Set(ref alsoCompress, value);
    }

    public int SizeLimitMb
    {
        get => sizeLimitMb;
        set => Set(ref sizeLimitMb, value);
    }

    /// <summary>
    /// The downloaded subtitle, offered rather than chosen. It sits beneath the
    /// empty field with its provenance attached, so accepting it is one click.
    /// </summary>
    public string SuggestedSubtitlePath
    {
        get => suggestedSubtitlePath;
        private set
        {
            if (Set(ref suggestedSubtitlePath, value))
            {
                RaiseAll(nameof(HasSuggestion), nameof(SuggestedSubtitleName));
                UseSuggestionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SuggestedSubtitleNote
    {
        get => suggestedSubtitleNote;
        private set => Set(ref suggestedSubtitleNote, value);
    }

    public bool HasSuggestion => SuggestedSubtitlePath.Length > 0;

    public string SuggestedSubtitleName =>
        SuggestedSubtitlePath.Length > 0 ? Path.GetFileName(SuggestedSubtitlePath) : string.Empty;

    public void OfferSubtitle(string? path, string note)
    {
        SubtitlePath = string.Empty;
        SuggestedSubtitlePath = path ?? string.Empty;
        SuggestedSubtitleNote = note;
    }

    public override void OnToolsChanged() => StartCommand.RaiseCanExecuteChanged();

    protected override void RaiseCanStart() => StartCommand.RaiseCanExecuteChanged();

    protected override string SuggestOutput(string source) =>
        Path.Combine(
            Path.GetDirectoryName(source) ?? string.Empty,
            Path.GetFileNameWithoutExtension(source) + "-subbed.mp4");

    private void AcceptSuggestion()
    {
        var path = SuggestedSubtitlePath;
        SuggestedSubtitlePath = string.Empty;
        SubtitlePath = path;
    }

    private void BrowseSubtitle()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a subtitle file",
            Filter = "SubRip subtitle|*.srt|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            SubtitlePath = dialog.FileName;
        }
    }

    private bool CanStart() =>
        !Shell.Busy &&
        Tools.CanProcessVideo &&
        File.Exists(VideoPath) &&
        File.Exists(SubtitlePath) &&
        OutputPath.Length > 0;

    private Task StartAsync()
    {
        var request = new BurnRequest
        {
            VideoPath = VideoPath,
            SubtitlePath = SubtitlePath,
            OutputPath = OutputPath,
            FontName = FontName,
            FontSize = FontSize,
            TextColour = (TextColour.R, TextColour.G, TextColour.B),
            BackgroundColour = (BackgroundColour.R, BackgroundColour.G, BackgroundColour.B),
            SizeLimitBytes = AlsoCompress ? (long)SizeLimitMb * 1024 * 1024 : null,
        };

        return RunAsync(
            (progress, token) => Session.BurnAsync(request, progress, Shell.Log, token),
            "Burning subtitles");
    }
}

public sealed class CompressViewModel : MediaViewModelBase
{
    private int sizeLimitMb = 120;
    private string sourceSummary = string.Empty;
    private TimeSpan sourceDuration;

    public CompressViewModel(MainViewModel shell, ToolSet tools) : base(shell, tools)
    {
        BrowseVideoCommand = new RelayCommand(() => { if (PickVideo() is { } path) VideoPath = path; });
        BrowseOutputCommand = new RelayCommand(() => { if (PickOutput(OutputPath) is { } path) OutputPath = path; });
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
    }

    public RelayCommand BrowseVideoCommand { get; }

    public RelayCommand BrowseOutputCommand { get; }

    public AsyncRelayCommand StartCommand { get; }

    public int SizeLimitMb
    {
        get => sizeLimitMb;
        set
        {
            if (Set(ref sizeLimitMb, value))
            {
                RaiseCanStart();
                Raise(nameof(LimitWarning));
            }
        }
    }

    /// <summary>"Source is 512 MB · 24 min"</summary>
    public string SourceSummary
    {
        get => sourceSummary;
        private set => Set(ref sourceSummary, value);
    }

    /// <summary>
    /// Said before the encode rather than after it. A limit below what the
    /// length of the video allows cannot be met however the encode is run, and
    /// finding that out after waiting for it is the worst way to learn it.
    /// </summary>
    public string LimitWarning
    {
        get
        {
            if (sourceDuration <= TimeSpan.Zero)
            {
                return string.Empty;
            }

            var smallest = MediaSession.SmallestReachableBytes(sourceDuration);
            return (long)SizeLimitMb * 1024 * 1024 >= smallest
                ? string.Empty
                : $"{SizeLimitMb} MB is below what {(int)Math.Round(sourceDuration.TotalMinutes)} minutes of " +
                  $"video can be squeezed into. The smallest this one gets is about " +
                  $"{DownloadFailure.FormatSize(smallest)}.";
        }
    }

    public override void OnToolsChanged() => StartCommand.RaiseCanExecuteChanged();

    protected override void RaiseCanStart() => StartCommand.RaiseCanExecuteChanged();

    protected override string SuggestOutput(string source) =>
        Path.Combine(
            Path.GetDirectoryName(source) ?? string.Empty,
            Path.GetFileNameWithoutExtension(source) + "-small.mp4");

    protected override async void OnVideoChanged()
    {
        SourceSummary = string.Empty;
        sourceDuration = TimeSpan.Zero;
        Raise(nameof(LimitWarning));

        if (!File.Exists(VideoPath) || !Tools.CanProcessVideo)
        {
            return;
        }

        try
        {
            var info = await Session.InspectAsync(VideoPath, CancellationToken.None).ConfigureAwait(true);
            SourceSummary = info.Summary;
            sourceDuration = info.Duration;
        }
        catch (Exception)
        {
            // The tab still works without the summary line.
            SourceSummary = string.Empty;
        }

        Raise(nameof(LimitWarning));
    }

    private bool CanStart() =>
        !Shell.Busy &&
        Tools.CanProcessVideo &&
        File.Exists(VideoPath) &&
        OutputPath.Length > 0 &&
        SizeLimitMb > 0;

    private Task StartAsync()
    {
        var request = new CompressRequest
        {
            VideoPath = VideoPath,
            OutputPath = OutputPath,
            SizeLimitBytes = (long)SizeLimitMb * 1024 * 1024,
        };

        return RunAsync(
            (progress, token) => Session.CompressAsync(request, progress, Shell.Log, token),
            "Compressing");
    }
}
