using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SubtitleVideoTool.App;

/// <summary>Shows an element only when the bound string has something in it.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Set to true to collapse when the value is true.</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Compares the bound value with the parameter, for radio-style selection.</summary>
public sealed class EqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? parameter : Binding.DoNothing;
}

/// <summary>
/// Paints one step of the five-step rail. The parameter is that step's index;
/// steps at or before the current one are accented, the rest stay grey. The rail
/// never changes shape, so only the fill moves.
/// </summary>
public sealed class RailStepBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int current ||
            !int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var step))
        {
            return Application.Current.TryFindResource("StrokeBrush");
        }

        return Application.Current.TryFindResource(step <= current ? "AccentBrush" : "StrokeBrush");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Dims the label of a rail step the run has not reached.</summary>
public sealed class RailStepTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int current ||
            !int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var step))
        {
            return Application.Current.TryFindResource("TextSecondaryBrush");
        }

        return Application.Current.TryFindResource(step <= current ? "TextPrimaryBrush" : "TextSecondaryBrush");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
