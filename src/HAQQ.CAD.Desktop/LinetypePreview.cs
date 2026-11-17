using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// A strip that draws one linetype: its dashes, spaces and dots, repeated across the width.
/// <see cref="UnitsAcross"/> is how many drawing units the strip shows; strips with the same value are to the
/// same scale and can be compared by eye.
/// </summary>
public sealed class LinetypePreview : FrameworkElement
{
    public static readonly DependencyProperty PatternProperty = DependencyProperty.Register(
        nameof(Pattern), typeof(IEnumerable<double>), typeof(LinetypePreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitsAcrossProperty = DependencyProperty.Register(
        nameof(UnitsAcross), typeof(double), typeof(LinetypePreview),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(LinetypePreview),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Dash, space, dash, space... in drawing units; a dash of 0 is a dot. Empty = solid line.</summary>
    public IEnumerable<double>? Pattern
    {
        get => (IEnumerable<double>?)GetValue(PatternProperty);
        set => SetValue(PatternProperty, value);
    }

    public double UnitsAcross
    {
        get => (double)GetValue(UnitsAcrossProperty);
        set => SetValue(UnitsAcrossProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, y = System.Math.Round(ActualHeight / 2) + 0.5;
        if (width < 2 || ActualHeight < 2) return;

        var brush = Stroke ?? Brushes.White;
        var pen = new Pen(brush, 1.6);
        double[] pattern = Pattern?.ToArray() ?? System.Array.Empty<double>();
        double units = UnitsAcross;
        double pixelsPerUnit = units > 0 && !double.IsNaN(units) && !double.IsInfinity(units) ? width / units : 0;
        double repeat = pattern.Sum() * pixelsPerUnit;

        // Solid line, or a pattern too fine to show.
        if (pattern.Length < 2 || pattern.Length % 2 != 0 || !(repeat >= 2) || double.IsInfinity(repeat))
        {
            dc.DrawLine(pen, new Point(0, y), new Point(width, y));
            return;
        }

        double x = 0;
        for (int guard = 0; x < width && guard < 4000; guard++)
        {
            for (int i = 0; i < pattern.Length && x < width; i++)
            {
                double length = pattern[i] * pixelsPerUnit;
                if (i % 2 == 0)
                {
                    if (length < 1.0) dc.DrawEllipse(brush, null, new Point(x, y), 1.3, 1.3);      // dot
                    else dc.DrawLine(pen, new Point(x, y), new Point(System.Math.Min(x + length, width), y));
                }
                x += length;
            }
        }
    }
}
