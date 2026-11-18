using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Documents.Pdf;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Presentation;
using Xunit;
using static HAQQ.CAD.Tests.T;

namespace HAQQ.CAD.Tests;

public class PdfReaderTests
{
    [Fact]
    public void Reads_the_lines_curves_pens_and_text_of_a_page()
    {
        var pdf = PdfFile.Open(TestPdfs.Plan);
        Assert.Equal(2, pdf.PageCount);
        var page = pdf.ReadPage(0);
        Assert.Equal((792.0, 612.0), (page.Width, page.Height));
        Assert.Equal(5, page.Paths.Count);
        Assert.Equal(0, page.ImageCount);
        Assert.False(page.LooksScanned);

        var room = page.Paths[0];                                                     // a rectangle drawn with a 1.5 pt pen
        Assert.True(room.Stroked && !room.Filled && !room.Dashed);
        Assert.Equal(1.5, room.LineWidth, 9);
        Assert.True(room.Subpaths.Single().Closed);
        Assert.Equal(4, room.Subpaths[0].Segments.Count);
        Assert.Equal((72.0, 72.0, 360.0, 72.0), (room.Subpaths[0].Segments[0].X1, room.Subpaths[0].Segments[0].Y1, room.Subpaths[0].Segments[0].X2, room.Subpaths[0].Segments[0].Y2));

        Assert.True(page.Paths[2].Dashed);                                            // the dashed line
        Assert.Equal(4, page.Paths[3].Subpaths[0].Segments.Count(s => s.IsCurve));    // a circle is four curves in a PDF
        Assert.Equal((255, 0, 0), (page.Paths[4].R, page.Paths[4].G, page.Paths[4].B));

        Assert.Equal(new[] { "SCALE: 1/4\" = 1'-0\"", "FLOOR PLAN", "NORTH WALL" }, page.Texts.Select(t => t.Text).ToArray());
        var title = page.Texts[1];
        Assert.Equal((300.0, 560.0, 18.0, 0.0), (title.X, title.Y, title.Height, title.Angle));
        Assert.Equal(Math.PI / 2, page.Texts[2].Angle, 6);                            // text turned to read upwards

        Assert.Single(pdf.ReadPage(1).Paths);
        Assert.Throws<PdfReadException>(() => pdf.ReadPage(2));
    }

    [Fact]
    public void Reads_files_written_in_other_ways_the_same()
    {
        // Objects packed into object streams (how most programs write PDFs today).
        var packed = PdfFile.Open(TestPdfs.PlanObjectStreams).ReadPage(0);
        Assert.Equal((5, 3), (packed.Paths.Count, packed.Texts.Count));
        Assert.Equal("FLOOR PLAN", packed.Texts[1].Text);

        // A page that is stored turned a quarter: it comes out upright, 612 wide and 792 high.
        var turned = PdfFile.Open(TestPdfs.PlanRotated).ReadPage(0);
        Assert.Equal((612.0, 792.0), (turned.Width, turned.Height));
        var side = turned.Paths[0].Subpaths[0].Segments[0];
        Assert.Equal((72.0, 720.0, 72.0, 432.0), (Math.Round(side.X1, 6), Math.Round(side.Y1, 6), Math.Round(side.X2, 6), Math.Round(side.Y2, 6)));
        Assert.Equal(0.0, turned.Texts[2].Angle, 6);                                  // the text that read upwards now reads across

        // Matplotlib: its own kind of fonts; a white background and two lines.
        var plot = PdfFile.Open(TestPdfs.Matplotlib).ReadPage(0);
        Assert.Equal("MATPLOT TEXT", plot.Texts.Single().Text);
        Assert.Equal(2, plot.Paths.Count(p => p.Stroked));
        Assert.Single(plot.Paths.Where(p => p.Dashed));

        // LibreOffice Draw: two-byte fonts with a letter table.
        var draw = PdfFile.Open(TestPdfs.LibreOffice).ReadPage(0);
        Assert.Equal("ROOM 101", draw.Texts.Single().Text);
        Assert.Equal(3, draw.Paths.Count(p => p.Stroked));
    }

    [Fact]
    public void Says_plainly_why_a_file_cannot_be_used()
    {
        var locked = Assert.Throws<PdfReadException>(() => PdfFile.Open(TestPdfs.PlanLocked));
        Assert.Contains("locked", locked.Message);
        Assert.Contains("not a PDF", Assert.Throws<PdfReadException>(() => PdfFile.Open(System.Text.Encoding.ASCII.GetBytes("Just some text, long enough to look at."))).Message);
        Assert.Throws<PdfReadException>(() => PdfFile.Open(Array.Empty<byte>()));
        // A file cut off in the middle still gives what it holds, or a plain refusal - never a crash.
        var cut = TestPdfs.Plan[..(TestPdfs.Plan.Length / 2)];
        try { PdfFile.Open(cut).ReadPage(0); }
        catch (PdfReadException) { }
    }
}

public class DrawingCopyTests
{
    private static CopyResult Copy(byte[] pdf, Action<CopyOptions>? set = null, int page = 0)
    {
        var options = new CopyOptions();
        set?.Invoke(options);
        return DrawingCopy.Convert(PdfFile.Open(pdf).ReadPage(page), options);
    }

    [Fact]
    public void A_page_becomes_lines_polylines_circles_arcs_and_text_at_the_written_scale()
    {
        var first = Copy(TestPdfs.Plan);
        Assert.Equal("1/4\" = 1'-0\"", first.ScaleText);                              // found written on the sheet
        Assert.Equal(1 / 48.0, first.Scale!.Value, 12);

        double k = DrawingCopy.UnitsPerPoint(first.Scale.Value, LinearUnit.Inch);
        Assert.Equal(48.0 / 72.0, k, 12);
        var copy = Copy(TestPdfs.Plan, o => o.UnitsPerPoint = k);

        // The room is 4 x 3 inches on paper: 16 ft x 12 ft for real.
        var room = copy.Included.OfType<PolylineEntity>().Single();
        Assert.True(room.IsClosed);
        Assert.Equal(new BoundingBox(48, 48, 240, 192), room.Bounds);
        Assert.Equal((192.0, 144.0), (room.Bounds.Width, room.Bounds.Height));

        var circle = copy.Included.OfType<CircleEntity>().Single();                   // four curves taken back to a circle
        Assert.True(circle.Center.NearlyEquals(P(500 * k, 300 * k), 0.01));       // a PDF curve is itself only close to a circle
        Assert.Equal(36 * k, circle.Radius, 2);
        var arc = copy.Included.OfType<ArcEntity>().Single();                         // a quarter circle
        Assert.Equal(36 * k, arc.Radius, 2);
        Assert.Equal(Math.PI / 2, arc.Sweep, 3);
        Assert.Equal(2, copy.Included.OfType<LineEntity>().Count());
        Assert.Equal(3, copy.Included.OfType<TextEntity>().Count());
        var title = copy.Included.OfType<TextEntity>().Single(t => t.Text == "FLOOR PLAN");
        Assert.Equal((TextJustify.Left, 18 * 0.7 * k), (title.Justify, title.Height));
        Assert.Equal(300 * k, title.Position.X, 9);

        // Sorted by pen, each group for its own layer; black ink shows white on the dark background.
        var byKey = copy.Groups.ToDictionary(g => g.Key);
        Assert.Equal(new[] { "dashed||black", "line|Heavy|black", "line|Medium|#FF0000", "line|Medium|black", "text||black" }, byKey.Keys.OrderBy(x => x).ToArray());
        Assert.Equal(("Copy Heavy", 1), (byKey["line|Heavy|black"].LayerName, byKey["line|Heavy|black"].Entities.Count));
        Assert.Equal((Linetypes.Dash, "Copy Dashed"), (byKey["dashed||black"].Linetype, byKey["dashed||black"].LayerName));
        Assert.Equal(CadColor.FromRgb(255, 255, 255), byKey["line|Heavy|black"].Color);
        Assert.Equal(CadColor.FromRgb(255, 0, 0), byKey["line|Medium|#FF0000"].Color);
        Assert.Empty(copy.Notes);
    }

    [Fact]
    public void Options_leave_out_text_fills_and_everything_outside_a_region()
    {
        Assert.Empty(Copy(TestPdfs.Plan, o => o.IncludeText = false).Included.OfType<TextEntity>());

        // LibreOffice paints each shape twice: a filled area and its outline. The page's white background is never copied.
        var both = Copy(TestPdfs.LibreOffice);
        Assert.Contains(both.Groups, g => g.Key.StartsWith("fill"));
        Assert.DoesNotContain(both.Included, e => e.Bounds.Width > 590 / 72.0);
        var noFills = Copy(TestPdfs.LibreOffice, o => o.IncludeFills = false);
        Assert.DoesNotContain(noFills.Groups, g => g.Key.StartsWith("fill"));
        Assert.Single(noFills.Included.OfType<CircleEntity>());                       // the circle, from its curves

        // Only the room: a part of the page chosen in points.
        var part = Copy(TestPdfs.Plan, o => o.Region = new BoundingBox(60, 60, 370, 300));
        Assert.Equal(3, part.Included.Count());                                       // the room, the line across it, the dashed line
        Assert.Empty(part.Included.OfType<CircleEntity>());
    }

    [Fact]
    public void Clean_up_straightens_merges_removes_doubles_and_joins_within_the_tolerance()
    {
        Guid layer = Guid.NewGuid();
        LineEntity L(double x1, double y1, double x2, double y2) => new(P(x1, y1), P(x2, y2)) { LayerId = layer };
        var messy = new List<Entity>
        {
            L(0, 0, 50, 0.02),                                                        // a hair off horizontal
            L(50, 0, 100, 0),                                                         // carries on along the same line
            L(100, 0, 100, 60),                                                       // a corner
            L(100, 60, 0, 60),
            L(0, 60, 0, 0),                                                           // back to the start: a closed shape
            L(0, 60, 0, 0),                                                           // the same line drawn twice
            L(200, 200, 200.001, 200),                                                // a speck
            L(300, 0, 340, 30),                                                       // a sloping line stays sloping
            new CircleEntity(P(50, 30), 10) { LayerId = layer },
            new CircleEntity(P(50, 30), 10) { LayerId = layer },                      // doubled
        };
        var clean = GeometryCleanup.Clean(messy, tolerance: 0.05, angleToleranceDegrees: 0.5, join: true, out var report);
        Assert.Equal((1, 2, 1, 1, 4), (report.Straightened, report.DuplicatesRemoved, report.Merged, report.TinyRemoved, report.Joined));

        var outline = clean.OfType<PolylineEntity>().Single();
        Assert.True(outline.IsClosed);
        Assert.Equal(4, outline.Vertices.Count);                                      // the two pieces of the bottom edge became one side
        Assert.Equal(new BoundingBox(0, 0.01, 100, 60), outline.Bounds);              // moved by a hundredth, inside the tolerance
        Assert.Equal(layer, outline.LayerId);
        var slope = clean.OfType<LineEntity>().Single();
        Assert.Equal((P(300, 0), P(340, 30)), (slope.Start, slope.End));
        Assert.Single(clean.OfType<CircleEntity>());
        Assert.Equal(3, clean.Count);

        // With a tolerance too small for it, the off-line stays as drawn, and nothing is joined when not asked.
        var strict = GeometryCleanup.Clean(messy, 0.001, 0.5, join: false, out var little);
        Assert.Equal(0, little.Straightened);
        Assert.Empty(strict.OfType<PolylineEntity>());
        Assert.False(new CleanupReport(0, 0, 0, 0, 0).ChangedAnything);
    }

    [Fact]
    public void Clean_copy_of_a_page_reports_what_it_changed_and_exact_copy_changes_nothing()
    {
        var exact = Copy(TestPdfs.LibreOffice);
        Assert.Null(exact.Cleanup);
        var clean = Copy(TestPdfs.LibreOffice, o => { o.Clean = true; o.TolerancePoints = 0.5; });
        Assert.NotNull(clean.Cleanup);
        Assert.True(clean.Included.Count() <= exact.Included.Count());
        Assert.Equal(exact.Included.OfType<TextEntity>().Single().Text, clean.Included.OfType<TextEntity>().Single().Text);
    }

    [Fact]
    public void Scales_written_in_other_ways_are_found_and_metric_drawings_get_metric_sizes()
    {
        PdfPageContent Page(params string[] texts)
        {
            var page = new PdfPageContent { Width = 612, Height = 792 };
            double y = 700;
            foreach (string t in texts) page.Texts.Add(new PdfText(t, 50, y -= 30, 10, 0, 100));
            return page;
        }
        double? Found(params string[] texts) => DrawingCopy.Convert(Page(texts), new CopyOptions()).Scale;

        Assert.Equal(1 / 96.0, Found("SCALE 1/8\" = 1'-0\"")!.Value, 12);
        Assert.Equal(1 / 240.0, Found("1\" = 20'")!.Value, 12);                       // an engineer's scale
        Assert.Equal(1 / 8.0, Found("DETAIL 3   1 1/2\" = 1'-0\"")!.Value, 12);
        Assert.Equal(1 / 50.0, Found("Scale 1:50")!.Value, 12);
        Assert.Null(Found("GRID 1:2 MIX", "12:30"));                                  // a ratio without the word scale is not taken
        Assert.Null(Found("NOT TO SCALE"));
        var two = DrawingCopy.Convert(Page("SCALE 1/4\" = 1'-0\"", "SCALE 1/2\" = 1'-0\""), new CopyOptions());
        Assert.Equal(1 / 48.0, two.Scale!.Value, 12);
        Assert.Contains(two.Notes, n => n.Contains("More than one scale"));

        // 1:50 in a millimetre drawing: a point of paper is 25.4 / 72 mm, times 50.
        Assert.Equal(25.4 / 72 * 50, DrawingCopy.UnitsPerPoint(1 / 50.0, LinearUnit.Millimeter), 9);

        var scan = new PdfPageContent { Width = 612, Height = 792, ImageCount = 1 };
        Assert.True(scan.LooksScanned);
        Assert.Contains(DrawingCopy.Convert(scan, new CopyOptions()).Notes, n => n.Contains("scan"));
    }
}

public class DrawingCopyWorkflowTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        return s;
    }

    [Fact]
    public void A_pdf_page_is_reviewed_placed_on_its_layers_and_can_be_moved_and_undone()
    {
        var s = Session();
        string? asked = null;
        s.ViewCommandRequested += name => asked = name;
        s.StartCommand("COPYPDF");
        Assert.Equal("CopyPdf", asked);

        var vm = new DrawingCopyViewModel(s, new DrawingCopyMemoryStore(_temp.File("copy.json")));
        Assert.False(vm.CanPlace);
        Assert.False(vm.Open(new byte[100], "nothing.pdf"));
        Assert.Contains("not a PDF", vm.Message);
        Assert.False(vm.Open(TestPdfs.PlanLocked, "locked.pdf"));
        Assert.Contains("locked", vm.Message);

        Assert.True(vm.Open(TestPdfs.Plan, @"C:\Jobs\Plan A1.pdf"));
        Assert.Equal(("Plan A1.pdf", 2, 0), (vm.FileName, vm.Pages.Count, vm.PageIndex));
        Assert.Equal("1/4\" = 1'-0\"", vm.ScaleText);                                 // read off the sheet
        Assert.Contains("written on the sheet", vm.ScaleHint);
        Assert.Contains("11 x 8.5 in", vm.PageInfo);
        Assert.Contains("2 lines, 1 polylines, 1 arcs, 1 circles, 3 texts", vm.Summary);
        Assert.Equal(8, vm.Preview.Count);
        Assert.Equal(5, vm.Groups.Count);

        // Leave the text out and send the heavy lines to a layer of my own.
        vm.Groups.Single(g => g.Group.Key == "text||black").Include = false;
        vm.Groups.Single(g => g.Group.Key == "line|Heavy|black").LayerName = "A-WALL";
        Assert.Contains("0 texts", vm.Summary);
        Assert.Equal(5, vm.Preview.Count);
        vm.ScaleText = "one to fifty";
        Assert.False(vm.CanPlace);
        Assert.Contains("not a scale", vm.Message);
        vm.ScaleText = "1/4\"=1'";
        Assert.True(vm.CanPlace);
        Assert.True(vm.Place());

        // It follows the cursor by its lower-left corner; a click puts it down.
        Assert.IsType<PlaceCopyCommand>(s.ActiveCommand);
        Assert.True(s.ActiveCommand!.GetPreview(P(1000, 1000)).Count >= 5);
        s.SubmitPoint(P(1000, 2000));
        Assert.Null(s.ActiveCommand);
        var doc = s.Document;
        Assert.Equal(5, doc.EntityCount);
        Assert.Equal(5, s.Selection.Count);                                           // selected, ready to move
        var wall = doc.Entities.OfType<PolylineEntity>().Single();
        Assert.Equal("A-WALL", doc.GetLayer(wall.LayerId)!.Name);
        Assert.Equal(new BoundingBox(1000, 2000, 1192, 2144), wall.Bounds);           // the 16 ft x 12 ft room, corner on the click
        var dashed = doc.FindLayer("Copy Dashed")!;
        Assert.Equal(Linetypes.Dash, dashed.Linetype);
        Assert.Equal(CadColor.FromRgb(255, 0, 0), doc.FindLayer("Copy Medium #FF0000")!.Color);
        Assert.Null(doc.FindLayer("Copy Text"));

        s.StartCommand("MOVE");                                                       // "move the model at the end"
        s.SubmitPoint(P(1000, 2000));
        s.SubmitPoint(P(0, 0));
        Assert.Equal(new BoundingBox(0, 0, 192, 144), doc.Entities.OfType<PolylineEntity>().Single().Bounds);

        s.Undo();                                                                     // the move
        s.Undo();                                                                     // the whole copy, layers and all
        Assert.Equal(0, doc.EntityCount);
        Assert.Null(doc.FindLayer("A-WALL"));
    }

    [Fact]
    public void Choices_and_a_calibration_are_remembered_for_the_next_copy()
    {
        var store = new DrawingCopyMemoryStore(_temp.File("copy.json"));
        using var temp = new TempDirectory();
        var main = new MainViewModel(new FakeUi(), new HAQQ.CAD.Documents.RecoveryStore(temp.File("recovery"))) { CopyMemory = store };
        var s = main.Session;
        s.Document.Settings.Format = LengthFormat.Decimal;

        var vm = new DrawingCopyViewModel(s, store);
        vm.Open(TestPdfs.Plan, "plan.pdf");
        vm.ScaleText = "";                                                            // no scale: paper size, to be calibrated
        vm.Clean = true;
        vm.Groups.Single(g => g.Group.Key == "line|Heavy|black").LayerName = "A-WALL";
        vm.Groups.Single(g => g.Group.Key == "text||black").Include = false;
        Assert.True(vm.Place());
        s.SubmitPoint(P(0, 0));
        var room = s.Document.Entities.OfType<PolylineEntity>().Single();
        Assert.Equal(4.0, room.Bounds.Width, 9);                                      // 4 inches of paper

        // The long side of the room is really 16 ft.
        s.StartCommand("CALIBRATE");
        Assert.Contains("5 object(s)", s.Prompt);                                     // the copy is taken by itself
        s.SubmitPoint(P(room.Bounds.MinX, room.Bounds.MinY));
        s.SubmitPoint(P(room.Bounds.MaxX, room.Bounds.MinY));
        Assert.Contains("4.0000", s.Prompt);
        s.SubmitText("16'");
        Assert.Null(s.ActiveCommand);
        room = s.Document.Entities.OfType<PolylineEntity>().Single();
        Assert.Equal((192.0, 144.0), (Math.Round(room.Bounds.Width, 9), Math.Round(room.Bounds.Height, 9)));
        Assert.Equal(P(0, 0), P(Math.Round(room.Bounds.MinX, 9), Math.Round(room.Bounds.MinY, 9)));   // scaled about the first point

        // Next time, on any drawing: the same page comes in at that size, on my layers, tidied, without the text.
        var next = new DrawingCopyViewModel(Session(), store);
        next.Open(TestPdfs.Plan, "PLAN.PDF");
        Assert.Contains("before", next.ScaleHint);
        Assert.True(next.Clean);
        Assert.Equal("A-WALL", next.Groups.Single(g => g.Group.Key == "line|Heavy|black").LayerName);
        Assert.False(next.Groups.Single(g => g.Group.Key == "text||black").Include);
        Assert.Contains("the way you named them", next.Notes);
        Assert.Contains("309.3333 x 176.0000", next.Summary);                        // 48 times the paper, from the calibration

        // Another file starts from what is written on its own sheet, but keeps the layer names.
        var other = new DrawingCopyViewModel(Session(), store);
        other.Open(TestPdfs.PlanObjectStreams, "other.pdf");
        Assert.Equal("1/4\" = 1'-0\"", other.ScaleText);
        Assert.Equal("A-WALL", other.Groups.Single(g => g.Group.Key == "line|Heavy|black").LayerName);
    }

    [Fact]
    public void Cleanup_command_tidies_selected_lines_as_one_undo_step()
    {
        var s = Session();
        AddLine(s.Document, 0, 0, 50, 0.02);
        AddLine(s.Document, 50, 0, 100, 0);
        AddLine(s.Document, 100, 0, 100, 60);
        AddLine(s.Document, 100, 0, 100, 60);                                         // doubled
        s.SelectAll();
        s.StartCommand("CLEANUP");
        Assert.Contains("Tolerance", s.Prompt);
        s.SubmitText("0.05");
        Assert.Null(s.ActiveCommand);
        var corner = s.Document.Entities.OfType<PolylineEntity>().Single();           // two sides, joined
        Assert.Equal(3, corner.Vertices.Count);
        Assert.Equal(1, s.Document.EntityCount);
        Assert.Equal(0.05, s.CleanupTolerance);
        s.Undo();
        Assert.Equal(4, s.Document.Entities.OfType<LineEntity>().Count());

        s.SelectAll();
        s.StartCommand("CLEANUP");
        s.SubmitText("0");                                                            // nothing may move: the exact double goes, and
        Assert.Equal(2, s.Document.EntityCount);                                      // only ends that touch exactly are joined
        Assert.Equal((0.0, 0.02), (s.Document.Entities.OfType<LineEntity>().Single().Start.Y, s.Document.Entities.OfType<LineEntity>().Single().End.Y));
    }
}

