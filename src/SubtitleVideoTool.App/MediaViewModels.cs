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

    public string ResultText => Outcome is { Succeeded: true } result
        ? result.OvershotLimit
            ? $"Done — {DownloadFailure.FormatSize(result.OutputBytes)}, a little over the limit you set."
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
    private string subtitlePath = string.Empty;
    private string fontName = "Segoe UI";
    private int fontSize = 18;
    private Color textColour = Colors.White;
    private Color backgroundColour = Colors.Black;
    private bool alsoCompress;
    private int sizeLimitMb = 120;
    private string suggestedSubtitlePath = string.Empty;
    private string suggestedSubtitleNote = string.Empty;

    public BurnViewModel(MainViewModel shell, ToolSet tools) : base(shell, tools)
    {
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
        }
    }

    public string FontName
    {
        get => fontName;
        set => Set(ref fontName, value);
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
        set => Set(ref sizeLimitMb, value);
    }

    /// <summary>"Source is 512 MB · 24 min"</summary>
    public string SourceSummary
    {
        get => sourceSummary;
        private set => Set(ref sourceSummary, value);
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
        if (!File.Exists(VideoPath) || !Tools.CanProcessVideo)
        {
            return;
        }

        try
        {
            var info = await Session.InspectAsync(VideoPath, CancellationToken.None).ConfigureAwait(true);
            SourceSummary = info.Summary;
        }
        catch (Exception)
        {
            // The tab still works without the summary line.
            SourceSummary = string.Empty;
        }
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
