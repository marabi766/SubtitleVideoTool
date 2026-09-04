using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SubtitleVideoTool.App.Views;

public partial class BurnView : UserControl
{
    /// <summary>
    /// Guards the font box against reacting to its own text changes. Narrowing
    /// the list drops the ComboBox's selection, and an editable ComboBox clears
    /// its text when that happens — so the text is written back, and that write
    /// must not read as more typing.
    /// </summary>
    private bool writingFontText;

    public BurnView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => ShowChosenFont();
        Loaded += (_, _) => ShowChosenFont();
    }

    private BurnViewModel? Model => DataContext as BurnViewModel;

    private void ShowChosenFont()
    {
        if (Model is { } model)
        {
            WriteFontText(model.FontName);
        }
    }

    private void WriteFontText(string text)
    {
        writingFontText = true;
        try
        {
            FontBox.Text = text;
        }
        finally
        {
            writingFontText = false;
        }
    }

    /// <summary>
    /// Typing narrows the list. There are around a thousand families installed,
    /// and scrolling that is no way to find one.
    /// </summary>
    private void FontTyped(object sender, RoutedEventArgs e)
    {
        if (writingFontText || Model is not { } model || e.OriginalSource is not TextBox box)
        {
            return;
        }

        var typed = box.Text;
        var caret = box.SelectionStart;

        writingFontText = true;
        try
        {
            model.FontSearch = typed;

            if (box.Text != typed)
            {
                box.Text = typed;
                box.SelectionStart = Math.Min(caret, typed.Length);
                box.SelectionLength = 0;
            }
        }
        finally
        {
            writingFontText = false;
        }

        // An empty box is the user clearing the field, not a search.
        FontBox.IsDropDownOpen = typed.Trim().Length > 0 && model.HasFontMatches;
    }

    /// <summary>Both clicking an entry and arrowing onto one land here.</summary>
    private void FontPicked(object sender, SelectionChangedEventArgs e)
    {
        if (writingFontText || Model is not { } model)
        {
            return;
        }

        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not string family)
        {
            return;
        }

        model.FontName = family;
        WriteFontText(family);
    }

    private void FontListClosed(object? sender, EventArgs e) => ResetFontSearch();

    /// <summary>
    /// A half-typed name is not a font, so leaving the box either commits a real
    /// family or puts back the one that was already chosen.
    /// </summary>
    private void FontCommitted(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Model is { } model &&
            model.AllFonts.FirstOrDefault(name =>
                name.Equals(FontBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)) is { } match)
        {
            model.FontName = match;
        }

        ResetFontSearch();
    }

    private void ResetFontSearch()
    {
        if (Model is not { } model)
        {
            return;
        }

        model.FontSearch = string.Empty;
        WriteFontText(model.FontName);
    }

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
