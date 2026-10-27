using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AiDevControlPanel.Core.Domain;

namespace AiDevControlPanel.App.Converters;

/// <summary>StateColor → background brush from Themes/StateColors.xaml.</summary>
public sealed class StateColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current.TryFindResource($"StateBrush.{value}") as Brush ?? Brushes.White;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>StateColor → readable text brush (white on dark blue).</summary>
public sealed class StateColorToForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current.TryFindResource(value is StateColor.DarkBlue ? "StateForeground.Light" : "StateForeground.Dark") as Brush ?? Brushes.Black;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is true, a non-empty string or a non-null object; "Invert" as parameter flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            _ => true,
        };
        if (parameter is "Invert")
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
