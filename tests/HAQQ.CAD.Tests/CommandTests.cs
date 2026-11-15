using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Snapping;
using Xunit;
using static HAQQ.CAD.Tests.T;

namespace HAQQ.CAD.Tests;

public class CommandInputParserTests
{
    private static readonly DrawingSettings Settings = new();

    private static CommandInput Parse(string text, Point2D? last = null)
    {
        Assert.True(CommandInputParser.TryParse(text, last, Settings, out var input, out string? error), error);
        return input!;
    }

    [Fact]
    public void Absolute_relative_and_polar_coordinates()
    {
        Assert.Equal(P(10, 20), Assert.IsType<CommandInput.PointInput>(Parse("10,20")).Point);
        Assert.Equal(P(-2.5, 4), Assert.IsType<CommandInput.PointInput>(Parse(" -2.5 , 4 ")).Point);
        Assert.Equal(P(6, 6), Assert.IsType<CommandInput.PointInput>(Parse("@5,5", P(1, 1))).Point);

        var polar = Assert.IsType<CommandInput.PointInput>(Parse("@10<90", P(1, 1))).Point;
        Assert.Equal(1.0, polar.X, 9);
        Assert.Equal(11.0, polar.Y, 9);

        var fromOrigin = Assert.IsType<CommandInput.PointInput>(Parse("10<0", P(50, 50))).Point;
        Assert.Equal(10.0, fromOrigin.X, 9);
        Assert.Equal(0.0, fromOrigin.Y, 9);
    }

    [Fact]
    public void Feet_and_inches_in_coordinates()
    {
        Assert.Equal(P(66, 36), Assert.IsType<CommandInput.PointInput>(Parse("5'-6\",3'")).Point);
        Assert.Equal(P(12.5, 0.75), Assert.IsType<CommandInput.PointInput>(Parse("@1'-0 1/2\",3/4\"", P(0, 0))).Point);
        var polar = Assert.IsType<CommandInput.PointInput>(Parse("@2'<180", P(0, 0))).Point;
        Assert.Equal(-24.0, polar.X, 9);
    }

    [Fact]
    public void Numbers_keywords_and_enter()
    {
        Assert.Equal(12.0, Assert.IsType<CommandInput.NumberInput>(Parse("12")).Value);
        Assert.Equal(30.0, Assert.IsType<CommandInput.NumberInput>(Parse("2'6\"")).Value);
        Assert.Equal("C", Assert.IsType<CommandInput.KeywordInput>(Parse("c")).Keyword);
        Assert.IsType<CommandInput.EnterInput>(Parse("   "));
    }

    [Theory]
    [InlineData("@5")]
    [InlineData("1,2,3")]
    [InlineData("1,")]
    [InlineData("10<abc")]
    [InlineData("x!")]
    [InlineData("@")]
    public void Invalid_input_is_reported(string text)
    {
        Assert.False(CommandInputParser.TryParse(text, P(0, 0), Settings, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Relative_input_needs_a_previous_point()
    {
        Assert.False(CommandInputParser.TryParse("@1,1", null, Settings, out _, out string? error));
        Assert.Contains("previous point", error);
    }
}

public class DrawCommandTests
{
    [Fact]
    public void Line_from_clicked_points_is_one_undo_step()
    {
        var s = new EditorSession();
        s.ContinuousLines = true;                 // these steps draw a chain of lines (LINEMODE Continuous)
        Assert.True(s.StartCommand("L"));
        Assert.Equal("LINE", s.ActiveCommand!.Name);
        Assert.Contains("first point", s.Prompt);
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(10, 0));
        s.SubmitPoint(P(10, 10));
        s.SubmitEnter();

        Assert.Null(s.ActiveCommand);
        Assert.Equal("Command:", s.Prompt);
        var lines = s.Document.Entities.Cast<LineEntity>().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(P(10, 0), lines[0].End);
        Assert.Equal(P(10, 0), lines[1].Start);
        Assert.Equal(s.Document.CurrentLayerId, lines[0].LayerId);
        Assert.Equal(CadColor.ByLayer, lines[0].Color);

        s.Undo();
        Assert.Equal(0, s.Document.EntityCount);
        s.Redo();
        Assert.Equal(2, s.Document.EntityCount);
        Assert.Equal(lines[0].Id, s.Document.Entities[0].Id); // ids are stable across undo/redo
    }

    [Fact]
    public void Line_from_typed_coordinates_with_close()
    {
        var s = new EditorSession();
        s.ContinuousLines = true;                 // these steps draw a chain of lines (LINEMODE Continuous)
        s.SubmitText("line");
        s.SubmitText("0,0");
        s.SubmitText("@10,0");
        s.SubmitText("@5<90");
        s.SubmitText("c");
        Assert.Null(s.ActiveCommand);

        var lines = s.Document.Entities.Cast<LineEntity>().ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal(10.0, lines[1].End.X, 9);
        Assert.Equal(5.0, lines[1].End.Y, 9);
        Assert.Equal(P(0, 0), lines[2].End);
    }

    [Fact]
    public void Line_undo_option_removes_the_last_segment()
    {
        var s = new EditorSession();
        s.ContinuousLines = true;                 // these steps draw a chain of lines (LINEMODE Continuous)
        s.StartCommand("LINE");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(5, 0));
        s.SubmitPoint(P(5, 5));
        s.SubmitText("U");
        Assert.Equal(1, s.Document.EntityCount);
        Assert.Equal(P(5, 0), s.ActiveCommand!.LastPoint);
        s.SubmitPoint(P(9, 0));
        s.SubmitEnter();
        var lines = s.Document.Entities.Cast<LineEntity>().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(P(9, 0), lines[1].End);
    }

    [Fact]
    public void Escape_keeps_segments_already_drawn()
    {
        var s = new EditorSession();
        s.StartCommand("LINE");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(5, 0));
        s.Cancel();
        Assert.Null(s.ActiveCommand);
        Assert.Equal(1, s.Document.EntityCount);
        s.Undo();
        Assert.Equal(0, s.Document.EntityCount);
    }

    [Fact]
    public void Direct_distance_entry_follows_the_cursor_and_ortho()
    {
        var s = new EditorSession { OrthoEnabled = true };
        s.ContinuousLines = true;                 // these steps draw a chain of lines (LINEMODE Continuous)
        s.Snap.Enabled = false;
        s.StartCommand("LINE");
        s.SubmitPoint(P(0, 0));
        s.UpdateCursor(P(7, 2), 0.1);                  // mostly along +X: ortho locks to horizontal
        Assert.Equal(P(7, 0), s.EffectiveCursor);
        s.SubmitText("10");
        s.UpdateCursor(P(11, 30), 0.1);                // mostly along +Y from (10,0)
        s.SubmitText("2'");
        s.SubmitEnter();

        var lines = s.Document.Entities.Cast<LineEntity>().ToList();
        Assert.Equal(P(10, 0), lines[0].End);
        Assert.Equal(P(10, 24), lines[1].End);
    }

    [Fact]
    public void Holding_shift_temporarily_flips_ortho()
    {
        var s = new EditorSession();
        s.Snap.Enabled = false;
        s.StartCommand("LINE");
        s.SubmitPoint(P(0, 0));
        s.UpdateCursor(P(7, 2), 0.1);
        Assert.Equal(P(7, 2), s.EffectiveCursor);       // ortho off: free cursor

        s.OrthoOverride = true;                         // Shift held
        Assert.True(s.OrthoActive);
        Assert.Equal(P(7, 0), s.EffectiveCursor);
        s.UpdateCursor(P(1, -9), 0.1);
        Assert.Equal(P(0, -9), s.EffectiveCursor);      // perpendicular direction
        s.SubmitPoint(s.EffectiveCursor);
        Assert.Equal(P(0, -9), ((LineEntity)s.Document.Entities[0]).End);

        s.OrthoOverride = false;                        // Shift released
        Assert.Equal(P(1, -9), s.EffectiveCursor);

        s.OrthoEnabled = true;                          // with ORTHO on, Shift releases it
        s.OrthoOverride = true;
        Assert.False(s.OrthoActive);
    }

    [Fact]
    public void Cursor_snaps_to_geometry_drawn_earlier_in_the_same_command()
    {
        var s = new EditorSession();
        s.ContinuousLines = true;                 // these steps draw a chain of lines (LINEMODE Continuous)
        s.StartCommand("LINE");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(10, 0));
        s.SubmitPoint(P(10, 10));
        s.UpdateCursor(P(0.2, 0.1), 0.5);
        Assert.NotNull(s.CurrentSnap);
        Assert.Equal(SnapModes.Endpoint, s.CurrentSnap!.Value.Mode);
        s.SubmitPoint(s.EffectiveCursor);
        s.SubmitEnter();
        Assert.Equal(P(0, 0), ((LineEntity)s.Document.Entities[2]).End);

        // No snap is offered while idle.
        s.UpdateCursor(P(0.2, 0.1), 0.5);
        Assert.Null(s.CurrentSnap);
    }

    [Fact]
    public void Snap_can_be_switched_off()
    {
        var s = new EditorSession();
        AddLine(s.Document, 0, 0, 10, 0);
        s.Snap.Enabled = false;
        s.StartCommand("LINE");
        s.UpdateCursor(P(0.2, 0.1), 0.5);
        Assert.Null(s.CurrentSnap);
        Assert.Equal(P(0.2, 0.1), s.EffectiveCursor);
    }

    [Fact]
    public void Polyline_creates_a_single_entity()
    {
        var s = new EditorSession();
        s.StartCommand("PL");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(4, 0));
        s.SubmitPoint(P(4, 3));
        Assert.Single(s.GetPreview());
        s.SubmitText("C");

        var poly = Assert.IsType<PolylineEntity>(Assert.Single(s.Document.Entities));
        Assert.True(poly.IsClosed);
        Assert.Equal(3, poly.Vertices.Count);
        Assert.Equal(12.0, poly.Length, 12);

        s.StartCommand("PLINE");
        s.SubmitText("10,10");
        s.SubmitText("@5,0");
        s.SubmitEnter();
        var open = (PolylineEntity)s.Document.Entities[1];
        Assert.False(open.IsClosed);
        Assert.Equal(2, open.Vertices.Count);
    }

    [Fact]
    public void Polyline_with_one_point_creates_nothing()
    {
        var s = new EditorSession();
        s.StartCommand("PLINE");
        s.SubmitPoint(P(0, 0));
        s.SubmitEnter();
        Assert.Equal(0, s.Document.EntityCount);
        Assert.False(s.UndoStack.CanUndo);
    }

    [Fact]
    public void Rectangle_from_corner_and_relative_size()
    {
        var s = new EditorSession();
        var messages = CaptureMessages(s);
        s.StartCommand("REC");
        s.SubmitText("2,2");
        s.SubmitText("@0,5");                           // no width: rejected, command stays active
        Assert.NotNull(s.ActiveCommand);
        Assert.Contains(messages, m => m.Contains("width and a height"));
        s.SubmitText("@4,3");

        var rect = Assert.IsType<PolylineEntity>(Assert.Single(s.Document.Entities));
        Assert.True(rect.IsClosed);
        Assert.Equal(new BoundingBox(2, 2, 6, 5), rect.Bounds);
        Assert.Equal(12.0, rect.Area, 12);
        Assert.Equal(14.0, rect.Length, 12);
    }

    [Fact]
    public void Circle_by_radius_diameter_and_point()
    {
        var s = new EditorSession();
        s.StartCommand("C");
        s.SubmitText("0,0");
        s.SubmitText("-3");                             // rejected
        Assert.NotNull(s.ActiveCommand);
        s.SubmitText("5");
        Assert.Equal(5.0, ((CircleEntity)s.Document.Entities[0]).Radius);

        s.StartCommand("CIRCLE");
        s.SubmitPoint(P(20, 0));
        s.SubmitText("D");
        Assert.Contains("diameter", s.Prompt);
        s.SubmitText("10");
        Assert.Equal(5.0, ((CircleEntity)s.Document.Entities[1]).Radius);

        s.StartCommand("CIRCLE");
        s.SubmitPoint(P(40, 0));
        s.SubmitPoint(P(43, 4));
        Assert.Equal(5.0, ((CircleEntity)s.Document.Entities[2]).Radius, 12);
        Assert.Equal(3, s.Document.EntityCount);
    }

    [Fact]
    public void New_objects_go_on_the_current_layer()
    {
        var s = new EditorSession();
        var layer = s.AddLayer("REBAR")!;
        Assert.True(s.SetCurrentLayer(layer.Id));
        s.StartCommand("CIRCLE");
        s.SubmitText("0,0");
        s.SubmitText("1");
        Assert.Equal(layer.Id, s.Document.Entities[0].LayerId);
    }

    [Fact]
    public void Unknown_commands_and_repeat_with_enter()
    {
        var s = new EditorSession();
        var messages = CaptureMessages(s);
        Assert.False(s.StartCommand("FROBNICATE"));
        Assert.Contains(messages, m => m.Contains("Unknown command"));
        Assert.Null(s.ActiveCommand);

        s.SubmitEnter();                                // nothing to repeat yet
        Assert.Null(s.ActiveCommand);

        s.SubmitText("circle");
        s.SubmitText("0,0");
        s.SubmitText("1");
        s.SubmitEnter();                                // repeats CIRCLE
        Assert.Equal("CIRCLE", s.ActiveCommand!.Name);
    }

    [Fact]
    public void Starting_a_command_cancels_the_running_one()
    {
        var s = new EditorSession();
        s.StartCommand("LINE");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(1, 0));
        s.StartCommand("CIRCLE");
        Assert.Equal("CIRCLE", s.ActiveCommand!.Name);
        Assert.Equal(1, s.Document.EntityCount);        // the segment already drawn is kept
    }
}

public class ModifyCommandTests
{
    private static EditorSession SessionWithLineAndCircle(out LineEntity line, out CircleEntity circle)
    {
        var s = new EditorSession();
        line = AddLine(s.Document, 0, 0, 10, 0);
        circle = AddCircle(s.Document, 50, 50, 5);
        s.Document.MarkSaved();
        return s;
    }

    [Fact]
    public void Move_preselected_objects()
    {
        var s = SessionWithLineAndCircle(out var line, out var circle);
        Assert.True(s.SelectAt(P(5, 0.1), 0.5));
        s.StartCommand("M");
        Assert.False(s.WantsSelection);
        Assert.Contains("base point", s.Prompt);
        s.SubmitPoint(P(0, 0));
        s.UpdateCursor(P(3, 4), 0.01);
        Assert.Equal(2, s.GetPreview().Count);          // ghost of the line + rubber band
        s.SubmitPoint(P(5, 5));

        Assert.Null(s.ActiveCommand);
        var moved = (LineEntity)s.Document.GetEntity(line.Id)!;
        Assert.Equal(P(5, 5), moved.Start);
        Assert.Equal(P(15, 5), moved.End);
        Assert.Equal(P(50, 50), ((CircleEntity)s.Document.GetEntity(circle.Id)!).Center);
        Assert.Equal(2, s.Document.EntityCount);
        Assert.True(s.Selection.Contains(line.Id));     // selection survives the move

        s.Undo();
        Assert.Equal(P(0, 0), ((LineEntity)s.Document.GetEntity(line.Id)!).Start);
        s.Redo();
        Assert.Equal(P(5, 5), ((LineEntity)s.Document.GetEntity(line.Id)!).Start);
    }

    [Fact]
    public void Move_selecting_inside_the_command()
    {
        var s = SessionWithLineAndCircle(out var line, out var circle);
        s.StartCommand("MOVE");
        Assert.True(s.WantsSelection);
        Assert.Contains("Select objects", s.Prompt);
        s.SelectAt(P(55, 50), 0.5);
        s.SelectBox(new BoundingBox(-1, -1, 11, 1), crossing: false);
        Assert.Contains("2 selected", s.Prompt);
        s.SubmitEnter();
        Assert.False(s.WantsSelection);
        s.SubmitText("0,0");
        s.SubmitText("@100,0");

        Assert.Equal(P(100, 0), ((LineEntity)s.Document.GetEntity(line.Id)!).Start);
        Assert.Equal(P(150, 50), ((CircleEntity)s.Document.GetEntity(circle.Id)!).Center);
        s.Undo();                                       // both objects move back in one step
        Assert.Equal(P(0, 0), ((LineEntity)s.Document.GetEntity(line.Id)!).Start);
        Assert.Equal(P(50, 50), ((CircleEntity)s.Document.GetEntity(circle.Id)!).Center);
    }

    [Fact]
    public void Move_by_typed_distance_in_the_cursor_direction()
    {
        var s = SessionWithLineAndCircle(out var line, out _);
        s.Selection.Add(line.Id);
        s.StartCommand("MOVE");
        s.SubmitPoint(P(0, 0));
        s.UpdateCursor(P(0, 9), 0.01);
        s.SubmitText("25");
        Assert.Equal(P(0, 25), ((LineEntity)s.Document.GetEntity(line.Id)!).Start);
    }

    [Fact]
    public void Move_without_a_selection_ends_cleanly()
    {
        var s = SessionWithLineAndCircle(out _, out _);
        var messages = CaptureMessages(s);
        s.StartCommand("MOVE");
        s.SubmitEnter();
        Assert.Null(s.ActiveCommand);
        Assert.Contains("No objects selected.", messages);
        Assert.False(s.Document.IsDirty);
    }

    [Fact]
    public void Copy_places_multiple_copies_with_new_ids()
    {
        var s = SessionWithLineAndCircle(out var line, out _);
        s.Selection.Add(line.Id);
        s.StartCommand("CO");
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(0, 10));
        s.SubmitText("@0,20");                          // relative to the base point
        Assert.NotNull(s.ActiveCommand);
        s.SubmitEnter();
        Assert.Null(s.ActiveCommand);

        Assert.Equal(4, s.Document.EntityCount);
        var copies = s.Document.Entities.OfType<LineEntity>().Where(l => l.Id != line.Id).ToList();
        Assert.Equal(2, copies.Count);
        Assert.Equal(P(0, 10), copies[0].Start);
        Assert.Equal(P(10, 20), copies[1].End);
        Assert.Equal(P(0, 0), ((LineEntity)s.Document.GetEntity(line.Id)!).Start); // original untouched
        Assert.Equal(line.LayerId, copies[0].LayerId);

        s.Undo();
        Assert.Equal(3, s.Document.EntityCount);
        s.Undo();
        Assert.Equal(2, s.Document.EntityCount);
    }

    [Fact]
    public void Erase_preselected_and_undo()
    {
        var s = SessionWithLineAndCircle(out var line, out var circle);
        s.SelectAll();
        Assert.Equal(2, s.Selection.Count);
        s.StartCommand("E");
        Assert.Null(s.ActiveCommand);
        Assert.Equal(0, s.Document.EntityCount);
        Assert.Equal(0, s.Selection.Count);             // erased objects leave the selection

        s.Undo();
        Assert.Equal(new[] { line.Id, circle.Id }, s.Document.Entities.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void Erase_selecting_inside_the_command_with_all()
    {
        var s = SessionWithLineAndCircle(out _, out _);
        s.StartCommand("ERASE");
        s.SubmitText("all");
        Assert.Equal(2, s.Selection.Count);
        s.SubmitEnter();
        Assert.Equal(0, s.Document.EntityCount);
    }

    [Fact]
    public void Escape_clears_the_selection_when_idle()
    {
        var s = SessionWithLineAndCircle(out var line, out _);
        s.Selection.Add(line.Id);
        s.Cancel();
        Assert.Equal(0, s.Selection.Count);
    }

    [Fact]
    public void Toggle_selection_deselects()
    {
        var s = SessionWithLineAndCircle(out var line, out _);
        Assert.True(s.SelectAt(P(5, 0), 0.5, toggle: true));
        Assert.True(s.Selection.Contains(line.Id));
        Assert.True(s.SelectAt(P(5, 0), 0.5, toggle: true));
        Assert.False(s.Selection.Contains(line.Id));
        Assert.False(s.SelectAt(P(500, 500), 0.5));
    }

    [Fact]
    public void Layer_operations_are_undoable_and_guarded()
    {
        var s = new EditorSession();
        var messages = CaptureMessages(s);
        var a = s.AddLayer()!;
        var b = s.AddLayer()!;
        Assert.Equal("Layer 1", a.Name);
        Assert.Equal("Layer 2", b.Name);

        Assert.True(s.UpdateLayer(a.Id, l => { l.Name = "WALLS"; l.Color = CadColor.FromRgb(255, 0, 0); }));
        Assert.Equal("WALLS", s.Document.GetLayer(a.Id)!.Name);
        Assert.False(s.UpdateLayer(b.Id, l => l.Name = "walls")); // duplicate name
        Assert.False(s.UpdateLayer(b.Id, l => l.Name = "  "));
        Assert.Equal("Layer 2", s.Document.GetLayer(b.Id)!.Name);

        s.SetCurrentLayer(a.Id);
        AddLine(s.Document, 0, 0, 1, 1);
        s.SetCurrentLayer(b.Id);
        Assert.False(s.DeleteLayer(a.Id));              // still has an object
        Assert.Contains(messages, m => m.Contains("still contains objects"));
        Assert.False(s.DeleteLayer(b.Id));              // current
        s.SetCurrentLayer(s.Document.Layers[0].Id);
        Assert.True(s.DeleteLayer(b.Id));
        Assert.Equal(2, s.Document.Layers.Count);
        s.Undo();
        Assert.Equal(3, s.Document.Layers.Count);
        Assert.Equal("Layer 2", s.Document.Layers[2].Name);
    }

    [Fact]
    public void Locking_a_layer_removes_its_objects_from_the_selection()
    {
        var s = SessionWithLineAndCircle(out var line, out _);
        s.Selection.Add(line.Id);
        s.UpdateLayer(line.LayerId, l => l.IsLocked = true);
        Assert.Equal(0, s.Selection.Count);
        Assert.False(s.SelectAt(P(5, 0), 0.5));
        Assert.Equal(0, s.SelectAll());
    }

    [Fact]
    public void Property_edits_are_validated_and_undoable()
    {
        var s = SessionWithLineAndCircle(out var line, out var circle);
        Assert.True(s.ModifyEntities(new[] { circle.Id }, e => ((CircleEntity)e).Radius = 8, "Edit radius"));
        Assert.Equal(8.0, ((CircleEntity)s.Document.GetEntity(circle.Id)!).Radius);

        Assert.False(s.ModifyEntities(new[] { circle.Id }, e => ((CircleEntity)e).Radius = 0, "Edit radius"));
        Assert.False(s.ModifyEntities(new[] { line.Id }, e => ((LineEntity)e).End = ((LineEntity)e).Start, "Edit end"));
        Assert.Equal(8.0, ((CircleEntity)s.Document.GetEntity(circle.Id)!).Radius);

        s.Undo();
        Assert.Equal(5.0, ((CircleEntity)s.Document.GetEntity(circle.Id)!).Radius);
        Assert.False(s.UndoStack.CanUndo);
    }
}
