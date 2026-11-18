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

public class BlockTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        return s;
    }

    private static BlockReferenceEntity Reference(EditorSession s) => s.Document.Entities.OfType<BlockReferenceEntity>().Single();

    /// <summary>A 10 x 4 box with a circle in it, selected.</summary>
    private static void DrawPart(EditorSession s)
    {
        s.Document.AddEntity(new PolylineEntity(new[] { P(10, 10), P(20, 10), P(20, 14), P(10, 14) }, true) { LayerId = s.Document.CurrentLayerId });
        s.Document.AddEntity(new CircleEntity(P(15, 12), 1) { LayerId = s.Document.CurrentLayerId });
        s.SelectAll();
    }

    [Fact]
    public void Block_is_made_from_the_selection_and_takes_its_place()
    {
        var s = Session();
        DrawPart(s);
        s.StartCommand("BLOCK");
        s.SubmitPoint(P(10, 10));                                                     // the base point
        s.SubmitText("Anchor plate");                                                 // a name with a space
        Assert.Null(s.ActiveCommand);

        var block = s.Document.Blocks.Single();
        Assert.Equal(("Anchor plate", 2), (block.Name, block.Entities.Count));
        Assert.Equal(new BoundingBox(0, 0, 10, 4), block.Bounds);                     // about its base point
        var reference = Reference(s);                                                 // the two objects became one
        Assert.Equal(1, s.Document.EntityCount);
        Assert.Equal((P(10, 10), 1.0, 0.0), (reference.Position, reference.Scale, reference.Rotation));
        Assert.Equal(new BoundingBox(10, 10, 20, 14), reference.Bounds);              // nothing moved
        Assert.True(s.Selection.Contains(reference.Id));
        Assert.Equal(0.0, reference.DistanceTo(P(15, 10)));                           // a click on any part picks all of it
        Assert.True(reference.DistanceTo(P(15, 30)) > 15);

        s.StartCommand("BLOCK");                                                      // the name is taken
        s.SubmitPoint(P(0, 0));
        s.SubmitText("anchor plate");
        Assert.NotNull(s.ActiveCommand);
        s.Cancel();

        s.Undo();
        Assert.Empty(s.Document.Blocks);
        Assert.Equal(2, s.Document.EntityCount);
        Assert.Empty(s.Document.Entities.OfType<BlockReferenceEntity>());
    }

    [Fact]
    public void Insert_places_the_block_again_and_again_with_scale_and_angle()
    {
        var s = Session();
        DrawPart(s);
        s.CreateBlock("Plate", P(10, 10), s.GetSelectedEntities());
        var block = s.Document.Blocks.Single();

        Assert.True(s.BeginBlockInsert(block, 2, 90));
        Assert.IsType<BlockReferenceEntity>(s.ActiveCommand!.GetPreview(P(1, 1)).Single());
        s.SubmitPoint(P(100, 0));
        s.SubmitPoint(P(200, 0));                                                     // it carries on
        s.SubmitKeyword("R");
        s.SubmitText("0");
        s.SubmitKeyword("S");
        s.SubmitText("1");
        s.SubmitPoint(P(300, 0));
        s.SubmitEnter();
        Assert.Null(s.ActiveCommand);
        Assert.Equal(4, s.Document.Entities.OfType<BlockReferenceEntity>().Count());
        Assert.Equal(4, s.Document.CountBlockReferences("plate"));

        var turned = s.Document.Entities.OfType<BlockReferenceEntity>().Single(r => r.Position == P(100, 0));
        var b = turned.Bounds;                                                        // 10 x 4 doubled and stood up: 8 wide to the left, 20 high
        Assert.Equal((92.0, 0.0, 100.0, 20.0), (Math.Round(b.MinX, 9), Math.Round(b.MinY, 9), Math.Round(b.MaxX, 9), Math.Round(b.MaxY, 9)));
        var plain = s.Document.Entities.OfType<BlockReferenceEntity>().Single(r => r.Position == P(300, 0));
        Assert.Equal(new BoundingBox(300, 0, 310, 4), plain.Bounds);

        // Moving and scaling a placed block.
        var copy = (BlockReferenceEntity)plain.Clone();
        copy.Transform(Transform2D.Scaling(3, P(300, 0)));
        Assert.Equal((3.0, new BoundingBox(300, 0, 330, 12)), (copy.Scale, copy.Bounds));
        Assert.Same(block, copy.Definition);                                          // the block itself is kept once

        Assert.False(s.DeleteBlock("Plate"));                                         // in use
        s.StartCommand("-INSERT");
        s.SubmitText("plate");
        Assert.IsType<InsertBlockCommand>(s.ActiveCommand);
        s.Cancel();
        s.StartCommand("-INSERT");
        s.SubmitText("no such block");
        Assert.IsType<TypedInsertCommand>(s.ActiveCommand);
    }

    [Fact]
    public void Explode_turns_a_block_back_into_objects_and_a_block_never_holds_a_block()
    {
        var s = Session();
        DrawPart(s);
        s.CreateBlock("Plate", P(10, 10), s.GetSelectedEntities());
        s.BeginBlockInsert(s.Document.Blocks[0], 1, 0);
        s.SubmitPoint(P(50, 50));
        s.SubmitEnter();

        s.SelectAll();                                                                // two placed blocks into one new block
        Assert.True(s.CreateBlock("Pair", P(0, 0), s.GetSelectedEntities()));
        var pair = s.Document.FindBlock("Pair")!;
        Assert.Equal(4, pair.Entities.Count);                                         // their objects, not the blocks
        Assert.DoesNotContain(pair.Entities, e => e is BlockReferenceEntity);
        Assert.True(s.DeleteBlock("Plate"));                                          // no longer used anywhere

        s.SelectAll();
        s.StartCommand("EXPLODE");
        Assert.Equal(4, s.Document.EntityCount);
        Assert.Equal(2, s.Document.Entities.OfType<CircleEntity>().Count());
        Assert.Contains(s.Document.Entities.OfType<CircleEntity>(), c => c.Center == P(55, 52));
        Assert.True(s.Document.Entities.All(e => e.LayerId == s.Document.CurrentLayerId));
        Assert.NotNull(s.Document.FindBlock("Pair"));                                 // the block stays for another INSERT
        s.Undo();
        Assert.Single(s.Document.Entities);
    }

    [Fact]
    public void Blocks_are_drawn_snapped_to_and_saved()
    {
        var s = Session();
        DrawPart(s);
        s.CreateBlock("Plate", P(10, 10), s.GetSelectedEntities());
        var reference = Reference(s);

        var view = new ViewTransform();
        view.SetViewport(800, 600);
        view.SetView(P(15, 12), 20);
        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, s.Document, view, new RenderOptions { ShowGrid = false });
        Assert.Single(surface.Circles);
        Assert.Equal(20.0, surface.Circles[0].R, 9);
        Assert.True(surface.Polylines.Count + surface.Lines.Count >= 1);              // the box

        // The corner of the box inside the block is an endpoint to snap to, and the circle has a centre.
        var points = SnapEngine.GetSnapPoints(reference, SnapModes.Endpoint | SnapModes.Center).Select(p => p.Point).ToList();
        Assert.Contains(P(20, 14), points);
        Assert.Contains(P(15, 12), points);
        Assert.Equal(new[] { P(10, 10) }, EntityGrips.GetGripPoints(reference).ToArray());

        byte[] bytes = HcadSerializer.SaveToBytes(s.Document);
        Assert.Contains("\"minReaderVersion\": 9", System.Text.Encoding.UTF8.GetString(bytes));
        var loaded = HcadSerializer.LoadFromBytes(bytes);
        Assert.Empty(loaded.Warnings);
        var block = loaded.Document.Blocks.Single();
        Assert.Equal(("Plate", 2), (block.Name, block.Entities.Count));
        var back = loaded.Document.Entities.OfType<BlockReferenceEntity>().Single();
        Assert.Same(block, back.Definition);
        Assert.Equal((reference.Id, reference.Position, reference.Bounds), (back.Id, back.Position, back.Bounds));

        // A place that names a block the file does not have is kept, not shown.
        string json = System.Text.Encoding.UTF8.GetString(bytes).Replace("\"block\": \"Plate\"", "\"block\": \"Gone\"");
        var broken = HcadSerializer.LoadFromBytes(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Empty(broken.Document.Entities);
        Assert.Single(broken.Document.UnknownEntities);
        Assert.NotEmpty(broken.Warnings);
    }

    [Fact]
    public void HAQQ_library_symbols_are_all_valid_and_sized_as_named()
    {
        var all = StandardBlocks.All;
        Assert.True(all.Count >= 35);
        Assert.Equal(all.Count, all.Select(b => b.Name.ToLowerInvariant()).Distinct().Count());
        Assert.True(all.All(b => b.IsValid && b.Description.Length > 0 && StandardBlocks.Categories.Contains(b.Category)));
        foreach (string category in StandardBlocks.Categories) Assert.Contains(all, b => b.Category == category);

        BlockDefinition Get(string name) => all.Single(b => b.Name == name);
        void Size(string name, double width, double depth)
        {
            var box = Get(name).Bounds;
            Assert.True(Math.Abs(box.Width - width) < 1e-6 && Math.Abs(box.Height - depth) < 1e-6, $"{name}: {box.Width} x {box.Height}");
        }
        Size("Sofa 84x36", 84, 36);
        Size("Bed queen 60x80", 60, 80);
        Size("Toilet 20x28", 20, 28);
        Size("Bathtub 60x30", 60, 30);
        Size("Shower 36x36", 36, 36);
        Size("Kitchen sink 33x22 double", 33, 22);
        Size("Range 30x26", 30, 26);
        Size("Vanity 60x22 double", 60, 22);
        // The back edge is on the base point's line, the symbol comes out into the room.
        Assert.Equal((-42.0, 0.0), (Get("Sofa 84x36").Bounds.MinX, Get("Sofa 84x36").Bounds.MaxY));

        // In a millimetre drawing the same sofa is 2133.6 wide.
        Assert.Equal(84 * 25.4, StandardBlocks.ForUnit(Get("Sofa 84x36"), LinearUnit.Millimeter).Bounds.Width, 6);
    }

    [Fact]
    public void Library_window_places_HAQQ_symbols_and_keeps_my_own_blocks_for_other_drawings()
    {
        var store = new BlockLibraryStore(_temp.File("blocks"));
        var s = Session();
        string? asked = null;
        s.ViewCommandRequested += name => asked = name;
        s.StartCommand("INSERT");
        Assert.Equal("Blocks", asked);

        var vm = new BlockLibraryViewModel(s, store);
        Assert.Equal(StandardBlocks.Furniture, vm.Category);                          // nothing in the drawing yet
        Assert.Contains(vm.Items, i => i.Name == "Sofa 84x36" && i.Size == "84.0000 x 36.0000");
        vm.Category = StandardBlocks.Plumbing;
        vm.Selected = vm.Items.Single(i => i.Name == "Toilet 20x28");
        Assert.False(vm.CanSaveToLibrary || vm.CanDelete);
        vm.ScaleText = "zero";
        Assert.False(vm.Insert());
        vm.ScaleText = "1";
        vm.RotationText = "180";
        Assert.True(vm.Insert());
        s.SubmitPoint(P(100, 100));
        s.SubmitPoint(P(200, 100));
        s.SubmitEnter();
        Assert.Equal("Toilet 20x28", s.Document.Blocks.Single().Name);                // brought in with its first use
        Assert.Equal(2, s.Document.Entities.OfType<BlockReferenceEntity>().Count());
        s.Undo();                                                                     // the second one
        s.Undo();                                                                     // the first one and the block with it
        Assert.Empty(s.Document.Blocks);
        s.Redo();

        vm.Filter = "sink";                                                           // looks in every group
        Assert.Equal(2, vm.Items.Count);
        vm.Filter = "";

        // My own block: into the library, then into a metric drawing.
        DrawPart(s);
        s.ClearSelection();
        s.SelectAt(P(15, 10), 0.5, toggle: false);
        s.SelectAt(P(16, 12), 0.5, toggle: true);
        s.CreateBlock("Plate", P(10, 10), s.GetSelectedEntities());
        vm.Category = BlockLibraryViewModel.DrawingCategory;
        vm.Selected = vm.Items.Single(i => i.Name == "Plate");
        Assert.Equal("Placed 1 time(s) in the drawing", vm.Selected.Description);
        Assert.False(vm.Delete());                                                    // placed
        Assert.True(vm.SaveToLibrary());

        var metric = Session();
        metric.Document.Settings.Unit = LinearUnit.Millimeter;
        var other = new BlockLibraryViewModel(metric, store) { Category = BlockLibraryStore.Category };
        var mine = other.Items.Single();
        Assert.Equal("Plate", mine.Name);
        Assert.Equal(254.0, mine.Block.Bounds.Width, 6);                              // 10 inches
        other.Selected = mine;
        Assert.True(other.Insert());
        metric.SubmitPoint(P(0, 0));
        metric.SubmitEnter();
        Assert.Equal(254.0, metric.Document.Entities.Single().Bounds.Width, 6);
        Assert.True(other.Delete());                                                  // out of my library
        Assert.Empty(new BlockLibraryStore(_temp.File("blocks")).Load());
    }
}
