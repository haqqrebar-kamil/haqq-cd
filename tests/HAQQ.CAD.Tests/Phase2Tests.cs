using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Documents;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Presentation;
using HAQQ.CAD.Rendering;
using HAQQ.CAD.Snapping;
using Xunit;
using static HAQQ.CAD.Tests.T;

namespace HAQQ.CAD.Tests;

public class ArcTests
{
    private const double Deg = Math.PI / 180.0;

    [Fact]
    public void Arc_geometry()
    {
        var arc = new ArcEntity(P(10, 10), 5, 0, 90 * Deg);
        Assert.True(arc.IsValid);
        Assert.Equal(90 * Deg, arc.Sweep, 12);
        Assert.Equal(5 * Math.PI / 2, arc.Length, 12);
        Assert.Equal(15.0, arc.StartPoint.X, 12);
        Assert.Equal(10.0, arc.StartPoint.Y, 12);
        Assert.Equal(10.0, arc.EndPoint.X, 12);
        Assert.Equal(15.0, arc.EndPoint.Y, 12);
        Assert.Equal(10 + 5 * Math.Cos(45 * Deg), arc.MidPoint.X, 12);

        var b = arc.Bounds;                              // only the first quadrant
        Assert.Equal(10.0, b.MinX, 9);
        Assert.Equal(10.0, b.MinY, 9);
        Assert.Equal(15.0, b.MaxX, 9);
        Assert.Equal(15.0, b.MaxY, 9);

        // An arc that crosses 0 degrees includes the +X quadrant point in its bounds.
        var wrap = new ArcEntity(P(0, 0), 5, 300 * Deg, 60 * Deg);
        Assert.Equal(120 * Deg, wrap.Sweep, 12);
        Assert.Equal(5.0, wrap.Bounds.MaxX, 9);
        Assert.Equal(2.5, wrap.Bounds.MinX, 9);

        Assert.False(new ArcEntity(P(0, 0), 5, 1, 1).IsValid);      // no sweep
        Assert.False(new ArcEntity(P(0, 0), 0, 0, 1).IsValid);      // no radius
    }

    [Fact]
    public void Distance_to_arc_uses_the_curve_or_the_nearest_end()
    {
        var arc = new ArcEntity(P(0, 0), 10, 0, 90 * Deg);
        Assert.Equal(2.0, arc.DistanceTo(P(12 * Math.Cos(45 * Deg), 12 * Math.Sin(45 * Deg))), 9);   // outside, facing the arc
        Assert.Equal(3.0, arc.DistanceTo(P(7 * Math.Cos(30 * Deg), 7 * Math.Sin(30 * Deg))), 9);     // inside
        Assert.Equal(5.0, arc.DistanceTo(P(10, -5)), 9);                                              // beyond the start point
        Assert.Equal(Math.Sqrt(200) , arc.DistanceTo(P(-10, 0)), 9);                                  // opposite side: nearest end is (0,10)
    }

    [Fact]
    public void Arc_box_intersection_and_selection()
    {
        var doc = new DrawingDocument();
        var arc = new ArcEntity(P(0, 0), 10, 0, 90 * Deg) { LayerId = doc.CurrentLayerId };
        doc.AddEntity(arc);

        Assert.True(arc.IntersectsBox(new BoundingBox(6, 6, 8, 8)));        // straddles the curve
        Assert.False(arc.IntersectsBox(new BoundingBox(1, 1, 3, 3)));       // inside the radius, no contact
        Assert.False(arc.IntersectsBox(new BoundingBox(-8, -8, -6, -6)));   // on the circle's far side, not on the arc
        Assert.False(arc.IntersectsBox(new BoundingBox(-8.5, 4, -6, 7.5))); // touches the full circle but not this arc
        Assert.True(arc.IntersectsBox(new BoundingBox(-1, -1, 11, 11)));    // contains the whole arc

        Assert.Equal(arc.Id, SelectionService.PickAt(doc, P(7.2, 7.2), 0.5)!.Id);
        Assert.Null(SelectionService.PickAt(doc, P(-7.07, -7.07), 0.5));    // the missing part of the circle
        Assert.Single(SelectionService.SelectInBox(doc, new BoundingBox(-1, -1, 11, 11), crossing: false));
        Assert.Empty(SelectionService.SelectInBox(doc, new BoundingBox(6, 6, 8, 8), crossing: false));
        Assert.Single(SelectionService.SelectInBox(doc, new BoundingBox(6, 6, 8, 8), crossing: true));
    }

    [Fact]
    public void Arc_transforms()
    {
        var arc = new ArcEntity(P(0, 0), 10, 0, 90 * Deg);
        arc.Transform(Transform2D.Translation(new Vector2D(100, 50)));
        Assert.Equal(P(100, 50), arc.Center);
        Assert.Equal(0.0, arc.StartAngle);                                  // exactly unchanged by a move
        Assert.Equal(90 * Deg, arc.EndAngle);

        arc.Transform(Transform2D.Rotation(90 * Deg, P(100, 50)));
        Assert.Equal(90 * Deg, arc.StartAngle, 9);
        Assert.Equal(180 * Deg, arc.EndAngle, 9);
        Assert.Equal(10.0, arc.Radius, 9);

        arc.Transform(Transform2D.Scaling(2, P(100, 50)));
        Assert.Equal(20.0, arc.Radius, 9);
        Assert.Equal(90 * Deg, arc.Sweep, 9);

        // Mirror in the Y axis: still a 90 degree arc, direction of travel swapped.
        var mirrored = new ArcEntity(P(5, 0), 2, 0, 90 * Deg);
        mirrored.Transform(new Transform2D(-1, 0, 0, 1, 0, 0));
        Assert.Equal(P(-5, 0), mirrored.Center);
        Assert.Equal(90 * Deg, mirrored.StartAngle, 9);
        Assert.Equal(180 * Deg, mirrored.EndAngle, 9);
    }

    [Fact]
    public void Arc_snaps_and_round_trips_through_hcad()
    {
        var doc = new DrawingDocument();
        var arc = new ArcEntity(P(0, 0), 10, 0, 90 * Deg) { LayerId = doc.CurrentLayerId };
        doc.AddEntity(arc);
        const SnapModes all = SnapModes.Endpoint | SnapModes.Midpoint | SnapModes.Center;
        Assert.Equal(SnapModes.Endpoint, SnapEngine.FindSnap(doc, P(10.1, 0.1), 0.5, all)!.Value.Mode);
        Assert.Equal(SnapModes.Midpoint, SnapEngine.FindSnap(doc, P(7.1, 7.1), 0.5, all)!.Value.Mode);
        Assert.Equal(SnapModes.Center, SnapEngine.FindSnap(doc, P(0.1, 0.1), 0.5, all)!.Value.Mode);

        var loaded = HcadSerializer.LoadFromBytes(HcadSerializer.SaveToBytes(doc));
        Assert.Empty(loaded.Warnings);
        var back = Assert.IsType<ArcEntity>(Assert.Single(loaded.Document.Entities));
        Assert.Equal(arc.Id, back.Id);
        Assert.Equal(arc.EndAngle, back.EndAngle);
    }

    [Fact]
    public void Arc_is_drawn_as_a_curve_with_y_up()
    {
        var doc = new DrawingDocument();
        doc.AddEntity(new ArcEntity(P(0, 0), 10, 0, 90 * Deg) { LayerId = doc.CurrentLayerId });
        var view = new ViewTransform();
        view.SetViewport(800, 600);
        view.SetView(P(0, 0), 10);
        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, view, new RenderOptions { ShowGrid = false });

        var poly = Assert.Single(surface.Polylines);
        Assert.False(poly.Closed);
        Assert.True(poly.Points.Length > 8);
        Assert.Equal(500.0, poly.Points[0].X, 6);        // start at (10,0) -> right of centre
        Assert.Equal(300.0, poly.Points[0].Y, 6);
        Assert.Equal(400.0, poly.Points[^1].X, 6);       // end at (0,10) -> above centre on screen
        Assert.Equal(200.0, poly.Points[^1].Y, 6);
        foreach (var p in poly.Points)                   // every vertex lies on the circle
            Assert.Equal(100.0, Math.Sqrt((p.X - 400) * (p.X - 400) + (p.Y - 300) * (p.Y - 300)), 6);
    }

    [Fact]
    public void Three_point_circumcenter_and_segment_circle_intersections()
    {
        Assert.True(ArcMath.TryCircumcenter(P(1, 0), P(0, 1), P(-1, 0), out var c));
        Assert.Equal(0.0, c.X, 12);
        Assert.Equal(0.0, c.Y, 12);
        Assert.False(ArcMath.TryCircumcenter(P(0, 0), P(1, 1), P(2, 2), out _));

        Assert.Equal(2, ArcMath.SegmentCircleIntersections(P(-10, 0), P(10, 0), P(0, 0), 5, out var a, out var b));
        Assert.Equal(-5.0, a.X, 9);
        Assert.Equal(5.0, b.X, 9);
        Assert.Equal(1, ArcMath.SegmentCircleIntersections(P(0, 0), P(10, 0), P(0, 0), 5, out a, out _));
        Assert.Equal(5.0, a.X, 9);
        Assert.Equal(0, ArcMath.SegmentCircleIntersections(P(-10, 6), P(10, 6), P(0, 0), 5, out _, out _));
    }
}

public class ManualWorkflowTests
{
    private const double Deg = Math.PI / 180.0;

    private static EditorSession DecimalInches()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        s.Document.Settings.DecimalPrecision = 2;
        s.Snap.Enabled = false;
        return s;
    }

    /// <summary>The five ways of giving a point, used together in one LINE command.</summary>
    [Fact]
    public void Plate_outline_using_every_point_entry_method()
    {
        var s = DecimalInches();
        s.ContinuousLines = true;                 // these steps draw a chain of lines (LINEMODE Continuous)
        s.StartCommand("LINE");
        s.SubmitText("0,0");                 // absolute
        s.SubmitText("5.5,0");               // absolute
        s.SubmitText("@0,2.5");              // relative rectangular
        s.SubmitPoint(P(8, 2.5));            // picked on screen
        s.SubmitText("@3<90");               // relative polar
        s.SubmitText("@-1.5,1");             // relative rectangular with a negative offset
        s.UpdateCursor(P(-50, 6.5), 0.01);   // cursor directly to the left...
        s.SubmitText("6.5");                 // ...then direct distance entry
        s.SubmitText("C");                   // close back to the start
        Assert.Null(s.ActiveCommand);

        var lines = s.Document.Entities.Cast<LineEntity>().ToList();
        Assert.Equal(7, lines.Count);
        var expected = new[] { P(5.5, 0), P(5.5, 2.5), P(8, 2.5), P(8, 5.5), P(6.5, 6.5), P(0, 6.5), P(0, 0) };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].X, lines[i].End.X, 9);
            Assert.Equal(expected[i].Y, lines[i].End.Y, 9);
        }
    }

    [Fact]
    public void Circle_by_diameter_then_relative_to_the_previous_circle_with_default_radius()
    {
        var s = DecimalInches();
        s.StartCommand("CIRCLE");
        s.SubmitText("2.5,3");
        s.SubmitKeyword("Diameter");         // as chosen from the right-click menu
        s.SubmitText("2.5");
        var first = (CircleEntity)s.Document.Entities[0];
        Assert.Equal(1.25, first.Radius);

        s.SubmitEnter();                     // repeat CIRCLE
        s.SubmitText("@2.5,2");              // relative to the first circle's centre
        Assert.Contains("<1.25>", s.Prompt); // last radius offered as the default
        s.SubmitText("D");
        Assert.Contains("<2.50>", s.Prompt);
        s.SubmitText("1.5");
        var second = (CircleEntity)s.Document.Entities[1];
        Assert.Equal(P(5, 5), second.Center);
        Assert.Equal(0.75, second.Radius);

        s.StartCommand("CIRCLE");
        s.SubmitText("20,20");
        s.SubmitEnter();                     // Enter accepts the default radius
        Assert.Equal(0.75, ((CircleEntity)s.Document.Entities[2]).Radius);
    }

    [Fact]
    public void Circle_from_two_and_three_points()
    {
        var s = DecimalInches();
        s.StartCommand("C");
        Assert.Equal(new[] { "3P", "2P", "Ttr", "TTT", "Diameter" }, s.ActiveCommand!.Options.ToArray());
        s.SubmitText("2P");
        s.SubmitText("0,0");
        s.SubmitText("10,0");
        var two = (CircleEntity)s.Document.Entities[0];
        Assert.Equal(P(5, 0), two.Center);
        Assert.Equal(5.0, two.Radius);

        var messages = CaptureMessages(s);
        s.StartCommand("C");
        s.SubmitText("3p");
        s.SubmitText("1,0");
        s.SubmitText("0,1");
        s.SubmitText("-1,2");                // in a straight line with the first two: rejected
        Assert.NotNull(s.ActiveCommand);
        Assert.Contains(messages, m => m.Contains("straight line"));
        s.SubmitText("-1,0");
        var three = (CircleEntity)s.Document.Entities[1];
        Assert.Equal(0.0, three.Center.X, 9);
        Assert.Equal(0.0, three.Center.Y, 9);
        Assert.Equal(1.0, three.Radius, 9);
    }

    [Fact]
    public void Arc_by_three_points_in_either_direction()
    {
        var s = DecimalInches();
        s.StartCommand("ARC");
        s.SubmitText("10,0");
        s.SubmitText("0,10");
        Assert.Single(s.GetPreview());
        s.SubmitText("-10,0");               // counter-clockwise through the top
        var top = (ArcEntity)s.Document.Entities[0];
        Assert.Equal(0.0, top.Center.X, 9);
        Assert.Equal(10.0, top.Radius, 9);
        Assert.Equal(0.0, top.StartAngle, 9);
        Assert.Equal(180 * Deg, top.EndAngle, 9);

        s.StartCommand("A");
        s.SubmitText("10,0");
        s.SubmitText("0,-10");
        s.SubmitText("-10,0");               // clockwise through the bottom: stored counter-clockwise
        var bottom = (ArcEntity)s.Document.Entities[1];
        Assert.Equal(180 * Deg, bottom.StartAngle, 9);
        Assert.Equal(0.0, bottom.EndAngle, 9);
        Assert.Equal(0.0, bottom.DistanceTo(P(0, -10)), 9);
        Assert.True(bottom.DistanceTo(P(0, 10)) > 10);

        s.Undo();
        Assert.Equal(1, s.Document.EntityCount);
    }

    [Fact]
    public void Arc_by_center_start_end()
    {
        var s = DecimalInches();
        s.StartCommand("ARC");
        s.SubmitKeyword("Center");
        s.SubmitText("5,5");
        s.SubmitText("5,3");                 // start: below the centre
        s.SubmitText("5,9");                 // end direction: above the centre (radius comes from the start point)
        var arc = (ArcEntity)s.Document.Entities[0];
        Assert.Equal(P(5, 5), arc.Center);
        Assert.Equal(2.0, arc.Radius, 9);
        Assert.Equal(270 * Deg, arc.StartAngle, 9);
        Assert.Equal(90 * Deg, arc.EndAngle, 9);
        Assert.Equal(0.0, arc.DistanceTo(P(7, 5)), 9);   // sweeps counter-clockwise through the right side
    }

    [Fact]
    public void Units_command_switches_to_millimetres_and_is_undoable()
    {
        var s = new EditorSession();
        AddLine(s.Document, 0, 0, 36, 0);
        Assert.Equal("3'-0\"", UnitFormatter.FormatLength(36, s.Document.Settings));

        s.StartCommand("UNITS");
        Assert.Contains("<Architectural>", s.Prompt);
        s.SubmitText("D");
        Assert.Contains("<Inches>", s.Prompt);
        s.SubmitText("MM");
        Assert.Contains("decimal places", s.Prompt);
        s.SubmitText("1");
        Assert.Null(s.ActiveCommand);

        var settings = s.Document.Settings;
        Assert.Equal(LinearUnit.Millimeter, settings.Unit);
        Assert.Equal(LengthFormat.Decimal, settings.Format);
        Assert.Equal("36.0", UnitFormatter.FormatLength(36, settings));     // geometry is not rescaled
        Assert.Equal(100.0, settings.GridSpacing);
        Assert.True(s.Document.IsDirty);

        // The unit travels with the drawing.
        var loaded = HcadSerializer.LoadFromBytes(HcadSerializer.SaveToBytes(s.Document)).Document;
        Assert.Equal(LinearUnit.Millimeter, loaded.Settings.Unit);

        s.Undo();
        Assert.Equal(LinearUnit.Inch, s.Document.Settings.Unit);
        Assert.Equal(LengthFormat.Architectural, s.Document.Settings.Format);

        // Enter keeps each default; fractions are validated.
        s.StartCommand("UN");
        s.SubmitEnter();
        s.SubmitEnter();
        Assert.Contains("fraction", s.Prompt);
        s.SubmitText("10");
        Assert.NotNull(s.ActiveCommand);                                     // not a power of two
        s.SubmitText("32");
        Assert.Equal(32, s.Document.Settings.FractionDenominator);
    }

    [Fact]
    public void Snap_mode_rounds_cursor_points_to_the_increment()
    {
        var s = DecimalInches();
        s.StartCommand("SNAP");
        s.SubmitText("0.5");
        Assert.Equal(0.5, s.Document.Settings.SnapSpacing);
        Assert.False(s.GridSnapEnabled);
        s.StartCommand("SNAP");
        s.SubmitText("ON");
        Assert.True(s.GridSnapEnabled);

        s.StartCommand("LINE");
        s.UpdateCursor(P(1.26, 0.74), 0.01);
        Assert.Equal(P(1.5, 0.5), s.EffectiveCursor);
        s.SubmitPoint(s.EffectiveCursor);
        s.SubmitText("3.3,7.1");                                             // typed values are never rounded
        s.SubmitEnter();
        var line = (LineEntity)s.Document.Entities[0];
        Assert.Equal(P(1.5, 0.5), line.Start);
        Assert.Equal(P(3.3, 7.1), line.End);

        s.StartCommand("GRID");
        s.SubmitText("2");
        Assert.Equal(2.0, s.Document.Settings.GridSpacing);
    }

    [Fact]
    public void Polar_tracking_locks_near_horizontal_and_vertical_without_ortho()
    {
        var s = DecimalInches();
        s.ContinuousLines = true;                 // these steps draw a chain of lines (LINEMODE Continuous)
        Assert.True(s.PolarEnabled);
        s.StartCommand("LINE");
        s.UpdateCursor(P(5, 0.02), 0.5);
        Assert.Null(s.CurrentPolarTrack);                                    // no previous point yet
        s.SubmitPoint(P(10, 10));

        s.UpdateCursor(P(30, 10.3), 0.5);                                    // almost horizontal, to the right
        var track = s.CurrentPolarTrack;
        Assert.NotNull(track);
        Assert.Equal(0.0, track!.Value.AngleDegrees);
        Assert.Equal(P(10, 10), track.Value.From);
        Assert.Equal(P(30, 10), s.EffectiveCursor);                          // exactly horizontal

        s.UpdateCursor(P(9.8, -40), 0.5);                                    // almost straight down
        Assert.Equal(270.0, s.CurrentPolarTrack!.Value.AngleDegrees);
        Assert.Equal(P(10, -40), s.EffectiveCursor);

        s.UpdateCursor(P(-5, 10.4), 0.5);                                    // to the left
        Assert.Equal(180.0, s.CurrentPolarTrack!.Value.AngleDegrees);
        Assert.Equal(P(-5, 10), s.EffectiveCursor);

        s.UpdateCursor(P(30, 12), 0.5);                                      // clearly off the axis: free
        Assert.Null(s.CurrentPolarTrack);
        Assert.Equal(P(30, 12), s.EffectiveCursor);

        s.UpdateCursor(P(30, 10.3), 0.5);
        s.SubmitText("25");                                                  // typed distance follows the locked direction
        Assert.Equal(P(35, 10), ((LineEntity)s.Document.Entities[0]).End);

        s.PolarEnabled = false;
        s.UpdateCursor(P(60, 10.3), 0.5);
        Assert.Null(s.CurrentPolarTrack);
        Assert.Equal(P(60, 10.3), s.EffectiveCursor);

        // An object snap beats polar tracking.
        s.PolarEnabled = true;
        s.Snap.Enabled = true;
        s.UpdateCursor(P(10.2, 10.1), 0.5);                                  // at the start point of the first line
        Assert.NotNull(s.CurrentSnap);
        Assert.Null(s.CurrentPolarTrack);
    }

    [Fact]
    public void Polar_tracking_ray_is_drawn_and_named_beside_the_crosshair()
    {
        var doc = new DrawingDocument();
        var view = new ViewTransform();
        view.SetViewport(800, 600);
        view.SetView(P(0, 0), 10);
        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, view, new RenderOptions { ShowGrid = false, PolarTrack = (P(-10, 5), 0.0) });
        var ray = Assert.Single(surface.Lines);
        Assert.Equal(RenderTheme.PolarTrack, ray.Style);
        Assert.Equal(300.0, ray.X1, 6);                                      // starts at the base point
        Assert.Equal(250.0, ray.Y1, 6);
        Assert.Equal(250.0, ray.Y2, 6);                                      // horizontal
        Assert.True(ray.X2 >= 800);                                          // runs to the right-hand edge

        using var temp = new TempDirectory();
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(temp.File("r")));
        vm.Session.Snap.Enabled = false;
        vm.RunCommand("LINE");
        vm.Session.SubmitPoint(P(0, 0));
        vm.Session.UpdateCursor(P(36, 0.2), 0.5);
        Assert.Equal("Polar: 3'-0\" < 0\u00B0", vm.GetDynamicInput()!.PolarText);
        vm.Session.UpdateCursor(P(36, 20), 0.5);
        Assert.Equal("", vm.GetDynamicInput()!.PolarText);

        vm.PolarOn = false;
        Assert.False(vm.Session.PolarEnabled);
        Assert.Contains("<Polar off>", vm.History);
    }

    [Fact]
    public void Quit_command_asks_the_shell_to_close()
    {
        var s = new EditorSession();
        var requests = new List<string>();
        s.ViewCommandRequested += requests.Add;
        Assert.True(s.StartCommand("quit"));
        Assert.Equal(new[] { "Quit" }, requests.ToArray());
    }
}

public class ContextMenuTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUi _ui = new();

    public void Dispose() => _temp.Dispose();

    private MainViewModel NewViewModel() => new(_ui, new RecoveryStore(_temp.File("recovery")));

    private static void Pick(MainViewModel vm, string label) => vm.GetContextMenu().Single(e => e.Label == label).Invoke();

    private static string[] Labels(MainViewModel vm) => vm.GetContextMenu().Where(e => e.Children is null).Select(e => e.Label).ToArray();

    [Fact]
    public void Right_click_menu_follows_the_command_state()
    {
        var vm = NewViewModel();
        vm.Session.ContinuousLines = true;                 // these steps draw a chain of lines (LINEMODE Continuous)
        Assert.Equal(new[] { "Zoom Extents" }, Labels(vm));                  // nothing to repeat or undo yet

        vm.RunCommand("LINE");
        Assert.Equal(new[] { "Enter", "Cancel" }, Labels(vm));
        vm.Session.SubmitPoint(P(0, 0));
        vm.Session.SubmitPoint(P(5, 0));
        Assert.Equal(new[] { "Enter", "Cancel", "Undo" }, Labels(vm));
        vm.Session.SubmitPoint(P(5, 5));
        Assert.Equal(new[] { "Enter", "Cancel", "Close", "Undo" }, Labels(vm));

        Pick(vm, "Close");                                                   // closes the shape and ends LINE
        Assert.Null(vm.Session.ActiveCommand);
        Assert.Equal(3, vm.Session.Document.EntityCount);

        Assert.Equal(new[] { "Repeat LINE", "Undo LINE", "Zoom Extents" }, Labels(vm));
        Pick(vm, "Repeat LINE");
        Assert.Equal("LINE", vm.Session.ActiveCommand!.Name);
        Pick(vm, "Cancel");
        Assert.Null(vm.Session.ActiveCommand);

        Pick(vm, "Undo LINE");
        Assert.Equal(0, vm.Session.Document.EntityCount);
        Assert.Contains("Redo LINE", Labels(vm));

        vm.RunCommand("CIRCLE");
        Assert.Equal(new[] { "Enter", "Cancel", "3P", "2P", "Ttr", "TTT", "Diameter" }, Labels(vm));
        vm.Session.SubmitPoint(P(0, 0));
        Pick(vm, "Diameter");
        Assert.Contains("diameter", vm.Prompt);
    }

    [Fact]
    public void Crosshair_size_is_a_saved_user_setting()
    {
        string file = _temp.File("settings.json");
        var vm = new MainViewModel(_ui, new RecoveryStore(_temp.File("recovery")), new UserSettingsStore(file));
        Assert.Equal(8, vm.Session.CrosshairSizePercent);                    // default
        Assert.Equal(60.0, vm.GetCrosshairArmLength(1500, 900), 9);          // 8% of 1500, half each side

        vm.RunCommand("CURSORSIZE");
        Assert.Contains("<8>", vm.Prompt);
        vm.CommandText = "500"; vm.SubmitCommandLine();                      // out of range: rejected
        Assert.NotNull(vm.Session.ActiveCommand);
        vm.CommandText = "20"; vm.SubmitCommandLine();
        Assert.Null(vm.Session.ActiveCommand);
        Assert.Equal(20, vm.Session.CrosshairSizePercent);
        Assert.Equal(150.0, vm.GetCrosshairArmLength(1500, 900), 9);
        Assert.False(vm.Session.Document.IsDirty);                           // a preference, not part of the drawing

        // A new program start reads it back.
        var again = new MainViewModel(_ui, new RecoveryStore(_temp.File("recovery")), new UserSettingsStore(file));
        Assert.Equal(20, again.Session.CrosshairSizePercent);

        again.RunCommand("CS");
        again.CommandText = "100"; again.SubmitCommandLine();
        Assert.Equal(0.0, again.GetCrosshairArmLength(1500, 900));           // 100 = to the edges of the view

        // A damaged settings file falls back to the default instead of failing.
        File.WriteAllText(file, "not json");
        Assert.Equal(8, new UserSettingsStore(file).Load().CrosshairPercent);
        Assert.Equal(8, new MainViewModel(_ui, new RecoveryStore(_temp.File("recovery"))).Session.CrosshairSizePercent);
    }

    [Fact]
    public void Tab_moves_from_distance_to_angle_and_enter_places_the_point()
    {
        var vm = NewViewModel();
        vm.Session.ContinuousLines = true;                 // these steps draw a chain of lines (LINEMODE Continuous)
        vm.Session.Snap.Enabled = false;
        vm.Session.PolarEnabled = false;
        Assert.False(vm.SwitchInputField());                                 // no command: Tab does nothing here

        vm.RunCommand("LINE");
        vm.Session.SubmitPoint(P(0, 0));
        vm.Session.UpdateCursor(P(50, 20), 0.1);

        vm.CommandText = "10'";
        Assert.True(vm.SwitchInputField());                                  // Tab: distance kept, now typing the angle
        Assert.Equal("", vm.CommandText);
        var boxes = vm.GetDynamicInput()!;
        Assert.Equal("10'", boxes.Field1);
        Assert.Equal(1, boxes.ActiveField);
        Assert.Equal("< 21.8\u00B0", boxes.Field2);                          // empty angle box shows the live angle

        vm.CommandText = "90";
        Assert.Equal("< 90\u00B0", vm.GetDynamicInput()!.Field2);
        vm.SubmitCommandLine();
        var line = (LineEntity)vm.Session.Document.Entities[0];
        Assert.Equal(0.0, line.End.X, 9);
        Assert.Equal(120.0, line.End.Y, 9);
        Assert.Contains("LINE  Specify next point or [Undo]: @10'<90", vm.History);
        Assert.False(vm.IsFieldInputActive);

        // Tab back and forth keeps both values and lets either be corrected.
        vm.CommandText = "5'";
        vm.SwitchInputField();
        vm.CommandText = "0";
        vm.SwitchInputField();
        Assert.Equal("5'", vm.CommandText);
        vm.CommandText = "6'";
        vm.SubmitCommandLine();
        Assert.Equal(72.0, ((LineEntity)vm.Session.Document.Entities[1]).End.X, 9);
        Assert.Equal(120.0, ((LineEntity)vm.Session.Document.Entities[1]).End.Y, 9);

        // Angle only: Tab past the distance, type the angle, the distance comes from the cursor.
        vm.Session.UpdateCursor(P(72, 220), 0.1);                            // 100 away from (72,120)
        vm.SwitchInputField();
        vm.CommandText = "180";
        vm.SubmitCommandLine();
        Assert.Equal(-28.0, ((LineEntity)vm.Session.Document.Entities[2]).End.X, 9);
        Assert.Equal(120.0, ((LineEntity)vm.Session.Document.Entities[2]).End.Y, 9);

        // Distance typed, Tab, then a click: the length is the typed one, the direction is the cursor's.
        vm.Session.UpdateCursor(P(-28, 500), 0.1);
        vm.CommandText = "2'";
        vm.SwitchInputField();
        vm.ClickPoint();
        Assert.Equal(-28.0, ((LineEntity)vm.Session.Document.Entities[3]).End.X, 9);
        Assert.Equal(144.0, ((LineEntity)vm.Session.Document.Entities[3]).End.Y, 9);

        // Esc clears a half-entered value.
        vm.CommandText = "7";
        vm.SwitchInputField();
        vm.Cancel();
        Assert.False(vm.IsFieldInputActive);
        Assert.Equal("", vm.CommandText);
    }

    [Fact]
    public void Tab_moves_from_x_to_y_for_a_first_point()
    {
        var vm = NewViewModel();
        vm.Session.Snap.Enabled = false;
        vm.RunCommand("CIRCLE");
        vm.Session.UpdateCursor(P(3, 4), 0.1);
        vm.CommandText = "2'";
        vm.SwitchInputField();
        Assert.Equal("4\"", vm.GetDynamicInput()!.Field2);                   // Y box shows the cursor until typed
        vm.CommandText = "1'";
        vm.SubmitCommandLine();
        vm.CommandText = "6"; vm.SubmitCommandLine();
        Assert.Equal(P(24, 12), ((CircleEntity)vm.Session.Document.Entities[0]).Center);

        // An ordinary click still works when Tab was never pressed.
        vm.RunCommand("CIRCLE");
        vm.Session.UpdateCursor(P(100, 100), 0.1);
        vm.ClickPoint();
        vm.CommandText = "6"; vm.SubmitCommandLine();
        Assert.Equal(P(100, 100), ((CircleEntity)vm.Session.Document.Entities[1]).Center);
    }

    [Fact]
    public void Coordinate_readout_cycles_between_absolute_polar_and_off()
    {
        var vm = NewViewModel();
        vm.Session.Snap.Enabled = false;
        vm.RunCommand("LINE");
        vm.Session.SubmitPoint(P(0, 0));
        vm.UpdateCoordinates(P(0, 36));
        Assert.Equal("0\", 3'-0\"", vm.CoordinateText);

        vm.CycleCoordinateModeCommand.Execute(null);
        Assert.Equal(CoordinateDisplayMode.RelativePolar, vm.CoordinateMode);
        Assert.Equal("3'-0\" < 90°", vm.CoordinateText);

        vm.CycleCoordinateModeCommand.Execute(null);
        Assert.Equal("(coordinates off)", vm.CoordinateText);

        vm.CycleCoordinateModeCommand.Execute(null);
        Assert.Equal(CoordinateDisplayMode.Absolute, vm.CoordinateMode);
        Assert.Equal("0\", 3'-0\"", vm.CoordinateText);
    }

    [Fact]
    public void Snap_toggle_quit_and_arc_properties_reach_the_view_model()
    {
        var vm = NewViewModel();
        vm.SnapOn = true;
        Assert.True(vm.Session.GridSnapEnabled);
        Assert.Contains("<Snap on>", vm.History);

        int closes = 0;
        vm.CloseRequested += (_, _) => closes++;
        vm.RunCommand("QUIT");
        Assert.Equal(1, closes);

        vm.SnapOn = false;
        vm.RunCommand("ARC");
        vm.CommandText = "10,0"; vm.SubmitCommandLine();
        vm.CommandText = "0,10"; vm.SubmitCommandLine();
        vm.CommandText = "-10,0"; vm.SubmitCommandLine();
        Assert.Contains("   Arcs: 1", vm.ExplorerItems);

        vm.Session.SelectAll();
        Assert.Equal("Arc", vm.Properties.Header);
        Assert.Equal("10\"", vm.Properties.Rows.Single(r => r.Label == "Radius").Value);
        Assert.Equal("180°", vm.Properties.Rows.Single(r => r.Label == "Included angle").Value);
        vm.Properties.Rows.Single(r => r.Label == "Radius").Value = "1'";
        Assert.Equal(12.0, ((ArcEntity)vm.Session.Document.Entities[0]).Radius, 9);
    }
}

public class TangentCircleTests
{
    private static void AssertTangent(IReadOnlyList<TangentConstraint> constraints, CircleSolution c)
    {
        foreach (var constraint in constraints)
        {
            switch (constraint)
            {
                case TangentConstraint.Line l:
                    Assert.Equal(c.Radius, SegmentMath.DistanceToPoint(l.A + (l.A - l.B) * 1e4, l.B + (l.B - l.A) * 1e4, c.Center), 6);
                    break;
                case TangentConstraint.Circle k:
                    double d = c.Center.DistanceTo(k.Center);
                    Assert.True(Math.Abs(d - (c.Radius + k.Radius)) < 1e-6 || Math.Abs(d - Math.Abs(c.Radius - k.Radius)) < 1e-6,
                        $"not tangent: d={d}, r={c.Radius}, R={k.Radius}");
                    break;
                case TangentConstraint.FixedRadius f:
                    Assert.Equal(f.Radius, c.Radius, 9);
                    break;
            }
        }
    }

    [Fact]
    public void Tan_tan_radius_between_two_lines()
    {
        var constraints = new TangentConstraint[]
        {
            new TangentConstraint.Line(P(0, 0), P(10, 0)), new TangentConstraint.Line(P(0, 0), P(0, 10)), new TangentConstraint.FixedRadius(5),
        };
        var solutions = TangentCircleSolver.Solve(constraints);
        Assert.Equal(4, solutions.Count);                                    // one in each quadrant
        foreach (var s in solutions) AssertTangent(constraints, s);

        var best = TangentCircleSolver.ChooseNearest(constraints, new Point2D?[] { P(6, 0), P(0, 4), null }, solutions)!.Value;
        Assert.Equal(5.0, best.Center.X, 9);
        Assert.Equal(5.0, best.Center.Y, 9);
        var other = TangentCircleSolver.ChooseNearest(constraints, new Point2D?[] { P(-6, 0), P(0, -4), null }, solutions)!.Value;
        Assert.Equal(-5.0, other.Center.X, 9);
        Assert.Equal(-5.0, other.Center.Y, 9);
    }

    [Fact]
    public void Tan_tan_radius_with_circles_and_impossible_cases()
    {
        var lineAndCircle = new TangentConstraint[]
        {
            new TangentConstraint.Line(P(-20, 0), P(20, 0)), new TangentConstraint.Circle(P(0, 10), 4), new TangentConstraint.FixedRadius(3),
        };
        var solutions = TangentCircleSolver.Solve(lineAndCircle);
        Assert.NotEmpty(solutions);
        foreach (var s in solutions) AssertTangent(lineAndCircle, s);
        Assert.Contains(solutions, s => Math.Abs(s.Center.X) < 1e-9 && Math.Abs(s.Center.Y - 3) < 1e-9);  // sits between line and circle

        var twoCircles = new TangentConstraint[]
        {
            new TangentConstraint.Circle(P(0, 0), 5), new TangentConstraint.Circle(P(20, 0), 5), new TangentConstraint.FixedRadius(6),
        };
        solutions = TangentCircleSolver.Solve(twoCircles);
        Assert.True(solutions.Count >= 2);
        foreach (var s in solutions) AssertTangent(twoCircles, s);

        // Too small to reach both circles (gap is 10, diameter 4): no solution.
        var tooSmall = new TangentConstraint[]
        {
            new TangentConstraint.Circle(P(0, 0), 5), new TangentConstraint.Circle(P(20, 0), 5), new TangentConstraint.FixedRadius(2),
        };
        Assert.Empty(TangentCircleSolver.Solve(tooSmall));

        // Parallel lines 10 apart: only radius 5 works.
        var parallel = new TangentConstraint[]
        {
            new TangentConstraint.Line(P(0, 0), P(10, 0)), new TangentConstraint.Line(P(0, 10), P(10, 10)), new TangentConstraint.FixedRadius(3),
        };
        Assert.Empty(TangentCircleSolver.Solve(parallel));
    }

    [Fact]
    public void Tan_tan_tan_inscribed_circle_of_a_triangle()
    {
        var sides = new TangentConstraint[]
        {
            new TangentConstraint.Line(P(0, 0), P(4, 0)), new TangentConstraint.Line(P(0, 0), P(0, 3)), new TangentConstraint.Line(P(4, 0), P(0, 3)),
        };
        var solutions = TangentCircleSolver.Solve(sides);
        Assert.Equal(4, solutions.Count);                                    // the incircle and three excircles
        foreach (var s in solutions) AssertTangent(sides, s);

        var incircle = TangentCircleSolver.ChooseNearest(sides, new Point2D?[] { P(1, 0), P(0, 1), P(2, 1.5) }, solutions)!.Value;
        Assert.Equal(1.0, incircle.Center.X, 9);
        Assert.Equal(1.0, incircle.Center.Y, 9);
        Assert.Equal(1.0, incircle.Radius, 9);
    }

    [Fact]
    public void Tan_tan_tan_three_circles_and_mixed_objects()
    {
        double h = 10 * Math.Sqrt(3);
        var three = new TangentConstraint[]
        {
            new TangentConstraint.Circle(P(-10, 0), 5), new TangentConstraint.Circle(P(10, 0), 5), new TangentConstraint.Circle(P(0, h), 5),
        };
        var solutions = TangentCircleSolver.Solve(three);
        Assert.Equal(8, solutions.Count);                                    // the eight circles of Apollonius
        foreach (var s in solutions) AssertTangent(three, s);

        // The small circle in the middle touches all three from outside.
        double cy = h / 3, reach = 20 / Math.Sqrt(3);
        var picks = new Point2D?[] { P(-10 + 5 * 0.866, 2.5), P(10 - 5 * 0.866, 2.5), P(0, h - 5) };
        var inner = TangentCircleSolver.ChooseNearest(three, picks, solutions)!.Value;
        Assert.Equal(0.0, inner.Center.X, 7);
        Assert.Equal(cy, inner.Center.Y, 7);
        Assert.Equal(reach - 5, inner.Radius, 7);

        var mixed = new TangentConstraint[]
        {
            new TangentConstraint.Line(P(-50, 0), P(50, 0)), new TangentConstraint.Line(P(0, -50), P(0, 50)), new TangentConstraint.Circle(P(20, 20), 6),
        };
        solutions = TangentCircleSolver.Solve(mixed);
        Assert.NotEmpty(solutions);
        foreach (var s in solutions) AssertTangent(mixed, s);

        var random = new Random(3);
        for (int i = 0; i < 200; i++)                                        // every reported solution is truly tangent
        {
            TangentConstraint Any() => random.Next(2) == 0
                ? new TangentConstraint.Line(P(random.NextDouble() * 100, random.NextDouble() * 100), P(random.NextDouble() * 100 + 101, random.NextDouble() * 100))
                : new TangentConstraint.Circle(P(random.NextDouble() * 100, random.NextDouble() * 100), 1 + random.NextDouble() * 20);
            var set = new[] { Any(), Any(), Any() };
            foreach (var s in TangentCircleSolver.Solve(set)) AssertTangent(set, s);
        }
    }

    [Fact]
    public void Circle_command_tan_tan_radius_and_tan_tan_tan()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        s.Snap.Enabled = false;
        s.PolarEnabled = false;
        var messages = CaptureMessages(s);
        AddLine(s.Document, 0, 0, 40, 0);                                    // horizontal
        AddLine(s.Document, 0, 0, 0, 30);                                    // vertical
        s.Document.AddEntity(new PolylineEntity(new[] { P(40, 0), P(0, 30) }, false) { LayerId = s.Document.CurrentLayerId }); // hypotenuse

        s.UpdateCursor(P(0, 0), 0.5);                                        // sets the pick tolerance
        s.StartCommand("CIRCLE");
        s.SubmitText("TTR");
        Assert.Contains("first tangent", s.Prompt);
        s.SubmitPoint(P(200, 200));                                          // nothing there
        Assert.Contains(messages, m => m.Contains("Pick a line"));
        s.SubmitPoint(P(12, 0.2));
        Assert.Contains("second tangent", s.Prompt);
        s.SubmitPoint(P(0.1, 9));
        Assert.Contains("radius", s.Prompt);
        s.SubmitText("5");
        var fillet = (CircleEntity)s.Document.Entities[3];
        Assert.Equal(5.0, fillet.Center.X, 9);
        Assert.Equal(5.0, fillet.Center.Y, 9);
        Assert.Equal(5.0, fillet.Radius, 9);

        s.StartCommand("CIRCLE");
        s.SubmitKeyword("TTT");
        s.SubmitPoint(P(30, 0));
        s.SubmitPoint(P(0, 20));
        s.SubmitPoint(P(20, 15));                                            // on the polyline segment
        Assert.Null(s.ActiveCommand);
        var inscribed = (CircleEntity)s.Document.Entities[4];
        Assert.Equal(10.0, inscribed.Center.X, 7);                           // 30-40-50 triangle: inradius 10
        Assert.Equal(10.0, inscribed.Center.Y, 7);
        Assert.Equal(10.0, inscribed.Radius, 7);

        // Tangent to an existing circle and a line.
        s.StartCommand("CIRCLE");
        s.SubmitText("T");
        s.SubmitPoint(P(10, 5));                                             // right-most point of the radius-5 circle
        s.SubmitPoint(P(25, 0));
        s.SubmitText("2");
        var third = (CircleEntity)s.Document.Entities[5];
        Assert.Equal(2.0, third.Center.Y, 7);                                // rests on the horizontal line
        Assert.Equal(7.0, third.Center.DistanceTo(P(5, 5)), 7);              // touches the first circle from outside
        s.Undo();
        Assert.Equal(5, s.Document.EntityCount);
    }

    [Fact]
    public void Ribbon_scripts_start_a_command_with_its_option()
    {
        using var temp = new TempDirectory();
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(temp.File("r")));
        vm.RunCommand("CIRCLE D");
        Assert.Contains("diameter", vm.Prompt);
        vm.CommandText = "0,0"; vm.SubmitCommandLine();
        vm.CommandText = "10"; vm.SubmitCommandLine();
        Assert.Equal(5.0, ((CircleEntity)vm.Session.Document.Entities[0]).Radius);

        vm.RunCommand("CIRCLE 2P");
        Assert.Contains("first end point of diameter", vm.Prompt);
        vm.RunCommand("CIRCLE 3P");
        Assert.Contains("first point on circle", vm.Prompt);
        vm.RunCommand("CIRCLE TTR");
        Assert.Contains("first tangent", vm.Prompt);
        vm.RunCommand("CIRCLE TTT");
        Assert.Contains("first tangent", vm.Prompt);
        vm.RunCommand("ARC C");
        Assert.Contains("center point of arc", vm.Prompt);
        vm.RunCommand("ARC CSA");
        Assert.Contains("center point of arc", vm.Prompt);
        vm.RunCommand("ARC SER");
        Assert.Contains("start point of arc", vm.Prompt);
        vm.RunCommand("ARC CONTINUE");                      // nothing drawn yet: stays at the start prompt
        Assert.Contains("start point", vm.Prompt);
        vm.RunCommand("ARC");
        Assert.Contains("start point", vm.Prompt);
    }
}

public class TransformCommandTests
{
    private static EditorSession Session(out LineEntity line, out ArcEntity arc)
    {
        var s = new EditorSession();
        s.Snap.Enabled = false;
        s.PolarEnabled = false;
        line = AddLine(s.Document, 10, 0, 20, 0);
        arc = new ArcEntity(P(10, 0), 5, 0, Math.PI / 2) { LayerId = s.Document.CurrentLayerId };
        s.Document.AddEntity(arc);
        return s;
    }

    [Fact]
    public void Rotate_by_typed_angle_and_by_picked_direction()
    {
        var s = Session(out var line, out var arc);
        s.SelectAll();
        s.StartCommand("RO");
        s.SubmitText("0,0");
        s.UpdateCursor(P(5, 5), 0.01);
        Assert.Equal(3, s.GetPreview().Count);                               // two ghosts and the direction line
        s.SubmitText("90");

        var turned = (LineEntity)s.Document.GetEntity(line.Id)!;
        Assert.Equal(0.0, turned.Start.X, 9);
        Assert.Equal(10.0, turned.Start.Y, 9);
        Assert.Equal(20.0, turned.End.Y, 9);
        var turnedArc = (ArcEntity)s.Document.GetEntity(arc.Id)!;
        Assert.Equal(0.0, turnedArc.Center.X, 9);
        Assert.Equal(10.0, turnedArc.Center.Y, 9);
        Assert.Equal(Math.PI / 2, turnedArc.StartAngle, 9);
        Assert.Equal(Math.PI / 2, turnedArc.Sweep, 9);

        s.Undo();                                                            // one step for the whole selection
        Assert.Equal(P(10, 0), ((LineEntity)s.Document.GetEntity(line.Id)!).Start);

        s.StartCommand("ROTATE");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(-3, 0));                                             // direction of 180 degrees
        Assert.Equal(-20.0, ((LineEntity)s.Document.GetEntity(line.Id)!).End.X, 9);
    }

    [Fact]
    public void Scale_about_a_base_point()
    {
        var s = Session(out var line, out var arc);
        var messages = CaptureMessages(s);
        s.SelectAll();
        s.StartCommand("SC");
        s.SubmitText("10,0");
        s.SubmitText("0");                                                   // rejected
        Assert.NotNull(s.ActiveCommand);
        Assert.Contains(messages, m => m.Contains("must be positive"));
        s.SubmitText("2");
        var scaled = (LineEntity)s.Document.GetEntity(line.Id)!;
        Assert.Equal(P(10, 0), scaled.Start);                                // the base point stays put
        Assert.Equal(P(30, 0), scaled.End);
        Assert.Equal(10.0, ((ArcEntity)s.Document.GetEntity(arc.Id)!).Radius, 9);
    }

    [Fact]
    public void Mirror_keeps_or_erases_the_originals()
    {
        var s = Session(out var line, out var arc);
        s.SelectAll();
        s.StartCommand("MI");
        s.SubmitText("0,0");
        s.SubmitText("0,10");                                                // mirror in the Y axis
        Assert.Contains("Erase source", s.Prompt);
        s.SubmitEnter();                                                     // default No: copies are added
        Assert.Equal(4, s.Document.EntityCount);
        var copyLine = s.Document.Entities.OfType<LineEntity>().Single(l => l.Id != line.Id);
        Assert.Equal(-10.0, copyLine.Start.X, 9);
        Assert.Equal(-20.0, copyLine.End.X, 9);
        var copyArc = s.Document.Entities.OfType<ArcEntity>().Single(a => a.Id != arc.Id);
        Assert.Equal(-10.0, copyArc.Center.X, 9);
        Assert.Equal(Math.PI / 2, copyArc.StartAngle, 9);                    // quarter arc now on the left side
        Assert.Equal(Math.PI, copyArc.EndAngle, 9);
        Assert.Equal(P(10, 0), ((LineEntity)s.Document.GetEntity(line.Id)!).Start);

        s.ClearSelection();
        s.Selection.Add(line.Id);
        s.StartCommand("MIRROR");
        s.SubmitText("0,5");
        s.SubmitText("10,5");                                                // mirror in the line y = 5
        s.SubmitText("Y");                                                   // erase source: the object itself is flipped
        Assert.Equal(4, s.Document.EntityCount);
        Assert.Equal(10.0, ((LineEntity)s.Document.GetEntity(line.Id)!).Start.Y, 9);

        // A slanted mirror line: reflecting twice returns to the start.
        var t = MirrorCommand.Reflection(P(3, 1), P(7, 9));
        var back = t.Apply(t.Apply(P(12.5, -4)));
        Assert.Equal(12.5, back.X, 9);
        Assert.Equal(-4.0, back.Y, 9);
        Assert.Equal(P(3, 1).X, t.Apply(P(3, 1)).X, 9);                      // points on the line do not move
    }
}

public class RectangleSquareTests
{
    [Fact]
    public void Holding_shift_while_drawing_a_rectangle_makes_a_square()
    {
        var s = new EditorSession();
        s.Snap.Enabled = false;
        s.StartCommand("RECTANG");
        s.SubmitPoint(P(10, 10));

        s.UpdateCursor(P(40, 22), 0.5);
        Assert.Equal(P(40, 22), s.EffectiveCursor);                          // free rectangle
        s.OrthoOverride = true;                                              // Shift held
        Assert.Equal(P(40, 40), s.EffectiveCursor);                          // longer side wins: 30 x 30
        var preview = (PolylineEntity)Assert.Single(s.GetPreview());
        Assert.Equal(new BoundingBox(10, 10, 40, 40), preview.Bounds);

        s.UpdateCursor(P(-5, 18), 0.5);                                      // dragging up and to the left
        Assert.Equal(P(-5, 25), s.EffectiveCursor);
        s.UpdateCursor(P(14, -30), 0.5);                                     // down and to the right
        Assert.Equal(P(50, -30), s.EffectiveCursor);

        s.SubmitPoint(s.EffectiveCursor);
        var square = (PolylineEntity)s.Document.Entities[0];
        Assert.Equal(40.0, square.Bounds.Width, 9);
        Assert.Equal(40.0, square.Bounds.Height, 9);
        Assert.True(square.IsClosed);
    }

    [Fact]
    public void Ortho_and_polar_do_not_flatten_a_rectangle()
    {
        var s = new EditorSession { OrthoEnabled = true };                   // F8 on
        s.Snap.Enabled = false;
        s.StartCommand("RECTANG");
        s.SubmitPoint(P(0, 0));
        s.UpdateCursor(P(30, 12), 0.5);
        Assert.Equal(P(30, 12), s.EffectiveCursor);                          // not forced onto an axis
        s.UpdateCursor(P(30, 0.2), 0.5);
        Assert.Null(s.CurrentPolarTrack);                                    // no polar lock for rectangles
        s.UpdateCursor(P(30, 12), 0.5);
        s.SubmitPoint(s.EffectiveCursor);
        Assert.Equal(new BoundingBox(0, 0, 30, 12), s.Document.Entities[0].Bounds);

        // Typed sizes are never altered by Shift.
        s.StartCommand("RECTANG");
        s.OrthoOverride = true;
        s.SubmitText("100,0");
        s.SubmitText("@24,36");
        Assert.Equal(new BoundingBox(100, 0, 124, 36), s.Document.Entities[1].Bounds);
    }
}

public class PolylineArcTests
{
    /// <summary>A "stadium": two straight sides of length 10 joined by two semicircles of radius 2.</summary>
    private static PolylineEntity Stadium() =>
        new(new[] { P(0, 0), P(10, 0), P(10, 4), P(0, 4) }, true, new[] { 0.0, 1.0, 0.0, 1.0 });

    [Fact]
    public void Bulge_math()
    {
        // Semicircle, counter-clockwise from (1,0) to (-1,0) over the top.
        Assert.True(BulgeMath.TryGetArc(P(1, 0), P(-1, 0), 1, out var c, out double r, out double start, out double sweep));
        Assert.Equal(0.0, c.X, 12);
        Assert.Equal(0.0, c.Y, 12);
        Assert.Equal(1.0, r, 12);
        Assert.Equal(0.0, start, 12);
        Assert.Equal(Math.PI, sweep, 12);
        Assert.Equal(Math.PI, BulgeMath.Length(P(1, 0), P(-1, 0), 1), 12);
        Assert.Equal(Math.PI / 2, BulgeMath.SegmentArea(P(1, 0), P(-1, 0), 1), 12);
        Assert.Equal(-Math.PI / 2, BulgeMath.SegmentArea(P(1, 0), P(-1, 0), -1), 12);

        // Quarter circle of radius 10, clockwise from (0,10) to (10,0).
        double quarter = Math.Tan(Math.PI / 8);
        Assert.True(BulgeMath.TryGetArc(P(0, 10), P(10, 0), -quarter, out c, out r, out start, out sweep));
        Assert.Equal(0.0, c.X, 9);
        Assert.Equal(0.0, c.Y, 9);
        Assert.Equal(10.0, r, 9);
        Assert.Equal(0.0, start, 9);                                          // stored counter-clockwise: from (10,0)...
        Assert.Equal(Math.PI / 2, sweep, 9);                                  // ...to (0,10)

        Assert.False(BulgeMath.TryGetArc(P(0, 0), P(1, 0), 0, out _, out _, out _, out _));
        Assert.Equal(5.0, BulgeMath.Length(P(0, 0), P(3, 4), 0), 12);

        // Leaving (0,0) heading +X and ending at (2,2) is a quarter turn to the left.
        double b = BulgeMath.TangentArcBulge(P(0, 0), 0, P(2, 2))!.Value;
        Assert.Equal(quarter, b, 12);
        Assert.Equal(Math.PI / 2, BulgeMath.EndTangentAngle(P(0, 0), P(2, 2), b), 12);   // now heading +Y
        Assert.Null(BulgeMath.TangentArcBulge(P(0, 0), 0, P(-5, 0)));         // straight behind
        Assert.Equal(0.0, BulgeMath.TangentArcBulge(P(0, 0), 0, P(5, 0))!.Value, 12);    // straight ahead: a line
    }

    [Fact]
    public void Polyline_with_arcs_measures_and_bounds()
    {
        var p = Stadium();
        Assert.True(p.IsValid);
        Assert.True(p.HasArcs);
        Assert.Equal(4, p.SegmentCount);
        Assert.Equal(20 + 2 * Math.PI * 2, p.Length, 9);                      // two sides + a full circle of radius 2
        Assert.Equal(40 + Math.PI * 4, p.Area, 9);                            // rectangle + circle
        Assert.Equal(new BoundingBox(-2, 0, 12, 4), p.Bounds);                // the ends bulge outward

        Assert.Equal(0.0, p.DistanceTo(P(12, 2)), 9);                         // on the right-hand arc
        Assert.Equal(1.0, p.DistanceTo(P(11, 2)), 9);                         // inside it
        Assert.Equal(2.0, p.DistanceTo(P(10, 2)), 9);                         // at the arc's centre: the chord is not part of the shape
        Assert.True(p.IntersectsBox(new BoundingBox(11.5, 1.5, 12.5, 2.5)));
        Assert.False(p.IntersectsBox(new BoundingBox(9.5, 1.5, 10.5, 2.5)));  // around the missing chord

        p.Width = 1;
        Assert.Equal(new BoundingBox(-2.5, -0.5, 12.5, 4.5), p.Bounds);
        Assert.Equal(0.0, p.DistanceTo(P(5, 0.4)), 9);                        // anywhere on the band counts as a hit
        Assert.Equal(0.5, p.DistanceTo(P(5, 1)), 9);
    }

    [Fact]
    public void Polyline_arcs_survive_copy_move_mirror_and_scale()
    {
        var p = Stadium();
        p.Width = 0.5;
        var copy = (PolylineEntity)p.CopyWithNewId();
        Assert.Equal(p.Bulges.ToArray(), copy.Bulges.ToArray());
        Assert.Equal(0.5, copy.Width);

        copy.Transform(Transform2D.Translation(new Vector2D(100, 0)));
        Assert.Equal(p.Length, copy.Length, 9);
        Assert.Equal(new BoundingBox(97.75, -0.25, 112.25, 4.25), copy.Bounds);

        var mirrored = (PolylineEntity)p.Clone();
        mirrored.Transform(MirrorCommand.Reflection(P(0, 0), P(0, 1)));       // flip left-right
        Assert.Equal(-1.0, mirrored.Bulges[1]);                               // direction of curvature reverses
        Assert.Equal(p.Area, mirrored.Area, 9);
        Assert.Equal(-12.25, mirrored.Bounds.MinX, 9);                        // still bulging outward

        var scaled = (PolylineEntity)p.Clone();
        scaled.Transform(Transform2D.Scaling(2, P(0, 0)));
        Assert.Equal(p.Length * 2, scaled.Length, 9);
        Assert.Equal(1.0, scaled.Width, 9);
    }

    [Fact]
    public void Polyline_arcs_snap_draw_and_round_trip()
    {
        var doc = new DrawingDocument();
        var p = Stadium();
        p.LayerId = doc.CurrentLayerId;
        p.Width = 0.25;
        doc.AddEntity(p);

        const SnapModes all = SnapModes.Endpoint | SnapModes.Midpoint | SnapModes.Center;
        var mid = SnapEngine.FindSnap(doc, P(11.9, 2.1), 0.5, all)!.Value;
        Assert.Equal(SnapModes.Midpoint, mid.Mode);
        Assert.Equal(12.0, mid.Point.X, 9);                                   // middle of the arc, not of its chord
        Assert.Equal(SnapModes.Center, SnapEngine.FindSnap(doc, P(10.1, 2.1), 0.5, all)!.Value.Mode);

        var bytes = HcadSerializer.SaveToBytes(doc);
        string json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"minReaderVersion\": 3", json);                     // older programs must not open and flatten it
        var back = (PolylineEntity)HcadSerializer.LoadFromBytes(bytes).Document.Entities[0];
        Assert.Equal(p.Bulges.ToArray(), back.Bulges.ToArray());
        Assert.Equal(0.25, back.Width);
        Assert.Equal(p.Length, back.Length, 12);

        // A drawing without arcs or widths stays readable by the first release.
        var plain = new DrawingDocument();
        plain.AddEntity(new PolylineEntity(new[] { P(0, 0), P(1, 0), P(1, 1) }, false) { LayerId = plain.CurrentLayerId });
        string plainJson = System.Text.Encoding.UTF8.GetString(HcadSerializer.SaveToBytes(plain));
        Assert.Contains("\"minReaderVersion\": 1", plainJson);
        Assert.DoesNotContain("bulges", plainJson);

        var view = new ViewTransform();
        view.SetViewport(800, 600);
        view.SetView(P(5, 2), 20);
        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, view, new RenderOptions { ShowGrid = false });
        var drawn = Assert.Single(surface.Polylines);
        Assert.True(drawn.Closed);
        Assert.True(drawn.Points.Length > 20);                                // the semicircles are drawn as curves
        Assert.Equal(5.0, drawn.Style.Width, 9);                              // 0.25 units at 20 px per unit
        Assert.InRange(drawn.Points.Max(q => q.X), 539.7, 540.0);          // reaches the right-most point (12,2) within the chord tolerance
        Assert.InRange(drawn.Points.Min(q => q.X), 260.0, 260.3);          // and the left-most point (-2,2)
    }

    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        s.Snap.Enabled = false;
        s.PolarEnabled = false;
        return s;
    }

    [Fact]
    public void Pline_arc_line_and_close_options()
    {
        var s = Session();
        s.StartCommand("PLINE");
        Assert.Empty(s.ActiveCommand!.Options);
        s.SubmitText("0,0");
        Assert.Equal(new[] { "Arc", "Halfwidth", "Length", "Width" }, s.ActiveCommand.Options.ToArray());
        s.SubmitText("10,0");
        s.SubmitText("A");
        Assert.Contains("endpoint of arc", s.Prompt);
        Assert.Equal(new[] { "Line", "Halfwidth", "Undo", "Width" }, s.ActiveCommand.Options.ToArray());
        s.SubmitText("10,4");                                                 // tangent to the first side: a semicircle
        s.SubmitText("L");
        Assert.Contains("next point", s.Prompt);
        s.SubmitText("0,4");
        s.SubmitText("A");
        s.SubmitText("C");                                                    // close with a tangent arc
        Assert.Null(s.ActiveCommand);

        var p = Assert.IsType<PolylineEntity>(Assert.Single(s.Document.Entities));
        Assert.True(p.IsClosed);
        Assert.Equal(4, p.Vertices.Count);
        Assert.Equal(0.0, p.Bulges[0], 12);
        Assert.Equal(1.0, p.Bulges[1], 9);
        Assert.Equal(0.0, p.Bulges[2], 12);
        Assert.Equal(1.0, p.Bulges[3], 9);
        Assert.Equal(20 + 4 * Math.PI, p.Length, 9);

        s.Undo();
        Assert.Equal(0, s.Document.EntityCount);                              // the whole polyline is one step
    }

    [Fact]
    public void Pline_length_width_halfwidth_and_undo_options()
    {
        var s = Session();
        var messages = CaptureMessages(s);
        s.StartCommand("PL");
        s.SubmitText("0,0");
        s.SubmitText("W");
        Assert.Contains("starting width <0.0000>", s.Prompt);
        s.SubmitText("0.5");
        Assert.Contains("ending width <0.5000>", s.Prompt);
        s.SubmitEnter();                                                      // same at both ends
        s.SubmitText("5,5");                                                  // heading 45 degrees
        s.SubmitText("L");
        Assert.Contains("length of line", s.Prompt);
        s.SubmitText("-3");
        Assert.Contains(messages, m => m.Contains("must be positive"));
        s.SubmitText(Math.Sqrt(2).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        s.SubmitText("20,20");
        s.SubmitText("U");                                                    // removes the last point
        s.SubmitText("H");
        Assert.Contains("starting half-width <0.2500>", s.Prompt);
        s.SubmitText("1");                                                    // width becomes 2 from here on
        s.SubmitEnter();                                                      // ending half-width: same
        s.SubmitText("6,20");
        s.SubmitEnter();

        var p = (PolylineEntity)s.Document.Entities[0];
        Assert.Equal(4, p.Vertices.Count);
        Assert.Equal(6.0, p.Vertices[2].X, 9);                                // continued 45 degrees for sqrt(2)
        Assert.Equal(6.0, p.Vertices[2].Y, 9);
        Assert.Equal((0.5, 0.5), p.GetSegmentWidth(0));                       // drawn before the half-width change: untouched
        Assert.Equal((0.5, 0.5), p.GetSegmentWidth(1));
        Assert.Equal((2.0, 2.0), p.GetSegmentWidth(2));                       // only the segment drawn afterwards is wider
        Assert.False(p.HasUniformWidth);
        Assert.Equal(2.0, p.MaxWidth);
        Assert.False(p.IsClosed);
        Assert.False(p.HasArcs);
    }

    [Fact]
    public void Pline_arc_preview_and_points_an_arc_cannot_reach()
    {
        var s = Session();
        var messages = CaptureMessages(s);
        s.StartCommand("PLINE");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(10, 0));
        s.SubmitKeyword("Arc");
        s.UpdateCursor(P(14, 4), 0.01);
        var preview = (PolylineEntity)Assert.Single(s.GetPreview());
        Assert.Equal(3, preview.Vertices.Count);
        Assert.True(preview.HasArcs);

        s.SubmitPoint(P(2, 0));                                               // straight back along the line
        Assert.Contains(messages, m => m.Contains("straight behind"));
        s.SubmitPoint(P(14, 4));                                              // quarter turn upward
        s.SubmitPoint(P(10, 8));                                              // arcs continue tangent to each other
        s.SubmitEnter();

        var p = (PolylineEntity)s.Document.Entities[0];
        Assert.Equal(4, p.Vertices.Count);
        double quarter = Math.Tan(Math.PI / 8);
        Assert.Equal(quarter, p.Bulges[1], 9);
        Assert.Equal(quarter, p.Bulges[2], 9);
        Assert.Equal(0.0, p.DistanceTo(P(10 + 4 * Math.Cos(Math.PI / 4), 4 + 4 * Math.Sin(Math.PI / 4))), 9); // on the second arc, centre (10,4)
    }
}

public class PolylineWidthTests
{
    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        s.Snap.Enabled = false;
        s.PolarEnabled = false;
        return s;
    }

    [Fact]
    public void Width_changes_apply_only_to_segments_drawn_afterwards()
    {
        var s = Session();
        s.StartCommand("PLINE");
        s.SubmitText("0,0");
        s.SubmitText("10,0");                                                 // thin
        s.SubmitText("W");
        s.SubmitText("2");
        s.SubmitText("2");
        s.SubmitText("20,0");                                                 // wide
        s.SubmitText("W");
        s.SubmitText("0");
        s.SubmitText("0");
        s.SubmitText("30,0");                                                 // thin again
        s.SubmitEnter();

        var p = (PolylineEntity)s.Document.Entities[0];
        Assert.Equal((0.0, 0.0), p.GetSegmentWidth(0));
        Assert.Equal((2.0, 2.0), p.GetSegmentWidth(1));
        Assert.Equal((0.0, 0.0), p.GetSegmentWidth(2));
        Assert.True(p.HasWidths);
        Assert.False(p.HasUniformWidth);

        // Picking: the wide middle section is hit anywhere on its band, the thin ends only on the line.
        Assert.Equal(0.0, p.DistanceTo(P(15, 0.9)), 9);
        Assert.Equal(0.9, p.DistanceTo(P(5, 0.9)), 9);
        Assert.Equal(new BoundingBox(-1, -1, 31, 1), p.Bounds);
    }

    [Fact]
    public void Tapered_segment_then_following_segments_keep_the_ending_width()
    {
        var s = Session();
        s.StartCommand("PLINE");
        s.SubmitText("0,0");
        s.SubmitText("W");
        s.SubmitText("0");                                                    // starting width
        s.SubmitText("4");                                                    // ending width: an arrow-head shape
        s.SubmitText("10,0");
        s.SubmitText("20,0");                                                 // continues at 4
        s.SubmitText("U");
        s.SubmitText("20,5");
        s.SubmitText("C");
        var p = (PolylineEntity)s.Document.Entities[0];
        Assert.Equal((0.0, 4.0), p.GetSegmentWidth(0));
        Assert.Equal((4.0, 4.0), p.GetSegmentWidth(1));
        Assert.Equal((4.0, 4.0), p.GetSegmentWidth(2));                       // the closing segment
        Assert.True(p.IsClosed);
    }

    [Fact]
    public void Varying_widths_round_trip_and_survive_edits()
    {
        var doc = new DrawingDocument();
        var p = new PolylineEntity(new[] { P(0, 0), P(10, 0), P(20, 0), P(30, 0) }, false) { LayerId = doc.CurrentLayerId };
        p.SetSegmentWidth(0, 0, 0);
        p.SetSegmentWidth(1, 1, 3);
        p.SetSegmentWidth(2, 3, 3);
        doc.AddEntity(p);

        string json = System.Text.Encoding.UTF8.GetString(HcadSerializer.SaveToBytes(doc));
        Assert.Contains("\"widths\"", json);
        Assert.Contains("\"minReaderVersion\": 3", json);
        var back = (PolylineEntity)HcadSerializer.LoadFromBytes(System.Text.Encoding.UTF8.GetBytes(json)).Document.Entities[0];
        Assert.Equal((0.0, 0.0), back.GetSegmentWidth(0));
        Assert.Equal((1.0, 3.0), back.GetSegmentWidth(1));
        Assert.Equal((3.0, 3.0), back.GetSegmentWidth(2));

        var copy = (PolylineEntity)p.CopyWithNewId();
        copy.Transform(Transform2D.Scaling(2, P(0, 0)));
        Assert.Equal((2.0, 6.0), copy.GetSegmentWidth(1));
        Assert.Equal((1.0, 3.0), p.GetSegmentWidth(1));                       // the original is untouched

        copy.Width = 0.5;                                                     // "Width" in the properties panel sets every segment
        Assert.True(copy.HasUniformWidth);
        Assert.Equal((0.5, 0.5), copy.GetSegmentWidth(0));

        var bad = (PolylineEntity)p.Clone();
        bad.SetSegmentWidth(0, -1, 0);
        Assert.False(bad.IsValid);
    }

    [Fact]
    public void Varying_widths_are_drawn_per_section()
    {
        var doc = new DrawingDocument();
        var p = new PolylineEntity(new[] { P(0, 0), P(10, 0), P(20, 0), P(30, 0), P(40, 0) }, false) { LayerId = doc.CurrentLayerId };
        p.SetSegmentWidth(1, 2, 2);
        p.SetSegmentWidth(2, 2, 0);                                           // taper down to a point
        doc.AddEntity(p);

        var view = new ViewTransform();
        view.SetViewport(800, 600);
        view.SetView(P(20, 0), 10);
        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, view, new RenderOptions { ShowGrid = false });

        // thin first section, wide second section, centre line of the taper, thin last section
        var widths = surface.Polylines.Select(l => l.Style.Width).ToArray();
        Assert.Equal(new[] { 1.0, 20.0, 1.0, 1.0 }, widths);
        Assert.Equal(300.0, surface.Polylines[1].Points[0].X, 9);             // wide stroke covers x = 10..20 only
        Assert.Equal(400.0, surface.Polylines[1].Points[^1].X, 9);

        var taper = Assert.Single(surface.Polygons);
        Assert.Equal(4, taper.Points.Length);
        Assert.Contains(taper.Points, q => q.X == 400 && q.Y == 290);         // full width at its start (2 units = 20 px)
        Assert.Contains(taper.Points, q => q.X == 400 && q.Y == 310);
        Assert.Equal(2, taper.Points.Count(q => q.X == 500 && q.Y == 300));   // zero width at its end
    }
}

public class RectangleOptionTests
{
    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        s.Snap.Enabled = false;
        return s;
    }

    [Fact]
    public void Dimensions_option_asks_length_and_width_then_a_side()
    {
        var s = Session();
        s.StartCommand("RECTANG");
        Assert.Equal(new[] { "Chamfer", "Fillet", "Width" }, s.ActiveCommand!.Options.ToArray());
        s.SubmitText("10,10");
        Assert.Equal(new[] { "Area", "Dimensions", "Rotation" }, s.ActiveCommand.Options.ToArray());
        s.SubmitText("D");
        Assert.Contains("length", s.Prompt);
        s.SubmitText("24");
        Assert.Contains("width", s.Prompt);
        s.SubmitText("12");
        s.UpdateCursor(P(0, 0), 0.01);                                        // cursor below and to the left
        Assert.Equal(new BoundingBox(-14, -2, 10, 10), s.GetPreview()[0].Bounds);
        s.SubmitPoint(P(50, 0));                                              // click below and to the right
        Assert.Equal(new BoundingBox(10, -2, 34, 10), s.Document.Entities[0].Bounds);

        s.StartCommand("REC");
        s.SubmitText("0,100");
        s.SubmitKeyword("Dimensions");
        s.SubmitText("5");
        s.SubmitText("3");
        s.SubmitEnter();                                                      // Enter: up and to the right
        Assert.Equal(new BoundingBox(0, 100, 5, 103), s.Document.Entities[1].Bounds);
    }

    [Fact]
    public void Area_option_computes_the_other_side()
    {
        var s = Session();
        var messages = CaptureMessages(s);
        s.StartCommand("RECTANG");
        s.SubmitText("0,0");
        s.SubmitText("A");
        s.SubmitText("0");
        Assert.Contains(messages, m => m.Contains("greater than zero"));
        s.SubmitText("200");
        Assert.Contains("[Length/Width] <Length>", s.Prompt);
        s.SubmitEnter();                                                      // based on length
        s.SubmitText("20");
        var byLength = (PolylineEntity)s.Document.Entities[0];
        Assert.Equal(new BoundingBox(0, 0, 20, 10), byLength.Bounds);
        Assert.Equal(200.0, byLength.Area, 9);

        s.StartCommand("RECTANG");
        s.SubmitText("100,0");
        s.SubmitText("A");
        s.SubmitText("200");
        s.SubmitText("W");
        s.SubmitText("8");
        Assert.Equal(new BoundingBox(100, 0, 125, 8), s.Document.Entities[1].Bounds);
    }

    [Fact]
    public void Rotation_option_turns_the_rectangle_and_the_square()
    {
        var s = Session();
        s.StartCommand("RECTANG");
        s.SubmitText("0,0");
        s.SubmitText("R");
        s.SubmitText("90");
        s.SubmitText("D");
        s.SubmitText("10");                                                   // length now runs up the page
        s.SubmitText("4");
        s.SubmitEnter();
        var turned = (PolylineEntity)s.Document.Entities[0];
        Assert.Equal(-4.0, turned.Bounds.MinX, 9);
        Assert.Equal(0.0, turned.Bounds.MaxX, 9);
        Assert.Equal(10.0, turned.Bounds.MaxY, 9);
        Assert.Equal(40.0, turned.Area, 9);

        // 45 degrees, picked by a direction, then a corner along the rotated diagonal.
        s.StartCommand("RECTANG");
        s.SubmitPoint(P(0, 0));
        s.SubmitKeyword("Rotation");
        s.SubmitPoint(P(5, 5));
        s.OrthoOverride = true;                                               // Shift: square in the rotated frame
        s.UpdateCursor(P(0, 8), 0.01);
        var corner = s.EffectiveCursor;
        s.SubmitPoint(corner);
        var diamond = (PolylineEntity)s.Document.Entities[1];
        Assert.Equal(4, diamond.Vertices.Count);
        double side = diamond.Vertices[0].DistanceTo(diamond.Vertices[1]);
        Assert.Equal(side, diamond.Vertices[1].DistanceTo(diamond.Vertices[2]), 9);
        Assert.Equal(side * side, diamond.Area, 9);
        Assert.Equal(0.0, (diamond.Vertices[1] - diamond.Vertices[0]).Dot(diamond.Vertices[2] - diamond.Vertices[1]), 9); // right angles
    }

    [Fact]
    public void Fillet_chamfer_and_width_options()
    {
        var s = Session();
        s.StartCommand("RECTANG");
        s.SubmitText("F");
        Assert.Contains("fillet radius", s.Prompt);
        s.SubmitText("2");
        s.SubmitText("W");
        s.SubmitText("0.5");
        s.SubmitText("0,0");
        s.SubmitText("20,10");
        var rounded = (PolylineEntity)s.Document.Entities[0];
        Assert.Equal(8, rounded.Vertices.Count);
        Assert.True(rounded.HasArcs);
        Assert.Equal(0.5, rounded.Width);
        Assert.Equal(200 - (4 - Math.PI) * 4, rounded.Area, 9);               // four corner squares become quarter circles
        Assert.Equal(2 * (16 + 6) + 2 * Math.PI * 2, rounded.Length, 9);
        Assert.Equal(new BoundingBox(-0.25, -0.25, 20.25, 10.25), rounded.Bounds);

        s.StartCommand("RECTANG");
        s.SubmitText("C");
        s.SubmitText("3");
        Assert.Contains("second chamfer distance <3.0000>", s.Prompt);
        s.SubmitText("1");
        s.SubmitText("100,0");
        s.SubmitText("@-20,10");                                              // dragged to the left: still a proper outline
        var cut = (PolylineEntity)s.Document.Entities[1];
        Assert.Equal(8, cut.Vertices.Count);
        Assert.False(cut.HasArcs);
        Assert.Equal(200 - 4 * (3 * 1 / 2.0), cut.Area, 9);
        Assert.Equal(new BoundingBox(80, 0, 100, 10), cut.Bounds);

        // A fillet too large for the rectangle is left out rather than producing a broken shape.
        s.StartCommand("RECTANG");
        s.SubmitText("F");
        s.SubmitText("50");
        s.SubmitText("0,100");
        s.SubmitText("@6,4");
        Assert.Equal(4, ((PolylineEntity)s.Document.Entities[2]).Vertices.Count);
    }
}

public class ArcMethodTests
{
    private const double Deg = Math.PI / 180.0;

    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        return s;
    }

    private static ArcEntity Last(EditorSession s) => (ArcEntity)s.Document.Entities[^1];

    private static void Run(EditorSession s, params string[] inputs)
    {
        s.StartCommand("ARC");
        foreach (string input in inputs) s.SubmitText(input);
        Assert.Null(s.ActiveCommand);
    }

    private static void AssertArc(ArcEntity arc, double cx, double cy, double r, double startDeg, double endDeg)
    {
        Assert.Equal(cx, arc.Center.X, 7);
        Assert.Equal(cy, arc.Center.Y, 7);
        Assert.Equal(r, arc.Radius, 7);
        Assert.Equal(0.0, ArcMath.NormalizeAngle(arc.StartAngle - startDeg * Deg + Math.PI) - Math.PI, 7);
        Assert.Equal(0.0, ArcMath.NormalizeAngle(arc.EndAngle - endDeg * Deg + Math.PI) - Math.PI, 7);
    }

    [Fact]
    public void Start_center_end_by_options_and_by_preset()
    {
        var s = Session();
        Run(s, "10,0", "C", "0,0", "0,5");                 // end is on the ray through 0,5; radius from the start
        AssertArc(Last(s), 0, 0, 10, 0, 90);
        Run(s, "SCE", "10,0", "0,0", "0,-5");
        AssertArc(Last(s), 0, 0, 10, 0, 270);              // always counter-clockwise from the start
    }

    [Fact]
    public void Start_center_angle_positive_and_negative()
    {
        var s = Session();
        Run(s, "10,0", "C", "0,0", "A", "90");
        AssertArc(Last(s), 0, 0, 10, 0, 90);
        Run(s, "SCA", "10,0", "0,0", "-90");               // clockwise: stored counter-clockwise from 270 to 0
        AssertArc(Last(s), 0, 0, 10, 270, 0);
        Run(s, "CSA", "0,0", "10,0", "200");
        AssertArc(Last(s), 0, 0, 10, 0, 200);

        var messages = CaptureMessages(s);
        s.StartCommand("ARC");
        foreach (string t in new[] { "SCA", "10,0", "0,0", "0" }) s.SubmitText(t);
        Assert.NotNull(s.ActiveCommand);                   // 0 degrees is rejected, command keeps asking
        s.SubmitText("360");
        Assert.NotNull(s.ActiveCommand);
        Assert.Equal(2, messages.Count(m => m.Contains("angle must be")));
        s.Cancel();
    }

    [Fact]
    public void Start_center_chord_length()
    {
        var s = Session();
        Run(s, "SCL", "10,0", "0,0", "10");                // chord = radius: 60 degrees
        AssertArc(Last(s), 0, 0, 10, 0, 60);
        Run(s, "CSL", "0,0", "10,0", "-10");               // negative: the 300 degree arc
        AssertArc(Last(s), 0, 0, 10, 0, 300);
        Run(s, "10,0", "C", "0,0", "L", "20");             // chord = diameter: half circle
        AssertArc(Last(s), 0, 0, 10, 0, 180);

        s.StartCommand("ARC");
        foreach (string t in new[] { "SCL", "10,0", "0,0", "20.5" }) s.SubmitText(t);
        Assert.NotNull(s.ActiveCommand);                   // longer than the diameter
        s.Cancel();
        Assert.Equal(3, s.Document.EntityCount);
    }

    [Fact]
    public void Start_end_angle()
    {
        var s = Session();
        Run(s, "10,0", "E", "0,10", "A", "90");
        AssertArc(Last(s), 0, 0, 10, 0, 90);
        Run(s, "SEA", "10,0", "0,10", "-90");              // clockwise from start to end: center on the other side
        AssertArc(Last(s), 10, 10, 10, 180, 270);
        Run(s, "SEA", "10,0", "0,10", "270");              // counter-clockwise the long way round
        AssertArc(Last(s), 10, 10, 10, 270, 180);
        Run(s, "SEA", "10,0", "-10,0", "180");
        AssertArc(Last(s), 0, 0, 10, 0, 180);
    }

    [Fact]
    public void Start_end_radius()
    {
        var s = Session();
        Run(s, "SER", "10,0", "0,10", "10");               // minor arc, counter-clockwise
        AssertArc(Last(s), 0, 0, 10, 0, 90);
        Run(s, "10,0", "E", "0,10", "R", "-10");           // negative radius: major arc
        AssertArc(Last(s), 10, 10, 10, 270, 180);

        s.StartCommand("ARC");
        foreach (string t in new[] { "SER", "10,0", "0,10", "5" }) s.SubmitText(t);
        Assert.NotNull(s.ActiveCommand);                   // radius shorter than half the chord
        s.Cancel();
    }

    [Fact]
    public void Start_end_direction()
    {
        var s = Session();
        Run(s, "SED", "10,0", "0,10", "90");               // leaves the start heading up: quarter circle about the origin
        AssertArc(Last(s), 0, 0, 10, 0, 90);
        Run(s, "10,0", "E", "0,10", "D", "10,-5");         // direction by a point: heading down, so clockwise the long way
        var arc = Last(s);
        AssertArc(arc, 0, 0, 10, 90, 0);
        Assert.Equal(270 * Deg, arc.Sweep, 7);

        s.StartCommand("ARC");
        foreach (string t in new[] { "SED", "0,0", "10,0", "0" }) s.SubmitText(t);
        Assert.NotNull(s.ActiveCommand);                   // pointing straight at the end point
        s.Cancel();
    }

    [Fact]
    public void Start_end_center()
    {
        var s = Session();
        Run(s, "10,0", "E", "0,7", "0,0");
        AssertArc(Last(s), 0, 0, 10, 0, 90);
    }

    [Fact]
    public void Continue_is_tangent_to_the_last_line_and_to_the_last_arc()
    {
        var s = Session();
        s.StartCommand("ARC");
        s.SubmitEnter();
        Assert.NotNull(s.ActiveCommand);                   // nothing to continue from yet
        Assert.Contains("start point", s.Prompt);
        s.Cancel();

        s.StartCommand("LINE");
        s.SubmitText("0,0");
        s.SubmitText("10,0");
        s.SubmitEnter();

        s.StartCommand("ARC");
        s.SubmitEnter();                                   // Enter = Continue
        Assert.Contains("tangent", s.Prompt);
        s.UpdateCursor(P(18, 6), 0.1);
        Assert.IsType<ArcEntity>(Assert.Single(s.GetPreview()));
        s.SubmitText("20,10");
        Assert.Null(s.ActiveCommand);
        AssertArc(Last(s), 10, 10, 10, 270, 0);            // leaves 10,0 heading +X, ends at 20,10 heading +Y

        Run(s, "CONTINUE", "10,20");                       // continues from 20,10 heading +Y
        AssertArc(Last(s), 10, 10, 10, 0, 90);

        Run(s, "SCA", "10,0", "0,0", "-90");               // drawn clockwise: ends at 0,-10 heading -X
        Run(s, "CONTINUE", "-10,-20");
        AssertArc(Last(s), 0, -20, 10, 90, 180);
        Assert.Equal(0.0, Last(s).DistanceTo(P(-10, -20)), 7);

        s.Undo();
        s.Undo();
        s.Undo();
        s.Undo();
        Assert.Equal(1, s.Document.EntityCount);           // only the line is left
    }

    [Fact]
    public void Prompts_options_and_preview_follow_the_method()
    {
        var s = Session();
        s.StartCommand("ARC");
        Assert.Equal(new[] { "Center", "Continue" }, s.ActiveCommand!.Options);
        s.SubmitText("10,0");
        Assert.Equal(new[] { "Center", "End" }, s.ActiveCommand!.Options);
        s.SubmitKeyword("END");
        s.SubmitText("0,10");
        Assert.Equal(new[] { "Angle", "Direction", "Radius" }, s.ActiveCommand!.Options);
        s.SubmitKeyword("RADIUS");
        Assert.Contains("radius", s.Prompt);
        s.UpdateCursor(P(0, 0), 0.1);                      // 10 from the end point: preview shows the radius 10 arc
        var preview = s.GetPreview();
        var arc = Assert.IsType<ArcEntity>(preview[0]);
        Assert.Equal(10.0, arc.Radius, 7);
        s.Cancel();

        s.StartCommand("ARC");
        s.SubmitText("SED");
        Assert.Empty(s.ActiveCommand!.Options);            // a preset method asks no option questions
        s.SubmitText("10,0");
        Assert.Contains("end point", s.Prompt);
        s.SubmitText("0,10");
        Assert.Contains("tangent direction", s.Prompt);
        s.Cancel();
        Assert.Equal(0, s.Document.EntityCount);
    }
}

public class GripEditTests
{
    private const double Deg = Math.PI / 180.0;

    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        s.PolarEnabled = false;
        return s;
    }

    private static T Add<T>(EditorSession s, T entity) where T : Entity
    {
        entity.LayerId = s.Document.CurrentLayerId;
        s.Document.AddEntity(entity);
        s.Selection.Add(entity.Id);
        return entity;
    }

    [Fact]
    public void Line_end_grip_stretches_and_midpoint_grip_moves()
    {
        var s = Session();
        var id = Add(s, new LineEntity(P(0, 0), P(10, 0))).Id;

        Assert.False(s.TryStartGripEdit(P(3, 3), 0.5));            // not on a grip
        Assert.True(s.TryStartGripEdit(P(10.2, -0.3), 0.5));       // end grip
        Assert.Contains("STRETCH", s.Prompt);
        Assert.True(s.WantsPoint);
        s.UpdateCursor(P(10, 8), 0.1);
        var preview = s.GetPreview();
        Assert.Equal(P(10, 8), Assert.IsType<LineEntity>(preview[0]).End);
        Assert.Equal(P(10, 0), ((LineEntity)s.Document.GetEntity(id)!).End);   // nothing changed yet
        s.SubmitPoint(P(10, 8));
        Assert.Null(s.ActiveCommand);
        var line = (LineEntity)s.Document.GetEntity(id)!;
        Assert.Equal(P(0, 0), line.Start);
        Assert.Equal(P(10, 8), line.End);
        Assert.True(s.Selection.Contains(id));                     // stays selected, same persistent id

        Assert.True(s.TryStartGripEdit(P(5, 4), 0.5));             // midpoint grip moves the whole line
        s.SubmitText("@1,1");
        line = (LineEntity)s.Document.GetEntity(id)!;
        Assert.Equal(P(1, 1), line.Start);
        Assert.Equal(P(11, 9), line.End);

        s.Undo();
        s.Undo();
        line = (LineEntity)s.Document.GetEntity(id)!;
        Assert.Equal(P(10, 0), line.End);
    }

    [Fact]
    public void Cancel_enter_and_same_point_leave_the_object_alone()
    {
        var s = Session();
        var id = Add(s, new LineEntity(P(0, 0), P(10, 0))).Id;
        s.TryStartGripEdit(P(0, 0), 0.5);
        s.Cancel();
        s.TryStartGripEdit(P(0, 0), 0.5);
        s.SubmitEnter();
        s.TryStartGripEdit(P(0, 0), 0.5);
        s.SubmitPoint(P(0, 0));
        Assert.Null(s.ActiveCommand);
        Assert.Equal(P(0, 0), ((LineEntity)s.Document.GetEntity(id)!).Start);
        Assert.False(s.UndoStack.CanUndo);

        s.TryStartGripEdit(P(0, 0), 0.5);
        s.SubmitPoint(P(10, 0));                                   // would make a zero-length line
        Assert.NotNull(s.ActiveCommand);
        s.Cancel();
    }

    [Fact]
    public void Grips_need_a_selection_an_idle_editor_and_an_unlocked_layer()
    {
        var s = Session();
        var line = Add(s, new LineEntity(P(0, 0), P(10, 0)));
        s.ClearSelection();
        Assert.False(s.TryStartGripEdit(P(0, 0), 0.5));
        s.Selection.Add(line.Id);
        s.StartCommand("LINE");
        Assert.False(s.TryStartGripEdit(P(0, 0), 0.5));
        s.Cancel();
        s.Document.GetLayer(line.LayerId)!.IsLocked = true;
        Assert.False(s.TryStartGripEdit(P(0, 0), 0.5));
    }

    [Fact]
    public void Shared_corner_of_selected_lines_moves_together_in_one_undo_step()
    {
        var s = Session();
        var a = Add(s, new LineEntity(P(0, 0), P(10, 0)));
        var b = Add(s, new LineEntity(P(10, 0), P(10, 10)));
        s.TryStartGripEdit(P(10, 0), 0.5);
        s.UpdateCursor(P(12, -2), 0.1);
        Assert.Equal(3, s.GetPreview().Count);                     // both lines and the rubber band
        s.SubmitPoint(P(12, -2));
        Assert.Equal(P(12, -2), ((LineEntity)s.Document.GetEntity(a.Id)!).End);
        Assert.Equal(P(12, -2), ((LineEntity)s.Document.GetEntity(b.Id)!).Start);
        s.Undo();
        Assert.Equal(P(10, 0), ((LineEntity)s.Document.GetEntity(a.Id)!).End);
        Assert.Equal(P(10, 0), ((LineEntity)s.Document.GetEntity(b.Id)!).Start);
    }

    [Fact]
    public void Circle_center_moves_and_quadrant_changes_radius()
    {
        var s = Session();
        var id = Add(s, new CircleEntity(P(0, 0), 5)).Id;
        s.TryStartGripEdit(P(0, 5), 0.5);
        s.SubmitPoint(P(0, 8));
        var c = (CircleEntity)s.Document.GetEntity(id)!;
        Assert.Equal(8.0, c.Radius, 9);
        Assert.Equal(P(0, 0), c.Center);
        s.TryStartGripEdit(P(0, 0), 0.5);
        s.SubmitPoint(P(3, 4));
        c = (CircleEntity)s.Document.GetEntity(id)!;
        Assert.Equal(P(3, 4), c.Center);
        Assert.Equal(8.0, c.Radius, 9);
    }

    [Fact]
    public void Arc_grips_refit_through_three_points_or_move()
    {
        var s = Session();
        var id = Add(s, new ArcEntity(P(0, 0), 10, 0, 180 * Deg)).Id;      // top half circle

        s.TryStartGripEdit(P(0, 10), 0.5);                                  // midpoint: pull the bulge down to 5
        s.SubmitPoint(P(0, 5));
        var arc = (ArcEntity)s.Document.GetEntity(id)!;
        Assert.Equal(0.0, arc.DistanceTo(P(10, 0)), 9);
        Assert.Equal(0.0, arc.DistanceTo(P(-10, 0)), 9);
        Assert.Equal(0.0, arc.DistanceTo(P(0, 5)), 9);
        Assert.Equal(12.5, arc.Radius, 9);
        Assert.Equal(P(10, 0), arc.StartPoint);

        s.TryStartGripEdit(P(0, 5), 0.5);                                   // through the chord: flips to the other side
        s.SubmitPoint(P(0, -5));
        arc = (ArcEntity)s.Document.GetEntity(id)!;
        Assert.Equal(0.0, arc.DistanceTo(P(0, -5)), 9);
        Assert.True(arc.StartPoint.NearlyEquals(P(-10, 0)));                // still stored counter-clockwise

        s.TryStartGripEdit(P(0, -5), 0.5);
        s.SubmitPoint(P(0, 0));                                             // in line with the ends: refused
        Assert.NotNull(s.ActiveCommand);
        s.Cancel();

        s.TryStartGripEdit(P(10, 0), 0.5);                                  // end grip: other end and midpoint stay on the arc
        s.SubmitPoint(P(10, -20));
        Assert.Null(s.ActiveCommand);
        arc = (ArcEntity)s.Document.GetEntity(id)!;
        Assert.Equal(0.0, arc.DistanceTo(P(10, -20)), 9);
        Assert.Equal(0.0, arc.DistanceTo(P(-10, 0)), 9);
        Assert.Equal(0.0, arc.DistanceTo(P(0, -5)), 9);

        var center = arc.Center;
        double radius = arc.Radius, sweep = arc.Sweep;
        s.TryStartGripEdit(center, 0.5);                                    // center grip moves the arc
        s.SubmitText("@100,0");
        arc = (ArcEntity)s.Document.GetEntity(id)!;
        Assert.True(arc.Center.NearlyEquals(P(center.X + 100, center.Y)));
        Assert.Equal(radius, arc.Radius, 9);
        Assert.Equal(sweep, arc.Sweep, 9);
    }

    [Fact]
    public void Polyline_vertex_grip_keeps_widths_bulges_and_closure()
    {
        var s = Session();
        var pl = new PolylineEntity(new[] { P(0, 0), P(10, 0), P(10, 10), P(0, 10) }, true, new[] { 0.0, 1.0, 0.0, 0.0 });
        pl.SetSegmentWidth(0, 1, 2);
        var id = Add(s, pl).Id;
        s.TryStartGripEdit(P(0, 10), 0.5);
        s.SubmitPoint(P(-5, 15));
        var after = (PolylineEntity)s.Document.GetEntity(id)!;
        Assert.Equal(P(-5, 15), after.Vertices[3]);
        Assert.Equal(P(10, 10), after.Vertices[2]);
        Assert.True(after.IsClosed);
        Assert.Equal(1.0, after.Bulges[1]);
        Assert.Equal((1.0, 2.0), after.GetSegmentWidth(0));
    }

    [Fact]
    public void Ortho_and_direct_distance_work_from_the_grip()
    {
        var s = Session();
        var id = Add(s, new LineEntity(P(0, 0), P(10, 0))).Id;
        s.OrthoEnabled = true;
        s.TryStartGripEdit(P(10, 0), 0.5);
        s.UpdateCursor(P(10.4, 7), 0.01);
        Assert.Equal(P(10, 7), s.EffectiveCursor);
        s.SubmitText("3");                                         // 3 units toward the cursor (straight up)
        Assert.Equal(P(10, 3), ((LineEntity)s.Document.GetEntity(id)!).End);
    }
}

public class ObjectSnapTests : IDisposable
{
    private const double Deg = Math.PI / 180.0;
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    private static DrawingDocument Doc(params Entity[] entities)
    {
        var doc = new DrawingDocument();
        foreach (var e in entities)
        {
            e.LayerId = doc.CurrentLayerId;
            doc.AddEntity(e);
        }
        return doc;
    }

    private static void AssertSnap(SnapResult? snap, SnapModes mode, double x, double y)
    {
        Assert.NotNull(snap);
        Assert.Equal(mode, snap!.Value.Mode);
        Assert.Equal(x, snap.Value.Point.X, 7);
        Assert.Equal(y, snap.Value.Point.Y, 7);
    }

    [Fact]
    public void Intersection_of_lines_arcs_circles_and_polylines()
    {
        var doc = Doc(new LineEntity(P(0, 0), P(10, 10)), new LineEntity(P(0, 10), P(10, 0)),
                      new CircleEntity(P(30, 0), 5), new LineEntity(P(20, 0), P(40, 0)),
                      new CircleEntity(P(36, 0), 5),
                      new ArcEntity(P(60, 0), 5, 0, 90 * Deg), new LineEntity(P(50, 3), P(70, 3)));
        AssertSnap(SnapEngine.FindSnap(doc, P(5.2, 4.9), 0.5, SnapModes.Intersection), SnapModes.Intersection, 5, 5);
        AssertSnap(SnapEngine.FindSnap(doc, P(25.1, 0.2), 0.5, SnapModes.Intersection), SnapModes.Intersection, 25, 0);
        AssertSnap(SnapEngine.FindSnap(doc, P(33, 4.1), 0.5, SnapModes.Intersection), SnapModes.Intersection, 33, 4);   // two circles
        AssertSnap(SnapEngine.FindSnap(doc, P(64, 3.1), 0.5, SnapModes.Intersection), SnapModes.Intersection, 64, 3);   // arc and line
        Assert.Null(SnapEngine.FindSnap(doc, P(56, 3), 0.5, SnapModes.Intersection));       // the line crosses the full circle there, not the arc
        Assert.Null(SnapEngine.FindSnap(doc, P(2, 2), 0.5, SnapModes.Intersection));        // on a line, away from any crossing
    }

    [Fact]
    public void Quadrant_only_on_the_part_of_an_arc_that_exists()
    {
        var doc = Doc(new CircleEntity(P(0, 0), 5), new ArcEntity(P(20, 0), 5, 45 * Deg, 135 * Deg));
        AssertSnap(SnapEngine.FindSnap(doc, P(-4.9, 0.2), 0.5, SnapModes.Quadrant), SnapModes.Quadrant, -5, 0);
        AssertSnap(SnapEngine.FindSnap(doc, P(0.1, -5.1), 0.5, SnapModes.Quadrant), SnapModes.Quadrant, 0, -5);
        AssertSnap(SnapEngine.FindSnap(doc, P(20, 5.1), 0.5, SnapModes.Quadrant), SnapModes.Quadrant, 20, 5);
        Assert.Null(SnapEngine.FindSnap(doc, P(25, 0), 0.5, SnapModes.Quadrant));
    }

    [Fact]
    public void Perpendicular_to_a_line_and_to_a_circle_from_the_previous_point()
    {
        var doc = Doc(new LineEntity(P(0, 0), P(20, 0)), new CircleEntity(P(50, 0), 5));
        // Hovering anywhere on the line gives the foot of the perpendicular from 6,8.
        AssertSnap(SnapEngine.FindSnap(doc, P(15, 0.2), 0.5, SnapModes.Perpendicular, P(6, 8)), SnapModes.Perpendicular, 6, 0);
        Assert.Null(SnapEngine.FindSnap(doc, P(15, 0.2), 0.5, SnapModes.Perpendicular));               // no previous point
        Assert.Null(SnapEngine.FindSnap(doc, P(15, 0.2), 0.5, SnapModes.Perpendicular, P(30, 8)));     // foot is past the end of the line
        Assert.Null(SnapEngine.FindSnap(doc, P(15, 5), 0.5, SnapModes.Perpendicular, P(6, 8)));        // cursor not on an object
        // To a circle: the line aims at the centre; the side nearer the cursor is used.
        AssertSnap(SnapEngine.FindSnap(doc, P(45.2, 0.9), 0.5, SnapModes.Perpendicular, P(30, 0)), SnapModes.Perpendicular, 45, 0);
        AssertSnap(SnapEngine.FindSnap(doc, P(54.8, 0.9), 0.5, SnapModes.Perpendicular, P(30, 0)), SnapModes.Perpendicular, 55, 0);
    }

    [Fact]
    public void Tangent_to_a_circle_from_the_previous_point()
    {
        var doc = Doc(new CircleEntity(P(0, 0), 5));
        var from = P(0, 10);                       // tangent points are at 60 degrees either side of straight up
        double x = 5 * Math.Sin(60 * Deg), y = 5 * Math.Cos(60 * Deg);
        AssertSnap(SnapEngine.FindSnap(doc, P(3.6, 3.5), 0.5, SnapModes.Tangent, from), SnapModes.Tangent, x, y);
        AssertSnap(SnapEngine.FindSnap(doc, P(-3.6, 3.5), 0.5, SnapModes.Tangent, from), SnapModes.Tangent, -x, y);
        var snap = SnapEngine.FindSnap(doc, P(3.6, 3.5), 0.5, SnapModes.Tangent, from)!.Value.Point;
        Assert.Equal(0.0, (snap - from).Dot(snap - P(0, 0)), 7);                                        // really tangent
        Assert.Null(SnapEngine.FindSnap(doc, P(3.6, 3.5), 0.5, SnapModes.Tangent, P(1, 1)));            // from inside the circle
        Assert.Null(SnapEngine.FindSnap(Doc(new LineEntity(P(0, 0), P(9, 0))), P(4, 0), 0.5, SnapModes.Tangent, from));
    }

    [Fact]
    public void Nearest_is_anywhere_on_the_object_and_gives_way_to_definite_points()
    {
        var doc = Doc(new LineEntity(P(0, 0), P(20, 0)), new CircleEntity(P(50, 0), 5));
        AssertSnap(SnapEngine.FindSnap(doc, P(7.3, 0.3), 0.5, SnapModes.Nearest), SnapModes.Nearest, 7.3, 0);
        AssertSnap(SnapEngine.FindSnap(doc, P(50, 5.3), 0.5, SnapModes.Nearest), SnapModes.Nearest, 50, 5);
        Assert.Null(SnapEngine.FindSnap(doc, P(7.3, 2), 0.5, SnapModes.Nearest));
        AssertSnap(SnapEngine.FindSnap(doc, P(19.8, 0.3), 0.5, SnapSettings.AllModes), SnapModes.Endpoint, 20, 0);
        AssertSnap(SnapEngine.FindSnap(doc, P(7.3, 0.3), 0.5, SnapSettings.AllModes, P(3, 9)), SnapModes.Perpendicular, 3, 0);
        AssertSnap(SnapEngine.FindSnap(doc, P(7.3, 0.3), 0.5, SnapSettings.AllModes), SnapModes.Nearest, 7.3, 0);
    }

    [Fact]
    public void Right_click_override_draws_a_perpendicular_line_and_lasts_one_point()
    {
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("recovery")));
        vm.Session.ContinuousLines = true;                 // these steps draw a chain of lines (LINEMODE Continuous)
        var s = vm.Session;
        s.Document.Settings.Format = LengthFormat.Decimal;
        var target = AddLine(s.Document, 0, 0, 20, 0);
        vm.OsnapOn = false;                                        // the override works even with OSNAP off

        vm.RunCommand("LINE");
        var menu = vm.GetContextMenu();
        var overrides = menu.Single(e => e.Label == "Snap Overrides").Children!;
        Assert.Equal(new[] { "Endpoint", "Midpoint", "Intersection", "Center", "Quadrant", "Perpendicular", "Tangent", "Nearest", "None" },
            overrides.Select(e => e.Label).ToArray());

        s.SubmitPoint(P(6, 8));
        vm.GetContextMenu().Single(e => e.Label == "Snap Overrides").Children!.Single(e => e.Label == "Perpendicular").Invoke();
        Assert.Equal(SnapModes.Perpendicular, s.SnapOverride);
        s.UpdateCursor(P(15, 0.2), 0.5);
        Assert.Equal(SnapModes.Perpendicular, s.CurrentSnap!.Value.Mode);
        vm.ClickPoint();
        var drawn = (LineEntity)s.Document.Entities[^1];
        Assert.NotEqual(target.Id, drawn.Id);
        Assert.Equal(P(6, 0), drawn.End);                          // meets the other line at 90 degrees
        Assert.Null(s.SnapOverride);                               // one point only

        s.UpdateCursor(P(15, 0.2), 0.5);
        Assert.Null(s.CurrentSnap);                                // OSNAP is off again for the following point

        // An override that finds nothing does not place a free point.
        vm.GetContextMenu().Single(e => e.Label == "Snap Overrides").Children!.Single(e => e.Label == "Center").Invoke();
        int before = s.Document.EntityCount;
        s.UpdateCursor(P(40, 40), 0.5);
        vm.ClickPoint();
        Assert.Equal(before, s.Document.EntityCount);
        Assert.Null(s.SnapOverride);
        Assert.Contains(vm.History, h => h.Contains("No Center point"));
        vm.Cancel();
        Assert.DoesNotContain(vm.GetContextMenu(), e => e.Label == "Snap Overrides");   // only while a point is asked for
    }

    [Fact]
    public void Running_snap_check_boxes_change_what_snaps_automatically_and_are_remembered()
    {
        string path = _temp.File("settings.json");
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("recovery")), new UserSettingsStore(path));
        var s = vm.Session;
        AddLine(s.Document, 0, 0, 20, 0);
        Assert.True(vm.IsRunningSnap(SnapModes.Midpoint));
        Assert.False(vm.IsRunningSnap(SnapModes.Nearest));
        var boxes = vm.GetSnapSettingsMenu();
        Assert.Equal(8, boxes.Count);
        Assert.Equal(true, boxes.Single(e => e.Label == "Endpoint").IsChecked);
        Assert.Equal(false, boxes.Single(e => e.Label == "Tangent").IsChecked);

        vm.RunCommand("LINE");
        s.UpdateCursor(P(10.1, 0.2), 0.5);
        Assert.Equal(SnapModes.Midpoint, s.CurrentSnap!.Value.Mode);

        boxes.Single(e => e.Label == "Midpoint").Invoke();         // untick Midpoint: no longer shown
        Assert.False(vm.IsRunningSnap(SnapModes.Midpoint));
        Assert.Null(s.CurrentSnap);
        vm.GetSnapSettingsMenu().Single(e => e.Label == "Nearest").Invoke();
        Assert.Equal(SnapModes.Nearest, s.CurrentSnap!.Value.Mode);
        vm.Cancel();

        var again = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("recovery2")), new UserSettingsStore(path));
        Assert.False(again.IsRunningSnap(SnapModes.Midpoint));
        Assert.True(again.IsRunningSnap(SnapModes.Nearest));
        Assert.True(again.IsRunningSnap(SnapModes.Endpoint));
        Assert.Equal(UserSettings.DefaultCrosshairPercent, again.Session.CrosshairSizePercent);
    }

    [Fact]
    public void Each_snap_type_has_its_own_marker()
    {
        var doc = Doc(new LineEntity(P(0, 0), P(10, 0)));
        var view = new ViewTransform();
        view.SetViewport(800, 600);
        var counts = new List<string>();
        foreach (var mode in SnapSettings.MenuOrder)
        {
            var surface = new RecordingSurface();
            new SceneRenderer().Render(surface, doc, view, new RenderOptions { Snap = new SnapResult(P(5, 0), mode, Guid.Empty), ShowGrid = false });
            counts.Add($"{surface.Lines.Count}/{string.Join(",", surface.Polylines.Select(pl => pl.Points.Length + (pl.Points[0].Y == pl.Points[1].Y ? "h" : "")))}/{surface.Circles.Count}/{surface.Rectangles.Count}");
        }
        Assert.Equal(8, counts.Distinct().Count());
    }
}

public class EditToolTests
{
    private const double Deg = Math.PI / 180.0;

    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        s.PolarEnabled = false;
        return s;
    }

    private static T Add<T>(EditorSession s, T entity) where T : Entity
    {
        entity.LayerId = s.Document.CurrentLayerId;
        s.Document.AddEntity(entity);
        return entity;
    }

    private static void Click(EditorSession s, double x, double y)
    {
        s.UpdateCursor(P(x, y), 0.5);
        s.SubmitPoint(s.EffectiveCursor);
    }

    private static void AssertPoint(Point2D p, double x, double y)
    {
        Assert.Equal(x, p.X, 7);
        Assert.Equal(y, p.Y, 7);
    }

    // ------------------------------------------------------------------ OFFSET

    [Fact]
    public void Offset_line_circle_and_arc_to_the_clicked_side_and_remembers_the_distance()
    {
        var s = Session();
        var line = Add(s, new LineEntity(P(0, 0), P(10, 0)));
        Add(s, new CircleEntity(P(50, 0), 5));
        Add(s, new ArcEntity(P(100, 0), 10, 0, 90 * Deg));

        s.StartCommand("OFFSET");
        s.SubmitText("2");
        Assert.True(s.PickingObject);
        Click(s, 5, 0);
        Assert.False(s.PickingObject);                      // now a side is wanted, snaps may help again
        s.UpdateCursor(P(5, 3), 0.5);
        Assert.Equal(P(0, 2), Assert.IsType<LineEntity>(Assert.Single(s.GetPreview())).Start);
        Click(s, 5, 3);
        var up = (LineEntity)s.Document.Entities[^1];
        Assert.Equal(P(0, 2), up.Start);
        Assert.Equal(P(10, 2), up.End);
        Assert.Equal(line.LayerId, up.LayerId);

        Click(s, 5, 0);                                     // the command keeps going
        Click(s, 5, -9);
        Assert.Equal(P(10, -2), ((LineEntity)s.Document.Entities[^1]).End);

        Click(s, 55, 0);
        Click(s, 50, 1);                                    // inside the circle
        Assert.Equal(3.0, ((CircleEntity)s.Document.Entities[^1]).Radius, 9);
        Click(s, 55, 0);
        Click(s, 70, 0);                                    // outside
        Assert.Equal(7.0, ((CircleEntity)s.Document.Entities[^1]).Radius, 9);

        Click(s, 100 + 10 * Math.Cos(45 * Deg), 10 * Math.Sin(45 * Deg));
        Click(s, 120, 20);
        var arc = (ArcEntity)s.Document.Entities[^1];
        Assert.Equal(12.0, arc.Radius, 9);
        Assert.Equal(90 * Deg, arc.Sweep, 9);
        s.SubmitEnter();
        Assert.Null(s.ActiveCommand);

        s.StartCommand("O");
        Assert.Contains("<2.0000>", s.Prompt);              // last distance offered
        s.SubmitEnter();
        Click(s, 55, 0);
        int before = s.Document.EntityCount;
        var messages = CaptureMessages(s);
        s.SubmitText("OFFSET");                             // not a point: stays
        s.Cancel();
        Assert.Equal(before, s.Document.EntityCount);

        s.Undo();                                           // each offset is its own undo step
        Assert.Equal(before - 1, s.Document.EntityCount);
    }

    [Fact]
    public void Offset_too_large_for_a_circle_is_refused()
    {
        var s = Session();
        Add(s, new CircleEntity(P(0, 0), 5));
        s.StartCommand("OFFSET");
        s.SubmitText("5");
        Click(s, 5, 0);
        Click(s, 0, 1);
        Assert.Equal(1, s.Document.EntityCount);
        Assert.NotNull(s.ActiveCommand);
        s.Cancel();
    }

    [Fact]
    public void Offset_rectangle_inward_and_outward_keeps_corners()
    {
        var s = Session();
        var rect = Add(s, new PolylineEntity(new[] { P(0, 0), P(20, 0), P(20, 10), P(0, 10) }, true));
        rect.SetSegmentWidth(0, 0.5, 0.5);

        var inner = (PolylineEntity)OffsetCommand.Build(rect, P(5, 1), 2)!;       // click inside
        Assert.True(inner.IsClosed);
        AssertPoint(inner.Vertices[0], 2, 2);
        AssertPoint(inner.Vertices[1], 18, 2);
        AssertPoint(inner.Vertices[2], 18, 8);
        AssertPoint(inner.Vertices[3], 2, 8);
        Assert.Equal((0.5, 0.5), inner.GetSegmentWidth(0));
        Assert.NotEqual(rect.Id, inner.Id);

        var outer = (PolylineEntity)OffsetCommand.Build(rect, P(5, -3), 2)!;      // click outside
        AssertPoint(outer.Vertices[0], -2, -2);
        AssertPoint(outer.Vertices[2], 22, 12);

        // Same rectangle drawn the other way round: inside is still inside.
        var reversed = new PolylineEntity(new[] { P(0, 0), P(0, 10), P(20, 10), P(20, 0) }, true);
        var inner2 = (PolylineEntity)OffsetCommand.Build(reversed, P(5, 1), 2)!;
        AssertPoint(inner2.Vertices[0], 2, 2);
        AssertPoint(inner2.Vertices[2], 18, 8);

        Assert.Null(OffsetCommand.Build(new PolylineEntity(new[] { P(0, 0), P(10, 0), P(20, 0) }, false), P(5, 5), 0.0 / 0.0));
    }

    [Fact]
    public void Offset_filleted_rectangle_changes_the_corner_radius_and_fails_when_the_corner_vanishes()
    {
        // 20 x 10 rectangle with radius-2 corners, drawn counter-clockwise.
        double b = Math.Tan(22.5 * Deg);
        var rounded = new PolylineEntity(
            new[] { P(2, 0), P(18, 0), P(20, 2), P(20, 8), P(18, 10), P(2, 10), P(0, 8), P(0, 2) }, true,
            new[] { 0, b, 0, b, 0, b, 0, b });

        var inner = (PolylineEntity)OffsetCommand.Build(rounded, P(10, 1), 1)!;
        AssertPoint(inner.Vertices[0], 2, 1);
        AssertPoint(inner.Vertices[1], 18, 1);
        AssertPoint(inner.Vertices[2], 19, 2);
        Assert.True(inner.TryGetArc(1, out var c, out double r, out _, out double sweep));
        AssertPoint(c, 18, 2);
        Assert.Equal(1.0, r, 7);
        Assert.Equal(90 * Deg, sweep, 7);

        var outer = (PolylineEntity)OffsetCommand.Build(rounded, P(10, -1), 1)!;
        Assert.True(outer.TryGetArc(1, out _, out double r2, out _, out _));
        Assert.Equal(3.0, r2, 7);
        AssertPoint(outer.Vertices[0], 2, -1);

        Assert.Null(OffsetCommand.Build(rounded, P(10, 1), 2));                    // corner radius would be zero
    }

    [Fact]
    public void Offset_open_polyline_with_a_corner()
    {
        var path = new PolylineEntity(new[] { P(0, 0), P(10, 0), P(10, 10) }, false);
        var left = (PolylineEntity)OffsetCommand.Build(path, P(5, 1), 1)!;
        AssertPoint(left.Vertices[0], 0, 1);
        AssertPoint(left.Vertices[1], 9, 1);
        AssertPoint(left.Vertices[2], 9, 10);
        var right = (PolylineEntity)OffsetCommand.Build(path, P(5, -1), 1)!;
        AssertPoint(right.Vertices[1], 11, -1);
        Assert.False(right.IsClosed);
    }

    // -------------------------------------------------------------------- TRIM

    [Fact]
    public void Trim_line_end_middle_and_refuses_when_nothing_crosses()
    {
        var s = Session();
        var line = Add(s, new LineEntity(P(0, 0), P(30, 0)));
        Add(s, new LineEntity(P(10, -5), P(10, 5)));
        Add(s, new LineEntity(P(20, -5), P(20, 5)));

        s.StartCommand("TRIM");
        Assert.True(s.PickingObject);
        Click(s, 15, 0);                                    // middle: line splits in two
        Assert.Equal(4, s.Document.EntityCount);
        var left = (LineEntity)s.Document.GetEntity(line.Id)!;
        Assert.Equal(P(0, 0), left.Start);
        AssertPoint(left.End, 10, 0);
        var right = s.Document.Entities.OfType<LineEntity>().Single(l => l.Start.NearlyEquals(P(20, 0)));
        Assert.Equal(P(30, 0), right.End);
        Assert.Equal(line.LayerId, right.LayerId);

        Click(s, 25, 0);                                    // end piece: removed back to x = 20... which leaves nothing of it
        Assert.Equal(4, s.Document.EntityCount);            // (its only crossing is at its own start, so nothing to trim to)

        s.Undo();                                           // one step undoes the whole split
        Assert.Equal(3, s.Document.EntityCount);
        Assert.Equal(P(30, 0), ((LineEntity)s.Document.GetEntity(line.Id)!).End);

        s.StartCommand("TR");
        Click(s, 27, 0);                                    // right end: cut back to x = 20
        var cut = (LineEntity)s.Document.GetEntity(line.Id)!;
        Assert.Equal(P(0, 0), cut.Start);
        AssertPoint(cut.End, 20, 0);
        Click(s, 3, 0);                                     // left end: cut back to x = 10
        cut = (LineEntity)s.Document.GetEntity(line.Id)!;
        AssertPoint(cut.Start, 10, 0);
        AssertPoint(cut.End, 20, 0);
        Assert.Equal(3, s.Document.EntityCount);

        var messages = CaptureMessages(s);
        Click(s, 15, 0);                                    // now only touches the others at its ends
        Assert.Contains(messages, m => m.Contains("nothing to trim"));
        Click(s, 100, 100);                                 // empty space
        Assert.Contains(messages, m => m.Contains("No object there"));
        s.SubmitEnter();
        Assert.Null(s.ActiveCommand);
    }

    [Fact]
    public void Trim_circle_between_two_lines_and_arc_at_a_line()
    {
        var s = Session();
        var circle = Add(s, new CircleEntity(P(0, 0), 10));
        Add(s, new LineEntity(P(-20, 0), P(20, 0)));        // crosses at 0 and 180 degrees
        s.StartCommand("TRIM");
        Click(s, 0, 10);                                    // remove the top half
        var half = Assert.IsType<ArcEntity>(s.Document.GetEntity(circle.Id));
        Assert.Equal(180 * Deg, half.StartAngle, 7);
        Assert.Equal(0.0, ArcMath.NormalizeAngle(half.EndAngle + 1e-9), 7);
        Assert.Equal(0.0, half.DistanceTo(P(0, -10)), 7);
        s.SubmitEnter();
        s.Undo();
        Assert.IsType<CircleEntity>(s.Document.GetEntity(circle.Id));

        var arc = Add(s, new ArcEntity(P(100, 0), 10, 0, 180 * Deg));
        Add(s, new LineEntity(P(100, 0), P(100, 20)));      // crosses the arc at 90 degrees
        s.StartCommand("TRIM");
        Click(s, 100 + 10 * Math.Cos(135 * Deg), 10 * Math.Sin(135 * Deg));
        var kept = (ArcEntity)s.Document.GetEntity(arc.Id)!;
        Assert.Equal(0.0, kept.StartAngle, 7);
        Assert.Equal(90 * Deg, kept.EndAngle, 7);

        var lonely = Add(s, new CircleEntity(P(300, 300), 5));
        var messages = CaptureMessages(s);
        Click(s, 305, 300);
        Assert.Contains(messages, m => m.Contains("two crossings"));
        Assert.IsType<CircleEntity>(s.Document.GetEntity(lonely.Id));
        s.Cancel();
    }

    [Fact]
    public void Trim_uses_polylines_and_arcs_as_edges_and_points_to_explode_for_polyline_targets()
    {
        var s = Session();
        var rect = Add(s, new PolylineEntity(new[] { P(0, 0), P(10, 0), P(10, 10), P(0, 10) }, true));
        var line = Add(s, new LineEntity(P(-5, 5), P(15, 5)));
        s.StartCommand("TRIM");
        Click(s, 5, 5);                                     // the part inside the rectangle
        Assert.Equal(3, s.Document.EntityCount);
        AssertPoint(((LineEntity)s.Document.GetEntity(line.Id)!).End, 0, 5);

        var messages = CaptureMessages(s);
        Click(s, 5, 0);
        Assert.Contains(messages, m => m.Contains("EXPLODE"));
        Assert.NotNull(s.Document.GetEntity(rect.Id));
        s.Cancel();
    }

    // ------------------------------------------------------------------ EXTEND

    [Fact]
    public void Extend_line_and_arc_to_the_first_object_met()
    {
        var s = Session();
        var line = Add(s, new LineEntity(P(0, 0), P(10, 0)));
        Add(s, new LineEntity(P(15, -5), P(15, 5)));
        Add(s, new LineEntity(P(25, -5), P(25, 5)));
        Add(s, new CircleEntity(P(-20, 0), 5));

        s.StartCommand("EXTEND");
        Click(s, 9, 0);                                     // right end: stops at the first line, not the second
        AssertPoint(((LineEntity)s.Document.GetEntity(line.Id)!).End, 15, 0);
        Click(s, 1, 0);                                     // left end: reaches the near side of the circle
        AssertPoint(((LineEntity)s.Document.GetEntity(line.Id)!).Start, -15, 0);
        Click(s, 14, 0);                                    // again: on to the next line
        AssertPoint(((LineEntity)s.Document.GetEntity(line.Id)!).End, 25, 0);
        var messages = CaptureMessages(s);
        Click(s, 24, 0);                                    // nothing further
        Assert.Contains(messages, m => m.Contains("does not meet"));
        s.SubmitEnter();
        s.Undo();
        AssertPoint(((LineEntity)s.Document.GetEntity(line.Id)!).End, 15, 0);

        var arc = Add(s, new ArcEntity(P(100, 0), 10, 0, 90 * Deg));
        Add(s, new LineEntity(P(80, 0), P(100, 0)));        // meets the circle at 180 degrees
        Add(s, new LineEntity(P(100, -20), P(100, -5)));    // meets the circle at 270 degrees
        s.StartCommand("EX");
        Click(s, 100.5, 10);                                // near the end (90 degrees): grows counter-clockwise to 180
        var grown = (ArcEntity)s.Document.GetEntity(arc.Id)!;
        Assert.Equal(0.0, grown.StartAngle, 7);
        Assert.Equal(180 * Deg, grown.EndAngle, 7);
        Click(s, 110, 0.5);                                 // near the start (0 degrees): grows clockwise to 270
        grown = (ArcEntity)s.Document.GetEntity(arc.Id)!;
        Assert.Equal(270 * Deg, grown.StartAngle, 7);
        Assert.Equal(180 * Deg, grown.EndAngle, 7);
        s.Cancel();
    }

    // ------------------------------------------------------- FILLET / CHAMFER

    [Fact]
    public void Fillet_with_radius_trims_both_lines_and_adds_a_tangent_arc()
    {
        var s = Session();
        var a = Add(s, new LineEntity(P(0, 0), P(10, 0)));
        var b = Add(s, new LineEntity(P(10, 0), P(10, 10)));
        s.StartCommand("FILLET");
        Assert.Equal(new[] { "Radius" }, s.ActiveCommand!.Options);
        s.SubmitKeyword("R");
        Assert.False(s.PickingObject);
        s.SubmitText("2");
        Assert.Contains("radius = 2", s.Prompt);
        Click(s, 5, 0);
        Click(s, 10, 5);
        Assert.Null(s.ActiveCommand);

        AssertPoint(((LineEntity)s.Document.GetEntity(a.Id)!).Start, 0, 0);
        AssertPoint(((LineEntity)s.Document.GetEntity(a.Id)!).End, 8, 0);
        AssertPoint(((LineEntity)s.Document.GetEntity(b.Id)!).Start, 10, 2);
        var arc = s.Document.Entities.OfType<ArcEntity>().Single();
        AssertPoint(arc.Center, 8, 2);
        Assert.Equal(2.0, arc.Radius, 7);
        Assert.Equal(90 * Deg, arc.Sweep, 7);
        Assert.Equal(0.0, arc.DistanceTo(P(8, 0)), 7);
        Assert.Equal(0.0, arc.DistanceTo(P(10, 2)), 7);
        Assert.Equal(a.LayerId, arc.LayerId);

        s.Undo();                                           // one step
        Assert.Equal(2, s.Document.EntityCount);
        AssertPoint(((LineEntity)s.Document.GetEntity(a.Id)!).End, 10, 0);

        s.StartCommand("F");
        Assert.Contains("radius = 2", s.Prompt);            // the radius is remembered
        s.Cancel();
    }

    [Fact]
    public void Fillet_radius_zero_makes_a_sharp_corner_by_extending_or_cutting_back()
    {
        var s = Session();
        var a = Add(s, new LineEntity(P(0, 0), P(6, 0)));           // stops short of the corner
        var b = Add(s, new LineEntity(P(10, -3), P(10, 10)));       // overshoots it
        s.StartCommand("FILLET");
        Click(s, 3, 0);
        Click(s, 10, 6);                                    // keep the upper part
        AssertPoint(((LineEntity)s.Document.GetEntity(a.Id)!).End, 10, 0);
        AssertPoint(((LineEntity)s.Document.GetEntity(b.Id)!).Start, 10, 0);
        AssertPoint(((LineEntity)s.Document.GetEntity(b.Id)!).End, 10, 10);
        Assert.Equal(2, s.Document.EntityCount);
    }

    [Fact]
    public void Fillet_refuses_parallel_lines_too_large_a_radius_the_same_line_and_polylines()
    {
        var s = Session();
        Add(s, new LineEntity(P(0, 0), P(10, 0)));
        Add(s, new LineEntity(P(0, 5), P(10, 5)));
        Add(s, new LineEntity(P(20, 0), P(20, 3)));
        Add(s, new PolylineEntity(new[] { P(50, 0), P(60, 0), P(60, 10) }, false));
        var messages = CaptureMessages(s);

        s.StartCommand("FILLET");
        Click(s, 5, 0);
        Click(s, 6, 0);
        Assert.Contains(messages, m => m.Contains("different line"));
        Click(s, 5, 5);
        Assert.Contains(messages, m => m.Contains("parallel"));
        s.Cancel();

        s.FilletRadius = 50;
        s.StartCommand("FILLET");
        Click(s, 5, 0);
        Click(s, 20, 2);
        Assert.Contains(messages, m => m.Contains("too short"));
        s.Cancel();

        s.StartCommand("FILLET");
        Click(s, 55, 0);
        Assert.Contains(messages, m => m.Contains("EXPLODE"));
        s.Cancel();
        Assert.Equal(4, s.Document.EntityCount);
        Assert.False(s.UndoStack.CanUndo);
    }

    [Fact]
    public void Chamfer_cuts_the_corner_at_the_two_distances()
    {
        var s = Session();
        var a = Add(s, new LineEntity(P(0, 0), P(10, 0)));
        var b = Add(s, new LineEntity(P(10, 0), P(10, 10)));
        s.StartCommand("CHAMFER");
        s.SubmitKeyword("D");
        s.SubmitText("2");
        Assert.Contains("second chamfer distance <2.0000>", s.Prompt);
        s.SubmitText("3");
        Click(s, 5, 0);
        Click(s, 10, 5);
        AssertPoint(((LineEntity)s.Document.GetEntity(a.Id)!).End, 8, 0);
        AssertPoint(((LineEntity)s.Document.GetEntity(b.Id)!).Start, 10, 3);
        var cut = s.Document.Entities.OfType<LineEntity>().Single(l => l.Id != a.Id && l.Id != b.Id);
        AssertPoint(cut.Start, 8, 0);
        AssertPoint(cut.End, 10, 3);
        Assert.Equal((2.0, 3.0), s.ChamferDistances);
    }

    // ----------------------------------------------------------------- EXPLODE

    [Fact]
    public void Explode_turns_polylines_into_lines_and_arcs_in_one_undo_step()
    {
        var s = Session();
        double bulge = Math.Tan(22.5 * Deg);
        var pl = Add(s, new PolylineEntity(new[] { P(0, 0), P(10, 0), P(12, 2), P(12, 10) }, false, new[] { 0, bulge, 0, 0 }));
        pl.Color = CadColor.FromRgb(255, 0, 0);
        var line = Add(s, new LineEntity(P(50, 0), P(60, 0)));
        s.Selection.Add(pl.Id);
        s.Selection.Add(line.Id);

        s.StartCommand("EXPLODE");
        Assert.Null(s.ActiveCommand);
        Assert.Equal(4, s.Document.EntityCount);            // 2 lines + 1 arc from the polyline, plus the untouched line
        Assert.Null(s.Document.GetEntity(pl.Id));
        var arc = s.Document.Entities.OfType<ArcEntity>().Single();
        AssertPoint(arc.Center, 10, 2);
        Assert.Equal(2.0, arc.Radius, 7);
        Assert.Equal(CadColor.FromRgb(255, 0, 0), arc.Color);
        Assert.Equal(0, s.Selection.Count);

        s.Undo();
        Assert.Equal(2, s.Document.EntityCount);
        Assert.NotNull(s.Document.GetEntity(pl.Id));

        s.Selection.Add(line.Id);
        var messages = CaptureMessages(s);
        s.StartCommand("X");
        Assert.Contains(messages, m => m.Contains("Nothing to explode"));
    }

    [Fact]
    public void Object_snap_is_off_while_picking_an_object()
    {
        var s = Session();
        Add(s, new LineEntity(P(0, 0), P(10, 0)));
        s.StartCommand("LINE");
        s.UpdateCursor(P(9.8, 0.1), 0.5);
        Assert.NotNull(s.CurrentSnap);                      // drawing: the endpoint is acquired
        s.Cancel();
        s.StartCommand("TRIM");
        s.UpdateCursor(P(9.8, 0.1), 0.5);
        Assert.Null(s.CurrentSnap);                         // picking: the click is taken where it is
        s.Cancel();
    }
}

public class LineModeTests
{
    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        return s;
    }

    [Fact]
    public void Line_ends_at_the_second_point_and_enter_or_space_starts_the_next_one()
    {
        var s = Session();
        Assert.False(s.ContinuousLines);
        s.StartCommand("LINE");
        s.SubmitText("0,0");
        Assert.Contains("second point", s.Prompt);
        Assert.NotNull(s.ActiveCommand);
        s.SubmitText("10,0");
        Assert.Null(s.ActiveCommand);                       // finished without pressing Enter
        Assert.Equal(1, s.Document.EntityCount);

        s.SubmitEnter();                                    // repeats LINE
        Assert.Equal("LINE", s.ActiveCommand!.Name);
        s.SubmitText("@0,5");                               // relative to the end of the previous line
        s.SubmitText("3");                                  // direct distance also ends the command
        Assert.Null(s.ActiveCommand);
        Assert.Equal(2, s.Document.EntityCount);
        Assert.Equal(P(10, 5), ((LineEntity)s.Document.Entities[1]).Start);

        s.Undo();                                           // each line is its own undo step
        Assert.Equal(1, s.Document.EntityCount);

        s.StartCommand("LINE");
        s.SubmitText("0,0");
        s.SubmitText("0,0");                                // zero length: ignored, still waiting
        Assert.NotNull(s.ActiveCommand);
        s.SubmitKeyword("UNDO");                            // takes back the first point
        Assert.Contains("first point", s.Prompt);
        s.Cancel();
        Assert.Equal(1, s.Document.EntityCount);
    }

    [Fact]
    public void Linemode_switches_between_single_and_continuous()
    {
        var s = Session();
        s.StartCommand("LINEMODE");
        Assert.Contains("<Single>", s.Prompt);
        s.SubmitKeyword("C");
        Assert.True(s.ContinuousLines);
        Assert.Null(s.ActiveCommand);

        s.StartCommand("LINE");
        s.SubmitText("0,0");
        s.SubmitText("10,0");
        s.SubmitText("10,10");
        Assert.NotNull(s.ActiveCommand);                    // keeps going
        s.SubmitKeyword("CLOSE");
        Assert.Equal(3, s.Document.EntityCount);

        s.StartCommand("LINEMODE");
        Assert.Contains("<Continuous>", s.Prompt);
        s.SubmitKeyword("SINGLE");
        Assert.False(s.ContinuousLines);
    }
}

public class ScalePreviewTests
{
    private static EditorSession Session(out PolylineEntity rect)
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        s.PolarEnabled = false;
        rect = new PolylineEntity(new[] { P(0, 0), P(40, 0), P(40, 30), P(0, 30) }, true) { LayerId = s.Document.CurrentLayerId };
        s.Document.AddEntity(rect);
        s.Selection.Add(rect.Id);
        return s;
    }

    [Fact]
    public void Scaled_result_follows_the_cursor_and_a_click_accepts_it()
    {
        var s = Session(out var rect);
        s.StartCommand("SCALE");
        Assert.Empty(s.GetPreview());                       // no base point yet
        s.SubmitPoint(P(0, 0));                             // farthest corner is 40,30: 50 away = same size

        s.UpdateCursor(P(40, 30), 0.1);
        var same = Assert.IsType<PolylineEntity>(s.GetPreview()[0]);
        Assert.Equal(P(40, 30), same.Vertices[2]);
        Assert.IsType<LineEntity>(s.GetPreview()[1]);       // rubber band from the base point

        s.UpdateCursor(P(100, 0), 0.1);                     // 100 away: twice the size, whatever the direction
        var bigger = (PolylineEntity)s.GetPreview()[0];
        Assert.Equal(80.0, bigger.Vertices[2].X, 9);
        Assert.Equal(60.0, bigger.Vertices[2].Y, 9);
        Assert.Equal(P(40, 30), ((PolylineEntity)s.Document.GetEntity(rect.Id)!).Vertices[2]);   // nothing changed yet

        s.UpdateCursor(P(0, 25), 0.1);                      // 25 away: half size
        Assert.Equal(20.0, ((PolylineEntity)s.GetPreview()[0]).Vertices[2].X, 9);

        var messages = CaptureMessages(s);
        s.SubmitPoint(P(0, 25));
        Assert.Null(s.ActiveCommand);
        var result = (PolylineEntity)s.Document.GetEntity(rect.Id)!;
        Assert.Equal(20.0, result.Vertices[2].X, 9);
        Assert.Equal(15.0, result.Vertices[2].Y, 9);
        Assert.Contains(messages, m => m.Contains("Scaled by 0.5"));

        s.Undo();
        Assert.Equal(P(40, 30), ((PolylineEntity)s.Document.GetEntity(rect.Id)!).Vertices[2]);
    }

    [Fact]
    public void A_typed_factor_still_works_and_a_click_on_the_base_point_is_refused()
    {
        var s = Session(out var rect);
        s.StartCommand("SCALE");
        s.SubmitPoint(P(0, 0));
        s.UpdateCursor(P(0, 0), 0.1);
        Assert.Empty(s.GetPreview());
        s.SubmitPoint(P(0, 0));
        Assert.NotNull(s.ActiveCommand);                    // refused, still asking
        s.SubmitText("3");
        Assert.Null(s.ActiveCommand);
        Assert.Equal(P(120, 90), ((PolylineEntity)s.Document.GetEntity(rect.Id)!).Vertices[2]);
    }

    [Fact]
    public void Base_point_away_from_the_object_uses_its_farthest_corner_as_the_same_size_distance()
    {
        var s = Session(out var rect);
        s.StartCommand("SCALE");
        s.SubmitPoint(P(-10, 0));                           // farthest corner 40,30 is sqrt(2500+900) away
        double reference = Math.Sqrt(50 * 50 + 30 * 30);
        var scale = (ScaleCommand)s.ActiveCommand!;
        Assert.Equal(1.0, scale.FactorAt(P(40, 30))!.Value, 9);
        Assert.Equal(2.0, scale.FactorAt(P(-10 + 2 * reference, 0))!.Value, 9);
        s.Cancel();
    }
}

public class TangentLineTests
{
    private static EditorSession Session(params Entity[] entities)
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        s.PolarEnabled = false;
        s.Snap.Modes = SnapModes.Tangent;
        foreach (var e in entities)
        {
            e.LayerId = s.Document.CurrentLayerId;
            s.Document.AddEntity(e);
        }
        return s;
    }

    private static void Click(EditorSession s, double x, double y)
    {
        s.UpdateCursor(P(x, y), 0.5);
        s.SubmitPoint(s.EffectiveCursor);
    }

    private static LineEntity LastLine(EditorSession s) => s.Document.Entities.OfType<LineEntity>().Last();

    private static void AssertTangent(LineEntity line, Point2D center, double radius)
    {
        Assert.Equal(radius, SegmentMath.DistanceToPoint(line.Start, line.End, center), 6);     // touches, does not cross
    }

    [Fact]
    public void Tangent_snap_works_on_the_first_point_of_a_line()
    {
        var s = Session(new CircleEntity(P(0, 0), 5));
        s.StartCommand("LINE");
        s.UpdateCursor(P(0.2, 5.1), 0.5);                   // hovering the top of the circle, no previous point
        var snap = s.CurrentSnap!.Value;
        Assert.Equal(SnapModes.Tangent, snap.Mode);
        Assert.True(snap.Deferred);
        Click(s, 0.2, 5.1);
        Assert.NotNull(s.ActiveCommand);
        Assert.Equal(0, s.Document.Entities.OfType<LineEntity>().Count());

        s.UpdateCursor(P(20, 5), 0.5);                      // the preview already starts on the tangent point
        var preview = Assert.IsType<LineEntity>(Assert.Single(s.GetPreview()));
        AssertTangent(preview, P(0, 0), 5);

        s.SubmitText("20,5");                               // from 20,5 the upper tangent point is 0,5
        Assert.Null(s.ActiveCommand);
        var line = LastLine(s);
        Assert.Equal(0.0, line.Start.X, 6);
        Assert.Equal(5.0, line.Start.Y, 6);
        Assert.Equal(P(20, 5), line.End);
        AssertTangent(line, P(0, 0), 5);
    }

    [Fact]
    public void The_side_of_the_circle_that_was_clicked_decides_which_tangent_is_used()
    {
        var s = Session(new CircleEntity(P(0, 0), 5));
        s.StartCommand("LINE");
        Click(s, 0.2, -5.1);                                // bottom of the circle
        s.SubmitText("20,0");
        var line = LastLine(s);
        Assert.True(line.Start.Y < 0);                      // the lower of the two tangents from 20,0
        AssertTangent(line, P(0, 0), 5);

        var messages = CaptureMessages(s);
        s.StartCommand("LINE");
        Click(s, 5.1, 0.1);
        s.SubmitText("1,1");                                // inside the circle: no tangent exists
        Assert.NotNull(s.ActiveCommand);
        Assert.Contains(messages, m => m.Contains("No tangent"));
        s.SubmitKeyword("UNDO");                            // back to a normal first point
        s.SubmitText("50,50");
        s.SubmitText("60,50");
        Assert.Equal(P(50, 50), LastLine(s).Start);
    }

    [Fact]
    public void Line_tangent_to_two_circles_outer_and_crossing()
    {
        var s = Session(new CircleEntity(P(0, 0), 5), new CircleEntity(P(30, 0), 5));
        s.StartCommand("LINE");
        Click(s, 0, 5.2);                                   // top of the first
        Click(s, 30, 5.2);                                  // top of the second: the outer tangent along y = 5
        var top = LastLine(s);
        Assert.Equal(5.0, top.Start.Y, 6);
        Assert.Equal(5.0, top.End.Y, 6);
        Assert.Equal(0.0, top.Start.X, 6);
        Assert.Equal(30.0, top.End.X, 6);

        s.StartCommand("LINE");
        Click(s, 0.5, 5.1);                                 // top of the first
        Click(s, 29.5, -5.1);                               // bottom of the second: the crossing tangent
        var cross = LastLine(s);
        AssertTangent(cross, P(0, 0), 5);
        AssertTangent(cross, P(30, 0), 5);
        Assert.True(cross.Start.Y > 0 && cross.End.Y < 0);
        Assert.Equal(2, s.Document.Entities.OfType<LineEntity>().Count());
    }

    [Fact]
    public void Tangent_as_the_second_point_still_works_and_other_commands_are_not_offered_a_first_point_tangent()
    {
        var s = Session(new CircleEntity(P(0, 0), 5));
        s.StartCommand("LINE");
        s.SubmitText("0,10");
        Click(s, 4.4, 2.6);                                 // near the right-hand tangent point (60 degrees either side of up)
        var line = LastLine(s);
        Assert.Equal(P(0, 10), line.Start);
        Assert.Equal(5 * Math.Sin(Math.PI / 3), line.End.X, 6);
        Assert.Equal(2.5, line.End.Y, 6);

        s.StartCommand("CIRCLE");
        s.UpdateCursor(P(0.2, 5.1), 0.5);
        Assert.Null(s.CurrentSnap);                         // a circle's centre cannot be "tangent" to anything
        s.Cancel();
    }

    [Fact]
    public void Line_from_a_point_to_the_tangent_of_a_circle_by_right_click_override_or_typed_TAN()
    {
        // The owner's sequence: LINE, any first point, choose Tangent, click the circle.
        var s = Session(new CircleEntity(P(100, 50), 24));
        s.Snap.Enabled = false;                             // works with OSNAP off
        s.StartCommand("LINE");
        s.SubmitText("0,80");
        s.SetSnapOverride(SnapModes.Tangent);               // what right-click > Snap Overrides > Tangent does
        s.UpdateCursor(P(100, 74.2), 0.4);                  // hovering the top of the circle
        var snap = s.CurrentSnap!.Value;
        Assert.Equal(SnapModes.Tangent, snap.Mode);
        Assert.False(snap.Deferred);
        s.SubmitPoint(s.EffectiveCursor);
        var line = LastLine(s);
        Assert.Equal(P(0, 80), line.Start);
        AssertTangent(line, P(100, 50), 24);
        Assert.Equal(0.0, (line.End - line.Start).Dot(line.End - P(100, 50)), 6);   // 90 degrees to the radius at the touching point
        Assert.Equal(24.0, line.End.DistanceTo(P(100, 50)), 6);

        s.StartCommand("LINE");
        s.SubmitText("0,80");
        s.SubmitText("tan");                                // typed instead of the menu
        Assert.Equal(SnapModes.Tangent, s.SnapOverride);
        s.UpdateCursor(P(100, 25.8), 0.4);                  // the bottom of the circle: the other tangent
        s.SubmitPoint(s.EffectiveCursor);
        var lower = LastLine(s);
        AssertTangent(lower, P(100, 50), 24);
        Assert.True(lower.End.Y < 50);
        Assert.Null(s.SnapOverride);
    }

    [Fact]
    public void Typed_snap_codes_give_way_to_command_options_with_the_same_letters()
    {
        var s = Session(new LineEntity(P(0, 0), P(10, 0)));
        s.StartCommand("ARC");
        s.SubmitText("50,50");
        s.SubmitText("END");                                // ARC's own End option, not the Endpoint snap
        Assert.Null(s.SnapOverride);
        Assert.Contains("end point", s.Prompt);
        s.Cancel();

        s.StartCommand("LINE");
        s.SubmitText("END");
        Assert.Equal(SnapModes.Endpoint, s.SnapOverride);
        s.UpdateCursor(P(9.7, 0.2), 0.5);
        s.SubmitPoint(s.EffectiveCursor);
        s.SubmitText("mid");
        s.UpdateCursor(P(5.2, 0.1), 0.5);
        Assert.Equal(SnapModes.Midpoint, s.CurrentSnap!.Value.Mode);
        s.Cancel();
        Assert.Null(s.SnapOverride);
    }

    [Fact]
    public void Shift_right_click_menu_and_the_codes_for_snaps_that_do_not_exist_yet()
    {
        using var temp = new TempDirectory();
        var vm = new MainViewModel(new FakeUi(), new HAQQ.CAD.Documents.RecoveryStore(temp.File("recovery")));
        var idle = vm.GetObjectSnapMenu();                  // nothing asking for a point: the check boxes
        Assert.Equal(8, idle.Count);
        Assert.True(idle.All(e => e.IsChecked is not null));

        vm.RunCommand("LINE");
        var menu = vm.GetObjectSnapMenu();                  // asking for a point: one-shot snaps first
        Assert.Equal(new[] { "Endpoint", "Midpoint", "Intersection", "Center", "Quadrant", "Perpendicular", "Tangent", "Nearest", "None", "Object Snap Settings" },
            menu.Select(e => e.Label).ToArray());
        menu.Single(e => e.Label == "Quadrant").Invoke();
        Assert.Equal(SnapModes.Quadrant, vm.Session.SnapOverride);

        vm.Session.SubmitText("NOD");
        vm.Session.SubmitText("ins");
        Assert.Contains(vm.History, h => h.Contains("Node snap needs point objects"));
        Assert.Contains(vm.History, h => h.Contains("Insertion snap needs blocks"));
        Assert.NotNull(vm.Session.ActiveCommand);           // the command carries on
        Assert.Equal(0, vm.Session.Document.EntityCount);
    }

    [Fact]
    public void Common_tangents_of_two_circles()
    {
        Assert.Equal(4, TangentMath.Common(P(0, 0), 5, P(30, 0), 3).Count());       // apart: two outer, two crossing
        Assert.Equal(2, TangentMath.Common(P(0, 0), 5, P(6, 0), 3).Count());        // overlapping: outer only
        Assert.Empty(TangentMath.Common(P(0, 0), 5, P(1, 0), 2));                   // one inside the other
        Assert.Empty(TangentMath.Common(P(0, 0), 5, P(0, 0), 5));
        foreach (var (a, b) in TangentMath.Common(P(0, 0), 5, P(30, 10), 3))
        {
            Assert.Equal(5.0, a.DistanceTo(P(0, 0)), 9);
            Assert.Equal(3.0, b.DistanceTo(P(30, 10)), 9);
            Assert.Equal(0.0, (b - a).Dot(a - P(0, 0)), 7);                          // perpendicular to both radii
            Assert.Equal(0.0, (b - a).Dot(b - P(30, 10)), 7);
        }
        Assert.Empty(TangentMath.FromPoint(P(0, 0), 5, P(1, 1)));
        Assert.Equal(2, TangentMath.FromPoint(P(0, 0), 5, P(10, 0)).Count());
    }
}

public class JoinTests
{
    private const double Deg = Math.PI / 180.0;

    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        return s;
    }

    private static T Add<T>(EditorSession s, T entity) where T : Entity
    {
        entity.LayerId = s.Document.CurrentLayerId;
        s.Document.AddEntity(entity);
        s.Selection.Add(entity.Id);
        return entity;
    }

    [Fact]
    public void Lines_drawn_in_any_order_and_direction_join_into_one_polyline()
    {
        var s = Session();
        Add(s, new LineEntity(P(10, 0), P(10, 10)));        // middle
        Add(s, new LineEntity(P(0, 0), P(10, 0)));          // first
        Add(s, new LineEntity(P(20, 10), P(10, 10)));       // last, drawn backwards
        var apart = Add(s, new LineEntity(P(100, 100), P(110, 100)));

        s.StartCommand("JOIN");
        Assert.Null(s.ActiveCommand);
        Assert.Equal(2, s.Document.EntityCount);            // the polyline and the line that touches nothing
        var pl = s.Document.Entities.OfType<PolylineEntity>().Single();
        Assert.False(pl.IsClosed);
        Assert.Equal(4, pl.Vertices.Count);
        Assert.Equal(30.0, pl.Length, 9);
        var ends = new[] { pl.Vertices[0], pl.Vertices[^1] };
        Assert.Contains(P(0, 0), ends);
        Assert.Contains(P(20, 10), ends);
        Assert.NotNull(s.Document.GetEntity(apart.Id));
        Assert.True(s.Selection.Contains(pl.Id));           // the result is selected
        Assert.Equal(1, s.Selection.Count);

        s.Undo();                                           // one step
        Assert.Equal(4, s.Document.EntityCount);
        Assert.Empty(s.Document.Entities.OfType<PolylineEntity>());
    }

    [Fact]
    public void Four_lines_forming_a_rectangle_become_a_closed_polyline()
    {
        var s = Session();
        Add(s, new LineEntity(P(0, 0), P(20, 0)));
        Add(s, new LineEntity(P(20, 0), P(20, 10)));
        Add(s, new LineEntity(P(0, 10), P(20, 10)));
        Add(s, new LineEntity(P(0, 10), P(0, 0)));
        s.StartCommand("J");
        var pl = Assert.IsType<PolylineEntity>(Assert.Single(s.Document.Entities));
        Assert.True(pl.IsClosed);
        Assert.Equal(4, pl.Vertices.Count);
        Assert.Equal(60.0, pl.Length, 9);
        Assert.Equal(200.0, Math.Abs(pl.Area), 9);
    }

    [Fact]
    public void Arcs_and_polylines_join_too_and_keep_their_curve_and_width()
    {
        var s = Session();
        Add(s, new LineEntity(P(0, 0), P(10, 0)));
        Add(s, new ArcEntity(P(10, 5), 5, 270 * Deg, 90 * Deg));           // half circle from 10,0 round to 10,10
        var wide = new PolylineEntity(new[] { P(0, 10), P(10, 10) }, false);   // meets the arc with its END
        wide.SetSegmentWidth(0, 1, 2);
        Add(s, wide);

        s.StartCommand("JOIN");
        var pl = Assert.IsType<PolylineEntity>(Assert.Single(s.Document.Entities));
        Assert.Equal(4, pl.Vertices.Count);
        Assert.Equal(10 + 5 * Math.PI + 10, pl.Length, 7);
        int arcIndex = Enumerable.Range(0, pl.SegmentCount).Single(i => pl.TryGetArc(i, out _, out _, out _, out _));
        Assert.True(pl.TryGetArc(arcIndex, out var c, out double r, out _, out double sweep));
        Assert.Equal(10.0, c.X, 7);
        Assert.Equal(5.0, c.Y, 7);
        Assert.Equal(5.0, r, 7);
        Assert.Equal(180 * Deg, sweep, 7);
        Assert.Equal(0.0, pl.DistanceTo(P(15, 5)), 7);                     // bulges the right way
        // The wide piece was reversed to fit, so its taper is reversed with it.
        int wideIndex = Enumerable.Range(0, pl.SegmentCount).Single(i => pl.GetSegmentWidth(i) != (0.0, 0.0));
        var (a, b) = pl.GetSegment(wideIndex);
        var (ws, we) = pl.GetSegmentWidth(wideIndex);
        Assert.Equal(a.X < b.X ? (1.0, 2.0) : (2.0, 1.0), (ws, we));
    }

    [Fact]
    public void Nothing_joins_when_ends_do_not_touch()
    {
        var s = Session();
        Add(s, new LineEntity(P(0, 0), P(10, 0)));
        Add(s, new LineEntity(P(10.5, 0), P(20, 0)));       // a gap
        Add(s, new LineEntity(P(5, -5), P(5, 5)));          // crosses, but not at an end
        Add(s, new CircleEntity(P(0, 0), 3));
        var messages = CaptureMessages(s);
        s.StartCommand("JOIN");
        Assert.Equal(4, s.Document.EntityCount);
        Assert.Contains(messages, m => m.Contains("Nothing to join"));
        Assert.False(s.UndoStack.CanUndo);
    }

    [Fact]
    public void Line_ending_on_a_circle_joins_into_one_polyline_that_goes_round_the_circle()
    {
        var s = Session();
        Add(s, new LineEntity(P(-20, 0), P(-5, 0)));        // ends on the left of the circle
        Add(s, new CircleEntity(P(0, 0), 5));
        s.StartCommand("JOIN");
        var pl = Assert.IsType<PolylineEntity>(Assert.Single(s.Document.Entities));
        Assert.False(pl.IsClosed);
        Assert.Equal(15 + 2 * Math.PI * 5, pl.Length, 7);
        foreach (var q in new[] { P(5, 0), P(0, 5), P(0, -5), P(-12, 0) }) Assert.Equal(0.0, pl.DistanceTo(q), 7);
        Assert.True(pl.DistanceTo(P(0, 0)) > 4.9);          // still a circle, not a filled shape

        s.Undo();
        Assert.Equal(2, s.Document.EntityCount);
        Assert.Single(s.Document.Entities.OfType<CircleEntity>());
    }

    [Fact]
    public void Line_joins_a_rectangle_at_a_corner_or_part_way_along_a_side()
    {
        var s = Session();
        Add(s, new LineEntity(P(-10, -10), P(0, 0)));       // to the corner
        Add(s, new PolylineEntity(new[] { P(0, 0), P(20, 0), P(20, 10), P(0, 10) }, true));
        s.StartCommand("JOIN");
        var corner = Assert.IsType<PolylineEntity>(Assert.Single(s.Document.Entities));
        Assert.Equal(Math.Sqrt(200) + 60, corner.Length, 7);
        Assert.Equal(0.0, corner.DistanceTo(P(20, 5)), 7);
        Assert.Equal(0.0, corner.DistanceTo(P(-5, -5)), 7);

        var t = Session();
        var rect = new PolylineEntity(new[] { P(0, 0), P(20, 0), P(20, 10), P(0, 10) }, true);
        rect.SetSegmentWidth(0, 2, 4);                      // tapered bottom side, which the line meets at its middle
        Add(t, rect);
        Add(t, new LineEntity(P(10, -8), P(10, 0)));
        t.StartCommand("JOIN");
        var side = Assert.IsType<PolylineEntity>(Assert.Single(t.Document.Entities));
        Assert.Equal(8 + 60, side.Length, 7);
        foreach (var q in new[] { P(5, 0), P(15, 0), P(20, 5), P(10, 10), P(0, 5), P(10, -4) }) Assert.Equal(0.0, side.DistanceTo(q), 7);
        // The split side keeps its taper: 2 at one end, 3 where the line meets it, 4 at the other end.
        var widths = Enumerable.Range(0, side.SegmentCount).Select(side.GetSegmentWidth).Where(w => w != (0.0, 0.0)).ToList();
        Assert.Equal(2, widths.Count);
        Assert.Contains((3.0, 4.0), widths);
        Assert.Contains((2.0, 3.0), widths);
    }

    [Fact]
    public void Line_arc_and_circle_in_one_chain_and_closed_shapes_alone_do_not_join()
    {
        var s = Session();
        Add(s, new LineEntity(P(0, 0), P(10, 0)));
        Add(s, new ArcEntity(P(10, 5), 5, 270 * Deg, 90 * Deg));           // from 10,0 round to 10,10
        Add(s, new CircleEntity(P(10, 13), 3));                            // touches the arc's end at 10,10
        s.StartCommand("JOIN");
        var pl = Assert.IsType<PolylineEntity>(Assert.Single(s.Document.Entities));
        Assert.Equal(10 + 5 * Math.PI + 2 * Math.PI * 3, pl.Length, 7);

        var t = Session();
        Add(t, new CircleEntity(P(0, 0), 5));
        Add(t, new PolylineEntity(new[] { P(5, 0), P(20, 0), P(20, 10), P(5, 10) }, true));   // touches the circle, but nothing has an end
        Add(t, new LineEntity(P(50, 50), P(60, 50)));
        var messages = CaptureMessages(t);
        t.StartCommand("JOIN");
        Assert.Equal(3, t.Document.EntityCount);
        Assert.Contains(messages, m => m.Contains("Nothing to join"));
    }

    [Fact]
    public void Two_separate_chains_give_two_polylines()
    {
        var s = Session();
        Add(s, new LineEntity(P(0, 0), P(10, 0)));
        Add(s, new LineEntity(P(10, 0), P(10, 10)));
        Add(s, new LineEntity(P(100, 0), P(110, 0)));
        Add(s, new LineEntity(P(110, 0), P(110, 10)));
        s.StartCommand("JOIN");
        Assert.Equal(2, s.Document.Entities.OfType<PolylineEntity>().Count());
        Assert.Equal(2, s.Document.EntityCount);
    }
}

public class LinetypeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        return s;
    }

    private static LineEntity AddLine(EditorSession s, double x1, double y1, double x2, double y2)
    {
        var line = new LineEntity(P(x1, y1), P(x2, y2)) { LayerId = s.Document.CurrentLayerId };
        s.Document.AddEntity(line);
        return line;
    }

    [Fact]
    public void Every_drawing_has_continuous_dashed_and_center()
    {
        var doc = new DrawingDocument();
        Assert.Equal(new[] { "Solid", "Dash", "Short Dash", "Long Short", "Long Short Short", "Dash Dot", "Dotted", "Dash Dash Dot" }, doc.LinetypeTable.Select(l => l.Name).ToArray());
        Assert.Equal(new[] { 1.25, 0.25, 0.25, 0.25, 0.25, 0.25 }, doc.FindLinetype("long short short")!.Pattern.ToArray());
        Assert.True(doc.FindLinetype("solid")!.IsContinuous);
        Assert.Equal(new[] { 0.5, 0.25 }, doc.FindLinetype("DASH")!.Pattern.ToArray());
        Assert.Equal(2.0, doc.FindLinetype("Long Short")!.PatternLength, 12);
        Assert.NotNull(doc.GetLinetypeRemovalBlocker("Dash"));           // built-in
        Assert.NotNull(doc.GetLinetypeRemovalBlocker("nope"));
    }

    [Fact]
    public void Patterns_typed_as_dash_gap_pairs_or_with_negative_gaps()
    {
        Assert.True(LinetypeDefinition.TryParsePattern("0.5, 0.25", out var a));
        Assert.Equal(new[] { 0.5, 0.25 }, a);
        Assert.True(LinetypeDefinition.TryParsePattern("1 0.25 0 0.25", out var b));      // dash, gap, dot, gap
        Assert.Equal(new[] { 1, 0.25, 0, 0.25 }, b);
        Assert.True(LinetypeDefinition.TryParsePattern("A,.5,-.25,0,-.25", out var c));   // classic form
        Assert.Equal(new[] { 0.5, 0.25, 0, 0.25 }, c);

        foreach (string bad in new[] { "", "0.5", "0.5,0", "0,0", "x,1", "0.5,-0.25,-0.25", "-0.5,0.25", "1,1,1" })
            Assert.False(LinetypeDefinition.TryParsePattern(bad, out _), bad);
        Assert.Throws<ArgumentException>(() => new LinetypeDefinition("X", "", new[] { 1.0 }));
    }

    [Fact]
    public void Bylayer_follows_the_layer_object_linetype_overrides_and_byblock_is_continuous_for_now()
    {
        var s = Session();
        var line = AddLine(s, 0, 0, 10, 0);
        var doc = s.Document;
        Assert.Equal("ByLayer", line.Linetype);
        Assert.True(doc.ResolveLinetype(line).IsContinuous);

        s.UpdateLayer(doc.CurrentLayerId, l => l.Linetype = "Dash");
        Assert.Equal("Dash", doc.ResolveLinetype(doc.GetEntity(line.Id)!).Name);

        s.ModifyEntities(new[] { line.Id }, e => e.Linetype = "Long Short", "t");
        Assert.Equal("Long Short", doc.ResolveLinetype(doc.GetEntity(line.Id)!).Name);

        s.ModifyEntities(new[] { line.Id }, e => e.Linetype = Linetypes.ByBlock, "t");
        Assert.True(doc.ResolveLinetype(doc.GetEntity(line.Id)!).IsContinuous);          // no blocks yet

        s.ModifyEntities(new[] { line.Id }, e => e.Linetype = "Missing", "t");
        Assert.True(doc.ResolveLinetype(doc.GetEntity(line.Id)!).IsContinuous);          // unknown name: solid
    }

    [Fact]
    public void Linetype_command_creates_lists_and_deletes_custom_linetypes_with_undo()
    {
        var s = Session();
        var messages = CaptureMessages(s);
        s.StartCommand("-LINETYPE");
        s.SubmitKeyword("NEW");
        s.SubmitText("Dash");                             // taken
        Assert.Contains(messages, m => m.Contains("already a linetype"));
        s.SubmitText("Rebar hidden");                       // names may have spaces
        s.SubmitText("oops");                               // not a pattern
        Assert.NotNull(s.ActiveCommand);
        s.SubmitText("0.75, 0.2, 0, 0.2");                  // read as a pattern, not as coordinates
        Assert.Null(s.ActiveCommand);
        var custom = s.Document.FindLinetype("rebar hidden")!;
        Assert.Equal(new[] { 0.75, 0.2, 0, 0.2 }, custom.Pattern.ToArray());

        s.StartCommand("-LT");
        s.SubmitEnter();                                    // List
        Assert.Contains(messages, m => m.Contains("Rebar hidden: 0.75, 0.2, 0, 0.2"));
        Assert.Contains(messages, m => m.Contains("Linetype scale: 1"));

        var line = AddLine(s, 0, 0, 10, 0);
        s.ModifyEntities(new[] { line.Id }, e => e.Linetype = "Rebar hidden", "t");
        s.StartCommand("-LINETYPE");
        s.SubmitKeyword("DELETE");
        s.SubmitText("Rebar hidden");                       // in use: refused
        Assert.NotNull(s.Document.FindLinetype("Rebar hidden"));
        Assert.Contains(messages, m => m.Contains("used by objects"));

        s.ModifyEntities(new[] { line.Id }, e => e.Linetype = "ByLayer", "t");
        s.StartCommand("-LINETYPE");
        s.SubmitKeyword("D");
        s.SubmitText("rebar hidden");
        Assert.Null(s.Document.FindLinetype("Rebar hidden"));
        s.Undo();
        Assert.NotNull(s.Document.FindLinetype("Rebar hidden"));
        Assert.Equal(Linetypes.BuiltIn.Count + 1, s.Document.LinetypeTable.Count);

        s.StartCommand("-LINETYPE");
        s.SubmitKeyword("D");
        s.SubmitText("Long Short");
        Assert.Contains(messages, m => m.Contains("built-in"));
    }

    [Fact]
    public void Ltscale_and_object_scale_change_the_drawn_pattern_and_can_be_undone()
    {
        var s = Session();
        var line = AddLine(s, 0, 0, 100, 0);
        s.UpdateLayer(s.Document.CurrentLayerId, l => l.Linetype = "Dash");

        var view = new ViewTransform();
        view.SetViewport(800, 600);
        view.ZoomExtents(new BoundingBox(0, -10, 100, 10));
        double zoom = view.Scale;

        double[] Drawn()
        {
            var surface = new RecordingSurface();
            new SceneRenderer().Render(surface, s.Document, view, new RenderOptions { ShowGrid = false });
            var style = surface.Lines.Single().Style;
            Assert.False(style.Dashed);
            return style.Pattern!;
        }

        var normal = Drawn();
        Assert.Equal(2, normal.Length);
        Assert.True(Math.Abs(normal[0] - 0.5 * zoom) <= 0.5 * zoom * 0.01);   // within the half-percent rounding
        Assert.True(Math.Abs(normal[1] - 0.25 * zoom) <= 0.25 * zoom * 0.01);

        s.StartCommand("LTSCALE");
        Assert.Contains("<1>", s.Prompt);
        s.SubmitText("4");
        Assert.Equal(4.0, s.Document.Settings.LinetypeScale);
        Assert.True(Math.Abs(Drawn()[0] - 4 * 0.5 * zoom) <= 4 * 0.5 * zoom * 0.01);

        s.ModifyEntities(new[] { line.Id }, e => e.LinetypeScale = 0.5, "t");
        Assert.True(Math.Abs(Drawn()[0] - 2 * 0.5 * zoom) <= 2 * 0.5 * zoom * 0.01);

        s.Undo();
        s.Undo();
        Assert.Equal(1.0, s.Document.Settings.LinetypeScale);

        s.StartCommand("LTS");
        s.SubmitText("0");
        Assert.NotNull(s.ActiveCommand);                    // refused
        s.Cancel();
    }

    [Fact]
    public void A_pattern_too_fine_to_see_is_drawn_solid_and_the_same_zoom_reuses_the_same_pattern()
    {
        var renderer = new SceneRenderer();
        var dashed = Linetypes.BuiltIn[1];
        Assert.Null(renderer.PixelPattern(dashed, 1.0));                   // 0.75 px repeat
        Assert.Null(renderer.PixelPattern(Linetypes.BuiltIn[0], 100));     // continuous
        Assert.Null(renderer.PixelPattern(dashed, double.NaN));
        var a = renderer.PixelPattern(dashed, 40.0)!;
        var b = renderer.PixelPattern(dashed, 40.01)!;                     // same rounding step
        Assert.Same(a, b);
        Assert.NotSame(a, renderer.PixelPattern(dashed, 80.0));
        Assert.Equal(new RenderStyle(1, 2, 3, 1, false, a), new RenderStyle(1, 2, 3, 1, false, b));   // equal styles: one pen
    }

    [Fact]
    public void Custom_linetypes_scales_and_object_settings_survive_save_and_open()
    {
        var s = Session();
        s.AddLinetype("Rebar hidden", "Custom", new[] { 0.75, 0.2, 0, 0.2 });
        s.SetLinetypeScale(12);
        s.UpdateLayer(s.Document.CurrentLayerId, l => l.Linetype = "Rebar hidden");
        var a = AddLine(s, 0, 0, 10, 0);
        var b = AddLine(s, 0, 5, 10, 5);
        s.ModifyEntities(new[] { b.Id }, e => { e.Linetype = Linetypes.ByBlock; e.LinetypeScale = 2.5; }, "t");

        byte[] bytes = HcadSerializer.SaveToBytes(s.Document);
        string json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"linetypes\"", json);
        Assert.DoesNotContain("\"name\": \"Dashed\"", json);               // built-in linetypes are not stored
        var loaded = HcadSerializer.LoadFromBytes(bytes);
        Assert.Empty(loaded.Warnings);
        var doc = loaded.Document;
        Assert.Equal(new[] { "Solid", "Dash", "Short Dash", "Long Short", "Long Short Short", "Dash Dot", "Dotted", "Dash Dash Dot", "Rebar hidden" }, doc.LinetypeTable.Select(l => l.Name).ToArray());
        Assert.Equal(new[] { 0.75, 0.2, 0, 0.2 }, doc.FindLinetype("Rebar hidden")!.Pattern.ToArray());
        Assert.Equal(12.0, doc.Settings.LinetypeScale);
        Assert.Equal("Rebar hidden", doc.GetLayer(doc.CurrentLayerId)!.Linetype);
        Assert.Equal("ByLayer", doc.GetEntity(a.Id)!.Linetype);
        Assert.Equal(1.0, doc.GetEntity(a.Id)!.LinetypeScale);
        Assert.Equal("ByBlock", doc.GetEntity(b.Id)!.Linetype);
        Assert.Equal(2.5, doc.GetEntity(b.Id)!.LinetypeScale);
        Assert.Equal("Rebar hidden", doc.ResolveLinetype(doc.GetEntity(a.Id)!).Name);

        // A damaged linetype entry is skipped with a warning; the drawing still opens.
        string broken = json.Replace("0.75", "-3");
        var repaired = HcadSerializer.LoadFromBytes(System.Text.Encoding.UTF8.GetBytes(broken));
        Assert.Contains(repaired.Warnings, w => w.Contains("Rebar hidden"));
        Assert.Null(repaired.Document.FindLinetype("Rebar hidden"));
        Assert.Equal(2, repaired.Document.EntityCount);
    }

    [Fact]
    public void Properties_panel_and_layer_table_set_linetypes()
    {
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("recovery")));
        var s = vm.Session;
        Assert.Equal(new[] { "Solid", "Dash", "Short Dash", "Long Short", "Long Short Short", "Dash Dot", "Dotted", "Dash Dash Dot" }, vm.LinetypeNames.ToArray());
        Assert.Equal(new[] { "ByLayer", "ByBlock", "Solid", "Dash", "Short Dash", "Long Short", "Long Short Short", "Dash Dot", "Dotted", "Dash Dash Dot" }, vm.Properties.LinetypeNames.ToArray());
        Assert.Null(vm.Properties.SelectedLinetype);        // nothing selected

        var line = new LineEntity(P(0, 0), P(10, 0)) { LayerId = s.Document.CurrentLayerId };
        s.Document.AddEntity(line);
        s.Selection.Add(line.Id);
        Assert.Equal("ByLayer", vm.Properties.SelectedLinetype);
        vm.Properties.SelectedLinetype = "Dash";
        Assert.Equal("Dash", s.Document.GetEntity(line.Id)!.Linetype);
        Assert.Equal("Dash", vm.Properties.SelectedLinetype);
        vm.Properties.Rows.Single(r => r.Label == "Linetype scale").Value = "3";
        Assert.Equal(3.0, s.Document.GetEntity(line.Id)!.LinetypeScale);
        vm.Properties.Rows.Single(r => r.Label == "Linetype scale").Value = "0";          // refused
        Assert.Equal(3.0, s.Document.GetEntity(line.Id)!.LinetypeScale);
        vm.Properties.SelectedLinetype = "ByBlock";
        Assert.Equal("ByBlock", s.Document.GetEntity(line.Id)!.Linetype);

        var row = vm.Layers.Single(l => l.IsCurrent);
        row.Linetype = "Long Short";
        Assert.Equal("Long Short", s.Document.GetLayer(row.Id)!.Linetype);
        row.Linetype = "No such linetype";                  // ignored
        Assert.Equal("Long Short", row.Linetype);

        s.AddLinetype("Mine", "Custom", new[] { 1.0, 0.5 });
        Assert.Contains("Mine", vm.LinetypeNames);
        Assert.Contains("Mine", vm.Properties.LinetypeNames);
        Assert.Equal("ByBlock", vm.Properties.SelectedLinetype);            // selection kept while the list grew
        s.Undo();
        Assert.DoesNotContain("Mine", vm.LinetypeNames);
    }

    [Fact]
    public void Drawings_saved_with_the_earlier_built_in_names_open_with_the_current_ones()
    {
        var doc = new DrawingDocument();
        var layer = doc.Layers[0];
        var a = new LineEntity(P(0, 0), P(10, 0)) { LayerId = layer.Id };
        var b = new LineEntity(P(0, 1), P(10, 1)) { LayerId = layer.Id };
        doc.AddEntity(a);
        doc.AddEntity(b);
        string json = System.Text.Encoding.UTF8.GetString(HcadSerializer.SaveToBytes(doc));
        Assert.Contains("\"linetype\": \"Solid\"", json);
        // What an earlier build wrote: layer "Continuous", objects "Center" and "ByLayer".
        json = json.Replace("\"linetype\": \"Solid\"", "\"linetype\": \"Continuous\"");
        int at = json.IndexOf("\"linetype\": \"ByLayer\"", StringComparison.Ordinal);
        json = json.Remove(at, "\"linetype\": \"ByLayer\"".Length).Insert(at, "\"linetype\": \"Center\"");
        var opened = HcadSerializer.LoadFromBytes(System.Text.Encoding.UTF8.GetBytes(json)).Document;
        Assert.Equal("Solid", opened.Layers[0].Linetype);
        var linetypes = opened.Entities.Select(e => e.Linetype).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "ByLayer", "Long Short" }, linetypes);
        Assert.Equal("Dash Dash Dot", Linetypes.FromEarlierName("border"));
        Assert.Equal("Rebar hidden", Linetypes.FromEarlierName("Rebar hidden"));
    }
}
