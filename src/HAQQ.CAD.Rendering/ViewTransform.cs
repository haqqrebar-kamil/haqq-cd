using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Rendering;

/// <summary>A position on the drawing surface in device-independent pixels (Y grows downward).</summary>
public readonly record struct ScreenPoint(double X, double Y);

/// <summary>
/// Maps model space (double precision, Y up) to the screen (pixels, Y down). Pixels are only ever
/// derived from model coordinates through this class; they are never stored in the drawing.
/// </summary>
public sealed class ViewTransform
{
    public const double MinScale = 1e-9;
    public const double MaxScale = 1e9;

    /// <summary>Pixels per model unit.</summary>
    public double Scale { get; private set; } = 4.0;

    /// <summary>Model point shown at the centre of the viewport.</summary>
    public Point2D Center { get; private set; } = Point2D.Origin;

    public double ViewportWidth { get; private set; } = 1;
    public double ViewportHeight { get; private set; } = 1;

    public void SetViewport(double width, double height)
    {
        ViewportWidth = Math.Max(1, width);
        ViewportHeight = Math.Max(1, height);
    }

    public void SetView(Point2D center, double scale)
    {
        Center = center;
        Scale = Math.Clamp(scale, MinScale, MaxScale);
    }

    public ScreenPoint WorldToScreen(Point2D p) => new(
        (p.X - Center.X) * Scale + ViewportWidth / 2.0,
        ViewportHeight / 2.0 - (p.Y - Center.Y) * Scale);

    public Point2D ScreenToWorld(ScreenPoint s) => new(
        (s.X - ViewportWidth / 2.0) / Scale + Center.X,
        (ViewportHeight / 2.0 - s.Y) / Scale + Center.Y);

    /// <summary>Converts a length in pixels to model units at the current zoom.</summary>
    public double PixelsToWorld(double pixels) => pixels / Scale;

    /// <summary>Zooms by <paramref name="factor"/> keeping the model point under <paramref name="anchor"/> fixed.</summary>
    public void ZoomAt(ScreenPoint anchor, double factor)
    {
        if (!(factor > 0) || !double.IsFinite(factor)) return;
        var world = ScreenToWorld(anchor);
        Scale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        Center = new Point2D(
            world.X - (anchor.X - ViewportWidth / 2.0) / Scale,
            world.Y + (anchor.Y - ViewportHeight / 2.0) / Scale);
    }

    /// <summary>Pans by a screen-space drag.</summary>
    public void Pan(double dxPixels, double dyPixels) =>
        Center = new Point2D(Center.X - dxPixels / Scale, Center.Y + dyPixels / Scale);

    /// <summary>Fits <paramref name="box"/> in the viewport with a margin. An empty box resets to a default view.</summary>
    public void ZoomExtents(BoundingBox box, double marginFraction = 0.05)
    {
        if (box.IsEmpty)
        {
            SetView(Point2D.Origin, 4.0);
            return;
        }
        double w = Math.Max(box.Width, 1e-9), h = Math.Max(box.Height, 1e-9);
        double usable = Math.Max(0.1, 1.0 - 2.0 * marginFraction);
        double scale = Math.Min(ViewportWidth * usable / w, ViewportHeight * usable / h);
        if (box.Width <= 1e-9 && box.Height <= 1e-9) scale = Scale; // a single point: keep the zoom
        SetView(box.Center, scale);
    }

    /// <summary>The model-space rectangle currently visible.</summary>
    public BoundingBox VisibleBounds
    {
        get
        {
            var a = ScreenToWorld(new ScreenPoint(0, ViewportHeight));
            var b = ScreenToWorld(new ScreenPoint(ViewportWidth, 0));
            return BoundingBox.FromCorners(a, b);
        }
    }
}
