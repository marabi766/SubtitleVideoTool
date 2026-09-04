using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SubtitleVideoTool.Core;

namespace SubtitleVideoTool.App;

public enum ToolHealth
{
    Ready,
    Degraded,
    Broken,
}

public sealed class MainViewModel : ObservableObject
{
    private string toolSummary = "Checking tools…";
    private ToolHealth health = ToolHealth.Degraded;
    private string activityText = "Nothing running. Choose your files and press Start.";
    private double progressValue;
    private bool progressIndeterminate;
    private bool busy;
    private bool logExpanded;
    private int selectedTab;

    public MainViewModel()
    {
        AppRoot = ResolveAppRoot();
        Tools = new ToolSet(AppRoot);
        Download = new DownloadViewModel(this, Tools);
        Burn = new BurnViewModel(this, Tools);
        Compress = new CompressViewModel(this, Tools);

        ToggleLogCommand = new RelayCommand(() => LogExpanded = !LogExpanded);
        LocateFfmpegCommand = new AsyncRelayCommand(LocateFfmpegAsync);
        CancelCommand = new RelayCommand(Cancel, () => Busy);
    }

    public string AppRoot { get; }

    public ToolSet Tools { get; }

    public DownloadViewModel Download { get; }

    public BurnViewModel Burn { get; }

    public CompressViewModel Compress { get; }

    public ObservableCollection<string> LogLines { get; } = [];

    public RelayCommand ToggleLogCommand { get; }

    public AsyncRelayCommand LocateFfmpegCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>Set by whatever is running, so Cancel reaches it.</summary>
    public CancellationTokenSource? CurrentWork { get; set; }

    public int SelectedTab
    {
        get => selectedTab;
        set => Set(ref selectedTab, value);
    }

    public string ToolSummary
    {
        get => toolSummary;
        private set => Set(ref toolSummary, value);
    }

    public ToolHealth Health
    {
        get => health;
        private set
        {
            if (Set(ref health, value))
            {
                RaiseAll(nameof(IsHealthy), nameof(IsBroken));
            }
        }
    }

    public bool IsHealthy => Health == ToolHealth.Ready;

    public bool IsBroken => Health == ToolHealth.Broken;

    public string ActivityText
    {
        get => activityText;
        set => Set(ref activityText, value);
    }

    public double ProgressValue
    {
        get => progressValue;
        set => Set(ref progressValue, value);
    }

    public bool ProgressIndeterminate
    {
        get => progressIndeterminate;
        set => Set(ref progressIndeterminate, value);
    }

    public bool Busy
    {
        get => busy;
        set
        {
            if (Set(ref busy, value))
            {
                CancelCommand.RaiseCanExecuteChanged();
                Raise(nameof(NotBusy));
            }
        }
    }

    public bool NotBusy => !Busy;

    public bool LogExpanded
    {
        get => logExpanded;
        set
        {
            if (Set(ref logExpanded, value))
            {
                Raise(nameof(LogHeader));
            }
        }
    }

    /// <summary>"Show log — 41 lines, 3 attempts" in the design's footer.</summary>
    public string LogHeader => LogExpanded
        ? $"Hide log — {LogLines.Count} lines"
        : $"Show log — {LogLines.Count} lines";

    /// <summary>
    /// Safe to call from any thread. Every tool writes its output on a reader
    /// thread of its own, and a bound ObservableCollection touched from there
    /// takes the whole window down, so the hop happens here rather than being
    /// left to each caller to remember.
    /// </summary>
    public void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Append(line);
        }
        else
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => Append(line));
        }
    }

    private void Append(string line)
    {
        LogLines.Add(line);

        // A verbose run would otherwise grow without bound; the newest part is
        // the part that explains what just happened.
        while (LogLines.Count > 2000)
        {
            LogLines.RemoveAt(0);
        }

        Raise(nameof(LogHeader));
    }

    public async Task RefreshToolsAsync()
    {
        await Tools.RefreshAsync().ConfigureAwait(true);
        UpdateToolSummary();
    }

    public void UpdateToolSummary()
    {
        ToolSummary = Tools.SummaryText;
        Health = Tools.UnavailableCount switch
        {
            0 => ToolHealth.Ready,
            // Downloading still works without FFmpeg; burning and compressing do not.
            _ when Tools.CanDownload => ToolHealth.Degraded,
            _ => ToolHealth.Broken,
        };

        Download.OnToolsChanged();
        Burn.OnToolsChanged();
        Compress.OnToolsChanged();
    }

    private async Task LocateFfmpegAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select ffmpeg.exe",
            Filter = "ffmpeg.exe|ffmpeg.exe|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (await Tools.AdoptFfmpegAsync(dialog.FileName).ConfigureAwait(true))
        {
            Log("FFmpeg and FFprobe adopted from " + Path.GetDirectoryName(dialog.FileName));
        }
        else
        {
            Log("That FFmpeg could not be used — ffprobe.exe must sit beside it and both must run.");
        }

        UpdateToolSummary();
    }

    private void Cancel()
    {
        CurrentWork?.Cancel();
        ActivityText = "Cancelling…";
    }

    /// <summary>
    /// Installed, the tools sit beside the executable. In a development build
    /// they are still in the repository root, four levels up from bin.
    /// </summary>
    private static string ResolveAppRoot()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidate = new DirectoryInfo(baseDirectory);

        for (var depth = 0; depth < 6 && candidate is not null; depth++)
        {
            if (Directory.Exists(Path.Combine(candidate.FullName, "tools")))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        return baseDirectory;
    }
}
