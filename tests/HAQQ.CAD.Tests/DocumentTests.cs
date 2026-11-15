using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Snapping;
using Xunit;
using static HAQQ.CAD.Tests.T;

namespace HAQQ.CAD.Tests;

public class SpatialIndexTests
{
    [Fact]
    public void Query_returns_only_nearby_items()
    {
        var index = new SpatialIndex(10);
        var ids = new Dictionary<Guid, BoundingBox>();
        for (int x = 0; x < 40; x++)
        for (int y = 0; y < 25; y++)
        {
            var id = Guid.NewGuid();
            var box = new BoundingBox(x * 10 + 1, y * 10 + 1, x * 10 + 9, y * 10 + 9);
            ids[id] = box;
            index.Insert(id, box);
        }
        Assert.Equal(1000, index.Count);

        var hits = index.Query(new BoundingBox(100, 100, 120, 110));
        Assert.Equal(2, hits.Count);
        foreach (var id in hits) Assert.True(ids[id].Intersects(new BoundingBox(100, 100, 120, 110)));
    }

    [Fact]
    public void Matches_a_linear_scan_for_random_data()
    {
        var random = new Random(12345);
        var index = new SpatialIndex(25);
        var boxes = new Dictionary<Guid, BoundingBox>();
        for (int i = 0; i < 600; i++)
        {
            double x = random.NextDouble() * 2000 - 1000, y = random.NextDouble() * 2000 - 1000;
            // A few very large items exercise the oversize path.
            double w = i % 50 == 0 ? 5000 : random.NextDouble() * 60, h = random.NextDouble() * 60;
            var id = Guid.NewGuid();
            boxes[id] = new BoundingBox(x, y, x + w, y + h);
            index.Insert(id, boxes[id]);
        }

        // Remove a third, then compare queries of many sizes against brute force.
        foreach (var id in boxes.Keys.Where((_, i) => i % 3 == 0).ToList())
        {
            Assert.True(index.Remove(id));
            boxes.Remove(id);
        }

        void Check()
        {
            for (int q = 0; q < 80; q++)
            {
                double x = random.NextDouble() * 2400 - 1200, y = random.NextDouble() * 2400 - 1200;
                double size = q % 10 == 0 ? 5000 : random.NextDouble() * 300;
                var query = new BoundingBox(x, y, x + size, y + size);
                var expected = boxes.Where(p => p.Value.Intersects(query)).Select(p => p.Key).OrderBy(g => g).ToList();
                var actual = index.Query(query).OrderBy(g => g).ToList();
                Assert.Equal(expected, actual);
            }
        }

        Check();
        index.Rebuild(boxes);
        Assert.Equal(boxes.Count, index.Count);
        Check();
    }

    [Fact]
    public void Reinserting_an_id_replaces_its_box()
    {
        var index = new SpatialIndex(10);
        var id = Guid.NewGuid();
        index.Insert(id, new BoundingBox(0, 0, 1, 1));
        index.Insert(id, new BoundingBox(500, 500, 501, 501));
        Assert.Equal(1, index.Count);
        Assert.Empty(index.Query(new BoundingBox(-5, -5, 5, 5)));
        Assert.Single(index.Query(new BoundingBox(499, 499, 502, 502)));
        Assert.False(index.Remove(Guid.NewGuid()));
    }
}

public class DocumentTests
{
    [Fact]
    public void New_document_has_layer_zero_as_current()
    {
        var doc = new DrawingDocument();
        var layer = Assert.Single(doc.Layers);
        Assert.Equal("0", layer.Name);
        Assert.Equal(layer.Id, doc.CurrentLayerId);
        Assert.False(doc.IsDirty);
        Assert.True(doc.GetExtents().IsEmpty);
    }

    [Fact]
    public void Entities_can_be_added_queried_replaced_and_removed()
    {
        var doc = new DrawingDocument();
        var a = AddLine(doc, 0, 0, 10, 0);
        var b = AddCircle(doc, 100, 100, 5);
        Assert.Equal(2, doc.EntityCount);
        Assert.True(doc.IsDirty);
        Assert.Equal(new[] { a.Id, b.Id }, doc.Entities.Select(e => e.Id).ToArray());

        Assert.Equal(a.Id, Assert.Single(doc.QueryEntities(new BoundingBox(-1, -1, 1, 1))).Id);

        var moved = (LineEntity)a.Clone();
        moved.Start = P(200, 200);
        moved.End = P(210, 200);
        doc.ReplaceEntity(moved);
        Assert.Empty(doc.QueryEntities(new BoundingBox(-1, -1, 1, 1)));
        Assert.Equal(a.Id, Assert.Single(doc.QueryEntities(new BoundingBox(199, 199, 201, 201))).Id);
        Assert.Equal(new[] { a.Id, b.Id }, doc.Entities.Select(e => e.Id).ToArray()); // draw order kept

        doc.RemoveEntity(a.Id);
        Assert.Null(doc.GetEntity(a.Id));
        Assert.Equal(new BoundingBox(95, 95, 105, 105), doc.GetExtents());
    }

    [Fact]
    public void Invalid_entities_are_rejected()
    {
        var doc = new DrawingDocument();
        var zeroLength = new LineEntity(P(1, 1), P(1, 1)) { LayerId = doc.CurrentLayerId };
        Assert.Throws<InvalidOperationException>(() => doc.AddEntity(zeroLength));

        var noLayer = new LineEntity(P(0, 0), P(1, 1)) { LayerId = Guid.NewGuid() };
        Assert.Throws<InvalidOperationException>(() => doc.AddEntity(noLayer));

        var nan = new CircleEntity(P(double.NaN, 0), 1) { LayerId = doc.CurrentLayerId };
        Assert.Throws<InvalidOperationException>(() => doc.AddEntity(nan));

        var line = AddLine(doc, 0, 0, 1, 1);
        Assert.Throws<InvalidOperationException>(() => doc.AddEntity(line)); // duplicate id
        Assert.Equal(1, doc.EntityCount);
    }

    [Fact]
    public void Layer_rules()
    {
        var doc = new DrawingDocument();
        var layer0 = doc.Layers[0];
        var rebar = new Layer(Guid.NewGuid(), "REBAR") { Color = CadColor.FromRgb(255, 0, 0) };
        doc.AddLayer(rebar);
        Assert.Same(rebar, doc.FindLayer("rebar"));
        Assert.Throws<InvalidOperationException>(() => doc.AddLayer(new Layer(Guid.NewGuid(), "Rebar")));

        Assert.Equal("Layer 0 cannot be deleted.", doc.GetLayerRemovalBlocker(layer0.Id));
        doc.CurrentLayerId = rebar.Id;
        Assert.NotNull(doc.GetLayerRemovalBlocker(rebar.Id)); // current layer
        var line = AddLine(doc, 0, 0, 1, 1);
        doc.CurrentLayerId = layer0.Id;
        Assert.Contains("still contains objects", doc.GetLayerRemovalBlocker(rebar.Id));
        doc.RemoveEntity(line.Id);
        Assert.Null(doc.GetLayerRemovalBlocker(rebar.Id));
        doc.RemoveLayer(rebar.Id);
        Assert.Single(doc.Layers);

        var renamed = layer0.Clone();
        renamed.Name = "Other";
        Assert.Throws<InvalidOperationException>(() => doc.ReplaceLayer(renamed));
    }

    [Fact]
    public void Extents_ignore_hidden_and_frozen_layers()
    {
        var doc = new DrawingDocument();
        AddLine(doc, 0, 0, 10, 10);
        var hidden = new Layer(Guid.NewGuid(), "HIDDEN") { IsVisible = false };
        doc.AddLayer(hidden);
        doc.AddEntity(new LineEntity(P(1000, 1000), P(2000, 2000)) { LayerId = hidden.Id });
        Assert.Equal(new BoundingBox(0, 0, 10, 10), doc.GetExtents());
    }

    [Fact]
    public void Batches_coalesce_change_notifications()
    {
        var doc = new DrawingDocument();
        int events = 0;
        doc.Changed += (_, _) => events++;
        using (doc.BeginBatch())
        {
            for (int i = 0; i < 50; i++) AddLine(doc, i, 0, i, 5);
            Assert.Equal(0, events);
        }
        Assert.Equal(1, events);
        AddLine(doc, 0, 9, 5, 9);
        Assert.Equal(2, events);
    }

    [Fact]
    public void Index_stays_correct_while_the_drawing_grows()
    {
        // Crossing the re-tuning thresholds of the spatial index must not lose entities.
        var doc = new DrawingDocument();
        for (int i = 0; i < 3000; i++) AddLine(doc, i * 3, 0, i * 3 + 2, 2);
        var hits = doc.QueryEntities(new BoundingBox(299, -1, 305.5, 3));
        Assert.Equal(3, hits.Count); // lines starting at 297 (ends 299), 300 and 303
        Assert.Equal(3000, doc.QueryEntities(new BoundingBox(-1e6, -1e6, 1e6, 1e6)).Count);
    }

    [Fact]
    public void Entity_clone_is_deep_and_copy_gets_a_new_id()
    {
        var poly = new PolylineEntity(new[] { P(0, 0), P(4, 0), P(4, 3) }, true)
        {
            Color = CadColor.FromRgb(1, 2, 3),
            LineweightMm = 0.5,
            Linetype = "Hidden",
        };
        poly.CustomProperties["mark"] = "A1";

        var clone = (PolylineEntity)poly.Clone();
        Assert.Equal(poly.Id, clone.Id);
        clone.Transform(Transform2D.Translation(new Vector2D(10, 10)));
        clone.CustomProperties["mark"] = "B2";
        Assert.Equal(P(0, 0), poly.Vertices[0]);
        Assert.Equal("A1", poly.CustomProperties["mark"]);
        Assert.Equal(new BoundingBox(10, 10, 14, 13), clone.Bounds);

        var copy = (PolylineEntity)poly.CopyWithNewId();
        Assert.NotEqual(poly.Id, copy.Id);
        Assert.Equal(CadColor.FromRgb(1, 2, 3), copy.Color);
        Assert.Equal(0.5, copy.LineweightMm);
        Assert.Equal("Hidden", copy.Linetype);
        Assert.True(copy.IsClosed);
        Assert.Equal(12.0, poly.Length, 12);
        Assert.Equal(6.0, poly.Area, 12);
    }
}

public class UndoTests
{
    [Fact]
    public void Add_undo_redo()
    {
        var doc = new DrawingDocument();
        var undo = new UndoStack();
        var line = new LineEntity(P(0, 0), P(5, 5)) { LayerId = doc.CurrentLayerId };

        undo.Execute(doc, new AddEntitiesAction("LINE", new[] { line }));
        Assert.Equal(1, doc.EntityCount);
        Assert.True(undo.CanUndo);
        Assert.False(undo.CanRedo);

        Assert.NotNull(undo.Undo(doc));
        Assert.Equal(0, doc.EntityCount);
        Assert.True(undo.CanRedo);

        Assert.NotNull(undo.Redo(doc));
        Assert.Equal(line.Id, Assert.Single(doc.Entities).Id);
        Assert.Null(undo.Redo(doc));
    }

    [Fact]
    public void Erase_undo_restores_draw_order_and_properties()
    {
        var doc = new DrawingDocument();
        var undo = new UndoStack();
        var a = AddLine(doc, 0, 0, 1, 0);
        var b = AddLine(doc, 0, 1, 1, 1);
        var c = AddLine(doc, 0, 2, 1, 2);

        undo.Execute(doc, new RemoveEntitiesAction("ERASE", new[] { b.Id }));
        Assert.Equal(new[] { a.Id, c.Id }, doc.Entities.Select(e => e.Id).ToArray());

        undo.Undo(doc);
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, doc.Entities.Select(e => e.Id).ToArray());
        var restored = (LineEntity)doc.GetEntity(b.Id)!;
        Assert.Equal(P(0, 1), restored.Start);
    }

    [Fact]
    public void Modify_undo_redo_and_new_action_clears_redo()
    {
        var doc = new DrawingDocument();
        var undo = new UndoStack();
        var line = AddLine(doc, 0, 0, 10, 0);
        var after = (LineEntity)line.Clone();
        after.End = P(10, 10);

        undo.Execute(doc, new ModifyEntitiesAction("Edit", new[] { line }, new[] { after }));
        Assert.Equal(P(10, 10), ((LineEntity)doc.GetEntity(line.Id)!).End);
        undo.Undo(doc);
        Assert.Equal(P(10, 0), ((LineEntity)doc.GetEntity(line.Id)!).End);
        Assert.True(undo.CanRedo);

        undo.Execute(doc, new AddEntitiesAction("LINE", new[] { new LineEntity(P(1, 1), P(2, 2)) { LayerId = doc.CurrentLayerId } }));
        Assert.False(undo.CanRedo);
    }

    [Fact]
    public void Undo_snapshots_are_isolated_from_later_edits()
    {
        var doc = new DrawingDocument();
        var undo = new UndoStack();
        var source = new LineEntity(P(0, 0), P(5, 0)) { LayerId = doc.CurrentLayerId };
        undo.Execute(doc, new AddEntitiesAction("LINE", new[] { source }));
        source.End = P(99, 99); // mutating the caller's instance must not affect the document or history
        Assert.Equal(P(5, 0), ((LineEntity)doc.GetEntity(source.Id)!).End);
        undo.Undo(doc);
        undo.Redo(doc);
        Assert.Equal(P(5, 0), ((LineEntity)doc.GetEntity(source.Id)!).End);
    }

    [Fact]
    public void Layer_actions_are_reversible()
    {
        var doc = new DrawingDocument();
        var undo = new UndoStack();
        var layer = new Layer(Guid.NewGuid(), "DIM");
        undo.Execute(doc, new AddLayerAction(layer));
        Assert.Equal(2, doc.Layers.Count);

        var changed = doc.GetLayer(layer.Id)!.Clone();
        changed.Color = CadColor.FromRgb(0, 255, 0);
        changed.IsLocked = true;
        undo.Execute(doc, new ModifyLayerAction(doc.GetLayer(layer.Id)!, changed));
        Assert.True(doc.GetLayer(layer.Id)!.IsLocked);

        undo.Execute(doc, new SetCurrentLayerAction(doc.CurrentLayerId, layer.Id));
        Assert.Equal(layer.Id, doc.CurrentLayerId);

        undo.Undo(doc);
        undo.Undo(doc);
        Assert.False(doc.GetLayer(layer.Id)!.IsLocked);
        Assert.Equal(CadColor.White, doc.GetLayer(layer.Id)!.Color);

        undo.Execute(doc, new RemoveLayerAction(layer.Id));
        Assert.Single(doc.Layers);
        undo.Undo(doc);
        Assert.Equal("DIM", doc.Layers[1].Name);
    }
}

public class SnappingTests
{
    private const SnapModes All = SnapModes.Endpoint | SnapModes.Midpoint | SnapModes.Center;

    [Fact]
    public void Snaps_to_line_endpoint_and_midpoint()
    {
        var doc = new DrawingDocument();
        var line = AddLine(doc, 0, 0, 10, 0);

        var end = SnapEngine.FindSnap(doc, P(9.8, 0.3), 0.5, All);
        Assert.NotNull(end);
        Assert.Equal(P(10, 0), end!.Value.Point);
        Assert.Equal(SnapModes.Endpoint, end.Value.Mode);
        Assert.Equal(line.Id, end.Value.EntityId);

        var mid = SnapEngine.FindSnap(doc, P(5.2, -0.2), 0.5, All);
        Assert.Equal(P(5, 0), mid!.Value.Point);
        Assert.Equal(SnapModes.Midpoint, mid.Value.Mode);

        Assert.Null(SnapEngine.FindSnap(doc, P(2.5, 0), 0.5, All)); // on the line but near no snap point
        Assert.Null(SnapEngine.FindSnap(doc, P(10, 2), 0.5, All));  // outside the aperture
    }

    [Fact]
    public void Nearest_snap_point_wins()
    {
        var doc = new DrawingDocument();
        AddLine(doc, 0, 0, 10, 0);
        AddLine(doc, 10.4, 0, 20, 0);
        var snap = SnapEngine.FindSnap(doc, P(10.3, 0), 1.0, All);
        Assert.Equal(P(10.4, 0), snap!.Value.Point);
    }

    [Fact]
    public void Mode_filter_is_respected()
    {
        var doc = new DrawingDocument();
        AddLine(doc, 0, 0, 10, 0);
        Assert.Null(SnapEngine.FindSnap(doc, P(5, 0), 0.5, SnapModes.Endpoint));
        Assert.NotNull(SnapEngine.FindSnap(doc, P(5, 0), 0.5, SnapModes.Midpoint));
        Assert.Null(SnapEngine.FindSnap(doc, P(0, 0), 0.5, SnapModes.None));
    }

    [Fact]
    public void Polyline_vertices_and_segment_midpoints_including_the_closing_segment()
    {
        var doc = new DrawingDocument();
        doc.AddEntity(new PolylineEntity(new[] { P(0, 0), P(4, 0), P(4, 3) }, true) { LayerId = doc.CurrentLayerId });
        Assert.Equal(P(4, 3), SnapEngine.FindSnap(doc, P(4.1, 3.1), 0.3, All)!.Value.Point);
        Assert.Equal(P(4, 1.5), SnapEngine.FindSnap(doc, P(4.1, 1.4), 0.3, All)!.Value.Point);
        var closing = SnapEngine.FindSnap(doc, P(2.05, 1.45), 0.3, All);
        Assert.Equal(P(2, 1.5), closing!.Value.Point);
        Assert.Equal(SnapModes.Midpoint, closing.Value.Mode);
    }

    [Fact]
    public void Circle_center()
    {
        var doc = new DrawingDocument();
        AddCircle(doc, 50, 50, 20);
        var snap = SnapEngine.FindSnap(doc, P(50.2, 49.9), 0.5, All);
        Assert.Equal(P(50, 50), snap!.Value.Point);
        Assert.Equal(SnapModes.Center, snap.Value.Mode);
    }

    [Fact]
    public void Frozen_and_hidden_layers_do_not_snap_but_locked_layers_do()
    {
        var doc = new DrawingDocument();
        var layer = new Layer(Guid.NewGuid(), "L1");
        doc.AddLayer(layer);
        doc.AddEntity(new LineEntity(P(0, 0), P(10, 0)) { LayerId = layer.Id });

        var locked = layer.Clone(); locked.IsLocked = true;
        doc.ReplaceLayer(locked);
        Assert.NotNull(SnapEngine.FindSnap(doc, P(0, 0), 0.5, All));

        var frozen = layer.Clone(); frozen.IsFrozen = true;
        doc.ReplaceLayer(frozen);
        Assert.Null(SnapEngine.FindSnap(doc, P(0, 0), 0.5, All));

        var hidden = layer.Clone(); hidden.IsVisible = false;
        doc.ReplaceLayer(hidden);
        Assert.Null(SnapEngine.FindSnap(doc, P(0, 0), 0.5, All));
    }

    [Fact]
    public void Works_in_a_large_drawing()
    {
        var doc = new DrawingDocument();
        using (doc.BeginBatch())
            for (int x = 0; x < 200; x++)
            for (int y = 0; y < 100; y++)
                AddLine(doc, x * 10, y * 10, x * 10 + 6, y * 10 + 4);
        Assert.Equal(20000, doc.EntityCount);
        var snap = SnapEngine.FindSnap(doc, P(1236.1, 783.9), 0.5, All);
        Assert.Equal(P(1236, 784), snap!.Value.Point);
    }
}

public class SelectionTests
{
    [Fact]
    public void Pick_selects_the_nearest_entity_within_tolerance()
    {
        var doc = new DrawingDocument();
        var a = AddLine(doc, 0, 0, 10, 0);
        var b = AddLine(doc, 0, 1, 10, 1);
        Assert.Equal(a.Id, SelectionService.PickAt(doc, P(5, 0.2), 0.5)!.Id);
        Assert.Equal(b.Id, SelectionService.PickAt(doc, P(5, 0.8), 0.5)!.Id);
        Assert.Null(SelectionService.PickAt(doc, P(5, 5), 0.5));

        // A circle is picked on its circumference, not its interior.
        var c = AddCircle(doc, 100, 100, 10);
        Assert.Null(SelectionService.PickAt(doc, P(100, 100), 0.5));
        Assert.Equal(c.Id, SelectionService.PickAt(doc, P(110.2, 100), 0.5)!.Id);
    }

    [Fact]
    public void Window_needs_full_containment_and_crossing_needs_contact()
    {
        var doc = new DrawingDocument();
        var inside = AddLine(doc, 1, 1, 4, 4);
        var crossing = AddLine(doc, 3, 3, 20, 3);
        var outside = AddLine(doc, 30, 30, 40, 40);
        var bigCircle = AddCircle(doc, 2.5, 2.5, 50); // the box lies entirely inside this circle
        var box = new BoundingBox(0, 0, 5, 5);

        var window = SelectionService.SelectInBox(doc, box, crossing: false).Select(e => e.Id).ToList();
        Assert.Equal(new[] { inside.Id }, window.ToArray());

        var cross = SelectionService.SelectInBox(doc, box, crossing: true).Select(e => e.Id).ToList();
        Assert.Equal(new[] { inside.Id, crossing.Id }, cross.ToArray());
        Assert.DoesNotContain(outside.Id, cross);
        Assert.DoesNotContain(bigCircle.Id, cross);
    }

    [Fact]
    public void Locked_and_hidden_layers_cannot_be_selected()
    {
        var doc = new DrawingDocument();
        var layer = new Layer(Guid.NewGuid(), "LOCKED") { IsLocked = true };
        doc.AddLayer(layer);
        doc.AddEntity(new LineEntity(P(0, 0), P(10, 0)) { LayerId = layer.Id });
        Assert.Null(SelectionService.PickAt(doc, P(5, 0), 0.5));
        Assert.Empty(SelectionService.SelectInBox(doc, new BoundingBox(-1, -1, 11, 1), crossing: true));
    }
}
