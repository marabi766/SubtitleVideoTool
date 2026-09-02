using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace SubtitleVideoTool.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel model = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = model;

        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Health))
            {
                UpdateToolDot();
            }
        };

        // The log is only useful if it follows the newest line.
        model.LogLines.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && model.LogExpanded)
            {
                LogScroller.ScrollToEnd();
            }
        };

        Loaded += async (_, _) =>
        {
            model.Log("Checking the bundled tools…");
            await model.RefreshToolsAsync();
            UpdateToolDot();
            model.Log(model.ToolSummary);
        };
    }

    /// <summary>
    /// Colour never carries the meaning alone — the summary text next to the dot
    /// changes with it — but the dot is what makes the state readable at a glance.
    /// </summary>
    private void UpdateToolDot()
    {
        var key = model.Health switch
        {
            ToolHealth.Ready => "SuccessBrush",
            ToolHealth.Degraded => "CautionBrush",
            _ => "CriticalBrush",
        };

        if (TryFindResource(key) is Brush brush)
        {
            ToolDot.Fill = brush;
        }
    }
}
