using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Documents;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Presentation;
using HAQQ.CAD.Rendering;
using Xunit;
using static HAQQ.CAD.Tests.T;

namespace HAQQ.CAD.Tests;

public class TextTests
{
    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        return s;
    }

    private static TextEntity Text(EditorSession s) => s.Document.Entities.OfType<TextEntity>().Single();

    [Fact]
    public void Text_window_places_a_text_with_font_height_and_style_by_a_click()
    {
        var s = Session();
        string? asked = null;
        s.ViewCommandRequested += name => asked = name;
        s.StartCommand("T");
        Assert.Equal("Text", asked);                                                  // the window is asked for

        var vm = new TextEditorViewModel(s);
        Assert.False(vm.IsEditing);
        Assert.Equal("Arial", vm.Font);
        Assert.False(vm.Commit());                                                    // nothing typed yet
        Assert.NotEqual("", vm.Message);
        vm.Text = "GENERAL NOTES\r\n1. ALL REBAR GRADE 60";
        vm.HeightText = "6";
        vm.Font = "Calibri";
        vm.Bold = true;
        vm.Underline = true;
        vm.RotationText = "90";
        vm.HeightText = "tall";
        Assert.False(vm.Commit());
        vm.HeightText = "6";
        Assert.True(vm.Commit());

        Assert.NotNull(s.ActiveCommand);                                              // it follows the cursor now
        Assert.IsType<TextEntity>(s.ActiveCommand!.GetPreview(P(3, 3)).Single());
        s.SubmitPoint(P(100, 50));
        Assert.Null(s.ActiveCommand);
        var text = Text(s);
        Assert.Equal(new[] { "GENERAL NOTES", "1. ALL REBAR GRADE 60" }, text.Lines);
        Assert.Equal((6.0, "Calibri", true, false, true), (text.Height, text.Font, text.Bold, text.Italic, text.Underline));
        Assert.Equal(TextJustify.Left, text.Justify);
        Assert.Equal(Math.PI / 2, text.Angle, 12);
        Assert.Equal(P(100, 50), text.Position);
        Assert.Equal(s.Document.CurrentLayerId, text.LayerId);

        // The next text starts with the same settings.
        var next = new TextEditorViewModel(s);
        Assert.Equal(("6.0000", "Calibri", true, "90"), (next.HeightText, next.Font, next.Bold, next.RotationText));
        s.Undo();
        Assert.Empty(s.Document.Entities);
    }

    [Fact]
    public void A_text_is_edited_through_the_same_window_and_keeps_its_place()
    {
        var s = Session();
        Assert.True(s.BeginTextPlacement("SECTION A-A", new TextSettings(8, "Arial", false, false, false, TextJustify.Center, 0)));
        s.SubmitPoint(P(10, 10));
        var id = Text(s).Id;

        Guid? opened = null;
        s.ObjectEditRequested += x => opened = x;
        s.StartCommand("TEXTEDIT");
        s.SubmitPoint(P(10, 10));                                                     // anywhere on the text
        Assert.Equal(id, opened);
        Assert.Null(s.ActiveCommand);

        var vm = new TextEditorViewModel(s, id);
        Assert.True(vm.IsEditing);
        Assert.Equal(("SECTION A-A", "8.0000", "Center"), (vm.Text, vm.HeightText, vm.Justify));
        vm.Text = "SECTION B-B";
        vm.Italic = true;
        vm.HeightText = "10";
        Assert.True(vm.Commit());
        Assert.Null(s.ActiveCommand);                                                 // saved, not placed again
        var text = Text(s);
        Assert.Equal(("SECTION B-B", 10.0, true, P(10, 10)), (text.Text, text.Height, text.Italic, text.Position));
        s.Undo();
        Assert.Equal("SECTION A-A", Text(s).Text);

        // A locked layer is not opened.
        opened = null;
        s.Document.GetLayer(Text(s).LayerId)!.IsLocked = true;
        Assert.False(s.RequestEdit(id));
        Assert.Null(opened);
    }

    [Fact]
    public void Text_can_be_typed_on_the_command_line_too()
    {
        var s = Session();
        s.StartCommand("-TEXT");
        s.SubmitPoint(P(0, 0));
        s.SubmitText("5");                                                            // the height
        s.SubmitText("TYP. EACH SIDE");                                               // the words, spaces and all
        Assert.Null(s.ActiveCommand);
        var text = Text(s);
        Assert.Equal(("TYP. EACH SIDE", 5.0, TextJustify.Left), (text.Text, text.Height, text.Justify));
        Assert.Equal(5.0, s.TextDefaults.Height);
    }

    [Fact]
    public void Text_box_follows_the_lines_the_side_and_the_angle()
    {
        // Without a font measurer a letter is taken as 0.6 of the height wide.
        var left = new TextEntity(P(10, 10), 10, "ABCDE\nAB") { Justify = TextJustify.Left };
        Assert.Equal(30.0, left.Width, 9);
        Assert.Equal(new BoundingBox(10, 10 - 16 - 5, 40, 15), left.Bounds);          // two lines, 1.6 heights apart
        Assert.Equal(P(10, -6), left.LineAnchor(1));
        Assert.Equal(0.0, left.DistanceTo(P(25, 0)));                                 // anywhere on it picks it
        Assert.Equal(5.0, left.DistanceTo(P(45, 10)), 9);
        Assert.True(left.IntersectsBox(new BoundingBox(20, 5, 21, 6)));               // a small window inside it
        Assert.False(left.IntersectsBox(new BoundingBox(50, 50, 60, 60)));

        var right = new TextEntity(P(10, 10), 10, "ABCDE") { Justify = TextJustify.Right };
        Assert.Equal(new BoundingBox(-20, 5, 10, 15), right.Bounds);

        var up = new TextEntity(P(0, 0), 10, "ABCDE") { Justify = TextJustify.Left, Angle = Math.PI / 2 };
        var b = up.Bounds;
        Assert.Equal((-5.0, 0.0, 5.0, 30.0), (Math.Round(b.MinX, 9), Math.Round(b.MinY, 9), Math.Round(b.MaxX, 9), Math.Round(b.MaxY, 9)));

        // A measurer supplied by the application is used instead of the estimate.
        try
        {
            TextMeasure.WidthPerHeight = (text, font, bold, italic) => text.Length * (bold ? 2.0 : 1.0);
            Assert.Equal(50.0, new TextEntity(P(0, 0), 10, "ABCDE").Width, 9);
            Assert.Equal(100.0, new TextEntity(P(0, 0), 10, "ABCDE") { Bold = true }.Width, 9);
        }
        finally
        {
            TextMeasure.WidthPerHeight = null;
        }

        Assert.False(new TextEntity(P(0, 0), 10, "A\tB").IsValid);                    // a tab cannot be drawn
        Assert.False(new TextEntity(P(0, 0), 10, "A") { Font = " " }.IsValid);
        var scaled = (TextEntity)left.Clone();
        scaled.Transform(Transform2D.Scaling(2, P(10, 10)));
        Assert.Equal((20.0, "Arial", TextJustify.Left), (scaled.Height, scaled.Font, scaled.Justify));
    }

    [Fact]
    public void Renderer_draws_each_line_in_its_font_and_text_is_saved()
    {
        var s = Session();
        s.BeginTextPlacement("FIRST\nSECOND", new TextSettings(10, "Times New Roman", true, true, false, TextJustify.Right, 0));
        s.SubmitPoint(P(0, 0));

        var view = new ViewTransform();
        view.SetViewport(800, 600);
        view.SetView(P(0, 0), 2);
        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, s.Document, view, new RenderOptions { ShowGrid = false });
        Assert.Equal(new[] { "FIRST", "SECOND" }, surface.StyledTexts.Select(t => t.Text).ToArray());
        var first = surface.StyledTexts[0];
        Assert.Equal((400.0, 300.0, 20.0), (first.X, first.Y, first.Height));
        Assert.Equal(new TextLook("Times New Roman", true, true, false, 1), first.Look);   // anchored at its right end
        Assert.Equal(300 + 16 * 2.0, surface.StyledTexts[1].Y, 9);                    // the second line is below

        view.SetView(P(0, 0), 0.1);                                                   // too small to read: strokes instead
        var far = new RecordingSurface();
        new SceneRenderer().Render(far, s.Document, view, new RenderOptions { ShowGrid = false });
        Assert.Empty(far.StyledTexts);
        Assert.Equal(2, far.Lines.Count);

        var loaded = HcadSerializer.LoadFromBytes(HcadSerializer.SaveToBytes(s.Document));
        Assert.Empty(loaded.Warnings);
        var text = loaded.Document.Entities.OfType<TextEntity>().Single();
        Assert.Equal(("FIRST\nSECOND", "Times New Roman", true, true, false, TextJustify.Right),
            (text.Text, text.Font, text.Bold, text.Italic, text.Underline, text.Justify));
    }

    [Fact]
    public void Properties_show_and_change_a_text()
    {
        using var temp = new TempDirectory();
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(temp.File("recovery")));
        var s = vm.Session;
        vm.Properties.Dispatch = a => a();
        s.BeginTextPlacement("NOTE", new TextSettings(10, "Arial", false, false, false, TextJustify.Left, 0));
        s.SubmitPoint(P(0, 0));
        s.SelectAt(P(5, 0), 1);
        PropertyRow Row(string label) => vm.Properties.Rows.Single(r => r.Label == label);
        Assert.Equal("NOTE", Row("Text").Value);
        Row("Bold").Value = "yes";
        Assert.True(Text(s).Bold);
        Row("Font").Value = "Verdana";
        Row("Angle").Value = "45";
        Assert.Equal("Verdana", Text(s).Font);
        Assert.Equal(Math.PI / 4, Text(s).Angle, 12);
        Row("Justify").Value = "middle";                                              // not a side
        Assert.Equal(TextJustify.Left, Text(s).Justify);
    }
}

public class TableTests
{
    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        return s;
    }

    private static TableEntity Table(EditorSession s) => s.Document.Entities.OfType<TableEntity>().Single();

    [Fact]
    public void Table_window_builds_a_table_and_places_it_by_a_click()
    {
        var s = Session();
        string? asked = null;
        s.ViewCommandRequested += name => asked = name;
        s.StartCommand("TABLE");
        Assert.Equal("Table", asked);

        var vm = new TableEditorViewModel(s);
        Assert.Equal((4, 3), (vm.Rows.Count, vm.Columns.Count));                      // a start to change
        int relaid = 0;
        vm.StructureChanged += (_, _) => relaid++;
        vm.RowCountText = "3";
        vm.ColumnCountText = "4";
        Assert.True(vm.ApplySize());
        Assert.Equal((3, 4, 1), (vm.Rows.Count, vm.Columns.Count, relaid));
        vm.RowCountText = "0";
        Assert.False(vm.ApplySize());
        Assert.Contains("Rows", vm.Message);
        vm.RowCountText = "3";

        vm.Title = "BAR LIST";
        vm.TextHeightText = "4";
        vm.RowHeightText = "10";
        string[] head = { "MARK", "SIZE", "QTY", "LENGTH" };
        for (int c = 0; c < 4; c++) { vm.Rows[0].Cells[c].Text = head[c]; vm.Columns[c].WidthText = c == 3 ? "60" : "40"; }
        vm.Rows[1].Cells[0].Text = "A1";
        vm.Rows[1].Cells[3].Text = "12'-6\"";
        vm.Columns[1].WidthText = "wide";
        Assert.False(vm.Commit());
        Assert.Contains("column 2", vm.Message);
        vm.Columns[1].WidthText = "40";
        vm.RowHeightText = "4";                                                       // too low for the text
        Assert.False(vm.Commit());
        vm.RowHeightText = "10";
        Assert.True(vm.Commit());

        Assert.IsType<TableEntity>(s.ActiveCommand!.GetPreview(P(1, 1)).Single());
        s.SubmitPoint(P(100, 200));
        var table = Table(s);
        Assert.Equal(P(100, 200), table.Position);
        Assert.Equal((3, 4, "BAR LIST", true), (table.RowCount, table.ColumnCount, table.Title, table.HeaderRow));
        Assert.Equal(new[] { 40.0, 40.0, 40.0, 60.0 }, table.ColumnWidths);
        Assert.Equal(180.0, table.Width);
        Assert.Equal(12.0 + 30.0, table.Height);                                      // the title row is three text heights
        Assert.Equal("12'-6\"", table.GetCell(1, 3));
        Assert.Equal(new BoundingBox(220, 168, 280, 178), table.CellBox(1, 3));
        Assert.Equal((1, 3), table.CellAt(P(250, 170)));
        Assert.Equal((-1, -1), table.CellAt(P(150, 195)));                            // on the title
        Assert.Equal(0.0, table.DistanceTo(P(150, 180)));                             // a click in a cell picks it
    }

    [Fact]
    public void A_table_is_edited_rows_and_columns_come_and_go_and_undo_restores_it()
    {
        var s = Session();
        var made = new TableEntity(P(0, 0), 3, 2, 30, 10, 4);
        made.SetCell(0, 0, "ITEM"); made.SetCell(0, 1, "QTY");
        made.SetCell(1, 0, "SINK"); made.SetCell(1, 1, "2");
        made.SetCell(2, 0, "FAUCET"); made.SetCell(2, 1, "2");
        made.SetRowHeight(2, 14);                                                     // one row was stretched
        Assert.True(s.BeginTablePlacement(made));
        s.SubmitPoint(P(0, 0));
        var id = Table(s).Id;

        Guid? opened = null;
        s.ObjectEditRequested += x => opened = x;
        s.SelectAt(P(10, -5), 1);
        s.StartCommand("TEXTEDIT");                                                   // the selected table opens straight away
        Assert.Equal(id, opened);

        var vm = new TableEditorViewModel(s, id);
        Assert.True(vm.IsEditing);
        Assert.Equal(TableEditorViewModel.Varies, vm.RowHeightText);
        Assert.True(vm.InsertRow(2));                                                 // between SINK and FAUCET
        vm.Rows[2].Cells[0].Text = "TOILET";
        vm.Rows[2].Cells[1].Text = "1";
        Assert.True(vm.InsertColumn(1));
        vm.Rows[0].Cells[1].Text = "MODEL";
        Assert.True(vm.DeleteRow(1));                                                 // SINK goes
        Assert.Equal(("3", "3"), (vm.RowCountText, vm.ColumnCountText));
        Assert.True(vm.Commit());

        var table = Table(s);
        Assert.Equal((3, 3), (table.RowCount, table.ColumnCount));
        Assert.Equal(new[] { "ITEM", "MODEL", "QTY" }, Enumerable.Range(0, 3).Select(c => table.GetCell(0, c)).ToArray());
        Assert.Equal(new[] { "TOILET", "", "1" }, Enumerable.Range(0, 3).Select(c => table.GetCell(1, c)).ToArray());
        Assert.Equal("FAUCET", table.GetCell(2, 0));
        Assert.Equal(new[] { 10.0, 10.0, 14.0 }, table.RowHeights);                   // "Varies" kept each row's height
        Assert.Equal(P(0, 0), table.Position);

        s.Undo();
        table = Table(s);
        Assert.Equal((3, 2, "SINK"), (table.RowCount, table.ColumnCount, table.GetCell(1, 0)));

        var one = new TableEditorViewModel(s, id);
        Assert.True(one.DeleteRow(0) && one.DeleteRow(0));
        Assert.False(one.DeleteRow(0));                                               // the last row stays
        Assert.True(one.DeleteColumn(0));
        Assert.False(one.DeleteColumn(0));
    }

    [Fact]
    public void Table_grips_move_it_and_resize_columns_and_rows()
    {
        var table = new TableEntity(P(0, 100), 2, 2, 30, 10, 4) { Title = "T" };      // title 12 high
        var grips = EntityGrips.GetGripPoints(table).ToList();
        Assert.Equal(new[] { P(0, 100), P(30, 88), P(60, 88), P(0, 78), P(0, 68) }, grips.ToArray());
        Assert.True(EntityGrips.Stretch(table, 1, P(45, 0)));                         // the first column wider
        Assert.Equal(new[] { 45.0, 30.0 }, table.ColumnWidths);
        Assert.True(EntityGrips.Stretch(table, 4, P(0, 60)));                         // the second row taller
        Assert.Equal(new[] { 10.0, 18.0 }, table.RowHeights);
        Assert.False(EntityGrips.Stretch(table, 3, P(0, 87)));                        // too low for the text
        Assert.True(EntityGrips.Stretch(table, 0, P(5, 5)));
        Assert.Equal(P(5, 5), table.Position);

        // Scaling makes everything larger; turning leaves it upright.
        var copy = (TableEntity)table.Clone();
        copy.Transform(Transform2D.Scaling(2, copy.Bounds.Center));
        Assert.Equal((8.0, 150.0), (copy.TextHeight, copy.Width));
        Assert.Equal(table.Bounds.Center, copy.Bounds.Center);
        Assert.Equal("T", copy.Title);
    }

    [Fact]
    public void Renderer_rules_the_table_and_draws_each_cell_cut_off_at_its_edges_and_it_is_saved()
    {
        var s = Session();
        var made = new TableEntity(P(0, 0), 2, 2, 50, 10, 4) { Title = "DOOR SCHEDULE", Justify = TextJustify.Right };
        made.SetCell(0, 0, "MARK"); made.SetCell(0, 1, "WIDTH");
        made.SetCell(1, 0, "D1");
        s.BeginTablePlacement(made);
        s.SubmitPoint(P(0, 0));

        var view = new ViewTransform();
        view.SetViewport(800, 600);
        view.SetView(P(50, -16), 4);
        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, s.Document, view, new RenderOptions { ShowGrid = false });
        // 1 top + 3 row lines + 3 column lines + the 2 sides of the title.
        Assert.Equal(9, surface.Lines.Count);
        Assert.Equal(new[] { "DOOR SCHEDULE", "MARK", "WIDTH", "D1" }, surface.StyledTexts.Select(t => t.Text).ToArray());   // the empty cell draws nothing
        Assert.Equal(4, surface.Clips.Count);
        Assert.Equal(0, surface.ClipDepth);
        var title = surface.StyledTexts[0];
        Assert.True(title.Look.Bold && title.Look.Anchor == 0);
        Assert.Equal(5.0 * 4, title.Height, 9);                                       // 1.25 of the text height
        Assert.True(surface.StyledTexts[1].Look.Bold);                                // the header row
        var d1 = surface.StyledTexts[3];
        Assert.False(d1.Look.Bold);
        Assert.Equal(1, d1.Look.Anchor);                                              // right, against the cell's margin
        Assert.Equal(view.WorldToScreen(P(50 - 2.4, -27)).X, d1.X, 9);

        var loaded = HcadSerializer.LoadFromBytes(HcadSerializer.SaveToBytes(s.Document));
        Assert.Empty(loaded.Warnings);
        var table = loaded.Document.Entities.OfType<TableEntity>().Single();
        Assert.Equal(("DOOR SCHEDULE", 2, 2, "D1", "WIDTH", TextJustify.Right, 4.0),
            (table.Title, table.RowCount, table.ColumnCount, table.GetCell(1, 0), table.GetCell(0, 1), table.Justify, table.TextHeight));
        Assert.Equal(new[] { 50.0, 50.0 }, table.ColumnWidths);
    }
}
