using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SubtitleVideoTool.App.Views;

public partial class BurnView : UserControl
{
    public BurnView() => InitializeComponent();

    private BurnViewModel? Model => DataContext as BurnViewModel;

    private void PickTextColour(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && Pick(model.TextColour) is { } colour)
        {
            model.TextColour = colour;
        }
    }

    private void PickBackgroundColour(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && Pick(model.BackgroundColour) is { } colour)
        {
            model.BackgroundColour = colour;
        }
    }

    /// <summary>
    /// WPF has no colour dialog of its own, so this is the Windows Forms one.
    /// It is the same dialog the rest of Windows uses.
    /// </summary>
    private static Color? Pick(Color current)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return null;
        }

        return Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
    }
}
