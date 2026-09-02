using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using SubtitleVideoTool.Core;

namespace SubtitleVideoTool.App;

/// <summary>
/// The design's "one block, three conditions": everything below the link is
/// empty, filled, or failed. The link field, folder and access cards never
/// change, so a failed probe is fixed and re-run without retyping anything.
/// </summary>
public enum LinkState
{
    Empty,
    Inspecting,
    Inspected,
    Failed,
}

public sealed class DownloadViewModel : ObservableObject
{
    private readonly MainViewModel shell;
    private readonly ToolSet tools;
    private readonly DownloadSession session;
    private readonly DispatcherTimer probeDebounce;

    private CancellationTokenSource? probeWork;

    private string link = string.Empty;
    private string outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";
    private LinkState state = LinkState.Empty;
    private VideoInfo? video;
    private QualityOption? selectedQuality;
    private bool downloadSubtitles = true;
    private CookieSource cookieSource = CookieSource.None;
    private string cookieFilePath = string.Empty;

    private FailureDescription? probeFailure;
    private string probeFailureDetail = string.Empty;

    private DownloadProgress progress = new();
    private FailureDescription? downloadFailure;
    private string downloadFailureDetail = string.Empty;
    private DownloadOutcome? outcome;
    private bool detailsExpanded;

    public DownloadViewModel(MainViewModel shell, ToolSet tools)
    {
        this.shell = shell;
        this.tools = tools;
        session = new DownloadSession(tools);

        // A probe is a 6-8 second network round trip; firing one per keystroke
        // would be both slow and rude, so typing settles first.
        probeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        probeDebounce.Tick += (_, _) =>
        {
            probeDebounce.Stop();
            _ = InspectAsync();
        };

        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        BrowseCookieFileCommand = new RelayCommand(BrowseCookieFile);
        CheckAgainCommand = new AsyncRelayCommand(InspectAsync, () => Link.Trim().Length > 0);
        SignInCommand = new RelayCommand(OpenSignIn);
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        StopProbeCommand = new RelayCommand(() => probeWork?.Cancel());
        ToggleDetailsCommand = new RelayCommand(() => DetailsExpanded = !DetailsExpanded);
        OpenFolderCommand = new RelayCommand(OpenOutputFolder);
        GoToBurnCommand = new RelayCommand(HandOffToBurn);
    }

    public ObservableCollection<QualityOption> Qualities { get; } = [];

    public RelayCommand BrowseFolderCommand { get; }

    public RelayCommand BrowseCookieFileCommand { get; }

    public AsyncRelayCommand CheckAgainCommand { get; }

    public RelayCommand SignInCommand { get; }

    public AsyncRelayCommand StartCommand { get; }

    public RelayCommand StopProbeCommand { get; }

    public RelayCommand ToggleDetailsCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand GoToBurnCommand { get; }

    // ------------------------------------------------------------- inputs

    public string Link
    {
        get => link;
        set
        {
            if (!Set(ref link, value))
            {
                return;
            }

            CheckAgainCommand.RaiseCanExecuteChanged();
            probeDebounce.Stop();

            if (string.IsNullOrWhiteSpace(value))
            {
                Reset();
            }
            else
            {
                probeDebounce.Start();
            }
        }
    }

    public string OutputFolder
    {
        get => outputFolder;
        set
        {
            if (Set(ref outputFolder, value))
            {
                StartCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool DownloadSubtitles
    {
        get => downloadSubtitles;
        set => Set(ref downloadSubtitles, value);
    }

    public CookieSource CookieSource
    {
        get => cookieSource;
        set
        {
            if (!Set(ref cookieSource, value))
            {
                return;
            }

            RaiseAll(nameof(IsAnonymous), nameof(IsAppSignIn), nameof(IsBrowserCookies), nameof(NeedsCookieFile));
            StartCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsAnonymous => CookieSource == CookieSource.None;

    public bool IsAppSignIn => CookieSource == CookieSource.AppSignIn;

    public bool IsBrowserCookies => CookieSource
        is CookieSource.Firefox or CookieSource.Edge or CookieSource.Chrome or CookieSource.Brave or CookieSource.File;

    public bool NeedsCookieFile => CookieSource == CookieSource.File;

    public string CookieFilePath
    {
        get => cookieFilePath;
        set => Set(ref cookieFilePath, value);
    }

    public QualityOption? SelectedQuality
    {
        get => selectedQuality;
        set
        {
            if (Set(ref selectedQuality, value))
            {
                Raise(nameof(ShowCodecNote));
            }
        }
    }

    /// <summary>
    /// Above 1080p most videos exist only as VP9/AV1. The design puts a quiet
    /// note next to that rather than a blocking dialog.
    /// </summary>
    public bool ShowCodecNote => SelectedQuality is { HasH264: false, Height: > 1080 };

    // -------------------------------------------------------- link state

    public LinkState State
    {
        get => state;
        private set
        {
            if (Set(ref state, value))
            {
                RaiseAll(nameof(IsEmpty), nameof(IsInspecting), nameof(IsInspected), nameof(IsLinkFailed));
                StartCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsEmpty => State == LinkState.Empty;

    public bool IsInspecting => State == LinkState.Inspecting;

    public bool IsInspected => State == LinkState.Inspected;

    public bool IsLinkFailed => State == LinkState.Failed;

    public VideoInfo? Video
    {
        get => video;
        private set
        {
            if (Set(ref video, value))
            {
                RaiseAll(nameof(VideoSummary), nameof(Subtitle), nameof(HasSubtitle));
            }
        }
    }

    public EnglishSubtitle Subtitle => Video?.Subtitle ?? new EnglishSubtitle();

    public bool HasSubtitle => Subtitle.IsAvailable;

    /// <summary>"3:33 · 8 qualities available · checked just now"</summary>
    public string VideoSummary => Video is null
        ? string.Empty
        : $"{Video.DurationText} · {Video.Qualities.Count} qualities available · checked just now";

    public FailureDescription? ProbeFailure
    {
        get => probeFailure;
        private set => Set(ref probeFailure, value);
    }

    public string ProbeFailureDetail
    {
        get => probeFailureDetail;
        private set => Set(ref probeFailureDetail, value);
    }

    // ---------------------------------------------------- download state

    public DownloadProgress Progress
    {
        get => progress;
        private set
        {
            if (Set(ref progress, value))
            {
                RaiseAll(nameof(IsRunning), nameof(RailIndex));
            }
        }
    }

    public bool IsRunning => Progress.Stage
        is DownloadStage.Preparing or DownloadStage.Started or DownloadStage.Downloading
        or DownloadStage.PostProcessing or DownloadStage.Retrying;

    /// <summary>How far along the five-step rail this state sits.</summary>
    public int RailIndex => (int)Progress.Step;

    public FailureDescription? DownloadFailureCard
    {
        get => downloadFailure;
        private set => Set(ref downloadFailure, value);
    }

    public string DownloadFailureDetail
    {
        get => downloadFailureDetail;
        private set => Set(ref downloadFailureDetail, value);
    }

    public DownloadOutcome? Outcome
    {
        get => outcome;
        private set
        {
            if (Set(ref outcome, value))
            {
                RaiseAll(nameof(HasSucceeded), nameof(SuggestedSubtitleName), nameof(SuggestedSubtitleNote));
            }
        }
    }

    public bool HasSucceeded => Outcome is { Succeeded: true };

    public string SuggestedSubtitleName => Outcome?.SubtitlePath is { } path ? Path.GetFileName(path) : string.Empty;

    /// <summary>
    /// The suggestion carries its provenance, so accepting it is an informed
    /// click rather than a blind one.
    /// </summary>
    public string SuggestedSubtitleNote => Subtitle.Kind switch
    {
        EnglishSubtitleKind.HumanWritten => "Written by a person",
        EnglishSubtitleKind.AutoGenerated => "Made by speech recognition — worth reading before you burn it in",
        EnglishSubtitleKind.AutoTranslated => "Machine-translated — expect to edit it before burning",
        _ => string.Empty,
    };

    public bool DetailsExpanded
    {
        get => detailsExpanded;
        set => Set(ref detailsExpanded, value);
    }

    public void OnToolsChanged() => StartCommand.RaiseCanExecuteChanged();

    // ------------------------------------------------------------ actions

    private void Reset()
    {
        State = LinkState.Empty;
        Video = null;
        Qualities.Clear();
        SelectedQuality = null;
        ProbeFailure = null;
        Outcome = null;
        DownloadFailureCard = null;
        Progress = new DownloadProgress();
    }

    private async Task InspectAsync()
    {
        var text = Link.Trim();
        if (text.Length == 0)
        {
            Reset();
            return;
        }

        string cleaned;
        try
        {
            cleaned = YoutubeUrl.Clean(text);
        }
        catch (InputException error)
        {
            State = LinkState.Failed;
            ProbeFailure = new FailureDescription
            {
                Kind = FailureKind.Unknown,
                Headline = "That doesn't look like a link",
                Explanation = error.Message,
                PrimaryAction = FailureAction.None,
            };
            ProbeFailureDetail = string.Empty;
            return;
        }

        probeWork?.Cancel();
        probeWork = new CancellationTokenSource();
        var token = probeWork.Token;

        State = LinkState.Inspecting;
        ProbeFailure = null;
        shell.Log($"Checking what {cleaned} offers…");

        try
        {
            var info = await session.ProbeAsync(BuildRequest(cleaned), token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            Video = info;
            Qualities.Clear();
            Qualities.Add(new QualityOption { Height = 0 });
            foreach (var quality in info.Qualities)
            {
                Qualities.Add(quality);
            }

            // Default to the best option at or below 1080p, which is where
            // H.264 still exists on most videos.
            SelectedQuality = info.Qualities.FirstOrDefault(q => q.Height <= 1080) ?? Qualities.FirstOrDefault();
            DownloadSubtitles = info.Subtitle.IsAvailable;
            State = LinkState.Inspected;
            shell.Log($"{info.Title} — {info.Qualities.Count} qualities, English subtitles: {info.Subtitle.Headline}");
        }
        catch (OperationCanceledException)
        {
            State = Video is null ? LinkState.Empty : LinkState.Inspected;
        }
        catch (ProbeFailedException failure)
        {
            ShowProbeFailure(DownloadFailure.Describe(failure.Output), failure.Output);
        }
        catch (ProbeException failure)
        {
            ShowProbeFailure(
                new FailureDescription
                {
                    Kind = FailureKind.Unknown,
                    Headline = "Couldn't ask YouTube about this video",
                    Explanation = failure.Message,
                    PrimaryAction = FailureAction.CheckAgain,
                    PrimaryActionLabel = "Check again",
                },
                failure.InnerException?.Message ?? string.Empty);
        }
    }

    private void ShowProbeFailure(FailureDescription description, string detail)
    {
        // The design's words for the bot-check case, which is the common one.
        if (description.Kind is FailureKind.BotCheck)
        {
            description = description with
            {
                Headline = "Couldn't ask YouTube about this video",
                Explanation =
                    "YouTube wants proof you're a person before it will say what this video contains. The link " +
                    "itself looks fine. Choose how to sign in below, then check again — you don't need to paste " +
                    "the link twice.",
                PrimaryActionLabel = "Sign in and check again",
            };
        }

        State = LinkState.Failed;
        ProbeFailure = description;
        ProbeFailureDetail = detail;
        shell.Log("Link check failed: " + description.Headline);
    }

    private bool CanStart() =>
        State == LinkState.Inspected &&
        tools.CanDownload &&
        !shell.Busy &&
        OutputFolder.Trim().Length > 0;

    private async Task StartAsync()
    {
        DownloadFailureCard = null;
        Outcome = null;

        var request = BuildRequest(YoutubeUrl.Clean(Link));
        using var work = new CancellationTokenSource();
        shell.CurrentWork = work;
        shell.Busy = true;
        shell.ProgressIndeterminate = true;

        var reporter = new Progress<DownloadProgress>(update =>
        {
            Progress = update;
            shell.ActivityText = update.Headline + (update.Badge.Length > 0 ? $" · {update.Badge}" : string.Empty);
            shell.ProgressIndeterminate = update.Percent is null;
            shell.ProgressValue = update.Percent ?? 0;
        });

        try
        {
            var result = await session
                .DownloadAsync(request, reporter, shell.Log, work.Token)
                .ConfigureAwait(true);

            Outcome = result;

            if (result.Cancelled)
            {
                Progress = new DownloadProgress
                {
                    Stage = DownloadStage.Cancelled,
                    Step = RailStep.Download,
                    Headline = "Cancelled",
                    Detail = "The partly downloaded file was removed. Nothing was left behind.",
                };
                shell.ActivityText = "Cancelled.";
            }
            else if (result.Succeeded)
            {
                Progress = new DownloadProgress
                {
                    Stage = DownloadStage.Succeeded,
                    Step = RailStep.Done,
                    Headline = "Download complete",
                    Detail = $"Finished in {Describe(result.Elapsed)}.",
                };
                shell.ActivityText = "Download complete.";
                shell.Log("Saved " + result.VideoPath);
            }
            else
            {
                Progress = new DownloadProgress
                {
                    Stage = DownloadStage.Failed,
                    Step = Progress.Step,
                    Headline = result.Failure?.Headline ?? "The download stopped",
                };
                DownloadFailureCard = result.Failure;
                DownloadFailureDetail = result.RawOutput;
                shell.ActivityText = "The download did not finish.";
            }
        }
        catch (OperationCanceledException)
        {
            shell.ActivityText = "Cancelled.";
        }
        catch (Exception error)
        {
            DownloadFailureCard = DownloadFailure.Describe(FailureKind.Unknown) with
            {
                Explanation = error.Message,
            };
            shell.ActivityText = "The download did not finish.";
        }
        finally
        {
            shell.Busy = false;
            shell.CurrentWork = null;
            shell.ProgressIndeterminate = false;
            shell.ProgressValue = 0;
        }
    }

    private DownloadRequest BuildRequest(string url) => new()
    {
        Url = url,
        OutputFolder = OutputFolder.Trim(),
        Height = SelectedQuality is { Height: > 0 } quality ? quality.Height : null,
        DownloadEnglishSubtitles = DownloadSubtitles && Subtitle.IsAvailable,
        CookieSource = CookieSource,
        CookieFilePath = CookieFilePath,
        SignInProfileFolder = SignIn.ProfileFolder(),
    };

    private static string Describe(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s"
        : $"{elapsed.Seconds} s";

    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where to save" };
        if (dialog.ShowDialog() == true)
        {
            OutputFolder = dialog.FolderName;
        }
    }

    private void BrowseCookieFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a cookies.txt file",
            Filter = "Netscape cookie file|cookies.txt;*.txt|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            CookieFilePath = dialog.FileName;
        }
    }

    private void OpenSignIn()
    {
        var folder = SignIn.ProfileFolder();
        if (SignIn.Open(folder))
        {
            CookieSource = CookieSource.AppSignIn;
            shell.Log("Opened a sign-in window in this app's own browser session.");
        }
        else
        {
            shell.Log("Firefox is not installed, so the app can't open its own sign-in window. Use a cookies.txt file instead.");
        }
    }

    private void OpenOutputFolder()
    {
        var target = Outcome?.VideoPath is { } path ? Path.GetDirectoryName(path) : OutputFolder;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
        {
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
    }

    /// <summary>
    /// The video carries over; the subtitle is offered there rather than chosen,
    /// because an auto-generated or translated track is often something the user
    /// wants to review or replace first.
    /// </summary>
    private void HandOffToBurn()
    {
        if (Outcome?.VideoPath is { } video)
        {
            shell.Burn.VideoPath = video;
            shell.Burn.OfferSubtitle(Outcome.SubtitlePath, SuggestedSubtitleNote);
        }

        shell.SelectedTab = 0;
    }
}
