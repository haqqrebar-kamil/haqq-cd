using System;
using System.Windows;
using System.Windows.Media;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Rendering;
using CadRenderOptions = HAQQ.CAD.Rendering.RenderOptions;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// A sample picture for the Dimension Settings window: a small shape with a horizontal, a vertical, a radius
/// and an angle dimension, drawn with the style being edited by the same code that draws the real drawing.
/// </summary>
public sealed class DimensionPreview : FrameworkElement
{
    public static readonly DependencyProperty StyleValuesProperty = DependencyProperty.Register(
        nameof(StyleValues), typeof(DimensionStyle), typeof(DimensionPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitsProperty = DependencyProperty.Register(
        nameof(Units), typeof(DrawingSettings), typeof(DimensionPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Backdrop = CreateBackdrop();

    private readonly WpfRenderSurface _surface = new();
    private readonly SceneRenderer _renderer = new();
    private readonly ViewTransform _view = new();

    /// <summary>The dimension style to show.</summary>
    public DimensionStyle? StyleValues
    {
        get => (DimensionStyle?)GetValue(StyleValuesProperty);
        set => SetValue(StyleValuesProperty, value);
    }

    /// <summary>The drawing's units, which a style may follow.</summary>
    public DrawingSettings? Units
    {
        get => (DrawingSettings?)GetValue(UnitsProperty);
        set => SetValue(UnitsProperty, value);
    }

    private static Brush CreateBackdrop()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x23));
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < 20 || ActualHeight < 20 || StyleValues is not { IsValid: true } style) return;

        var document = BuildSample(style, Units);
        _view.SetViewport(ActualWidth, ActualHeight);
        // The sample with its dimensions reaches from about (-19, -3) to (54, 80) in drawing units.
        _view.SetView(new Point2D(17.5, 38.5), Math.Min(ActualWidth / 80.0, ActualHeight / 90.0));

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        _surface.Target = dc;
        try
        {
            _renderer.Render(_surface, document, _view, new CadRenderOptions { ShowGrid = false });
        }
        finally
        {
            _surface.Target = null;
            dc.Pop();
        }
    }

    private static DrawingDocument BuildSample(DimensionStyle style, DrawingSettings? units)
    {
        var document = new DrawingDocument();
        if (units is not null)
        {
            document.Settings.Unit = units.Unit;
            document.Settings.Format = units.Format;
            document.Settings.DecimalPrecision = units.DecimalPrecision;
            document.Settings.FractionDenominator = units.FractionDenominator;
        }
        Guid layer = document.CurrentLayerId;

        void Add(Entity entity)
        {
            entity.LayerId = layer;
            if (entity.IsValid) document.AddEntity(entity);
        }

        // The shape: 51 wide and 60 high, with a circle of radius 12.
        Add(new LineEntity(new Point2D(0, 0), new Point2D(51, 0)));
        Add(new LineEntity(new Point2D(51, 0), new Point2D(51, 60)));
        Add(new LineEntity(new Point2D(51, 60), new Point2D(0, 60)));
        Add(new LineEntity(new Point2D(0, 60), new Point2D(0, 0)));
        var center = new Point2D(25.5, 32);
        Add(new CircleEntity(center, 12));

        Add(new DimensionEntity(DimensionKind.Linear) { Style = style, P1 = new Point2D(0, 60), P2 = new Point2D(51, 60), LinePoint = new Point2D(25, 72), Angle = 0 });
        Add(new DimensionEntity(DimensionKind.Linear) { Style = style, P1 = new Point2D(0, 0), P2 = new Point2D(0, 60), LinePoint = new Point2D(-12, 30), Angle = Math.PI / 2 });
        var toward = Vector2D.FromPolar(1.0, -Math.PI / 4);
        Add(new DimensionEntity(DimensionKind.Radius) { Style = style, Center = center, P1 = center + toward * 12, LinePoint = center + toward * 34 });
        Add(new DimensionEntity(DimensionKind.Angle) { Style = style, Center = new Point2D(51, 0), P1 = new Point2D(30, 0), P2 = new Point2D(51, 20), LinePoint = new Point2D(41, 10) });
        return document;
    }
}
