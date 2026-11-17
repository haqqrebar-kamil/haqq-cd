using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Documents;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Presentation;
using HAQQ.CAD.Rendering;
using Xunit;
using static HAQQ.CAD.Tests.T;

namespace HAQQ.CAD.Tests;

public class DimensionTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        s.Document.Settings.DecimalPrecision = 2;
        s.Document.ReplaceDimensionStyle(new DimensionStyle().Scaled(0.25));      // text 1 unit high
        return s;
    }

    private static DimensionEntity Only(EditorSession s) => s.Document.Entities.OfType<DimensionEntity>().Single();

    private static void Near(double expected, double actual) => Assert.True(Math.Abs(expected - actual) < 1e-9, $"expected {expected} but was {actual}");

    [Fact]
    public void Straight_dimension_is_horizontal_above_and_vertical_beside()
    {
        var s = Session();
        s.StartCommand("DIM");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(30, 40));
        s.SubmitPoint(P(15, 60));                       // above the points: horizontal distance
        var d = Only(s);
        Assert.Equal(DimensionKind.Linear, d.Kind);
        Near(30, d.Measurement);
        Assert.Equal("30.00", d.GetText(s.Document.Settings));
        Assert.Null(s.ActiveCommand);

        s.StartCommand("DL");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(30, 40));
        s.SubmitPoint(P(50, 20));                       // beside the points: vertical distance
        var vertical = s.Document.Entities.OfType<DimensionEntity>().Single(e => e.Id != d.Id);
        Near(40, vertical.Measurement);

        s.StartCommand("DIM");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(30, 40));
        s.SubmitKeyword("H");                           // forced, wherever the line is placed
        s.SubmitPoint(P(50, 20));
        Near(30, s.Document.Entities.OfType<DimensionEntity>().Last().Measurement);

        s.Undo(); s.Undo(); s.Undo();
        Assert.Equal(0, s.Document.EntityCount);
    }

    [Fact]
    public void Dimension_geometry_has_extension_lines_arrows_and_text_above_the_line()
    {
        var d = new DimensionEntity(DimensionKind.Linear) { P1 = P(0, 0), P2 = P(20, 0), LinePoint = P(5, 8), Angle = 0, TextHeight = 1 };
        Assert.True(d.IsValid);
        var g = d.BuildGeometry();
        Assert.Equal(3, g.Lines.Count);                 // two extension lines and the dimension line
        Assert.Contains(g.Lines, l => l.A == P(0, 8) && l.B == P(20, 8));
        Assert.Contains(g.Lines, l => l.A.X == 0 && l.B.X == 0 && l.A.Y > 0 && l.B.Y > 8);   // a gap at the point, a little past the line
        Assert.Equal(2, g.Arrows.Count);
        Assert.Equal(P(0, 8), g.Arrows[0].Tip);
        Assert.True(g.Arrows[0].Direction.X < 0 && g.Arrows[1].Direction.X > 0);            // pointing outwards
        Near(10, g.TextCenter.X);
        Assert.True(g.TextCenter.Y > 8);
        Near(0, g.TextAngle);

        // Too short for arrows inside: they sit outside and point in.
        var small = new DimensionEntity(DimensionKind.Linear) { P1 = P(0, 0), P2 = P(2, 0), LinePoint = P(0, 5), TextHeight = 1 };
        var sg = small.BuildGeometry();
        Assert.True(sg.Arrows[0].Direction.X > 0 && sg.Arrows[1].Direction.X < 0);

        // A vertical dimension reads from the right: the text runs upwards.
        var v = new DimensionEntity(DimensionKind.Linear) { P1 = P(0, 0), P2 = P(0, 20), LinePoint = P(-6, 3), Angle = Math.PI / 2, TextHeight = 1 };
        Near(Math.PI / 2, v.BuildGeometry().TextAngle);
        Near(20, v.Measurement);
        Assert.True(v.Bounds.Contains(P(-6, 10)));
        Assert.True(v.DistanceTo(P(-6, 10)) < 1e-9);    // the dimension line can be picked
    }

    [Fact]
    public void Aligned_dimension_measures_the_true_distance_and_takes_a_typed_offset()
    {
        var s = Session();
        s.StartCommand("DIMALIGN");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(30, 40));
        s.UpdateCursor(P(0, 40), 0.01);                 // the side the line goes on
        s.SubmitText("5");                              // 5 units away from the points
        var d = Only(s);
        Assert.Equal(DimensionKind.Aligned, d.Kind);
        Near(50, d.Measurement);
        var normal = new Vector2D(-0.8, 0.6);
        Near(5, normal.Dot(d.LinePoint - d.P1));
    }

    [Fact]
    public void Radius_and_diameter_are_taken_from_a_clicked_circle_or_arc()
    {
        var s = Session();
        AddCircle(s.Document, 0, 0, 10);
        s.StartCommand("DIMRAD");
        Assert.True(s.PickingObject);
        s.SubmitPoint(P(50, 50));                       // nothing there
        Assert.NotNull(s.ActiveCommand);
        s.SubmitPoint(P(10, 0));
        Assert.False(s.PickingObject);
        s.SubmitPoint(P(0, 20));                        // outside, straight up
        var r = Only(s);
        Near(10, r.Measurement);
        Assert.Equal("R 10.00", r.GetText(s.Document.Settings));
        Assert.Equal(P(0, 10), r.P1);
        var g = r.BuildGeometry();
        Assert.True(g.Arrows.Single().Direction.Y < 0);                  // from outside, pointing at the circle

        s.StartCommand("DIMDIA");
        s.SubmitPoint(P(-10, 0));
        s.SubmitPoint(P(3, 0));                         // inside
        var dia = s.Document.Entities.OfType<DimensionEntity>().Single(e => e.Kind == DimensionKind.Diameter);
        Near(20, dia.Measurement);
        Assert.True(dia.GetText(s.Document.Settings).StartsWith("Ø 20", StringComparison.Ordinal));
        Assert.Equal(2, dia.BuildGeometry().Arrows.Count);
    }

    [Fact]
    public void Angle_between_two_lines_is_measured_on_the_side_that_is_clicked()
    {
        var s = Session();
        AddLine(s.Document, 0, 0, 20, 0);
        AddLine(s.Document, 0, 0, 0, 20);
        s.StartCommand("DIMANGLE");
        s.SubmitPoint(P(10, 0));
        s.SubmitPoint(P(0, 10));
        s.SubmitPoint(P(5, 5));                         // inside the right angle
        var d = Only(s);
        Near(Math.PI / 2, d.Measurement);
        Assert.Equal("90°", d.GetText(s.Document.Settings));
        Assert.Equal(P(0, 0), d.Center);

        // Dragging the arc grip to the other side measures the rest of the turn.
        var copy = (DimensionEntity)d.Clone();
        Assert.True(EntityGrips.Stretch(copy, 2, P(-5, -5)));
        Near(1.5 * Math.PI, copy.Measurement);

        // Parallel lines have no angle.
        AddLine(s.Document, 0, 30, 20, 30);
        s.StartCommand("DIMANGLE");
        s.SubmitPoint(P(15, 0));
        s.SubmitPoint(P(15, 30));
        Assert.NotNull(s.ActiveCommand);
        s.Cancel();
        Assert.Equal(1, s.Document.Entities.OfType<DimensionEntity>().Count());
    }

    [Fact]
    public void Arc_length_follows_the_arc()
    {
        var s = Session();
        var arc = new ArcEntity(P(0, 0), 10, 0, Math.PI / 2) { LayerId = s.Document.CurrentLayerId };
        s.Document.AddEntity(arc);
        s.StartCommand("DIMARCLEN");
        s.SubmitPoint(ArcMath.PointAt(P(0, 0), 10, Math.PI / 4));
        s.SubmitPoint(P(12, 12));
        var d = Only(s);
        Near(10 * Math.PI / 2, d.Measurement);
        Assert.True(d.GetText(s.Document.Settings).StartsWith("⌒ 15.71", StringComparison.Ordinal));

        // Mirrored, it still measures the same quarter turn.
        var mirrored = (DimensionEntity)d.Clone();
        mirrored.Transform(new Transform2D(-1, 0, 0, 1, 0, 0));
        Near(10 * Math.PI / 2, mirrored.Measurement);
    }

    [Fact]
    public void Chain_and_stack_carry_on_from_the_last_dimension()
    {
        var s = Session();
        var messages = CaptureMessages(s);
        s.StartCommand("DIMCHAIN");                     // nothing to carry on from
        Assert.Null(s.ActiveCommand);
        Assert.Contains(messages, m => m.Contains("Place one"));

        s.StartCommand("DIM");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(10, 0));
        s.SubmitPoint(P(5, 6));

        s.StartCommand("DIMCHAIN");
        s.SubmitPoint(P(25, 0));
        s.SubmitPoint(P(45, 3));
        s.SubmitEnter();
        var dims = s.Document.Entities.OfType<DimensionEntity>().ToList();
        Assert.Equal(3, dims.Count);
        Assert.Equal(new[] { 10.0, 15.0, 20.0 }, dims.Select(d => Math.Round(d.Measurement, 9)).ToArray());
        Assert.Equal(P(10, 0), dims[1].P1);
        Assert.Equal(P(25, 0), dims[2].P1);
        Assert.True(dims.All(d => Math.Abs(d.BuildGeometry().Lines.Last().A.Y - 6) < 1e-9 || d.LinePoint.Y == 6));   // one line

        s.StartCommand("DIMSTACK");                     // from the last one placed: 25 to 45
        s.SubmitPoint(P(60, 0));
        s.SubmitPoint(P(80, 0));
        s.SubmitEnter();
        var stacked = s.Document.Entities.OfType<DimensionEntity>().Skip(3).ToList();
        Assert.Equal(new[] { 35.0, 55.0 }, stacked.Select(d => Math.Round(d.Measurement, 9)).ToArray());
        Assert.True(stacked.All(d => d.P1 == P(25, 0)));
        Near(6 + 2.2, stacked[0].LinePoint.Y);
        Near(6 + 4.4, stacked[1].LinePoint.Y);
    }

    [Fact]
    public void Dimension_follows_move_scale_and_grips()
    {
        var d = new DimensionEntity(DimensionKind.Linear) { P1 = P(0, 0), P2 = P(10, 0), LinePoint = P(0, 5), Angle = 0, TextHeight = 1 };
        d.Transform(Transform2D.Scaling(2, P(0, 0)));
        Near(20, d.Measurement);
        Near(2, d.TextHeight);
        d.Transform(Transform2D.Rotation(Math.PI / 2, P(0, 0)));
        Near(20, d.Measurement);                        // the measuring direction turned with it
        Near(Math.PI / 2, d.Angle);

        var h = new DimensionEntity(DimensionKind.Linear) { P1 = P(0, 0), P2 = P(10, 0), LinePoint = P(0, 5), Angle = 0, TextHeight = 1 };
        Assert.Equal(new[] { P(0, 0), P(10, 0), P(5, 5) }, EntityGrips.GetGripPoints(h).ToArray());
        Assert.True(EntityGrips.Stretch(h, 1, P(14, 3)));                 // stretch the second point
        Near(14, h.Measurement);
        Assert.True(EntityGrips.Stretch(h, 2, P(7, 9)));                  // move the dimension line
        Near(9, h.LinePoint.Y);
        Assert.False(EntityGrips.Stretch(h, 1, P(0, 0)));                 // both points in one place: refused
        Near(14, h.Measurement);

        var r = new DimensionEntity(DimensionKind.Radius) { Center = P(0, 0), P1 = P(10, 0), LinePoint = P(15, 0), TextHeight = 1 };
        Assert.True(EntityGrips.Stretch(r, 0, P(0, 30)));                 // swing it round the circle
        Near(10, r.Measurement);
        Near(0, r.P1.X);
    }

    [Fact]
    public void Text_can_be_overridden_and_the_size_changed()
    {
        using var temp = new TempDirectory();
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(temp.File("r")));
        var s = vm.Session;
        s.Document.Settings.Format = LengthFormat.Decimal;
        s.Document.Settings.DecimalPrecision = 1;
        s.StartCommand("DIM");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(12, 0));
        s.SubmitPoint(P(6, 10));
        s.SelectAt(P(6, 10), 0.5);
        Assert.Equal("Dimension", vm.Properties.Header);
        PropertyRow Row(string label) => vm.Properties.Rows.Single(r => r.Label == label);
        Assert.Equal("12.0", Row("Measured").Value);
        Assert.Equal("Horizontal distance", Row("Measures").Value);

        Row("Text").Value = "<> TYP";
        Assert.Equal("12.0 TYP", Only(s).GetText(s.Document.Settings));
        Row("Text").Value = "";
        Assert.Equal("12.0", Only(s).GetText(s.Document.Settings));
        Row("Text height").Value = "2";
        Assert.Equal(2.0, Only(s).TextHeight);

        s.ClearSelection();
        s.StartCommand("DIMSIZE");                      // the size of the current style, and of every dimension using it
        s.SubmitText("3");
        Assert.Null(s.ActiveCommand);
        Assert.Equal(3.0, s.Document.CurrentDimensionStyle.TextHeight);
        Assert.Equal(3.0, s.Document.CurrentDimensionStyle.ArrowSize);       // in proportion
        Assert.Equal(3.0, Only(s).TextHeight);
        s.Undo();
        Assert.Equal(2.0, Only(s).TextHeight);          // the dimension's own height comes back
        Assert.Equal(4.0, s.Document.CurrentDimensionStyle.TextHeight);
    }

    [Fact]
    public void Dimensions_take_the_properties_of_new_objects_and_survive_save_and_open()
    {
        var s = Session();
        s.ChangeNewObject(p => p.Color = CadColor.FromRgb(255, 0, 0));
        s.StartCommand("DIM");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(10, 5));
        s.SubmitPoint(P(5, 12));
        var d = Only(s);
        Assert.Equal(CadColor.FromRgb(255, 0, 0), d.Color);
        Assert.Equal("Standard", s.Document.GetLayer(d.LayerId)!.Name);         // the layer of its style, not the current layer
        s.ModifyEntities(new[] { d.Id }, e => ((DimensionEntity)e).TextOverride = "<> CLR", "t");

        var loaded = HcadSerializer.LoadFromBytes(HcadSerializer.SaveToBytes(s.Document));
        Assert.Empty(loaded.Warnings);
        var back = loaded.Document.Entities.OfType<DimensionEntity>().Single();
        Assert.Equal(d.Id, back.Id);
        Assert.Equal(DimensionKind.Linear, back.Kind);
        Assert.Equal(d.P1, back.P1);
        Assert.Equal(d.P2, back.P2);
        Assert.Equal(d.LinePoint, back.LinePoint);
        Assert.Equal(d.TextHeight, back.TextHeight);
        Assert.Equal("<> CLR", back.TextOverride);
        Assert.Equal(1.0, loaded.Document.CurrentDimensionStyle.TextHeight);
        Assert.Equal(d.Style, back.Style);
    }

    [Fact]
    public void Renderer_draws_lines_filled_arrows_and_the_text()
    {
        var doc = new DrawingDocument();
        doc.Settings.Format = LengthFormat.Decimal;
        doc.Settings.DecimalPrecision = 0;
        var d = new DimensionEntity(DimensionKind.Linear) { P1 = P(0, 0), P2 = P(20, 0), LinePoint = P(0, 8), TextHeight = 1, LayerId = doc.CurrentLayerId };
        doc.AddEntity(d);
        var view = new ViewTransform();
        view.SetViewport(800, 600);
        view.SetView(P(10, 4), 20);                     // 20 pixels per unit: the text is 20 pixels high

        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, view, new RenderOptions { ShowGrid = false });
        Assert.Equal(3, surface.Lines.Count);
        Assert.Equal(2, surface.Polygons.Count);        // the two arrowheads
        var text = Assert.Single(surface.Texts);
        Assert.Equal("20", text.Text);
        Assert.Equal(20.0, text.Height, 9);
        Assert.Equal(400.0, text.X, 9);                 // centred over the middle of the dimension line

        // Zoomed far out the text would be unreadable: only the lines are drawn.
        view.SetView(P(10, 4), 1);
        var far = new RecordingSurface();
        new SceneRenderer().Render(far, doc, view, new RenderOptions { ShowGrid = false });
        Assert.Empty(far.Texts);
        Assert.Empty(far.Polygons);
        Assert.Equal(3, far.Lines.Count);
    }
}

public class ObjectTrackingTests
{
    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        AddLine(s.Document, 0, 0, 10, 0);               // endpoints at (0,0) and (10,0)
        AddLine(s.Document, 30, 20, 30, 40);            // endpoint at (30,20)
        return s;
    }

    private const double Aperture = 0.5;

    [Fact]
    public void Hovering_over_a_snap_acquires_it_and_the_cursor_locks_onto_a_line_through_it()
    {
        var s = Session();
        s.StartCommand("LINE");
        Assert.Empty(s.TrackingPoints);

        s.UpdateCursor(P(10.1, 0.1), Aperture);         // over the endpoint, no click
        Assert.NotNull(s.CurrentSnap);
        Assert.Equal(new[] { P(10, 0) }, s.TrackingPoints.ToArray());
        Assert.Null(s.CurrentObjectTrack);              // still on the snap itself

        s.UpdateCursor(P(10.2, 15), Aperture);          // straight up from it, a little off the line
        var track = s.CurrentObjectTrack;
        Assert.NotNull(track);
        Assert.Equal(P(10, 15), track!.Point);          // exactly above the acquired point
        Assert.Equal((P(10, 0), 90.0), track.Lines.Single());
        Assert.Equal(P(10, 15), s.EffectiveCursor);

        s.UpdateCursor(P(14, 15), Aperture);            // away from every tracking line
        Assert.Null(s.CurrentObjectTrack);
        Assert.Equal(P(14, 15), s.EffectiveCursor);

        s.UpdateCursor(P(10.2, 15), Aperture);
        s.SubmitPoint(s.EffectiveCursor);               // the first point of the line lands on the tracking line
        Assert.Empty(s.TrackingPoints);                 // the next point starts with nothing acquired
        s.SubmitText("@5,0");
        var line = s.Document.Entities.OfType<LineEntity>().Last();
        Assert.Equal(P(10, 15), line.Start);
    }

    [Fact]
    public void Two_acquired_points_give_the_corner_where_their_lines_cross()
    {
        var s = Session();
        s.StartCommand("LINE");
        s.UpdateCursor(P(10, 0), Aperture);
        s.UpdateCursor(P(30, 20), Aperture);
        Assert.Equal(2, s.TrackingPoints.Count);

        s.UpdateCursor(P(10.2, 19.8), Aperture);        // above the first, level with the second
        var track = s.CurrentObjectTrack;
        Assert.NotNull(track);
        Assert.Equal(P(10, 20), track!.Point);
        Assert.Equal(2, track.Lines.Count);
    }

    [Fact]
    public void A_tracking_line_crosses_the_polar_ray_from_the_previous_point()
    {
        var s = Session();
        s.StartCommand("LINE");
        s.SubmitPoint(P(-20, 30));                      // first point
        s.UpdateCursor(P(10, 0), Aperture);             // acquire the endpoint
        s.UpdateCursor(P(10.2, 30.1), Aperture);        // level with the first point, above the endpoint
        Assert.NotNull(s.CurrentPolarTrack);
        Assert.Equal(P(10, 30), s.EffectiveCursor);
        s.SubmitPoint(s.EffectiveCursor);
        var line = s.Document.Entities.OfType<LineEntity>().Last();
        Assert.Equal(P(10, 30), line.End);
        Assert.Equal(30.0, line.Length, 9);
    }

    [Fact]
    public void A_typed_distance_is_measured_along_the_tracking_line_from_the_acquired_point()
    {
        var s = Session();
        s.StartCommand("LINE");
        s.UpdateCursor(P(10, 0), Aperture);
        s.UpdateCursor(P(18, 0.2), Aperture);           // to the right of the endpoint
        s.SubmitText("6");
        s.SubmitText("@0,5");
        var line = s.Document.Entities.OfType<LineEntity>().Last();
        Assert.Equal(P(16, 0), line.Start);             // 6 from the acquired point, along the line
    }

    [Fact]
    public void Otrack_off_ortho_or_no_command_means_no_tracking()
    {
        var s = Session();
        s.UpdateCursor(P(10, 0), Aperture);             // no command: nothing to acquire for
        Assert.Empty(s.TrackingPoints);

        s.TrackingEnabled = false;
        s.StartCommand("LINE");
        s.UpdateCursor(P(10, 0), Aperture);
        s.UpdateCursor(P(10.2, 15), Aperture);
        Assert.Empty(s.TrackingPoints);
        Assert.Equal(P(10.2, 15), s.EffectiveCursor);

        s.TrackingEnabled = true;
        s.UpdateCursor(P(10, 0), Aperture);
        s.UpdateCursor(P(0, 0), Aperture);
        s.UpdateCursor(P(30, 20), Aperture);
        s.UpdateCursor(P(30, 40), Aperture);            // a fourth one: the oldest is dropped
        Assert.Equal(EditorSession.MaxTrackingPoints, s.TrackingPoints.Count);
        Assert.DoesNotContain(P(10, 0), s.TrackingPoints);

        s.Cancel();
        Assert.Empty(s.TrackingPoints);
    }

    [Fact]
    public void Renderer_draws_the_tracking_line_and_marks_the_acquired_point()
    {
        var doc = new DrawingDocument();
        var view = new ViewTransform();
        view.SetViewport(800, 600);
        view.SetView(P(0, 0), 10);
        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, view, new RenderOptions
        {
            ShowGrid = false,
            TrackLines = new[] { (P(0, 0), 90.0) },
            TrackPoints = new[] { P(0, 0) },
        });
        Assert.Equal(1, surface.Lines.Count(l => l.Style == RenderTheme.PolarTrack));
        Assert.Equal(2, surface.Lines.Count(l => l.Style == RenderTheme.SnapMarker));       // the small cross
    }
}

public class DimensionStyleTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    private static DrawingSettings Arch() => new() { Unit = LinearUnit.Inch, Format = LengthFormat.Architectural, FractionDenominator = 16 };

    [Fact]
    public void A_style_decides_how_a_length_is_written()
    {
        var standard = new DimensionStyle();
        Assert.Equal("5'-6 1/2\"", standard.FormatLength(66.5, Arch()));                     // follows the drawing
        Assert.Equal("6\"", standard.FormatLength(6, Arch()));

        Assert.Equal("0'-6\"", (standard with { SuppressZeroFeet = false }).FormatLength(6, Arch()));
        Assert.Equal("5'", (standard with { SuppressZeroInches = true }).FormatLength(60, Arch()));
        Assert.Equal("5'-0\"", standard.FormatLength(60, Arch()));

        var half = standard with { RoundOff = 0.5 };
        Assert.Equal("5'-6 1/2\"", half.FormatLength(66.53125, Arch()));                    // to the nearest half inch
        Assert.Equal("5'-7\"", half.FormatLength(66.8, Arch()));

        var decimals = standard with { Format = DimensionUnitFormat.Decimal, Precision = 1, Prefix = "~", Suffix = " in" };
        Assert.Equal("~66.5 in", decimals.FormatLength(66.53125, Arch()));
        Assert.Equal("66 1/2\"", (standard with { Format = DimensionUnitFormat.Fractional, FractionDenominator = 2 }).FormatLength(66.53125, Arch()));
        Assert.Equal("11'-1\"", (standard with { ScaleFactor = 2 }).FormatLength(66.5, Arch()));

        Assert.Equal("34.2°", (standard with { AnglePrecision = 1 }).FormatAngle(GeoMath.DegreesToRadians(34.21)));
        Assert.Equal("90°", standard.FormatAngle(Math.PI / 2));

        Assert.NotNull((standard with { TextHeight = 0 }).Problem);
        Assert.NotNull((standard with { FractionDenominator = 3 }).Problem);
        Assert.NotNull((standard with { Name = " " }).Problem);
        Assert.Null(standard.Problem);
    }

    [Fact]
    public void New_dimensions_use_the_current_style_and_follow_it_when_it_changes()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        s.Document.Settings.DecimalPrecision = 2;
        Assert.True(s.AddDimensionStyle(new DimensionStyle { Name = "Rebar", TextHeight = 2, ArrowSize = 2, Arrow = DimensionArrow.Tick, Format = DimensionUnitFormat.Decimal, Precision = 0 }));
        Assert.False(s.AddDimensionStyle(new DimensionStyle { Name = "rebar" }));               // the name is taken
        Assert.True(s.SetCurrentDimensionStyle("Rebar"));

        s.StartCommand("DIM");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(12.4, 0));
        s.SubmitPoint(P(6, 8));
        var d = s.Document.Entities.OfType<DimensionEntity>().Single();
        Assert.Equal("Rebar", d.StyleName);
        Assert.Equal("12", d.GetText(s.Document.Settings));
        Assert.Equal(DimensionArrow.Tick, d.BuildGeometry().Arrow);

        // Changing the style changes the dimension; undo puts both back.
        Assert.True(s.ModifyDimensionStyle(s.Document.FindDimensionStyle("Rebar")! with { Precision = 1, TextHeight = 5 }));
        d = s.Document.Entities.OfType<DimensionEntity>().Single();
        Assert.Equal("12.4", d.GetText(s.Document.Settings));
        Assert.Equal(5.0, d.TextHeight);
        s.Undo();
        d = s.Document.Entities.OfType<DimensionEntity>().Single();
        Assert.Equal("12", d.GetText(s.Document.Settings));
        Assert.Equal(2.0, s.Document.FindDimensionStyle("Rebar")!.TextHeight);

        // A style in use, the current one and Standard cannot be deleted.
        Assert.False(s.DeleteDimensionStyle("Rebar"));
        Assert.True(s.SetCurrentDimensionStyle("Standard"));
        Assert.False(s.DeleteDimensionStyle("Rebar"));                                          // still used by the dimension
        Assert.False(s.DeleteDimensionStyle("Standard"));

        // Giving the selected dimension another style.
        s.SelectAt(P(6, 8), 0.5);
        Assert.True(s.SetDimensionStyleOfSelection("Standard"));
        Assert.Equal("Standard", s.Document.Entities.OfType<DimensionEntity>().Single().StyleName);
        Assert.True(s.DeleteDimensionStyle("Rebar"));
        s.Undo();
        Assert.NotNull(s.Document.FindDimensionStyle("Rebar"));
    }

    [Fact]
    public void Tick_and_dot_ends_are_drawn_and_never_turned_outside()
    {
        var doc = new DrawingDocument();
        var tick = new DimensionEntity(DimensionKind.Linear)
        {
            P1 = P(0, 0), P2 = P(2, 0), LinePoint = P(0, 5), LayerId = doc.CurrentLayerId,
            Style = new DimensionStyle { TextHeight = 1, ArrowSize = 1, TextGap = 0.3, ExtensionOffset = 0.3, ExtensionBeyond = 0.5, Arrow = DimensionArrow.Tick },
        };
        var g = tick.BuildGeometry();
        Assert.Contains(g.Lines, l => l.A == P(0, 5) && l.B == P(2, 5));                       // a short line is not stretched for ticks
        doc.AddEntity(tick);

        var view = new ViewTransform();
        view.SetViewport(800, 600);
        view.SetView(P(1, 3), 40);
        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, view, new RenderOptions { ShowGrid = false });
        Assert.Empty(surface.Polygons);                                                         // no arrowheads
        Assert.Equal(5, surface.Lines.Count);                                                   // 3 lines and 2 ticks

        doc.ReplaceEntity(With(tick, DimensionArrow.Dot));
        surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, view, new RenderOptions { ShowGrid = false });
        Assert.Equal(2, surface.Polygons.Count);
        Assert.Equal(12, surface.Polygons[0].Points.Length);

        doc.ReplaceEntity(With(tick, DimensionArrow.None));
        surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, view, new RenderOptions { ShowGrid = false });
        Assert.Empty(surface.Polygons);
        Assert.Equal(3, surface.Lines.Count);

        static DimensionEntity With(DimensionEntity source, DimensionArrow arrow)
        {
            var copy = (DimensionEntity)source.Clone();
            copy.Style = copy.Style with { Arrow = arrow };
            return copy;
        }
    }

    [Fact]
    public void Styles_are_saved_with_the_drawing_and_older_files_still_open()
    {
        var s = new EditorSession();
        s.AddDimensionStyle(new DimensionStyle { Name = "Rebar", TextHeight = 2, Arrow = DimensionArrow.Dot, Prefix = "(", Suffix = ")", RoundOff = 0.25, SuppressZeroInches = true });
        s.SetCurrentDimensionStyle("Rebar");
        s.ModifyDimensionStyle(new DimensionStyle { TextHeight = 6 });                          // Standard, changed
        s.StartCommand("DIM");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(24, 0));
        s.SubmitPoint(P(6, 8));

        byte[] bytes = HcadSerializer.SaveToBytes(s.Document);
        var loaded = HcadSerializer.LoadFromBytes(bytes);
        Assert.Empty(loaded.Warnings);
        var doc = loaded.Document;
        Assert.Equal(new[] { "Standard", "Rebar" }, doc.DimensionStyles.Select(x => x.Name).ToArray());
        Assert.Equal("Rebar", doc.CurrentDimensionStyleName);
        Assert.Equal(s.Document.FindDimensionStyle("Rebar"), doc.FindDimensionStyle("Rebar"));
        Assert.Equal(6.0, doc.FindDimensionStyle("Standard")!.TextHeight);
        var d = doc.Entities.OfType<DimensionEntity>().Single();
        Assert.Equal(s.Document.FindDimensionStyle("Rebar"), d.Style);
        Assert.Equal("(2')", d.GetText(doc.Settings));
        Assert.Equal(6, HcadSerializer.SchemaVersion);

        // A schema 5 file: a text height on the dimension and in the units, and no styles.
        string json = System.Text.Encoding.UTF8.GetString(bytes);
        int from = json.IndexOf("\"style\": {", json.IndexOf("\"type\": \"dimension\"", StringComparison.Ordinal), StringComparison.Ordinal);
        int to = json.IndexOf('}', from) + 1;
        json = json.Remove(from, to - from).Insert(from, "\"textHeight\": 8");
        var old = HcadSerializer.LoadFromBytes(System.Text.Encoding.UTF8.GetBytes(json)).Document;
        var od = old.Entities.OfType<DimensionEntity>().Single();
        Assert.Equal(8.0, od.TextHeight);
        Assert.Equal(8.0, od.Style.ArrowSize);                                                  // in proportion, as schema 5 drew it
        Assert.Equal("Standard", od.StyleName);
    }

    [Fact]
    public void Settings_window_edits_the_selected_style_and_makes_new_ones()
    {
        var s = new EditorSession();
        using var vm = new DimensionStylesViewModel(s);
        Assert.Equal(new[] { "Standard" }, vm.Styles.ToArray());
        Assert.Equal("Standard", vm.Selected);
        Assert.Equal("4", vm.TextHeightText);
        Assert.False(vm.DeleteCommand.CanExecute(null));

        vm.NewName = "Rebar";
        Assert.True(vm.AddNew());
        Assert.Equal("Rebar", vm.Selected);
        Assert.True(vm.AddNew());                                                               // no name given
        Assert.Equal("Style 1", vm.Selected);
        vm.NewName = "rebar";
        Assert.False(vm.AddNew());
        Assert.NotEqual("", vm.Message);

        vm.Selected = "Rebar";
        vm.TextHeightText = "3/4";
        vm.SelectedArrow = "Tick";
        vm.SelectedFormat = "Feet and inches";
        vm.SelectedFraction = "1/8\"";
        vm.RoundOffText = "1/2";
        vm.SuppressZeroInches = true;
        vm.Prefix = "#";
        var rebar = s.Document.FindDimensionStyle("Rebar")!;
        Assert.Equal(0.75, rebar.TextHeight);
        Assert.Equal(DimensionArrow.Tick, rebar.Arrow);
        Assert.Equal(DimensionUnitFormat.Architectural, rebar.Format);
        Assert.Equal(8, rebar.FractionDenominator);
        Assert.Equal(0.5, rebar.RoundOff);
        Assert.True(rebar.SuppressZeroInches);
        Assert.Equal("#", rebar.Prefix);
        Assert.Equal(3.2, vm.PreviewStyle.TextHeight, 9);                                       // the sample is always the same size
        Assert.True(vm.HasOwnFormat);
        Assert.Equal(4.0, s.Document.FindDimensionStyle("Standard")!.TextHeight);               // other styles untouched

        vm.TextHeightText = "tall";                                                             // refused, value kept
        Assert.Equal(0.75, s.Document.FindDimensionStyle("Rebar")!.TextHeight);
        Assert.NotEqual("", vm.Message);
        vm.TextHeightText = "0";
        Assert.Equal(0.75, s.Document.FindDimensionStyle("Rebar")!.TextHeight);

        vm.SetCurrentCommand.Execute(null);
        Assert.Equal("Rebar", s.Document.CurrentDimensionStyleName);
        vm.Selected = "Style 1";
        vm.DeleteCommand.Execute(null);
        Assert.Equal(new[] { "Standard", "Rebar" }, vm.Styles.ToArray());

        s.Undo();                                                                               // the window follows undo
        Assert.Contains("Style 1", vm.Styles);
    }

    [Fact]
    public void Ribbon_strip_shows_the_current_style_or_that_of_the_selected_dimension()
    {
        var main = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("recovery")));
        var s = main.Session;
        var bar = main.DimensionBar;
        s.Document.Settings.Format = LengthFormat.Decimal;
        s.AddDimensionStyle(new DimensionStyle { Name = "Rebar", TextHeight = 2, ArrowSize = 2 });
        Assert.Equal(new[] { "Standard", "Rebar" }, bar.StyleNames.ToArray());
        Assert.Equal("Standard", bar.SelectedStyle);
        Assert.Equal("Arrow", bar.SelectedArrow);
        Assert.False(bar.IsActive);

        main.RunCommand("DIM");
        Assert.True(bar.IsActive);                                                              // shown on the HOME tab while dimensioning
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(10, 0));
        s.SubmitPoint(P(5, 6));
        Assert.False(bar.IsActive);

        bar.SelectedStyle = "Rebar";                                                            // nothing selected: the current style
        Assert.Equal("Rebar", s.Document.CurrentDimensionStyleName);
        Assert.Equal("Standard", s.Document.Entities.OfType<DimensionEntity>().Single().StyleName);

        s.SelectAt(P(5, 6), 0.5);
        Assert.True(bar.IsActive);
        Assert.Equal("Standard", bar.SelectedStyle);                                            // the dimension's own style
        bar.SelectedStyle = "Rebar";
        Assert.Equal("Rebar", s.Document.Entities.OfType<DimensionEntity>().Single().StyleName);
        Assert.Equal("Dimension", main.Properties.Header);
        Assert.Equal("Rebar", main.Properties.Rows.Single(r => r.Label == "Style").Value);

        bar.TextHeightText = "5";                                                               // the whole style, in proportion
        var rebar = s.Document.FindDimensionStyle("Rebar")!;
        Assert.Equal(5.0, rebar.TextHeight);
        Assert.Equal(5.0, rebar.ArrowSize);
        Assert.Equal(5.0, s.Document.Entities.OfType<DimensionEntity>().Single().TextHeight);
        bar.SelectedArrow = "Dot";
        Assert.Equal(DimensionArrow.Dot, s.Document.FindDimensionStyle("Rebar")!.Arrow);

        int opened = 0;
        main.DimensionStylesRequested += (_, _) => opened++;
        s.ClearSelection();
        main.RunCommand("DIMSTYLE");
        main.RunCommand("DST");
        Assert.Equal(2, opened);
    }
}

public class DimensionLayerTests
{
    [Fact]
    public void Dimensions_go_on_a_layer_named_after_their_style()
    {
        var s = new EditorSession();
        string current = s.Document.CurrentLayer.Name;
        int layersBefore = s.Document.Layers.Count;
        Assert.Null(s.Document.FindLayer("Standard"));

        s.StartCommand("DIM");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(10, 0));
        s.SubmitPoint(P(5, 6));
        var d = s.Document.Entities.OfType<DimensionEntity>().Single();
        Assert.Equal("Standard", s.Document.GetLayer(d.LayerId)!.Name);          // made with the first dimension
        Assert.Equal(current, s.Document.CurrentLayer.Name);                     // lines still go where they went
        Assert.Equal(layersBefore + 1, s.Document.Layers.Count);

        s.StartCommand("LINE");
        s.SubmitPoint(P(0, 20));
        s.SubmitPoint(P(10, 20));
        Assert.Equal(current, s.Document.GetLayer(s.Document.Entities.OfType<LineEntity>().Single().LayerId)!.Name);

        s.StartCommand("DIMCHAIN");                                              // more dimensions: the same layer, no second one
        s.SubmitPoint(P(25, 0));
        s.SubmitEnter();
        Assert.Equal(layersBefore + 1, s.Document.Layers.Count);
        Assert.True(s.Document.Entities.OfType<DimensionEntity>().All(e => s.Document.GetLayer(e.LayerId)!.Name == "Standard"));

        s.Undo();                                                                // the chained dimension
        s.Undo();                                                                // the line
        s.Undo();                                                                // the first dimension and its layer, together
        Assert.Equal(0, s.Document.EntityCount);
        Assert.Null(s.Document.FindLayer("Standard"));
    }

    [Fact]
    public void A_new_style_gets_its_layer_at_once_and_a_dimension_changing_style_changes_layer()
    {
        var s = new EditorSession();
        Assert.True(s.AddDimensionStyle(new DimensionStyle { Name = "Rebar dims" }));
        var layer = s.Document.FindLayer("Rebar dims");
        Assert.NotNull(layer);
        s.Undo();                                                                // style and layer are one step
        Assert.Null(s.Document.FindLayer("Rebar dims"));
        Assert.Null(s.Document.FindDimensionStyle("Rebar dims"));
        s.Redo();

        s.StartCommand("DIM");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(10, 0));
        s.SubmitPoint(P(5, 6));
        s.SelectAt(P(5, 6), 0.5);
        Assert.True(s.SetDimensionStyleOfSelection("Rebar dims"));
        var d = s.Document.Entities.OfType<DimensionEntity>().Single();
        Assert.Equal("Rebar dims", d.StyleName);
        Assert.Equal("Rebar dims", s.Document.GetLayer(d.LayerId)!.Name);

        // A layer with the style's name that is already there is used as it is.
        var own = s.AddLayer("Site")!;
        Assert.True(s.AddDimensionStyle(new DimensionStyle { Name = "Site" }));
        Assert.Equal(own.Id, s.Document.FindLayer("Site")!.Id);
        Assert.Equal(1, s.Document.Layers.Count(l => l.Name == "Site"));
    }
}

