using System.Windows;
using System.Windows.Media;
using HAQQ.CAD.Rendering;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// WPF implementation of the renderer's drawing surface: translates primitives into
/// <see cref="DrawingContext"/> calls. Pens and brushes are frozen and cached per style.
/// </summary>
internal sealed class WpfRenderSurface : IRenderSurface
{
    private static readonly DashStyle Dashes = CreateDashes();

    private readonly Dictionary<RenderStyle, Pen> _pens = new();
    private readonly Dictionary<int, Brush> _fills = new();

    /// <summary>The context of the render pass in progress.</summary>
    public DrawingContext? Target { get; set; }

    private static DashStyle CreateDashes()
    {
        var dashes = new DashStyle(new double[] { 5, 4 }, 0);
        dashes.Freeze();
        return dashes;
    }

    private Pen GetPen(RenderStyle style)
    {
        if (_pens.TryGetValue(style, out var cached)) return cached;

        var brush = new SolidColorBrush(Color.FromRgb(style.R, style.G, style.B));
        brush.Freeze();
        var pen = new Pen(brush, style.Width)
        {
            StartLineCap = PenLineCap.Flat,
            EndLineCap = PenLineCap.Flat,
            LineJoin = PenLineJoin.Round,
        };
        if (style.Dashed) pen.DashStyle = Dashes;
        else if (style.Pattern is { Length: >= 2 } pattern)
        {
            // WPF measures dashes in multiples of the pen width. A dot (dash of 0) is drawn one pixel long.
            double width = Math.Max(style.Width, 0.1);
            var dashes = new double[pattern.Length];
            for (int i = 0; i < pattern.Length; i++)
                dashes[i] = Math.Max(pattern[i], i % 2 == 0 ? 1.0 : 0.5) / width;
            var dashStyle = new DashStyle(dashes, 0);
            dashStyle.Freeze();
            pen.DashStyle = dashStyle;
        }
        pen.Freeze();
        if (_pens.Count > 512) _pens.Clear(); // wide polylines need a new pen per zoom level; keep the cache bounded
        _pens[style] = pen;
        return pen;
    }

    private Brush GetFill(RenderStyle style, byte alpha)
    {
        int key = (alpha << 24) | (style.R << 16) | (style.G << 8) | style.B;
        if (_fills.TryGetValue(key, out var cached)) return cached;
        var brush = new SolidColorBrush(Color.FromArgb(alpha, style.R, style.G, style.B));
        brush.Freeze();
        _fills[key] = brush;
        return brush;
    }

    public void DrawLine(double x1, double y1, double x2, double y2, in RenderStyle style)
    {
        Target?.DrawLine(GetPen(style), new Point(x1, y1), new Point(x2, y2));
    }

    public void DrawPolyline(IReadOnlyList<ScreenPoint> points, bool closed, in RenderStyle style)
    {
        if (Target is null || points.Count < 2) return;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(points[0].X, points[0].Y), false, closed);
            for (int i = 1; i < points.Count; i++)
                context.LineTo(new Point(points[i].X, points[i].Y), true, true);
        }
        geometry.Freeze();
        Target.DrawGeometry(null, GetPen(style), geometry);
    }

    public void FillPolygon(IReadOnlyList<ScreenPoint> points, in RenderStyle style)
    {
        if (Target is null || points.Count < 3) return;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(points[0].X, points[0].Y), true, true);
            for (int i = 1; i < points.Count; i++)
                context.LineTo(new Point(points[i].X, points[i].Y), false, false);
        }
        geometry.Freeze();
        Target.DrawGeometry(GetFill(style, 255), null, geometry);
    }

    public void DrawCircle(double cx, double cy, double radius, in RenderStyle style)
    {
        Target?.DrawEllipse(null, GetPen(style), new Point(cx, cy), radius, radius);
    }

    public void DrawRectangle(double x, double y, double width, double height, in RenderStyle style, byte fillAlpha)
    {
        if (Target is null || width < 0 || height < 0) return;
        Brush? fill = fillAlpha > 0 ? GetFill(style, fillAlpha) : null;
        Target.DrawRectangle(fill, GetPen(style), new Rect(x, y, width, height));
    }
}
