using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Documents;
using HAQQ.CAD.Documents.Pdf;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Presentation;
using HAQQ.CAD.Rendering;
using Xunit;
using static HAQQ.CAD.Tests.T;

namespace HAQQ.CAD.Tests;

public class HatchTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

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
        return entity;
    }

    private static PolylineEntity Box(EditorSession s, double x1, double y1, double x2, double y2) =>
        Add(s, new PolylineEntity(new[] { P(x1, y1), P(x2, y1), P(x2, y2), P(x1, y2) }, true));

    private static void Near(double expected, double actual, double relative = 1e-9) =>
        Assert.True(Math.Abs(expected - actual) <= Math.Abs(expected) * relative, $"expected {expected}, got {actual}");

    private static HatchEntity Hatch(EditorSession s) => s.Document.Entities.OfType<HatchEntity>().Single();

    // ------------------------------------------------------------------ finding the area

    [Fact]
    public void Four_separate_lines_that_cross_give_the_square_in_the_middle()
    {
        var s = Session();
        Add(s, new LineEntity(P(0, 2), P(10, 2)));
        Add(s, new LineEntity(P(0, 8), P(10, 8)));
        Add(s, new LineEntity(P(3, 0), P(3, 10)));
        Add(s, new LineEntity(P(7, 0), P(7, 10)));
        var region = HatchBoundary.AtPoint(s.Document.Entities, P(5, 5), HatchIslands.Normal, 0, out string? problem, out _);
        Assert.Null(problem);
        Assert.Single(region!.Loops);
        Near(4 * 6, HatchGeometry.Area(region.Loops));
        Assert.Equal(4, region.SourceIds.Count);

        // Outside the # there is no closed area; the corner cells are open too.
        Assert.Null(HatchBoundary.AtPoint(s.Document.Entities, P(1, 1), HatchIslands.Normal, 0, out problem, out _));
        Assert.Contains("No closed area", problem);
        Assert.Null(HatchBoundary.AtPoint(s.Document.Entities, P(50, 50), HatchIslands.Normal, 0, out problem, out _));
        Assert.Contains("outside", problem);
    }

    [Fact]
    public void A_line_across_a_rectangle_gives_the_half_that_was_clicked_and_the_source_objects_are_untouched()
    {
        var s = Session();
        var box = Box(s, 0, 0, 10, 6);
        var line = Add(s, new LineEntity(P(4, -1), P(4, 7)));
        var left = HatchBoundary.AtPoint(s.Document.Entities, P(1, 1), HatchIslands.Normal, 0, out _, out _)!;
        var right = HatchBoundary.AtPoint(s.Document.Entities, P(9, 5), HatchIslands.Normal, 0, out _, out _)!;
        Near(24, HatchGeometry.Area(left.Loops));
        Near(36, HatchGeometry.Area(right.Loops));
        Assert.Equal(4, box.Vertices.Count);
        Assert.Equal((P(4, -1), P(4, 7)), (line.Start, line.End));
    }

    [Fact]
    public void A_slab_with_openings_is_hatched_around_them_by_the_island_mode()
    {
        var s = Session();
        Box(s, 0, 0, 100, 60);
        Add(s, new CircleEntity(P(30, 30), 10));
        Box(s, 60, 20, 80, 40);                          // a square opening
        Add(s, new CircleEntity(P(70, 30), 4));          // with something inside it

        double slab = 100 * 60, circle = Math.PI * 100, square = 400, inner = Math.PI * 16;
        var normal = HatchBoundary.AtPoint(s.Document.Entities, P(5, 5), HatchIslands.Normal, 0, out _, out _)!;
        Assert.Equal(4, normal.Loops.Count);
        Near(slab - circle - square + inner, HatchGeometry.Area(normal.Loops), 1e-3);     // circles are 128 sides
        var outer = HatchBoundary.AtPoint(s.Document.Entities, P(5, 5), HatchIslands.Outer, 0, out _, out _)!;
        Near(slab - circle - square, HatchGeometry.Area(outer.Loops), 1e-3);
        var ignore = HatchBoundary.AtPoint(s.Document.Entities, P(5, 5), HatchIslands.Ignore, 0, out _, out _)!;
        Near(slab, HatchGeometry.Area(ignore.Loops));

        // A click inside the square opening hatches the opening (around what is inside it).
        var opening = HatchBoundary.AtPoint(s.Document.Entities, P(62, 22), HatchIslands.Normal, 0, out _, out _)!;
        Near(square - inner, HatchGeometry.Area(opening.Loops), 1e-3);
        Assert.False(HatchGeometry.Contains(normal.Loops, P(30, 30)));
        Assert.True(HatchGeometry.Contains(normal.Loops, P(70, 30)));
        Assert.True(HatchGeometry.Contains(normal.Loops, P(5, 5)));
    }

    [Fact]
    public void Lines_that_end_on_a_circle_or_an_arc_close_with_it()
    {
        var s = Session();
        Add(s, new CircleEntity(P(0, 0), 5));
        // Two lines leaving the circle at angles that are no corner of its 128 sides, joined far out.
        var a = ArcMath.PointAt(P(0, 0), 5, 0.3);
        var b = ArcMath.PointAt(P(0, 0), 5, -0.41);
        Add(s, new LineEntity(a, P(20, 3)));
        Add(s, new LineEntity(b, P(20, -4)));
        Add(s, new LineEntity(P(20, 3), P(20, -4)));
        var region = HatchBoundary.AtPoint(s.Document.Entities, P(12, 0), HatchIslands.Normal, 0, out string? problem, out _);
        Assert.Null(problem);
        Assert.True(HatchGeometry.Contains(region!.Loops, P(6, 0)));
        Assert.False(HatchGeometry.Contains(region.Loops, P(4, 0)));                  // inside the circle is not part of it
        Assert.Equal(4, region.SourceIds.Count);

        // An arc and its chord; a polyline with an arc side.
        var s2 = Session();
        Add(s2, new ArcEntity(P(0, 0), 10, 0, Math.PI));
        Add(s2, new LineEntity(P(-10, 0), P(10, 0)));
        Near(Math.PI * 100 / 2, HatchGeometry.Area(HatchBoundary.AtPoint(s2.Document.Entities, P(0, 3), HatchIslands.Normal, 0, out _, out _)!.Loops), 1e-3);
        var s3 = Session();
        var rounded = Add(s3, new PolylineEntity(new[] { P(0, 0), P(10, 0), P(10, 10), P(0, 10) }, true, new[] { 0.0, 1.0, 0.0, 0.0 }));
        Near(rounded.Area, HatchGeometry.Area(HatchBoundary.FromObjects(s3.Document.Entities, HatchIslands.Normal, 0, out _)!.Loops), 1e-3);
    }

    [Fact]
    public void An_open_boundary_is_refused_with_its_loose_ends_and_closed_by_the_gap_tolerance()
    {
        var s = Session();
        Add(s, new LineEntity(P(0, 0), P(10, 0)));
        Add(s, new LineEntity(P(10, 0), P(10, 10)));
        Add(s, new LineEntity(P(10, 10), P(0, 10)));
        Add(s, new LineEntity(P(0, 10), P(0, 0.01)));                                  // stops 0.01 short of the corner
        Assert.Null(HatchBoundary.AtPoint(s.Document.Entities, P(5, 5), HatchIslands.Normal, 0, out string? problem, out var loose));
        Assert.Contains("open", problem);
        Assert.Contains(loose, l => l.DistanceTo(P(0, 0.01)) < 1e-9);
        Assert.Contains(loose, l => l.DistanceTo(P(0, 0)) < 1e-9);

        var bridged = HatchBoundary.AtPoint(s.Document.Entities, P(5, 5), HatchIslands.Normal, 0.02, out problem, out _);
        Assert.Null(problem);
        Near(100, HatchGeometry.Area(bridged!.Loops), 1e-3);
        Assert.Equal(P(0, 0.01), ((LineEntity)s.Document.Entities.Last()).End);       // the drawing itself is not repaired
        Assert.Null(HatchBoundary.AtPoint(s.Document.Entities, P(5, 5), HatchIslands.Normal, 0.005, out _, out _));   // a smaller tolerance does not bridge it

        Assert.Null(HatchBoundary.FromObjects(s.Document.Entities.Take(2), HatchIslands.Normal, 0, out problem));
        Assert.Contains("do not close", problem);
    }

    [Fact]
    public void Areas_far_from_the_origin_and_overlapping_duplicate_lines_are_found()
    {
        var s = Session();
        double x = 2_500_000, y = 7_300_000;                                          // survey coordinates
        Add(s, new LineEntity(P(x, y), P(x + 30, y)));
        Add(s, new LineEntity(P(x, y), P(x + 30, y)));                                // drawn twice
        Add(s, new LineEntity(P(x + 10, y), P(x + 40, y)));                           // and overlapping
        Add(s, new LineEntity(P(x + 30, y), P(x + 30, y + 20)));
        Add(s, new LineEntity(P(x + 30, y + 20), P(x, y + 20)));
        Add(s, new LineEntity(P(x, y + 20), P(x, y)));
        var region = HatchBoundary.AtPoint(s.Document.Entities, P(x + 15, y + 10), HatchIslands.Normal, 0, out string? problem, out _);
        Assert.Null(problem);
        Near(600, HatchGeometry.Area(region!.Loops), 1e-6);
    }

    // ------------------------------------------------------------------ the pattern

    [Fact]
    public void Pattern_lines_stay_inside_the_area_and_their_total_length_is_area_over_spacing()
    {
        var loops = new List<Point2D[]>
        {
            new[] { P(0, 0), P(20, 0), P(20, 10), P(0, 10) },
            new[] { P(5, 3), P(9, 3), P(9, 7), P(5, 7) },                              // a hole
        };
        double area = 200 - 16;
        foreach (var (name, scale, angle) in new[] { ("ANSI31", 1.0, 0.0), ("ANSI31", 4.0, 30.0), ("HORIZONTAL", 2.0, 0.0), ("CROSS", 1.0, 17.0) })
        {
            var pattern = HatchPatterns.Find(name)!;
            var lines = HatchGeometry.Lines(loops, pattern, scale, angle, P(0.37, 0.11), 200_000, out bool dense);
            Assert.False(dense);
            double length = lines.Sum(l => l.A.DistanceTo(l.B));
            double expected = pattern.Families.Sum(f => area / (f.Spacing * scale));
            Near(expected, length, 0.02);
            foreach (var (a, b) in lines)
            {
                var mid = Point2D.Midpoint(a, b);
                Assert.True(HatchGeometry.Contains(loops, mid), $"{name}: a piece at {mid} is outside the area");
                Assert.False(mid.X > 5.001 && mid.X < 8.999 && mid.Y > 3.001 && mid.Y < 6.999);
            }
        }

        // Scale changes the spacing, not the area; the angle turns the lines.
        var one = HatchGeometry.Lines(loops, HatchPatterns.Find("HORIZONTAL")!, 1, 0, Point2D.Origin, 200_000, out _);
        var turned = HatchGeometry.Lines(loops, HatchPatterns.Find("HORIZONTAL")!, 1, 90, Point2D.Origin, 200_000, out _);
        Assert.True(one.All(l => Math.Abs(l.A.Y - l.B.Y) < 1e-9));
        Assert.True(turned.All(l => Math.Abs(l.A.X - l.B.X) < 1e-9));

        // Brick: the joints of one course fall between the joints of the next, measured from the origin.
        var brick = HatchGeometry.Lines(new List<Point2D[]> { new[] { P(0, 0), P(3, 0), P(3, 1), P(0, 1) } }, HatchPatterns.Find("BRICK")!, 1, 0, Point2D.Origin, 200_000, out _);
        var joints = brick.Where(l => Math.Abs(l.A.X - l.B.X) < 1e-9).ToList();
        Assert.Contains(joints, j => Math.Abs(j.A.X - 0.75) < 1e-9 && Math.Min(j.A.Y, j.B.Y) < 1e-9);
        Assert.Contains(joints, j => Math.Abs(j.A.X - 0.375) < 1e-9 && Math.Abs(Math.Min(j.A.Y, j.B.Y) - 0.25) < 1e-9);
        Assert.DoesNotContain(joints, j => Math.Abs(j.A.X - 0.375) < 1e-9 && Math.Min(j.A.Y, j.B.Y) < 1e-9);

        // Too fine for the area: refused with a flag, not a frozen program.
        var huge = new List<Point2D[]> { new[] { P(0, 0), P(100000, 0), P(100000, 100000), P(0, 100000) } };
        Assert.Empty(HatchGeometry.Lines(huge, HatchPatterns.Find("ANSI31")!, 1, 0, Point2D.Origin, 200_000, out bool tooFine));
        Assert.True(tooFine);
    }

    [Fact]
    public void Every_built_in_pattern_draws_and_has_a_unique_name()
    {
        var loops = new List<Point2D[]> { new[] { P(0, 0), P(6, 0), P(6, 4), P(0, 4) } };
        Assert.Equal(HatchPatterns.All.Count, HatchPatterns.All.Select(p => p.Name.ToUpperInvariant()).Distinct().Count());
        Assert.True(HatchPatterns.All.Count >= 30);
        foreach (var name in new[] { "ANSI31", "ANSI32", "ANSI33", "ANSI34", "ANSI35", "ANSI36", "ANSI37", "ANSI38", "CONCRETE", "RCONCRETE", "BRICK", "CMU",
                     "STONE", "GRAVEL", "SAND", "EARTH", "ROCK", "ASPHALT", "WOOD", "PLYWOOD", "INSULATION", "STEEL", "ALUMINUM", "GLASS", "TILE", "DOTS", "CROSS" })
            Assert.NotNull(HatchPatterns.Find(name));
        foreach (var pattern in HatchPatterns.All)
        {
            if (pattern.IsSolid) continue;
            var lines = HatchGeometry.Lines(loops, pattern, 1, 0, Point2D.Origin, 200_000, out bool dense);
            Assert.False(dense, pattern.Name);
            Assert.True(lines.Count > 10, pattern.Name);
            Assert.True(lines.All(l => l.A.X > -1e-9 && l.A.X < 6 + 1e-9 && l.B.Y > -1e-9 && l.B.Y < 4 + 1e-9), pattern.Name);
        }
    }

    // ------------------------------------------------------------------ the tool

    [Fact]
    public void HATCH_makes_one_object_behind_its_boundary_that_is_undone_in_one_step()
    {
        var s = Session();
        var box = Box(s, 0, 0, 480, 240);                                             // a 40 ft x 20 ft slab, in inches
        Add(s, new CircleEntity(P(120, 120), 30));
        s.StartCommand("HATCH");                                                      // no window in a test: straight to picking
        Assert.Contains("Click inside a closed area", s.Prompt);
        s.SubmitPoint(P(400, 20));
        s.SubmitEnter();

        var hatch = Hatch(s);
        Assert.Equal(3, s.Document.EntityCount);
        Assert.Equal("ANSI31", hatch.Pattern);
        Assert.True(hatch.Scale >= 24, $"scale {hatch.Scale}");                       // chosen for the size of the slab, not 1
        Assert.False(hatch.TooDense);
        Assert.True(hatch.Lines.Count > 20);
        Near(480 * 240 - Math.PI * 900, hatch.Area, 1e-3);
        Assert.True(hatch.Associative);
        Assert.Equal(2, hatch.SourceIds.Count);
        Assert.Equal(s.Document.CurrentLayerId, hatch.LayerId);
        Assert.True(s.Document.GetDrawOrder(hatch.Id) < s.Document.GetDrawOrder(box.Id));
        Assert.True(s.HatchDefaults.ScaleChosen);

        // Picking: a click on the boundary takes the boundary, a click on a hatch line takes the hatch.
        Assert.Same(box, SelectionService.PickAt(s.Document, P(480, 100), 2));
        var onLine = hatch.Lines[hatch.Lines.Count / 2];
        Assert.Same(hatch, SelectionService.PickAt(s.Document, Point2D.Midpoint(onLine.A, onLine.B), 0.5));

        s.Undo();
        Assert.Equal(2, s.Document.EntityCount);
        s.Redo();
        Assert.Equal(3, s.Document.EntityCount);

        s.StartCommand("HATCHPICK");
        s.SubmitPoint(P(400, 20));                                                    // the same area again
        s.SubmitEnter();
        Assert.Equal(3, s.Document.EntityCount);
    }

    [Fact]
    public void Select_objects_hatches_what_the_selection_encloses_and_solid_is_a_fill()
    {
        var s = Session();
        var box = Box(s, 0, 0, 10, 10);
        var hole = Add(s, new CircleEntity(P(5, 5), 2));
        Add(s, new LineEntity(P(20, 0), P(30, 0)));
        s.HatchDefaults.Pattern = HatchPatterns.Solid;
        s.Selection.Add(box.Id);
        s.Selection.Add(hole.Id);
        s.StartCommand("HATCHSELECT");
        var hatch = Hatch(s);
        Assert.True(hatch.IsSolid);
        Assert.Empty(hatch.Lines);
        Near(100 - Math.PI * 4, hatch.Area, 1e-3);
        Assert.Null(hatch.Seed);

        var view = new ViewTransform();
        view.SetViewport(800, 600);
        view.SetView(P(5, 5), 40);
        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, s.Document, view, new RenderOptions());
        Assert.Single(surface.Regions);
        Assert.Equal(2, surface.Regions[0].Loops.Length);                             // outline and hole in one fill

        Assert.Same(hatch, SelectionService.PickAt(s.Document, P(1, 1), 0.2));        // a solid is picked anywhere inside
        Assert.Null(SelectionService.PickAt(s.Document, P(5, 5), 0.2));               // but not in its hole
    }

    [Fact]
    public void An_associative_hatch_follows_its_boundary_and_keeps_itself_when_the_boundary_is_lost()
    {
        var s = Session();
        var box = Box(s, 0, 0, 10, 10);
        var hole = Add(s, new CircleEntity(P(3, 3), 1));
        s.HatchDefaults.Scale = 1;
        s.HatchDefaults.ScaleChosen = true;
        Assert.NotNull(s.HatchAt(P(8, 8), out _));
        Near(100 - Math.PI, Hatch(s).Area, 1e-3);

        // Stretch the rectangle: the hatch grows with it.
        s.ModifyEntities(new[] { box.Id }, e => ((PolylineEntity)e).SetVertices(new[] { P(0, 0), P(20, 0), P(20, 10), P(0, 10) }), "Stretch");
        Near(200 - Math.PI, Hatch(s).Area, 1e-3);
        Assert.True(Hatch(s).Contains(P(18, 5)));

        // Move the opening: the unhatched place moves with it.
        s.ModifyEntities(new[] { hole.Id }, e => e.Transform(Transform2D.Translation(new Vector2D(10, 4))), "Move");
        Assert.True(Hatch(s).Contains(P(3, 3)));
        Assert.False(Hatch(s).Contains(P(13, 7)));

        s.Undo();                                                                     // undo follows too
        Assert.False(Hatch(s).Contains(P(3, 3)));
        s.Undo();
        Near(100 - Math.PI, Hatch(s).Area, 1e-3);

        // Move the boundary right away from the remembered inside point: the hatch goes with it.
        s.ModifyEntities(new[] { box.Id, hole.Id }, e => e.Transform(Transform2D.Translation(new Vector2D(500, 0))), "Move");
        Assert.True(Hatch(s).Contains(P(508, 8)));
        Near(100 - Math.PI, Hatch(s).Area, 1e-3);

        // Erase the boundary: the hatch stays, flagged, and comes back to life with an undo.
        s.Selection.Clear();
        s.Selection.Add(box.Id);
        s.StartCommand("ERASE");
        Assert.True(Hatch(s).BoundaryLost, $"sources {Hatch(s).SourceIds.Count} assoc {Hatch(s).Associative} entities {s.Document.EntityCount} box {s.Document.ContainsEntity(box.Id)}");
        Near(100 - Math.PI, Hatch(s).Area, 1e-3);
        s.Undo();
        Assert.False(Hatch(s).BoundaryLost);

        // Not associative: stays where it is.
        s.UpdateHatch(Hatch(s).Id, h => h.Associative = false);
        s.ModifyEntities(new[] { box.Id }, e => e.Transform(Transform2D.Translation(new Vector2D(0, 300))), "Move");
        Assert.True(Hatch(s).Contains(P(508, 8)));

        // A copy of a hatch is a hatch of its own and follows nothing.
        var copy = (HatchEntity)Hatch(s).CopyWithNewId();
        Assert.False(copy.Associative);
        Assert.Empty(copy.SourceIds);
    }

    [Fact]
    public void A_hatch_is_saved_and_read_back_and_moves_scales_and_turns_as_one_object()
    {
        var s = Session();
        Box(s, 0, 0, 10, 10);
        Add(s, new CircleEntity(P(5, 5), 2));
        s.HatchDefaults.Pattern = "BRICK";
        s.HatchDefaults.Scale = 2;
        s.HatchDefaults.ScaleChosen = true;
        s.HatchDefaults.Angle = 15;
        s.HatchDefaults.Islands = HatchIslands.Outer;
        s.HatchDefaults.OriginAtCorner = true;
        var made = s.HatchAt(P(1, 1), out _)!;

        var loaded = HcadSerializer.LoadFromBytes(HcadSerializer.SaveToBytes(s.Document));
        Assert.Empty(loaded.Warnings);
        var read = loaded.Document.Entities.OfType<HatchEntity>().Single();
        Assert.Equal((made.Pattern, made.Scale, made.Angle, made.Origin, made.Islands, made.Associative),
            (read.Pattern, read.Scale, read.Angle, read.Origin, read.Islands, read.Associative));
        Assert.Equal(("BRICK", 2.0, 15.0, P(0, 0), HatchIslands.Outer, true), (read.Pattern, read.Scale, read.Angle, read.Origin, read.Islands, read.Associative));
        Assert.Equal(made.SourceIds, read.SourceIds);
        Assert.Equal(made.Seed, read.Seed);
        Near(made.Area, read.Area);
        Assert.Equal(made.Lines.Count, read.Lines.Count);
        Assert.True(loaded.Document.GetDrawOrder(read.Id) < loaded.Document.GetDrawOrder(read.SourceIds[0]));

        var moved = (HatchEntity)read.Clone();
        moved.Transform(Transform2D.Rotation(Math.PI / 2, P(0, 0)));
        Assert.Equal(105, moved.Angle, 9);
        moved.Transform(Transform2D.Scaling(3, P(0, 0)));
        Assert.Equal(6, moved.Scale, 9);
        Near(read.Area * 9, moved.Area);
        Assert.Equal(read.Lines.Count, moved.Lines.Count);                            // the same picture, larger and turned

        // Properties: pattern, scale and angle can be typed; the area is shown.
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("recovery")));
        vm.Session.LoadDocument(loaded.Document);
        vm.Properties.Dispatch = a => a();
        vm.Session.SelectAt(Point2D.Midpoint(read.Lines[0].A, read.Lines[0].B), 0.01);
        var rows = vm.Properties.Rows;
        Assert.Contains(rows, r => r.Label == "Pattern" && r.Value == "BRICK");
        Assert.Contains(rows, r => r.Label == "Area" && r.Value.Contains("sq in"));
    }

    [Fact]
    public void Hatches_are_drawn_as_lines_clipped_in_viewports_and_written_to_PDF_as_vectors()
    {
        var ui = new FakeUi { PdfPath = _temp.File("hatch.pdf") };
        var vm = new MainViewModel(ui, new RecoveryStore(_temp.File("recovery")));
        var s = vm.Session;
        Box(s, 0, 0, 480, 240);
        var column = Add(s, new CircleEntity(P(240, 120), 40));
        s.HatchAt(P(20, 20), out _);
        s.HatchDefaults.Pattern = HatchPatterns.Solid;
        s.HatchAt(P(240, 120), out _);                                                // the column, filled
        var pattern = s.Document.Entities.OfType<HatchEntity>().Single(h => !h.IsSolid);

        var view = new ViewTransform();
        view.SetViewport(1200, 800);
        view.ZoomExtents(s.Document.GetExtents());
        var screen = new RecordingSurface();
        new SceneRenderer().Render(screen, s.Document, view, new RenderOptions());
        Assert.True(screen.Lines.Count >= pattern.Lines.Count);
        Assert.Single(screen.Regions);

        // Zoomed far out the pattern becomes a tone on screen (a plot still gets lines).
        view.SetView(P(240, 120), 0.05);
        var far = new RecordingSurface();
        new SceneRenderer().Render(far, s.Document, view, new RenderOptions { ShowGrid = false });
        Assert.True(far.Lines.Count < 20);

        vm.RunCommand("PDF");                                                         // Layout 1, through its viewport
        var page = PdfFile.Open(File.ReadAllBytes(ui.PdfPath)).ReadPage(0);
        Assert.Equal((36 * 72.0, 24 * 72.0), (page.Width, page.Height));
        Assert.True(page.Paths.Count > pattern.Lines.Count, $"{page.Paths.Count} paths, {pattern.Lines.Count} lines, dense {pattern.TooDense}, scale {pattern.Scale}");
        Assert.Contains(page.Paths, p => p.Filled);
    }

    /// <summary>Not a check: with HAQQ_PATTERN_SHEET set to a file name, writes every pattern's lines there so a picture can be made of them.</summary>
    [Fact]
    public void Pattern_sheet_can_be_written_for_a_look()
    {
        string? path = Environment.GetEnvironmentVariable("HAQQ_PATTERN_SHEET");
        if (string.IsNullOrEmpty(path)) return;
        var loops = new List<Point2D[]> { new[] { P(0, 0), P(3, 0), P(3, 2), P(0, 2) } };
        var text = new System.Text.StringBuilder();
        foreach (var pattern in HatchPatterns.All)
        {
            text.Append("P ").Append(pattern.Name).Append('|').Append(pattern.Title).Append('\n');
            foreach (var (a, b) in HatchGeometry.Lines(loops, pattern, 1, 0, Point2D.Origin, 200_000, out _))
                text.Append(FormattableString.Invariant($"{a.X:0.####} {a.Y:0.####} {b.X:0.####} {b.Y:0.####}\n"));
        }
        File.WriteAllText(path, text.ToString());
    }

    [Fact]
    public void The_Hatch_window_sets_up_new_hatches_changes_existing_ones_and_keeps_favorites()
    {
        string settings = _temp.File("settings.json");
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("recovery")), new UserSettingsStore(settings));
        var s = vm.Session;
        Guid? asked = Guid.Empty;
        vm.HatchRequested += id => asked = id;
        Box(s, 0, 0, 480, 240);
        var hole = Add(s, new CircleEntity(P(100, 100), 30));
        vm.RunCommand("H");                                                           // the shortcut opens the window
        Assert.Null(asked);

        var window = vm.CreateHatchViewModel();
        Assert.True(window.IsCreating);
        Assert.Equal(("ANSI31", HatchViewModel.AutoScale, "0"), (window.Selected!.Name, window.ScaleText, window.AngleText));
        Assert.Equal(HatchPatterns.All.Count, window.Items.Count);
        Assert.Equal(2, window.Selected.Entities.Count);                              // a sample to show: the hatch and its outline

        window.Filter = "brick";
        Assert.Equal(new[] { "ANSI31", "ANSI35", "BRICK" }, window.Items.Select(i => i.Name).ToArray());   // names and descriptions
        window.Filter = "";
        window.Category = HatchPatterns.CategoryMaterials;
        Assert.Contains(window.Items, i => i.Name == "CONCRETE");
        Assert.DoesNotContain(window.Items, i => i.Name == "ANSI31");
        window.Selected = window.Items.Single(i => i.Name == "CONCRETE");
        window.ToggleFavorite();
        window.Category = HatchViewModel.FavoritesCategory;
        Assert.Equal(new[] { "CONCRETE" }, window.Items.Select(i => i.Name).ToArray());
        Assert.Equal("CONCRETE", window.Selected.Name);                               // filtering does not lose the choice

        window.ScaleText = "nonsense";
        Assert.False(window.PickPoints());
        Assert.Contains("Scale", window.Message);
        window.ScaleText = "12";
        window.StepScale(larger: true);
        Assert.Equal("24", window.ScaleText);
        Assert.Contains("apart", window.SpacingText);
        window.AngleText = "30";
        window.Islands = window.IslandNames[1];
        Assert.True(window.PickPoints());
        Assert.Equal("HATCHPICK", s.ActiveCommand!.Name);
        s.SubmitPoint(P(400, 200));
        s.SubmitEnter();
        var hatch = Hatch(s);
        Assert.Equal(("CONCRETE", 24.0, 30.0, HatchIslands.Outer), (hatch.Pattern, hatch.Scale, hatch.Angle, hatch.Islands));

        // Double-click: the same window on that hatch.
        Assert.True(s.RequestEdit(hatch.Id));
        Assert.Equal(hatch.Id, asked);
        var edit = vm.CreateHatchViewModel(hatch.Id);
        Assert.True(edit.IsEditing && edit.CanFollow);
        Assert.Equal(("CONCRETE", "24", "30"), (edit.Selected!.Name, edit.ScaleText, edit.AngleText));
        Assert.True(edit.Selected.IsFavorite);
        Assert.Contains("sq ft", edit.AreaText);
        edit.Selected = edit.Items.Single(i => i.Name == HatchPatterns.Solid);
        Assert.False(edit.IsPattern);
        edit.Islands = edit.IslandNames[2];                                           // hatch through the opening
        Assert.True(edit.Apply());
        Assert.True(Hatch(s).IsSolid);
        Near(480 * 240, Hatch(s).Area, 1e-9);
        Assert.False(s.Document.ContainsEntity(Guid.Empty));
        s.Undo();
        Assert.Equal("CONCRETE", Hatch(s).Pattern);
        Assert.False(Hatch(s).Contains(hole.Center));

        // Used lately, and the favourite on the next start.
        var next = vm.CreateHatchViewModel();
        next.Category = HatchViewModel.RecentCategory;
        Assert.Equal(new[] { "CONCRETE" }, next.Items.Select(i => i.Name).ToArray());
        var again = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("r2")), new UserSettingsStore(settings));
        Assert.True(again.CreateHatchViewModel().Items.Single(i => i.Name == "CONCRETE").IsFavorite);

        // Object snap near a hatch and grips on it do not fail.
        s.SelectAt(Point2D.Midpoint(Hatch(s).Lines[0].A, Hatch(s).Lines[0].B), 0.5);
        Assert.Empty(EntityGrips.GetGripPoints(Hatch(s)));
        s.UpdateCursor(P(10, 10), 1);
    }
}
