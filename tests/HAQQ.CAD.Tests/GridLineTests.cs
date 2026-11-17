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

public class GridLineTests
{
    private static DrawingSettings Inches() => new() { Unit = LinearUnit.Inch, Format = LengthFormat.Architectural };

    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        return s;
    }

    private static List<GridLineEntity> Grid(EditorSession s) => s.Document.Entities.OfType<GridLineEntity>().ToList();

    [Fact]
    public void Spacings_are_read_as_a_list_with_a_short_form_for_equal_bays()
    {
        Assert.True(GridLines.TryParseSpacings("4x20', 15'-6, 2 @ 10', 96", Inches(), out double[] v, out _));
        Assert.Equal(new[] { 240.0, 240, 240, 240, 186, 120, 120, 96 }, v);
        Assert.True(GridLines.TryParseSpacings("", Inches(), out v, out _));
        Assert.Empty(v);
        Assert.False(GridLines.TryParseSpacings("20', wide", Inches(), out _, out string error));
        Assert.Contains("wide", error);
        Assert.False(GridLines.TryParseSpacings("0", Inches(), out _, out _));
        Assert.False(GridLines.TryParseSpacings("500x1", Inches(), out _, out error));
        Assert.Contains("at most", error);

        Assert.True(GridLines.TryParseAngles("6x15, 30", out double[] a, out _));
        Assert.Equal(7, a.Length);
        Assert.Equal(120.0, a.Sum(), 9);
    }

    [Fact]
    public void Labels_are_taken_as_typed_and_then_counted_on()
    {
        Assert.Equal(new[] { "A", "B", "C", "D" }, GridLines.Labels("A", 4, "A"));
        Assert.Equal(new[] { "1", "2", "3", "3.5", "4", "5" }, GridLines.Labels("1,2,3,3.5,4", 6, "1"));
        Assert.Equal(new[] { "A", "B", "C" }, GridLines.Labels("", 3, "A"));
        Assert.Equal(new[] { "X", "Y", "Z", "AA", "AB" }, GridLines.Labels("X", 5, "A"));
        Assert.Equal(new[] { "A9", "A10" }, GridLines.Labels("A9", 2, "A"));
        Assert.Equal(new[] { "10", "11" }, GridLines.Labels(" 10 ", 2, "1"));
        Assert.Equal("4", GridLines.NextLabel("3.5"));
        Assert.Equal("b", GridLines.NextLabel("a"));
        Assert.Equal("BA", GridLines.NextLabel("AZ"));
        Assert.Equal(new[] { "A", "B" }, GridLines.Labels("A,B,C,D", 2, "A"));      // more than needed: the rest is left out
    }

    [Fact]
    public void Rectangular_grid_has_one_grid_line_per_position_with_labels_on_the_chosen_side()
    {
        var entities = GridLines.Build(new RectangularGridSpec
        {
            XSpacings = new[] { 100.0, 50 }, YSpacings = new[] { 80.0 },
            XLabels = new[] { "A", "B", "C" }, YLabels = new[] { "1", "2" },
            XLabelEnds = GridLabelEnds.End, YLabelEnds = GridLabelEnds.Both,
            Extension = 10, BubbleDiameter = 8,
        });
        var lines = entities.Cast<GridLineEntity>().ToList();
        Assert.Equal(5, lines.Count);
        Assert.Single(lines.Select(l => l.GroupId).Distinct());                    // one group
        Assert.NotNull(lines[0].GroupId);

        var b = lines.Single(l => l.Label == "B");
        Assert.Equal(P(100, -10), b.Start);                                        // runs past the outermost crossings
        Assert.Equal(P(100, 90), b.End);
        Assert.Equal(new[] { P(100, 94) }, b.BubbleCenters.ToArray());             // label at the top only
        var two = lines.Single(l => l.Label == "2");
        Assert.Equal(P(-10, 80), two.Start);
        Assert.Equal(P(160, 80), two.End);
        Assert.Equal(new[] { P(-14, 80), P(164, 80) }, two.BubbleCenters.ToArray());   // left and right

        // Turned a quarter turn about its origin, and moved.
        var turned = GridLines.Build(new RectangularGridSpec
        {
            Origin = P(1000, 0), RotationDegrees = 90, XSpacings = new[] { 100.0 }, YSpacings = new[] { 80.0 },
            XLabels = new[] { "A", "B" }, YLabels = new[] { "1", "2" }, Extension = 0, BubbleDiameter = 8,
        }).Cast<GridLineEntity>().Single(l => l.Label == "B");
        Assert.True(turned.Start.NearlyEquals(P(1000, 100), 1e-9));
        Assert.True(turned.End.NearlyEquals(P(920, 100), 1e-9));
    }

    [Fact]
    public void Sector_grid_fans_straight_lines_out_and_draws_arcs_across_them()
    {
        var lines = GridLines.Build(new SectorGridSpec
        {
            RingSpacings = new[] { 100.0, 50, 50 }, AngleSpacings = new[] { 30.0, 30, 30 }, StartAngleDegrees = 0,
            RadialLabels = new[] { "A", "B", "C", "D" }, RingLabels = new[] { "1", "2", "3" }, Extension = 20, BubbleDiameter = 10,
        }).Cast<GridLineEntity>().ToList();
        Assert.Equal(4, lines.Count(l => !l.IsArc));
        Assert.Equal(3, lines.Count(l => l.IsArc));
        Assert.Equal(new[] { 100.0, 150, 200 }, lines.Where(l => l.IsArc).Select(l => l.Radius).ToArray());

        var d = lines.Single(l => l.Label == "D");                                 // the last straight line, at 90 degrees
        Assert.True(d.Start.NearlyEquals(P(0, 80), 1e-9));                         // from a little inside the first arc
        Assert.True(d.End.NearlyEquals(P(0, 220), 1e-9));                          // to a little past the last
        Assert.True(d.BubbleCenters.Single().NearlyEquals(P(0, 225), 1e-9));       // label at the outer end

        var ring = lines.Single(l => l.Label == "2");
        Assert.True(ring.Sweep > GeoMath.DegreesToRadians(90));                    // runs a little past the first and last line
        Assert.Single(ring.BubbleCenters);

        // A full turn: the last straight line would be the first again, and the arcs close into circles.
        var full = GridLines.Build(new SectorGridSpec { RingSpacings = new[] { 50.0 }, AngleSpacings = Enumerable.Repeat(90.0, 4).ToArray(), RadialLabels = new[] { "N", "W", "S", "E" } })
            .Cast<GridLineEntity>().ToList();
        Assert.Equal(4, full.Count(l => !l.IsArc));
        Assert.Equal(2 * Math.PI, full.Where(l => l.IsArc).Sum(l => l.Sweep), 9);
    }

    [Fact]
    public void A_grid_goes_on_a_layer_with_its_name_in_one_undo_step()
    {
        var s = Session();
        Assert.Equal("Grid 1", s.NextGridName());
        var spec = new RectangularGridSpec { XSpacings = new[] { 100.0 }, YSpacings = new[] { 100.0 }, XLabels = new[] { "A", "B" }, YLabels = new[] { "1", "2" } };
        Assert.True(s.CreateGrid("Grid 1", Linetypes.DashDot, GridLines.Build(spec)));
        var layer = s.Document.FindLayer("Grid 1")!;
        Assert.Equal("Dash Dot", layer.Linetype);
        Assert.Equal(4, Grid(s).Count);
        Assert.True(Grid(s).All(g => g.LayerId == layer.Id && g.Linetype == Linetypes.ByLayer));
        Assert.NotEqual(layer.Id, s.Document.CurrentLayerId);                      // the current layer is left alone
        Assert.Equal("Grid 2", s.NextGridName());

        s.Undo();
        Assert.Equal(0, s.Document.EntityCount);
        Assert.Null(s.Document.FindLayer("Grid 1"));

        Assert.False(s.CreateGrid(" ", Linetypes.DashDot, GridLines.Build(spec)));
        Assert.False(s.CreateGrid("Empty", Linetypes.DashDot, new List<Entity>()));
    }

    [Fact]
    public void A_click_selects_the_whole_grid_and_a_double_click_one_line()
    {
        var s = Session();
        s.CreateGrid("Grid 1", Linetypes.DashDot, GridLines.Build(new RectangularGridSpec { XSpacings = new[] { 100.0, 100 }, YSpacings = new[] { 100.0 }, Extension = 10 }));
        AddLine(s.Document, 500, 0, 600, 0);

        Assert.True(s.SelectAt(P(100, 50), 1));                                    // on the middle line
        Assert.Equal(5, s.Selection.Count);                                        // every line of the grid, not the stray line
        Assert.True(s.SelectAt(P(100, 50), 1, toggle: true));                      // Shift+click takes the group out again
        Assert.Equal(0, s.Selection.Count);

        s.SelectAt(P(100, 50), 1);
        Assert.True(s.SelectSingleAt(P(100, 50), 1));
        var one = s.GetSelectedEntities().Single();
        Assert.Equal(P(100, -10), ((GridLineEntity)one).Start);

        // A copy of the whole grid is a group of its own.
        s.ClearSelection();
        s.SelectAt(P(100, 50), 1);
        s.StartCommand("COPY");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(0, 1000));
        s.SubmitEnter();
        Assert.Equal(10, Grid(s).Count);
        Assert.Equal(2, Grid(s).Select(g => g.GroupId).Distinct().Count());
        s.ClearSelection();
        s.SelectAt(P(100, 1050), 1);
        Assert.Equal(5, s.Selection.Count);
    }

    [Fact]
    public void Dragging_an_end_lengthens_the_line_and_its_label_follows()
    {
        var line = new GridLineEntity(P(0, 0), P(0, 100)) { Label = "A", BubbleDiameter = 10, LabelAtEnd = true };
        Assert.Equal(new[] { P(0, 0), P(0, 50), P(0, 100) }, EntityGrips.GetGripPoints(line).ToArray());
        Assert.Equal(P(0, 105), line.BubbleCenters.Single());
        Assert.True(EntityGrips.Stretch(line, 2, P(0, 160)));
        Assert.Equal(P(0, 160), line.End);
        Assert.Equal(P(0, 165), line.BubbleCenters.Single());                      // the label circle moved with the end
        Assert.True(line.DistanceTo(P(0, 168)) < 1e-9);                            // a click in the circle picks the line
        Assert.True(line.Bounds.Contains(P(4, 168)));
        Assert.False(EntityGrips.Stretch(line, 2, P(0, 0)));                       // not onto the other end

        line.LabelAtStart = true;
        Assert.Equal(2, line.BubbleCenters.Count);
        line.Label = "";
        Assert.Empty(line.BubbleCenters);                                          // no label, no circle

        var arc = new GridLineEntity(P(0, 0), 100, 0, Math.PI / 2) { Label = "1", BubbleDiameter = 10, LabelAtStart = true, LabelAtEnd = false };
        Assert.True(arc.BubbleCenters.Single().NearlyEquals(P(100, -5), 1e-9));    // beyond the start, along the arc
        Assert.True(EntityGrips.Stretch(arc, 2, P(-50, 50)));                      // the end turns to 135 degrees
        Assert.Equal(3 * Math.PI / 4, arc.Sweep, 9);
        Assert.True(EntityGrips.Stretch(arc, 1, P(0, 150)));                       // the middle changes the radius
        Assert.Equal(150.0, arc.Radius, 9);
    }

    [Fact]
    public void Add_grid_line_puts_one_between_two_lines_or_past_the_end_with_a_suggested_label()
    {
        var s = Session();
        s.CreateGrid("Grid 1", Linetypes.DashDot, GridLines.Build(new RectangularGridSpec
        {
            XSpacings = new[] { 100.0, 100 }, YSpacings = new[] { 100.0 }, XLabels = new[] { "A", "B", "C" }, YLabels = new[] { "1", "2" }, Extension = 10,
        }));
        var group = Grid(s)[0].GroupId;

        // Between A and B, by a typed distance on the side of the cursor.
        s.StartCommand("GRIDADD");
        Assert.True(s.PickingObject);
        s.SubmitPoint(P(0, 50));                                                   // grid line A
        s.UpdateCursor(P(30, 50), 0.01);
        s.SubmitText("40");
        Assert.Contains("<A.5>", s.Prompt);
        s.SubmitText("A.1");                                                       // my own label
        var added = Grid(s).Single(g => g.Label == "A.1");
        Assert.Equal(P(40, -10), added.Start);
        Assert.Equal(P(40, 110), added.End);                                       // the same length as the line it was made from
        Assert.Equal(group, added.GroupId);
        Assert.Equal(s.Document.FindLayer("Grid 1")!.Id, added.LayerId);
        Assert.Null(s.ActiveCommand);

        // Past the last line, by a click; Enter takes the suggested label.
        s.StartCommand("GA");
        s.SubmitPoint(P(200, 50));                                                 // grid line C
        s.SubmitPoint(P(275, 20));
        Assert.Contains("<D>", s.Prompt);
        s.SubmitEnter();
        Assert.Equal(P(275, -10), Grid(s).Single(g => g.Label == "D").Start);

        // Before the first line.
        s.StartCommand("GRIDADD");
        s.SubmitPoint(P(0, 50));
        s.SubmitPoint(P(-60, 50));
        Assert.Contains("<A-1>", s.Prompt);
        s.SubmitText("");
        Assert.Equal(8, Grid(s).Count);

        s.Undo();
        Assert.Equal(7, Grid(s).Count);                                            // one step per added line

        s.StartCommand("GRIDADD");
        AddLine(s.Document, 500, 0, 600, 0);
        s.SubmitPoint(P(550, 0));                                                  // not a grid line
        Assert.True(s.PickingObject);
        s.Cancel();

        // An arc: the new one has another radius.
        var arc = new GridLineEntity(P(0, 0), 100, 0, 1) { Label = "1" };
        var outer = AddGridLineCommand.Offset(arc, P(130, 10), 25)!;
        Assert.Equal(125.0, outer.Radius, 9);
        Assert.Null(AddGridLineCommand.Offset(arc, P(100, 0), null));              // on the line itself
    }

    [Fact]
    public void Grid_lines_snap_at_their_crossings_and_survive_save_and_open()
    {
        var s = Session();
        s.CreateGrid("Grid 1", Linetypes.DashDot, GridLines.Build(new RectangularGridSpec
        {
            XSpacings = new[] { 100.0 }, YSpacings = new[] { 100.0 }, XLabels = new[] { "A", "B" }, YLabels = new[] { "1", "2" },
            XLabelEnds = GridLabelEnds.Both, Extension = 10, BubbleDiameter = 9,
        }));
        var snap = SnapEngine.FindSnap(s.Document, P(100.3, 99.8), 1, SnapModes.Intersection);
        Assert.NotNull(snap);
        Assert.True(snap!.Value.Point.NearlyEquals(P(100, 100), 1e-9));

        var arc = new GridLineEntity(P(500, 0), 50, 0.25, 1.5) { Label = "R1", LayerId = s.Document.CurrentLayerId, GroupId = Guid.NewGuid(), LabelAtStart = true, LabelAtEnd = false };
        s.Document.AddEntity(arc);
        s.Document.AddEntity(new TextEntity(P(0, -50), 6, "GRID PLAN") { LayerId = s.Document.CurrentLayerId, Angle = 0.5 });

        var loaded = HcadSerializer.LoadFromBytes(HcadSerializer.SaveToBytes(s.Document));
        Assert.Empty(loaded.Warnings);
        var before = s.Document.Entities.OfType<GridLineEntity>().OrderBy(g => g.Id).ToList();
        var after = loaded.Document.Entities.OfType<GridLineEntity>().OrderBy(g => g.Id).ToList();
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Id, after[i].Id);
            Assert.Equal(before[i].IsArc, after[i].IsArc);
            Assert.Equal(before[i].StartPoint, after[i].StartPoint);
            Assert.Equal(before[i].EndPoint, after[i].EndPoint);
            Assert.Equal(before[i].Label, after[i].Label);
            Assert.Equal(before[i].BubbleDiameter, after[i].BubbleDiameter);
            Assert.Equal(before[i].LabelAtStart, after[i].LabelAtStart);
            Assert.Equal(before[i].LabelAtEnd, after[i].LabelAtEnd);
            Assert.Equal(before[i].GroupId, after[i].GroupId);
        }
        var text = loaded.Document.Entities.OfType<TextEntity>().Single();
        Assert.Equal("GRID PLAN", text.Text);
        Assert.Equal(6.0, text.Height);
        Assert.Equal(0.5, text.Angle);
        Assert.Equal("Dash Dot", loaded.Document.FindLayer("Grid 1")!.Linetype);
    }

    [Fact]
    public void Renderer_draws_the_line_its_label_circle_and_the_label()
    {
        var doc = new DrawingDocument();
        doc.AddEntity(new GridLineEntity(P(0, 0), P(0, 100)) { Label = "A", BubbleDiameter = 10, LabelAtStart = true, LabelAtEnd = true, LayerId = doc.CurrentLayerId });
        doc.AddEntity(new TextEntity(P(50, 50), 5, "NOTE") { LayerId = doc.CurrentLayerId });
        var view = new ViewTransform();
        view.SetViewport(800, 600);
        view.SetView(P(20, 50), 4);

        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, view, new RenderOptions { ShowGrid = false });
        Assert.Single(surface.Lines);
        Assert.Equal(2, surface.Circles.Count);
        Assert.Equal(20.0, surface.Circles[0].R, 9);                               // 10 across at 4 pixels per unit
        Assert.Equal(new[] { "A", "A", "NOTE" }, surface.Texts.Select(t => t.Text).OrderBy(t => t).ToArray());
        Assert.Equal(20.0, surface.Texts.Single(t => t.Text == "NOTE").Height, 9);
    }

    [Fact]
    public void Wizard_builds_the_grid_from_what_is_typed_and_says_what_is_wrong()
    {
        var s = new EditorSession();                                                // inches, feet and inches
        var w = new GridWizardViewModel(s);
        Assert.Equal("Grid 1", w.Name);
        Assert.True(w.IsRectangular);
        Assert.True(w.CanCreate);
        Assert.Equal(9, w.Preview.Count);                                           // 4 and 3 bays: 5 + 4 lines
        Assert.Contains("A to E", w.Summary);
        Assert.Contains("1 to 4", w.Summary);

        w.XSpacings = "20', 15', 20'";
        w.XLabels = "1, 2, 2.5";
        w.YSpacings = "2x30'";
        w.YLabels = "A";
        w.SelectedXLabelEnds = "Bottom";
        w.SelectedYLabelEnds = "Left and right";
        Assert.Equal(7, w.Preview.Count);
        var lines = w.Preview.Cast<GridLineEntity>().ToList();
        Assert.Equal(new[] { "1", "2", "2.5", "3" }, lines.Take(4).Select(l => l.Label).ToArray());
        Assert.True(lines[0].LabelAtStart && !lines[0].LabelAtEnd);                 // bottom
        Assert.True(lines[4].LabelAtStart && lines[4].LabelAtEnd);                  // left and right
        Assert.Equal(420.0, lines[2].Start.X, 9);

        w.XSpacings = "20', oops";
        Assert.False(w.CanCreate);
        Assert.Contains("oops", w.Error);
        Assert.Empty(w.Preview);
        Assert.False(w.Create());
        w.XSpacings = "20'";

        w.OriginX = "100";
        w.Name = "Building A";
        Assert.True(w.Create());
        var layer = s.Document.FindLayer("Building A")!;
        Assert.Equal(5, s.Document.Entities.OfType<GridLineEntity>().Count(g => g.LayerId == layer.Id));
        Assert.Contains(s.Document.Entities.OfType<GridLineEntity>(), g => g.Label == "1" && g.Start.X == 100);

        // Sector, placed by a click in the drawing.
        var sector = new GridWizardViewModel(s);
        sector.SelectedKind = "Sector (radial)";
        Assert.True(sector.IsSector);
        sector.RingSpacings = "30', 2x10'";
        sector.AngleSpacings = "4x22.5";
        Assert.True(sector.CanCreate);
        Assert.Equal(8, sector.Preview.Count);                                      // 5 straight lines and 3 arcs
        sector.AngleSpacings = "5x90";
        Assert.Contains("full turn", sector.Error);
        sector.AngleSpacings = "4x22.5";
        Assert.True(sector.PlaceByClick());
        Assert.Equal("GRIDLINES", s.ActiveCommand!.Name);
        Assert.Equal(8, s.GetPreview().Count);                                      // it follows the cursor
        s.SubmitPoint(P(5000, 5000));
        Assert.Null(s.ActiveCommand);
        var arcs = s.Document.Entities.OfType<GridLineEntity>().Where(g => g.IsArc).ToList();
        Assert.Equal(3, arcs.Count);
        Assert.True(arcs.All(a => a.Center == P(5000, 5000)));
        Assert.NotNull(s.Document.FindLayer(sector.Name));
    }
}
