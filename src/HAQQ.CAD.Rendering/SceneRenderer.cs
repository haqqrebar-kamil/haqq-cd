using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Snapping;

namespace HAQQ.CAD.Rendering;

/// <summary>Stroke description handed to the drawing surface.</summary>
/// <param name="Dashed">The fixed short dashes used for previews and the selection highlight.</param>
/// <param name="Pattern">
/// A linetype pattern in pixels: dash, gap, dash, gap... (a dash of 0 is a dot); null for a solid line. The same
/// array instance is handed out for the same linetype at the same zoom, so styles compare equal and pens can be cached.
/// </param>
public readonly record struct RenderStyle(byte R, byte G, byte B, double Width = 1.0, bool Dashed = false, double[]? Pattern = null);

/// <summary>The font of a line of text and which point of it is given: -1 its left end, 0 its middle, 1 its right end.</summary>
public readonly record struct TextLook(string Font, bool Bold, bool Italic, bool Underline, int Anchor);

/// <summary>
/// Minimal immediate-mode 2D surface. The CAD renderer only talks to this interface, so the
/// geometry and scene logic stay independent of WPF (or any other backend such as SkiaSharp).
/// Point lists passed to the surface are reused by the caller: consume them during the call.
/// </summary>
public interface IRenderSurface
{
    void DrawLine(double x1, double y1, double x2, double y2, in RenderStyle style);
    void DrawPolyline(IReadOnlyList<ScreenPoint> points, bool closed, in RenderStyle style);
    void DrawCircle(double cx, double cy, double radius, in RenderStyle style);

    /// <summary>Fills a closed polygon with the style's colour (used for tapered polyline segments).</summary>
    void FillPolygon(IReadOnlyList<ScreenPoint> points, in RenderStyle style);

    /// <summary>Draws a rectangle outline, optionally filled with the stroke colour at <paramref name="fillAlpha"/> (0-255).</summary>
    void DrawRectangle(double x, double y, double width, double height, in RenderStyle style, byte fillAlpha);

    /// <summary>
    /// Draws one line of text centred on (<paramref name="cx"/>, <paramref name="cy"/>). <paramref name="height"/> is the
    /// height of a capital letter in pixels; <paramref name="angle"/> is counter-clockwise in radians (0 = left to right).
    /// </summary>
    void DrawText(string text, double cx, double cy, double height, double angle, in RenderStyle style);

    /// <summary>
    /// One line of text in a font. (x, y) is the middle of the line's height, at its left end, middle or right
    /// end according to <see cref="TextLook.Anchor"/>; the angle turns it about that point, counter-clockwise.
    /// </summary>
    void DrawStyledText(string text, double x, double y, double height, double angle, in TextLook look, in RenderStyle style);

    /// <summary>Everything drawn until the matching <see cref="PopClip"/> is cut off at this rectangle (a viewport's window).</summary>
    void PushClip(double x, double y, double width, double height);
    void PopClip();
}

public static class RenderTheme
{
    public static readonly RenderStyle GridMinor = new(44, 49, 57);
    public static readonly RenderStyle GridMajor = new(58, 64, 74);
    public static readonly RenderStyle AxisX = new(150, 70, 70);
    public static readonly RenderStyle AxisY = new(70, 140, 80);
    /// <summary>Selected objects: a wide glow underneath (<see cref="SelectionGlow"/>) and bright dashes on top.</summary>
    public static readonly RenderStyle Selection = new(150, 220, 255, 2.0, true);
    public static readonly RenderStyle SelectionGlow = new(0, 110, 215, 6.0);
    public static readonly RenderStyle Grip = new(64, 160, 255);
    public static readonly RenderStyle Preview = new(255, 205, 80);
    public static readonly RenderStyle RubberBand = new(255, 205, 80, 1.0, true);
    public static readonly RenderStyle SnapMarker = new(60, 255, 150, 2.0);
    public static readonly RenderStyle WindowSelect = new(64, 140, 255);
    public static readonly RenderStyle CrossingSelect = new(80, 220, 120, 1.0, true);
    public static readonly RenderStyle PolarTrack = new(70, 225, 100, 1.0, true);
    public static readonly RenderStyle Crosshair = new(240, 240, 240, 1.5);
    /// <summary>The sheet of a layout: a little lighter than the background, with an edge.</summary>
    public static readonly RenderStyle Paper = new(40, 44, 52);
    public static readonly RenderStyle PaperEdge = new(120, 128, 140);
    /// <summary>The frame of the viewport that is being worked in (after a double-click inside it).</summary>
    public static readonly RenderStyle ActiveViewport = new(242, 163, 58, 2.5);
}

public sealed class RenderOptions
{
    public Func<Guid, bool>? IsSelected { get; set; }
    public IReadOnlyList<Entity> Preview { get; set; } = Array.Empty<Entity>();
    public SnapResult? Snap { get; set; }
    public bool ShowGrid { get; set; } = true;

    /// <summary>The viewport being worked in, drawn with a heavy frame.</summary>
    public Guid? ActiveViewportId { get; set; }

    /// <summary>
    /// Plotting: layers with Plot off are left out, the sheet itself is not drawn, and lines get their
    /// lineweight (<see cref="PixelsPerMm"/> wide per millimetre).
    /// </summary>
    public bool Plot { get; set; }

    /// <summary>Plotting in black only (true), or in the objects' colours with near-white turned black.</summary>
    public bool PlotMonochrome { get; set; } = true;

    /// <summary>Output units per millimetre of lineweight when plotting.</summary>
    public double PixelsPerMm { get; set; } = 96.0 / 25.4;

    /// <summary>Polar tracking ray to show: base point and direction in degrees (model space).</summary>
    public (Point2D From, double AngleDegrees)? PolarTrack { get; set; }

    /// <summary>Object snap tracking lines to draw: the acquired point each starts at and its angle.</summary>
    public IReadOnlyList<(Point2D From, double AngleDegrees)> TrackLines { get; set; } = Array.Empty<(Point2D, double)>();

    /// <summary>Points acquired for object snap tracking, each marked with a small cross.</summary>
    public IReadOnlyList<Point2D> TrackPoints { get; set; } = Array.Empty<Point2D>();

    /// <summary>Selection rectangle being dragged, in screen pixels.</summary>
    public (ScreenPoint A, ScreenPoint B, bool Crossing)? SelectionRectangle { get; set; }

    /// <summary>Crosshair position in screen pixels; null hides it.</summary>
    public ScreenPoint? Crosshair { get; set; }

    /// <summary>Half size of the pick box drawn at the crosshair centre (0 hides it).</summary>
    public double PickBoxHalfSize { get; set; }

    /// <summary>Length of each crosshair arm in pixels, measured from the centre. 0 runs the arms to the edges of the view.</summary>
    public double CrosshairArmLength { get; set; }

    /// <summary>False draws only the pick box (used while a command is collecting a selection).</summary>
    public bool ShowCrosshairArms { get; set; } = true;
}

public readonly record struct RenderStats(int Candidates, int Drawn);

/// <summary>Draws a document through a <see cref="ViewTransform"/> onto an <see cref="IRenderSurface"/>.</summary>
public sealed class SceneRenderer
{
    private const double ClipMargin = 64;          // pixels beyond the viewport kept when clipping
    private const double SafeCoordinate = 30000;   // beyond this, primitives are clipped before drawing
    private const int MaxGrips = 2000;

    private readonly List<ScreenPoint> _buffer = new();
    private DrawingSettings _settings = new();
    private RenderOptions _options = new();

    public RenderStats Render(IRenderSurface surface, DrawingDocument document, ViewTransform view, RenderOptions options)
    {
        _settings = document.Settings;
        _options = options;
        var layout = document.ActiveLayout;
        if (layout is not null)
        {
            // A layout: the sheet instead of the grid (the sheet itself is not part of a plot).
            if (!options.Plot)
            {
                var a = view.WorldToScreen(new Point2D(0, layout.PaperHeight));
                var b = view.WorldToScreen(new Point2D(layout.PaperWidth, 0));
                double x = Math.Max(a.X, -SafeCoordinate), y = Math.Max(a.Y, -SafeCoordinate);
                double w = Math.Min(b.X, SafeCoordinate) - x, h = Math.Min(b.Y, SafeCoordinate) - y;
                if (w > 0 && h > 0)
                {
                    surface.DrawRectangle(x, y, w, h, RenderTheme.Paper, 255);
                    surface.DrawRectangle(x, y, w, h, RenderTheme.PaperEdge, 0);
                }
            }
        }
        else if (options.ShowGrid && !options.Plot) DrawGrid(surface, document, view);

        // Viewport culling through the spatial index: only entities near the view are visited.
        var visible = view.VisibleBounds;
        var candidates = document.QueryEntities(visible);
        int drawn = 0;
        List<Entity>? selected = null;
        ViewportEntity? active = null;

        // What is seen through the viewports goes down first, under everything drawn on the sheet.
        foreach (var entity in candidates)
        {
            if (entity is not ViewportEntity viewport || viewport.SpaceId == Guid.Empty) continue;
            if (options.ActiveViewportId == viewport.Id) active = viewport;
            if (document.GetLayer(viewport.LayerId) is { IsFrozen: false }) drawn += DrawViewportContent(surface, document, viewport, view);
        }

        Guid layerId = Guid.Empty;
        Layer? layer = null;
        foreach (var entity in candidates)
        {
            if (entity.LayerId != layerId || layer is null)
            {
                layerId = entity.LayerId;
                layer = document.GetLayer(layerId);
            }
            if (layer is null || !layer.IsDisplayed) continue;
            if (options.Plot && !layer.IsPlotted) continue;

            if (options.IsSelected is not null && options.IsSelected(entity.Id))
            {
                (selected ??= new List<Entity>()).Add(entity);
                continue;
            }

            DrawEntity(surface, entity, view, WithLinetype(StyleFor(entity, layer), document, entity, view));
            drawn++;
        }

        if (active is not null && !options.Plot) DrawEntity(surface, active, view, RenderTheme.ActiveViewport);

        if (selected is not null)
        {
            // The glow of every selected object goes down first, so that it never covers another object's dashes.
            foreach (var entity in selected) DrawEntity(surface, entity, view, RenderTheme.SelectionGlow);
            foreach (var entity in selected)
            {
                DrawEntity(surface, entity, view, RenderTheme.Selection);
                drawn++;
            }
            int grips = 0;
            foreach (var entity in selected)
            {
                if (grips > MaxGrips) break;
                foreach (var p in GetGripPoints(entity))
                {
                    var s = view.WorldToScreen(p);
                    surface.DrawRectangle(s.X - 3.5, s.Y - 3.5, 7, 7, RenderTheme.Grip, 255);
                    grips++;
                }
            }
        }

        foreach (var entity in options.Preview)
        {
            // The helper line from the base point to the cursor is drawn dashed.
            bool helper = entity is LineEntity && options.Preview.Count > 1 && ReferenceEquals(entity, options.Preview[^1]);
            DrawEntity(surface, entity, view, helper ? RenderTheme.RubberBand : RenderTheme.Preview);
        }

        if (options.SelectionRectangle is { } rect)
        {
            double x = Math.Min(rect.A.X, rect.B.X), y = Math.Min(rect.A.Y, rect.B.Y);
            double w = Math.Abs(rect.A.X - rect.B.X), h = Math.Abs(rect.A.Y - rect.B.Y);
            surface.DrawRectangle(x, y, w, h, rect.Crossing ? RenderTheme.CrossingSelect : RenderTheme.WindowSelect, 40);
        }

        if (options.PolarTrack is { } polar)
        {
            // A dashed ray from the base point through the cursor to the edge of the view.
            var start = view.WorldToScreen(polar.From);
            double a = GeoMath.DegreesToRadians(polar.AngleDegrees);
            double reach = Math.Abs(start.X) + Math.Abs(start.Y) + 2.0 * (view.ViewportWidth + view.ViewportHeight);
            var end = new ScreenPoint(start.X + reach * Math.Cos(a), start.Y - reach * Math.Sin(a));
            DrawSegment(surface, view, start, end, RenderTheme.PolarTrack);
        }

        foreach (var line in options.TrackLines)
        {
            // A dashed line from the acquired point through the cursor to the edge of the view.
            var start = view.WorldToScreen(line.From);
            double a = GeoMath.DegreesToRadians(line.AngleDegrees);
            double reach = Math.Abs(start.X) + Math.Abs(start.Y) + 2.0 * (view.ViewportWidth + view.ViewportHeight);
            var end = new ScreenPoint(start.X + reach * Math.Cos(a), start.Y - reach * Math.Sin(a));
            DrawSegment(surface, view, start, end, RenderTheme.PolarTrack);
        }

        foreach (var point in options.TrackPoints)
        {
            var s = view.WorldToScreen(point);
            if (Math.Abs(s.X) > SafeCoordinate || Math.Abs(s.Y) > SafeCoordinate) continue;
            surface.DrawLine(s.X - 5, s.Y, s.X + 5, s.Y, RenderTheme.SnapMarker);
            surface.DrawLine(s.X, s.Y - 5, s.X, s.Y + 5, RenderTheme.SnapMarker);
        }

        if (options.Snap is { } snap) DrawSnapMarker(surface, view.WorldToScreen(snap.Point), snap.Mode);

        if (options.Crosshair is { } c)
        {
            double gap = options.PickBoxHalfSize;
            if (options.ShowCrosshairArms)
            {
                double arm = options.CrosshairArmLength;
                double left = arm > 0 ? c.X - arm : 0, right = arm > 0 ? c.X + arm : view.ViewportWidth;
                double top = arm > 0 ? c.Y - arm : 0, bottom = arm > 0 ? c.Y + arm : view.ViewportHeight;
                // The arms stop at the pick box, or meet in the centre when there is no box.
                surface.DrawLine(left, c.Y, c.X - gap, c.Y, RenderTheme.Crosshair);
                surface.DrawLine(c.X + gap, c.Y, right, c.Y, RenderTheme.Crosshair);
                surface.DrawLine(c.X, top, c.X, c.Y - gap, RenderTheme.Crosshair);
                surface.DrawLine(c.X, c.Y + gap, c.X, bottom, RenderTheme.Crosshair);
            }
            if (gap > 0) surface.DrawRectangle(c.X - gap, c.Y - gap, gap * 2, gap * 2, RenderTheme.Crosshair, 0);
        }

        return new RenderStats(candidates.Count, drawn);
    }

    /// <summary>Resolves ByLayer/ByBlock colour to a visible stroke on the dark canvas.</summary>
    /// <summary>A repeat shorter than this many pixels cannot be seen as dashes: the line is drawn solid.</summary>
    public const double MinPatternPixels = 4.0;

    private readonly Dictionary<(LinetypeDefinition Linetype, long Step), double[]> _patterns = new();

    /// <summary>Adds the object's linetype pattern, scaled to pixels, to <paramref name="style"/>.</summary>
    private RenderStyle WithLinetype(RenderStyle style, DrawingDocument document, Entity entity, ViewTransform view)
    {
        var linetype = document.ResolveLinetype(entity);
        if (linetype.IsContinuous) return style;
        double scale = document.Settings.LinetypeScale * entity.LinetypeScale * view.Scale;      // pixels per pattern unit
        return PixelPattern(linetype, scale) is { } pattern ? style with { Pattern = pattern } : style;
    }

    /// <summary>
    /// The pattern of <paramref name="linetype"/> in pixels at <paramref name="scale"/> pixels per unit, or null when
    /// it would be too fine to see (or the scale is not usable), in which case the line is drawn solid.
    /// </summary>
    public double[]? PixelPattern(LinetypeDefinition linetype, double scale)
    {
        if (linetype.IsContinuous || !(scale > 0) || !double.IsFinite(scale)) return null;
        double repeat = linetype.PatternLength * scale;
        if (!(repeat >= MinPatternPixels) || repeat > 1e7) return null;

        // Zoom changes smoothly; round the scale to about half a percent so that pens can be reused.
        long step = (long)Math.Round(Math.Log(scale) * 200.0);
        if (_patterns.TryGetValue((linetype, step), out var cached)) return cached;

        double rounded = Math.Exp(step / 200.0);
        var pixels = new double[linetype.Pattern.Count];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = linetype.Pattern[i] * rounded;
        if (_patterns.Count > 256) _patterns.Clear();
        _patterns[(linetype, step)] = pixels;
        return pixels;
    }

    public static RenderStyle ResolveStyle(Entity entity, Layer layer)
    {
        var color = entity.Color.IsExplicit ? entity.Color : layer.Color;
        byte r = color.R, g = color.G, b = color.B;
        if (r < 48 && g < 48 && b < 48) { r = g = b = 220; } // near-black would vanish on the dark background
        return new RenderStyle(r, g, b);
    }

    /// <summary>
    /// The style an object is drawn with: on screen its colour, one pixel wide; in a plot black (or its colour)
    /// and as wide as its lineweight.
    /// </summary>
    private RenderStyle StyleFor(Entity entity, Layer layer)
    {
        if (!_options.Plot) return ResolveStyle(entity, layer);
        var color = entity.Color.IsExplicit ? entity.Color : layer.Color;
        byte r = color.R, g = color.G, b = color.B;
        if (_options.PlotMonochrome || (r > 200 && g > 200 && b > 200)) r = g = b = 0;      // white would not show on paper
        double millimetres = entity.LineweightMm ?? layer.LineweightMm;
        double width = Math.Max(millimetres * _options.PixelsPerMm, 0.5);
        return new RenderStyle(r, g, b, width);
    }

    /// <summary>
    /// Draws the part of the model that a viewport shows, cut off at its window. Returns how many objects were drawn.
    /// </summary>
    private int DrawViewportContent(IRenderSurface surface, DrawingDocument document, ViewportEntity viewport, ViewTransform view)
    {
        var frame = viewport.Frame;
        var topLeft = view.WorldToScreen(new Point2D(frame.MinX, frame.MaxY));
        var bottomRight = view.WorldToScreen(new Point2D(frame.MaxX, frame.MinY));
        double x = Math.Max(topLeft.X, -SafeCoordinate), y = Math.Max(topLeft.Y, -SafeCoordinate);
        double w = Math.Min(bottomRight.X, SafeCoordinate) - x, h = Math.Min(bottomRight.Y, SafeCoordinate) - y;
        if (w < 1 || h < 1) return 0;

        // A view of the model in which the model point at the middle of the window lands where the window is.
        double scale = view.Scale * viewport.Scale;
        if (!(scale >= ViewTransform.MinScale) || scale > ViewTransform.MaxScale) return 0;
        var inside = new ViewTransform();
        inside.SetViewport(view.ViewportWidth, view.ViewportHeight);
        inside.SetView(viewport.ModelCenter + (view.Center - viewport.Center) / viewport.Scale, scale);

        int drawn = 0;
        surface.PushClip(x, y, w, h);
        try
        {
            foreach (var entity in document.QueryEntities(viewport.ModelBounds, Guid.Empty))
            {
                if (document.GetLayer(entity.LayerId) is not { IsDisplayed: true } layer) continue;
                if (_options.Plot && !layer.IsPlotted) continue;
                DrawEntity(surface, entity, inside, WithLinetype(StyleFor(entity, layer), document, entity, inside));
                drawn++;
            }
        }
        finally
        {
            surface.PopClip();
        }
        return drawn;
    }

    public static IEnumerable<Point2D> GetGripPoints(Entity entity) => EntityGrips.GetGripPoints(entity);

    private void DrawEntity(IRenderSurface surface, Entity entity, ViewTransform view, in RenderStyle style)
    {
        // Level of detail: anything smaller than a pixel is drawn as a dot.
        var bounds = entity.Bounds;
        if (Math.Max(bounds.Width, bounds.Height) * view.Scale < 1.0)
        {
            var s = view.WorldToScreen(bounds.Center);
            surface.DrawLine(s.X - 0.5, s.Y, s.X + 0.5, s.Y, style);
            return;
        }

        switch (entity)
        {
            case LineEntity line:
                DrawSegment(surface, view, view.WorldToScreen(line.Start), view.WorldToScreen(line.End), style);
                break;

            case CircleEntity circle:
                DrawCircle(surface, view, circle, style);
                break;

            case PolylineEntity polyline:
                DrawPolyline(surface, view, polyline, style);
                break;

            case ArcEntity arc:
                DrawArc(surface, view, arc, style);
                break;

            case DimensionEntity dimension:
                DrawDimension(surface, view, dimension, style);
                break;

            case ViewportEntity viewport:
            {
                // The frame only; what is seen through it is drawn separately, under the sheet's own objects.
                var f = viewport.Frame;
                var p1 = view.WorldToScreen(new Point2D(f.MinX, f.MinY));
                var p2 = view.WorldToScreen(new Point2D(f.MaxX, f.MinY));
                var p3 = view.WorldToScreen(new Point2D(f.MaxX, f.MaxY));
                var p4 = view.WorldToScreen(new Point2D(f.MinX, f.MaxY));
                var frameStyle = style with { Pattern = null };
                DrawSegment(surface, view, p1, p2, frameStyle);
                DrawSegment(surface, view, p2, p3, frameStyle);
                DrawSegment(surface, view, p3, p4, frameStyle);
                DrawSegment(surface, view, p4, p1, frameStyle);
                break;
            }

            case GridLineEntity grid:
            {
                if (grid.IsArc) DrawArc(surface, view, new ArcEntity(grid.Center, grid.Radius, grid.StartAngle, grid.EndAngle), style);
                else DrawSegment(surface, view, view.WorldToScreen(grid.Start), view.WorldToScreen(grid.End), style);

                // The label circles and the label are solid whatever the linetype of the line.
                var solid = style with { Pattern = null };
                var plain = solid with { Dashed = false, Width = 1.0 };
                double radius = grid.BubbleDiameter / 2.0 * view.Scale, textPixels = grid.TextHeight * view.Scale;
                foreach (var center in grid.BubbleCenters)
                {
                    var c = view.WorldToScreen(center);
                    if (Math.Abs(c.X) > SafeCoordinate || Math.Abs(c.Y) > SafeCoordinate || radius > SafeCoordinate) continue;
                    if (radius >= 1.0) surface.DrawCircle(c.X, c.Y, radius, solid);
                    if (textPixels >= MinTextPixels) surface.DrawText(grid.Label, c.X, c.Y, textPixels, 0, plain);
                }
                break;
            }

            case TextEntity text:
            {
                double pixels = text.Height * view.Scale;
                var plain = style with { Pattern = null, Dashed = false, Width = 1.0 };
                var look = new TextLook(text.Font, text.Bold, text.Italic, text.Underline, AnchorOf(text.Justify));
                for (int i = 0; i < text.Lines.Count; i++)
                {
                    string line = text.Lines[i];
                    if (line.Length == 0) continue;
                    var at = view.WorldToScreen(text.LineAnchor(i));
                    if (Math.Abs(at.X) > SafeCoordinate || Math.Abs(at.Y) > SafeCoordinate) continue;
                    if (pixels >= MinTextPixels) surface.DrawStyledText(line, at.X, at.Y, pixels, text.Angle, look, plain);
                    else
                    {
                        // Too small to read: a stroke as long as the line, where the line is.
                        double length = Math.Max(TextMeasure.Width(line, text.Font, text.Bold, text.Italic, pixels), 1.0);
                        double from = look.Anchor < 0 ? 0 : look.Anchor == 0 ? -length / 2 : -length;
                        double cos = Math.Cos(text.Angle), sin = Math.Sin(text.Angle);
                        surface.DrawLine(at.X + from * cos, at.Y - from * sin, at.X + (from + length) * cos, at.Y - (from + length) * sin, plain);
                    }
                }
                break;
            }

            case TableEntity table:
                DrawTable(surface, table, view, style);
                break;

            case BlockReferenceEntity block:
                // The contents take the look of the reference (its layer's colour and linetype, or its own).
                foreach (var inner in block.Resolved) DrawEntity(surface, inner, view, style);
                break;
        }
    }

    private static int AnchorOf(TextJustify justify) => justify switch { TextJustify.Left => -1, TextJustify.Center => 0, _ => 1 };

    /// <summary>The ruled lines of a table and the text of its cells, each cut off at its own cell.</summary>
    private static void DrawTable(IRenderSurface surface, TableEntity table, ViewTransform view, in RenderStyle style)
    {
        var solid = style with { Pattern = null, Dashed = false };
        var plain = solid with { Width = 1.0 };
        double left = table.Position.X, right = left + table.Width, top = table.Position.Y, bottom = top - table.Height;

        void Rule(double x1, double y1, double x2, double y2, in RenderStyle ruleStyle) =>
            DrawSegment(surface, view, view.WorldToScreen(new Point2D(x1, y1)), view.WorldToScreen(new Point2D(x2, y2)), ruleStyle);

        Rule(left, top, right, top, solid);
        for (int r = 0; r <= table.RowCount; r++) Rule(left, table.RowTop(r), right, table.RowTop(r), solid);
        // The columns stop under the title, which runs across the whole width.
        for (int c = 0; c <= table.ColumnCount; c++) Rule(table.ColumnLeft(c), table.RowTop(0), table.ColumnLeft(c), bottom, solid);
        if (table.TitleHeight > 0)
        {
            Rule(left, top, left, table.RowTop(0), solid);
            Rule(right, top, right, table.RowTop(0), solid);
        }

        double pixels = table.TextHeight * view.Scale;
        if (pixels < MinTextPixels) return;                     // only the ruling when the text is too small to read
        var visible = view.VisibleBounds;

        void Cell(string text, BoundingBox box, double height, bool bold, TextJustify justify)
        {
            if (string.IsNullOrWhiteSpace(text) || !box.Intersects(visible)) return;
            var a = view.WorldToScreen(new Point2D(box.MinX, box.MaxY));
            var b = view.WorldToScreen(new Point2D(box.MaxX, box.MinY));
            double x = Math.Max(a.X, -SafeCoordinate), y = Math.Max(a.Y, -SafeCoordinate);
            double w = Math.Min(b.X, SafeCoordinate) - x, h = Math.Min(b.Y, SafeCoordinate) - y;
            if (w < 1 || h < 1) return;

            double anchorX = justify switch
            {
                TextJustify.Left => box.MinX + table.CellMargin,
                TextJustify.Center => (box.MinX + box.MaxX) / 2,
                _ => box.MaxX - table.CellMargin,
            };
            var at = view.WorldToScreen(new Point2D(anchorX, (box.MinY + box.MaxY) / 2));
            surface.PushClip(x, y, w, h);
            surface.DrawStyledText(text, at.X, at.Y, height * view.Scale, 0, new TextLook(table.Font, bold, false, false, AnchorOf(justify)), plain);
            surface.PopClip();
        }

        if (table.TitleHeight > 0)
            Cell(table.Title, new BoundingBox(left, table.RowTop(0), right, top), table.TitleTextHeight, true, TextJustify.Center);
        for (int r = 0; r < table.RowCount; r++)
        {
            bool header = table.HeaderRow && r == 0;
            for (int c = 0; c < table.ColumnCount; c++)
                Cell(table.GetCell(r, c), table.CellBox(r, c), table.TextHeight, header, header ? TextJustify.Center : table.Justify);
        }
    }

    private static void FillSafe(IRenderSurface surface, ViewTransform view, in RenderStyle style, params Point2D[] corners)
    {
        var points = new ScreenPoint[corners.Length];
        for (int i = 0; i < corners.Length; i++)
        {
            points[i] = view.WorldToScreen(corners[i]);
            if (Math.Abs(points[i].X) > SafeCoordinate || Math.Abs(points[i].Y) > SafeCoordinate) return;
        }
        surface.FillPolygon(points, style);
    }

    /// <summary>Text smaller than this many pixels is left out (it could not be read anyway).</summary>
    public const double MinTextPixels = 3.0;

    private void DrawDimension(IRenderSurface surface, ViewTransform view, DimensionEntity dimension, in RenderStyle style)
    {
        // Dimensions are always drawn with solid lines, whatever the linetype of their layer.
        var solid = style with { Pattern = null };
        var g = dimension.BuildGeometry();

        foreach (var (a, b) in g.Lines)
            DrawSegment(surface, view, view.WorldToScreen(a), view.WorldToScreen(b), solid);

        foreach (var arc in g.Arcs)
        {
            if (arc.Sweep >= 2.0 * Math.PI - 1e-9)
                DrawCircle(surface, view, new CircleEntity(arc.Center, arc.Radius), solid);
            else
                DrawArc(surface, view, new ArcEntity(arc.Center, arc.Radius, arc.StartAngle, arc.StartAngle + arc.Sweep), solid);
        }

        double height = dimension.TextHeight * view.Scale;          // pixels
        if (height < MinTextPixels) return;                        // arrows and text too small to see

        double size = dimension.Style.ArrowSize;
        var fill = solid with { Dashed = false, Width = 1.0 };
        foreach (var (tip, direction) in g.Arrows)
        {
            var side = new Vector2D(-direction.Y, direction.X);
            switch (g.Arrow)
            {
                case DimensionArrow.Arrow:
                {
                    var back = tip - direction * size;
                    FillSafe(surface, view, fill, tip, back + side * (size / 6.0), back - side * (size / 6.0));
                    break;
                }
                case DimensionArrow.Tick:
                {
                    // A slanted stroke through the end of the dimension line.
                    var slant = (direction + side).Normalized() * (size / 2.0);
                    DrawSegment(surface, view, view.WorldToScreen(tip - slant), view.WorldToScreen(tip + slant), fill with { Width = 2.0 });
                    break;
                }
                case DimensionArrow.Dot:
                {
                    var points = new Point2D[12];
                    for (int k = 0; k < points.Length; k++)
                        points[k] = tip + Vector2D.FromPolar(size / 4.0, 2.0 * Math.PI * k / points.Length);
                    FillSafe(surface, view, fill, points);
                    break;
                }
            }
        }

        var at = view.WorldToScreen(g.TextCenter);
        if (Math.Abs(at.X) <= SafeCoordinate && Math.Abs(at.Y) <= SafeCoordinate)
            surface.DrawText(dimension.GetText(_settings), at.X, at.Y, height, g.TextAngle, fill);
    }

    private void DrawArc(IRenderSurface surface, ViewTransform view, ArcEntity arc, in RenderStyle style)
    {
        var c = view.WorldToScreen(arc.Center);
        double r = arc.Radius * view.Scale;
        double sweep = arc.Sweep;

        // Chords short enough that the curve is never more than 0.25 px away from them.
        double step = 2.0 * Math.Acos(Math.Max(-1.0, 1.0 - 0.25 / Math.Max(r, 0.26)));
        int count = (int)Math.Clamp(Math.Ceiling(sweep / step), 2, 20000);

        _buffer.Clear();
        bool safe = true;
        for (int i = 0; i <= count; i++)
        {
            double a = arc.StartAngle + sweep * i / count;
            var p = new ScreenPoint(c.X + r * Math.Cos(a), c.Y - r * Math.Sin(a)); // screen Y points down
            if (Math.Abs(p.X) > SafeCoordinate || Math.Abs(p.Y) > SafeCoordinate) safe = false;
            _buffer.Add(p);
        }

        if (safe)
        {
            surface.DrawPolyline(_buffer, false, style);
            return;
        }
        for (int i = 1; i < _buffer.Count; i++) DrawSegment(surface, view, _buffer[i - 1], _buffer[i], style);
    }

    private const double MaxStrokePixels = 4000;

    private void DrawPolyline(IRenderSurface surface, ViewTransform view, PolylineEntity polyline, in RenderStyle style)
    {
        var vertices = polyline.Vertices;
        if (vertices.Count < 2) return;
        int segments = polyline.SegmentCount;
        bool closed = polyline.IsClosed && vertices.Count > 2;

        if (polyline.HasUniformWidth)
        {
            // One stroke for the whole polyline, at its width (never thinner than the hairline).
            double w = polyline.Width;
            var stroke = w > 0 ? style with { Width = Math.Max(style.Width, Math.Min(w * view.Scale, MaxStrokePixels)) } : style;
            _buffer.Clear();
            _buffer.Add(view.WorldToScreen(vertices[0]));
            for (int i = 0; i < segments; i++) AppendSegment(view, polyline, i, skipEnd: closed && i == segments - 1);
            Stroke(surface, view, closed, stroke);
            return;
        }

        // Widths differ along the polyline: draw runs of equal constant width as one stroke each,
        // and tapered segments as filled shapes.
        int index = 0;
        while (index < segments)
        {
            var (sw, ew) = polyline.GetSegmentWidth(index);
            if (sw != ew)
            {
                DrawTaperedSegment(surface, view, polyline, index, style);
                index++;
                continue;
            }

            var (first, _) = polyline.GetSegment(index);
            _buffer.Clear();
            _buffer.Add(view.WorldToScreen(first));
            while (index < segments)
            {
                var (s2, e2) = polyline.GetSegmentWidth(index);
                if (s2 != sw || e2 != sw) break;
                AppendSegment(view, polyline, index, skipEnd: false);
                index++;
            }
            var stroke = sw > 0 ? style with { Width = Math.Max(style.Width, Math.Min(sw * view.Scale, MaxStrokePixels)) } : style;
            Stroke(surface, view, false, stroke);
        }
    }

    /// <summary>Appends the screen points of one segment (after its start point) to the buffer.</summary>
    private void AppendSegment(ViewTransform view, PolylineEntity polyline, int index, bool skipEnd)
    {
        var (_, b) = polyline.GetSegment(index);
        double bulge = polyline.GetBulge(index);
        if (polyline.TryGetArc(index, out var center, out double radius, out double start, out double sweep))
        {
            int count = ChordCount(radius * view.Scale, sweep);
            for (int k = 1; k < count; k++)
            {
                double t = (double)k / count;
                // Chords along the arc, in the direction of travel from A to B.
                double angle = bulge > 0 ? start + sweep * t : start + sweep * (1.0 - t);
                _buffer.Add(view.WorldToScreen(ArcMath.PointAt(center, radius, angle)));
            }
        }
        if (!skipEnd) _buffer.Add(view.WorldToScreen(b)); // a closed figure is closed by the surface
    }

    /// <summary>Number of chords so the curve is never more than 0.25 px from them.</summary>
    private static int ChordCount(double radiusPixels, double sweep)
    {
        double step = 2.0 * Math.Acos(Math.Max(-1.0, 1.0 - 0.25 / Math.Max(radiusPixels, 0.26)));
        return (int)Math.Clamp(Math.Ceiling(sweep / step), 2, 20000);
    }

    private void Stroke(IRenderSurface surface, ViewTransform view, bool closed, in RenderStyle stroke)
    {
        if (_buffer.Count < 2) return;
        bool safe = true;
        foreach (var p in _buffer)
            if (Math.Abs(p.X) > SafeCoordinate || Math.Abs(p.Y) > SafeCoordinate) { safe = false; break; }

        if (safe)
        {
            surface.DrawPolyline(_buffer, closed, stroke);
            return;
        }
        // Deeply zoomed in: clip each chord so the backend never sees huge coordinates.
        for (int i = 1; i < _buffer.Count; i++) DrawSegment(surface, view, _buffer[i - 1], _buffer[i], stroke);
        if (closed) DrawSegment(surface, view, _buffer[^1], _buffer[0], stroke);
    }

    /// <summary>A segment whose width changes from its start to its end, drawn as a filled outline.</summary>
    private void DrawTaperedSegment(IRenderSurface surface, ViewTransform view, PolylineEntity polyline, int index, in RenderStyle style)
    {
        var (a, b) = polyline.GetSegment(index);
        var (sw, ew) = polyline.GetSegmentWidth(index);
        double bulge = polyline.GetBulge(index);
        bool isArc = polyline.TryGetArc(index, out var center, out double radius, out double start, out double sweep);
        int count = isArc ? ChordCount((radius + Math.Max(sw, ew) / 2.0) * view.Scale, sweep) : 1;

        var left = new List<ScreenPoint>(count + 1);
        var right = new List<ScreenPoint>(count + 1);
        _buffer.Clear();
        bool safe = true;

        for (int k = 0; k <= count; k++)
        {
            double t = (double)k / count;
            double half = (sw + (ew - sw) * t) / 2.0;
            Point2D on;
            Vector2D normal; // unit vector to the left of the direction of travel
            if (isArc)
            {
                double angle = bulge > 0 ? start + sweep * t : start + sweep * (1.0 - t);
                on = ArcMath.PointAt(center, radius, angle);
                var radial = new Vector2D(Math.Cos(angle), Math.Sin(angle));
                normal = bulge > 0 ? -radial : radial; // counter-clockwise travel keeps the centre on the left
            }
            else
            {
                on = a + (b - a) * t;
                var dir = (b - a).Normalized();
                normal = new Vector2D(-dir.Y, dir.X);
            }

            var centre = view.WorldToScreen(on);
            var l = view.WorldToScreen(on + normal * half);
            var r = view.WorldToScreen(on - normal * half);
            if (Math.Abs(l.X) > SafeCoordinate || Math.Abs(l.Y) > SafeCoordinate ||
                Math.Abs(r.X) > SafeCoordinate || Math.Abs(r.Y) > SafeCoordinate) safe = false;
            _buffer.Add(centre);
            left.Add(l);
            right.Add(r);
        }

        if (safe)
        {
            right.Reverse();
            left.AddRange(right);
            surface.FillPolygon(left, style);
        }
        // The centre line keeps the segment visible where it tapers to nothing or is thinner than a pixel.
        Stroke(surface, view, false, style);
    }

    private void DrawCircle(IRenderSurface surface, ViewTransform view, CircleEntity circle, in RenderStyle style)
    {
        var c = view.WorldToScreen(circle.Center);
        double r = circle.Radius * view.Scale;
        if (r < 4000 && Math.Abs(c.X) < SafeCoordinate && Math.Abs(c.Y) < SafeCoordinate)
        {
            surface.DrawCircle(c.X, c.Y, r, style);
            return;
        }

        // Very large on screen: draw only the visible part as clipped chords (chord error <= 0.25 px).
        double step = 2.0 * Math.Acos(Math.Max(-1.0, 1.0 - 0.25 / r));
        int segments = (int)Math.Clamp(Math.Ceiling(2.0 * Math.PI / step), 64, 200000);
        double viewRadius = Math.Sqrt(view.ViewportWidth * view.ViewportWidth + view.ViewportHeight * view.ViewportHeight) / 2.0 + ClipMargin;
        double cx = c.X - view.ViewportWidth / 2.0, cy = c.Y - view.ViewportHeight / 2.0;
        double centreDistance = Math.Sqrt(cx * cx + cy * cy);
        if (Math.Abs(centreDistance - r) > viewRadius) return; // circumference does not reach the viewport

        // Angular window of the circumference that can be on screen.
        double start = 0, sweep = 2.0 * Math.PI;
        if (centreDistance > viewRadius)
        {
            double toView = Math.Atan2(-cy, -cx);
            double half = Math.Asin(Math.Min(1.0, viewRadius / centreDistance)) + Math.Min(Math.PI, 2.0 * viewRadius / r);
            if (half < Math.PI) { start = toView - half; sweep = 2.0 * half; }
        }

        int count = (int)Math.Clamp(Math.Ceiling(segments * sweep / (2.0 * Math.PI)), 8, 200000);
        var prev = new ScreenPoint(c.X + r * Math.Cos(start), c.Y + r * Math.Sin(start));
        for (int i = 1; i <= count; i++)
        {
            double a = start + sweep * i / count;
            var next = new ScreenPoint(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
            DrawSegment(surface, view, prev, next, style);
            prev = next;
        }
    }

    private static void DrawSegment(IRenderSurface surface, ViewTransform view, ScreenPoint a, ScreenPoint b, in RenderStyle style)
    {
        if (ClipToViewport(view, ref a, ref b)) surface.DrawLine(a.X, a.Y, b.X, b.Y, style);
    }

    /// <summary>Liang-Barsky clip of a screen segment to the viewport (plus a margin).</summary>
    public static bool ClipToViewport(ViewTransform view, ref ScreenPoint a, ref ScreenPoint b)
    {
        double minX = -ClipMargin, minY = -ClipMargin;
        double maxX = view.ViewportWidth + ClipMargin, maxY = view.ViewportHeight + ClipMargin;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double t0 = 0, t1 = 1;

        if (!ClipTest(-dx, a.X - minX, ref t0, ref t1)) return false;
        if (!ClipTest(dx, maxX - a.X, ref t0, ref t1)) return false;
        if (!ClipTest(-dy, a.Y - minY, ref t0, ref t1)) return false;
        if (!ClipTest(dy, maxY - a.Y, ref t0, ref t1)) return false;

        var start = a;
        if (t1 < 1) b = new ScreenPoint(start.X + t1 * dx, start.Y + t1 * dy);
        if (t0 > 0) a = new ScreenPoint(start.X + t0 * dx, start.Y + t0 * dy);
        return true;
    }

    private static bool ClipTest(double p, double q, ref double t0, ref double t1)
    {
        if (p == 0) return q >= 0;
        double r = q / p;
        if (p < 0)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }
        return true;
    }

    private static void DrawSnapMarker(IRenderSurface surface, ScreenPoint p, SnapModes mode)
    {
        const double h = 6;
        var style = RenderTheme.SnapMarker;
        switch (mode)
        {
            case SnapModes.Endpoint:
                surface.DrawRectangle(p.X - h, p.Y - h, 2 * h, 2 * h, style, 0);
                break;
            case SnapModes.Midpoint:
                surface.DrawPolyline(new[]
                {
                    new ScreenPoint(p.X - h, p.Y + h), new ScreenPoint(p.X + h, p.Y + h), new ScreenPoint(p.X, p.Y - h),
                }, true, style);
                break;
            case SnapModes.Center:
                surface.DrawCircle(p.X, p.Y, h, style);
                break;
            case SnapModes.Intersection:
                surface.DrawLine(p.X - h, p.Y - h, p.X + h, p.Y + h, style);
                surface.DrawLine(p.X - h, p.Y + h, p.X + h, p.Y - h, style);
                break;
            case SnapModes.Quadrant:
                surface.DrawPolyline(new[]
                {
                    new ScreenPoint(p.X, p.Y - h), new ScreenPoint(p.X + h, p.Y), new ScreenPoint(p.X, p.Y + h), new ScreenPoint(p.X - h, p.Y),
                }, true, style);
                break;
            case SnapModes.Perpendicular:
                surface.DrawPolyline(new[]
                {
                    new ScreenPoint(p.X - h, p.Y - h), new ScreenPoint(p.X - h, p.Y + h), new ScreenPoint(p.X + h, p.Y + h),
                }, false, style);
                surface.DrawPolyline(new[]
                {
                    new ScreenPoint(p.X - h, p.Y), new ScreenPoint(p.X, p.Y), new ScreenPoint(p.X, p.Y + h),
                }, false, style);
                break;
            case SnapModes.Tangent:
                surface.DrawCircle(p.X, p.Y + 1, h - 1, style);
                surface.DrawLine(p.X - h, p.Y - h, p.X + h, p.Y - h, style);
                break;
            case SnapModes.Nearest:
                surface.DrawPolyline(new[]
                {
                    new ScreenPoint(p.X - h, p.Y - h), new ScreenPoint(p.X + h, p.Y - h), new ScreenPoint(p.X - h, p.Y + h), new ScreenPoint(p.X + h, p.Y + h),
                }, true, style);
                break;
        }
    }

    private static void DrawGrid(IRenderSurface surface, DrawingDocument document, ViewTransform view)
    {
        double spacing = document.Settings.GridSpacing;
        if (!(spacing > 0) || !double.IsFinite(spacing)) return;

        // Keep grid lines at least ~14 px apart by stepping the spacing up.
        bool imperial = UnitConverter.IsImperial(document.Settings.Unit);
        int guard = 0;
        while (spacing * view.Scale < 14 && guard < 80)
        {
            // Imperial grids double (12, 24, 48...); metric grids step 1-5-10-50...
            spacing *= imperial ? 2 : (guard % 2 == 0 ? 5 : 2);
            guard++;
        }
        if (spacing * view.Scale < 14) return;

        var bounds = view.VisibleBounds;
        long x0 = (long)Math.Floor(bounds.MinX / spacing), x1 = (long)Math.Ceiling(bounds.MaxX / spacing);
        long y0 = (long)Math.Floor(bounds.MinY / spacing), y1 = (long)Math.Ceiling(bounds.MaxY / spacing);
        if (x1 - x0 > 2000 || y1 - y0 > 2000) return;

        for (long i = x0; i <= x1; i++)
        {
            if (i == 0) continue;
            double sx = view.WorldToScreen(new Point2D(i * spacing, 0)).X;
            surface.DrawLine(sx, 0, sx, view.ViewportHeight, i % 5 == 0 ? RenderTheme.GridMajor : RenderTheme.GridMinor);
        }
        for (long j = y0; j <= y1; j++)
        {
            if (j == 0) continue;
            double sy = view.WorldToScreen(new Point2D(0, j * spacing)).Y;
            surface.DrawLine(0, sy, view.ViewportWidth, sy, j % 5 == 0 ? RenderTheme.GridMajor : RenderTheme.GridMinor);
        }

        // Model axes through the origin.
        var origin = view.WorldToScreen(Point2D.Origin);
        if (origin.Y >= 0 && origin.Y <= view.ViewportHeight)
            surface.DrawLine(0, origin.Y, view.ViewportWidth, origin.Y, RenderTheme.AxisX);
        if (origin.X >= 0 && origin.X <= view.ViewportWidth)
            surface.DrawLine(origin.X, 0, origin.X, view.ViewportHeight, RenderTheme.AxisY);
    }
}
