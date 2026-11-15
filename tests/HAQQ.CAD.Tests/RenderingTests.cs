using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Rendering;
using HAQQ.CAD.Snapping;
using Xunit;
using static HAQQ.CAD.Tests.T;

namespace HAQQ.CAD.Tests;

public class ViewTransformTests
{
    private static ViewTransform View(double width = 800, double height = 600)
    {
        var view = new ViewTransform();
        view.SetViewport(width, height);
        return view;
    }

    [Fact]
    public void World_and_screen_round_trip_with_y_up()
    {
        var view = View();
        view.SetView(P(100, 50), 2.0);

        var centre = view.WorldToScreen(P(100, 50));
        Assert.Equal(new ScreenPoint(400, 300), centre);

        // +Y in the model goes up the screen (smaller pixel Y).
        Assert.Equal(new ScreenPoint(400, 280), view.WorldToScreen(P(100, 60)));
        Assert.Equal(new ScreenPoint(420, 300), view.WorldToScreen(P(110, 50)));

        var world = view.ScreenToWorld(view.WorldToScreen(P(-12345.678, 9876.54321)));
        Assert.Equal(-12345.678, world.X, 8);
        Assert.Equal(9876.54321, world.Y, 8);
        Assert.Equal(5.0, view.PixelsToWorld(10), 12);
    }

    [Fact]
    public void Zoom_keeps_the_point_under_the_cursor_fixed()
    {
        var view = View();
        view.SetView(P(10, 10), 3.0);
        var anchor = new ScreenPoint(650, 120);
        var before = view.ScreenToWorld(anchor);

        view.ZoomAt(anchor, 1.25);
        Assert.Equal(3.75, view.Scale, 12);
        var after = view.ScreenToWorld(anchor);
        Assert.Equal(before.X, after.X, 9);
        Assert.Equal(before.Y, after.Y, 9);

        for (int i = 0; i < 40; i++) view.ZoomAt(anchor, 0.8);
        after = view.ScreenToWorld(anchor);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);

        view.ZoomAt(anchor, 0);          // ignored
        view.ZoomAt(anchor, double.NaN); // ignored
        Assert.True(view.Scale > 0);
    }

    [Fact]
    public void Pan_moves_the_drawing_with_the_mouse()
    {
        var view = View();
        view.SetView(P(0, 0), 2.0);
        var grabbed = view.ScreenToWorld(new ScreenPoint(100, 100));
        view.Pan(30, -20);
        var now = view.WorldToScreen(grabbed);
        Assert.Equal(130.0, now.X, 9);
        Assert.Equal(80.0, now.Y, 9);
    }

    [Fact]
    public void Zoom_extents_fits_the_drawing()
    {
        var view = View(1000, 500);
        var box = new BoundingBox(100, 200, 500, 300);   // 400 x 100
        view.ZoomExtents(box, 0.05);
        Assert.Equal(P(300, 250), view.Center);
        Assert.Equal(2.25, view.Scale, 12);              // limited by width: 1000 * 0.9 / 400

        var a = view.WorldToScreen(P(100, 200));
        var b = view.WorldToScreen(P(500, 300));
        Assert.InRange(a.X, 0.0, 1000.0);
        Assert.InRange(b.X, 0.0, 1000.0);
        Assert.InRange(a.Y, 0.0, 500.0);
        Assert.InRange(b.Y, 0.0, 500.0);
        Assert.True(view.VisibleBounds.Contains(box));

        view.ZoomExtents(BoundingBox.Empty);
        Assert.Equal(Point2D.Origin, view.Center);
    }

    [Fact]
    public void Scale_is_clamped()
    {
        var view = View();
        view.SetView(P(0, 0), 1e30);
        Assert.Equal(ViewTransform.MaxScale, view.Scale);
        view.SetView(P(0, 0), 0);
        Assert.Equal(ViewTransform.MinScale, view.Scale);
    }
}

public class SceneRendererTests
{
    private static ViewTransform View(Point2D center, double scale)
    {
        var view = new ViewTransform();
        view.SetViewport(800, 600);
        view.SetView(center, scale);
        return view;
    }

    private static readonly RenderOptions NoGrid = new() { ShowGrid = false };

    [Fact]
    public void Draws_each_entity_type_at_the_right_place()
    {
        var doc = new DrawingDocument();
        AddLine(doc, 0, 0, 10, 0);
        AddCircle(doc, 0, 0, 5);
        doc.AddEntity(new PolylineEntity(new[] { P(0, 0), P(4, 0), P(4, 3) }, true) { LayerId = doc.CurrentLayerId });

        var surface = new RecordingSurface();
        var stats = new SceneRenderer().Render(surface, doc, View(P(0, 0), 10), NoGrid);
        Assert.Equal(3, stats.Drawn);

        var line = Assert.Single(surface.Lines);
        Assert.Equal(400.0, line.X1, 9);
        Assert.Equal(300.0, line.Y1, 9);
        Assert.Equal(500.0, line.X2, 9);
        Assert.Equal(300.0, line.Y2, 9);

        var circle = Assert.Single(surface.Circles);
        Assert.Equal(50.0, circle.R, 9);

        var poly = Assert.Single(surface.Polylines);
        Assert.True(poly.Closed);
        Assert.Equal(new ScreenPoint(440, 270), poly.Points[2]);
    }

    [Fact]
    public void Only_entities_near_the_viewport_are_visited()
    {
        var doc = new DrawingDocument();
        using (doc.BeginBatch())
            for (int x = 0; x < 200; x++)
            for (int y = 0; y < 100; y++)
                AddLine(doc, x * 100, y * 100, x * 100 + 60, y * 100 + 40);
        Assert.Equal(20000, doc.EntityCount);

        // 800x600 px at 2 px/unit shows a 400 x 300 unit window: at most a few dozen lines.
        var surface = new RecordingSurface();
        var stats = new SceneRenderer().Render(surface, doc, View(P(10000, 5000), 2), NoGrid);
        Assert.InRange(stats.Candidates, 1, 40);
        Assert.Equal(stats.Candidates, stats.Drawn);
        Assert.Equal(stats.Drawn, surface.Lines.Count);

        // Zoomed out to see everything: every entity is drawn (as a line or a sub-pixel dot).
        var all = new ViewTransform();
        all.SetViewport(800, 600);
        all.ZoomExtents(doc.GetExtents());
        var allStats = new SceneRenderer().Render(new RecordingSurface(), doc, all, NoGrid);
        Assert.Equal(20000, allStats.Drawn);
    }

    [Fact]
    public void Hidden_and_frozen_layers_are_not_drawn()
    {
        var doc = new DrawingDocument();
        var off = new Layer(Guid.NewGuid(), "OFF") { IsVisible = false };
        var frozen = new Layer(Guid.NewGuid(), "FROZEN") { IsFrozen = true };
        doc.AddLayer(off);
        doc.AddLayer(frozen);
        AddLine(doc, 0, 0, 10, 0);
        doc.AddEntity(new LineEntity(P(0, 1), P(10, 1)) { LayerId = off.Id });
        doc.AddEntity(new LineEntity(P(0, 2), P(10, 2)) { LayerId = frozen.Id });

        var surface = new RecordingSurface();
        var stats = new SceneRenderer().Render(surface, doc, View(P(5, 0), 10), NoGrid);
        Assert.Equal(1, stats.Drawn);
        Assert.Single(surface.Lines);
    }

    [Fact]
    public void Colour_is_resolved_from_the_entity_or_its_layer()
    {
        var doc = new DrawingDocument();
        var red = new Layer(Guid.NewGuid(), "RED") { Color = CadColor.FromRgb(255, 0, 0) };
        doc.AddLayer(red);
        doc.AddEntity(new LineEntity(P(0, 0), P(10, 0)) { LayerId = red.Id });
        doc.AddEntity(new LineEntity(P(0, 1), P(10, 1)) { LayerId = red.Id, Color = CadColor.FromRgb(0, 200, 255) });
        doc.AddEntity(new LineEntity(P(0, 2), P(10, 2)) { LayerId = red.Id, Color = CadColor.FromRgb(0, 0, 0) });

        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, View(P(5, 0), 10), NoGrid);
        var styles = surface.Lines.OrderByDescending(l => l.Y1).Select(l => l.Style).ToList();
        Assert.Equal(new RenderStyle(255, 0, 0), styles[0]);
        Assert.Equal(new RenderStyle(0, 200, 255), styles[1]);
        Assert.Equal(new RenderStyle(220, 220, 220), styles[2]); // black is shown light on the dark canvas
    }

    [Fact]
    public void Selection_preview_snap_marker_and_selection_rectangle_are_drawn()
    {
        var doc = new DrawingDocument();
        var line = AddLine(doc, 0, 0, 10, 0);
        var options = new RenderOptions
        {
            ShowGrid = false,
            IsSelected = id => id == line.Id,
            Preview = new Entity[] { new CircleEntity(P(0, 0), 2) },
            Snap = new SnapResult(P(10, 0), SnapModes.Endpoint, line.Id),
            SelectionRectangle = (new ScreenPoint(10, 10), new ScreenPoint(60, 40), true),
        };

        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, View(P(0, 0), 10), options);

        Assert.Equal(RenderTheme.Selection, Assert.Single(surface.Lines).Style);
        Assert.Equal(RenderTheme.Preview, Assert.Single(surface.Circles).Style);
        // 3 grips for the selected line, 1 selection rectangle, 1 endpoint snap marker.
        Assert.Equal(5, surface.Rectangles.Count);
        Assert.Contains(surface.Rectangles, r => r.Style == RenderTheme.CrossingSelect && r.W == 50 && r.H == 30);
        Assert.Contains(surface.Rectangles, r => r.Style == RenderTheme.SnapMarker && r.X == 494 && r.Y == 294);
    }

    [Fact]
    public void Grid_and_crosshair_are_drawn_when_requested()
    {
        var doc = new DrawingDocument();
        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, View(P(0, 0), 4), new RenderOptions { ShowGrid = true });
        Assert.True(surface.Lines.Count > 10);
        Assert.Contains(surface.Lines, l => l.Style == RenderTheme.AxisX);
        Assert.Contains(surface.Lines, l => l.Style == RenderTheme.AxisY);

        surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, View(P(0, 0), 4),
            new RenderOptions { ShowGrid = false, Crosshair = new ScreenPoint(100, 100), PickBoxHalfSize = 4 });
        Assert.Equal(4, surface.Lines.Count);
        Assert.Single(surface.Rectangles);
    }

    [Fact]
    public void Crosshair_styles_for_idle_point_picking_and_selecting()
    {
        var doc = new DrawingDocument();
        var view = View(P(0, 0), 4);
        RecordingSurface Draw(RenderOptions o)
        {
            o.ShowGrid = false;
            o.Crosshair = new ScreenPoint(300, 200);
            var s = new RecordingSurface();
            new SceneRenderer().Render(s, doc, view, o);
            return s;
        }

        // Idle: short arms that stop at a square in the middle.
        var idle = Draw(new RenderOptions { CrosshairArmLength = 50, PickBoxHalfSize = 5 });
        Assert.Equal(4, idle.Lines.Count);
        Assert.Contains(idle.Lines, l => l.X1 == 250 && l.X2 == 295 && l.Y1 == 200);
        Assert.Contains(idle.Lines, l => l.X1 == 305 && l.X2 == 350);
        Assert.Contains(idle.Lines, l => l.Y1 == 150 && l.Y2 == 195 && l.X1 == 300);
        var box = Assert.Single(idle.Rectangles);
        Assert.Equal(10.0, box.W);
        Assert.Equal((byte)0, box.Fill);

        // Command asking for a point: the arms meet in the centre, no square.
        var point = Draw(new RenderOptions { CrosshairArmLength = 50, PickBoxHalfSize = 0 });
        Assert.Empty(point.Rectangles);
        Assert.Contains(point.Lines, l => l.X1 == 250 && l.X2 == 300);
        Assert.Contains(point.Lines, l => l.X1 == 300 && l.X2 == 350);

        // Command collecting a selection: the square alone.
        var select = Draw(new RenderOptions { CrosshairArmLength = 50, PickBoxHalfSize = 5, ShowCrosshairArms = false });
        Assert.Empty(select.Lines);
        Assert.Single(select.Rectangles);
    }

    [Fact]
    public void Extreme_zoom_never_sends_huge_coordinates_to_the_backend()
    {
        var doc = new DrawingDocument();
        AddLine(doc, -1e7, -1e7, 1e7, 1e7);                       // passes through the view
        AddCircle(doc, 1e6, 0, 1e6);                              // circumference passes through the origin
        doc.AddEntity(new PolylineEntity(new[] { P(-1e7, 1), P(1e7, 1), P(1e7, 1e7) }, false) { LayerId = doc.CurrentLayerId });

        var surface = new RecordingSurface();
        var view = View(P(0, 0), 5000);                           // 0.16 x 0.12 units on screen
        new SceneRenderer().Render(surface, doc, view, NoGrid);

        Assert.Empty(surface.Circles);
        Assert.Empty(surface.Polylines);
        Assert.NotEmpty(surface.Lines);
        foreach (var l in surface.Lines)
        {
            Assert.InRange(l.X1, -100.0, 900.0);
            Assert.InRange(l.X2, -100.0, 900.0);
            Assert.InRange(l.Y1, -100.0, 700.0);
            Assert.InRange(l.Y2, -100.0, 700.0);
        }

        // The diagonal line runs through the screen centre from the lower-left to the upper-right.
        Assert.Contains(surface.Lines, l => Math.Abs((l.X1 + l.X2) / 2 - 400) < 1 && Math.Abs((l.Y1 + l.Y2) / 2 - 300) < 1);
        // The circle is seen as a near-vertical arc through the centre of the screen.
        Assert.Contains(surface.Lines, l => Math.Abs(l.X1 - 400) < 2 && Math.Abs(l.X2 - 400) < 2 && Math.Abs(l.Y1 - l.Y2) > 1);
    }

    [Fact]
    public void Clip_rejects_segments_outside_the_viewport()
    {
        var view = View(P(0, 0), 1);
        var a = new ScreenPoint(-500, -500);
        var b = new ScreenPoint(-400, 2000);
        Assert.False(SceneRenderer.ClipToViewport(view, ref a, ref b));

        a = new ScreenPoint(-1000, 300);
        b = new ScreenPoint(2000, 300);
        Assert.True(SceneRenderer.ClipToViewport(view, ref a, ref b));
        Assert.Equal(-64.0, a.X, 9);
        Assert.Equal(864.0, b.X, 9);
        Assert.Equal(300.0, a.Y, 9);
    }
}
