using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using HAQQ.CAD.Presentation;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// Turns a colour as the drop-downs show it ("Red", "#FF8000") into a brush for its swatch.
/// ByLayer and ByBlock have no colour of their own and give a transparent brush (an empty swatch).
/// </summary>
public sealed class ColorNameToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string? text = value as string;
        string? hex = LayerViewModel.ColorFromName(text) ?? (text is { Length: 7 } && text[0] == '#' ? text : null);
        if (hex is null) return Brushes.Transparent;
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
