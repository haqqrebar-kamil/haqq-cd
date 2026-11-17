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
        s.Document.Settings.DimensionTextHeight = 1.0;
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
        s.StartCommand("DIMSIZE");
        s.SubmitText("3");
        Assert.Equal(3.0, s.Document.Settings.DimensionTextHeight);
        Assert.NotNull(s.ActiveCommand);                // asks about the dimensions already drawn
        s.SubmitKeyword("Y");
        Assert.Equal(3.0, Only(s).TextHeight);
        Assert.Null(s.ActiveCommand);
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
        Assert.Equal(s.Document.CurrentLayerId, d.LayerId);
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
        Assert.Equal(1.0, loaded.Document.Settings.DimensionTextHeight);
        Assert.Equal(5, HcadSerializer.SchemaVersion);
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
