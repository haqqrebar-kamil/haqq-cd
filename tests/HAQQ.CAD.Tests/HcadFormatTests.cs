using System.Text;
using HAQQ.CAD.Core;
using HAQQ.CAD.Documents;
using HAQQ.CAD.Geometry;
using Xunit;
using static HAQQ.CAD.Tests.T;

namespace HAQQ.CAD.Tests;

public class HcadSerializationTests
{
    private static DrawingDocument BuildSample()
    {
        var doc = new DrawingDocument();
        doc.Settings.Unit = LinearUnit.Inch;
        doc.Settings.Format = LengthFormat.Architectural;
        doc.Settings.FractionDenominator = 32;
        doc.Settings.DecimalPrecision = 3;
        doc.Settings.GridSpacing = 6;
        doc.Metadata.ProjectName = "Warehouse footing F-3";
        doc.Metadata.Author = "HAQQ Rebar";
        doc.Metadata.Revision = "B";
        doc.Metadata.Description = "Unicode check: Ø16 @ 200 — \"quoted\"";
        doc.Metadata.CreatedUtc = new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);
        doc.CustomProperties["client"] = "ACME";

        var rebar = new Layer(Guid.NewGuid(), "REBAR")
        {
            Color = CadColor.FromRgb(255, 0, 0),
            Linetype = "Short Dash",
            LineweightMm = 0.5,
            IsLocked = true,
        };
        var hidden = new Layer(Guid.NewGuid(), "NOTES") { IsVisible = false, IsFrozen = true };
        doc.AddLayer(rebar);
        doc.AddLayer(hidden);
        doc.CurrentLayerId = rebar.Id;

        var line = new LineEntity(P(0.1 + 0.2, 1e-7), P(123456789.123456789, -Math.PI))
        {
            LayerId = rebar.Id,
            Color = CadColor.FromRgb(10, 20, 30),
            Linetype = "Long Short",
            LineweightMm = 0.35,
        };
        line.CustomProperties["barMark"] = "A1";
        line.CustomProperties["note"] = "top \"T\" layer";
        doc.AddEntity(line);
        doc.AddEntity(new CircleEntity(P(-5.5, 7.25), 1.0 / 3.0) { LayerId = doc.Layers[0].Id, Color = CadColor.ByBlock });
        doc.AddEntity(new PolylineEntity(new[] { P(0, 0), P(4, 0), P(4, 3), P(0, 3) }, true) { LayerId = hidden.Id });
        doc.AddEntity(new PolylineEntity(new[] { P(1, 1), P(2, 5) }, false) { LayerId = rebar.Id });
        doc.AddEntity(new ArcEntity(P(10.25, -3), 2.0 / 3.0, 0.1, 4.0) { LayerId = rebar.Id });
        doc.Settings.SnapSpacing = 0.125;
        return doc;
    }

    private static void AssertSameDocument(DrawingDocument expected, DrawingDocument actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Settings.Unit, actual.Settings.Unit);
        Assert.Equal(expected.Settings.Format, actual.Settings.Format);
        Assert.Equal(expected.Settings.FractionDenominator, actual.Settings.FractionDenominator);
        Assert.Equal(expected.Settings.DecimalPrecision, actual.Settings.DecimalPrecision);
        Assert.Equal(expected.Settings.GridSpacing, actual.Settings.GridSpacing);
        Assert.Equal(expected.Settings.SnapSpacing, actual.Settings.SnapSpacing);
        Assert.Equal(expected.Metadata.ProjectName, actual.Metadata.ProjectName);
        Assert.Equal(expected.Metadata.Author, actual.Metadata.Author);
        Assert.Equal(expected.Metadata.Revision, actual.Metadata.Revision);
        Assert.Equal(expected.Metadata.Description, actual.Metadata.Description);
        Assert.Equal(expected.Metadata.CreatedUtc, actual.Metadata.CreatedUtc);
        Assert.Equal(expected.CustomProperties.OrderBy(p => p.Key).ToList(), actual.CustomProperties.OrderBy(p => p.Key).ToList());
        Assert.Equal(expected.CurrentLayerId, actual.CurrentLayerId);

        Assert.Equal(expected.Layers.Count, actual.Layers.Count);
        for (int i = 0; i < expected.Layers.Count; i++)
        {
            Layer e = expected.Layers[i], a = actual.Layers[i];
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.Name, a.Name);
            Assert.Equal(e.Color, a.Color);
            Assert.Equal(e.Linetype, a.Linetype);
            Assert.Equal(e.LineweightMm, a.LineweightMm);
            Assert.Equal(e.IsVisible, a.IsVisible);
            Assert.Equal(e.IsLocked, a.IsLocked);
            Assert.Equal(e.IsFrozen, a.IsFrozen);
        }

        Assert.Equal(expected.EntityCount, actual.EntityCount);
        for (int i = 0; i < expected.EntityCount; i++)
        {
            Entity e = expected.Entities[i], a = actual.Entities[i];
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.TypeName, a.TypeName);
            Assert.Equal(e.LayerId, a.LayerId);
            Assert.Equal(e.Color, a.Color);
            Assert.Equal(e.Linetype, a.Linetype);
            Assert.Equal(e.LineweightMm, a.LineweightMm);
            Assert.Equal(e.CustomProperties.OrderBy(p => p.Key).ToList(), a.CustomProperties.OrderBy(p => p.Key).ToList());
            switch (e)
            {
                case LineEntity l:
                    // Exact equality: coordinates must survive bit-for-bit.
                    Assert.Equal(l.Start, ((LineEntity)a).Start);
                    Assert.Equal(l.End, ((LineEntity)a).End);
                    break;
                case CircleEntity c:
                    Assert.Equal(c.Center, ((CircleEntity)a).Center);
                    Assert.Equal(c.Radius, ((CircleEntity)a).Radius);
                    break;
                case ArcEntity arc:
                    Assert.Equal(arc.Center, ((ArcEntity)a).Center);
                    Assert.Equal(arc.Radius, ((ArcEntity)a).Radius);
                    Assert.Equal(arc.StartAngle, ((ArcEntity)a).StartAngle);
                    Assert.Equal(arc.EndAngle, ((ArcEntity)a).EndAngle);
                    break;
                case PolylineEntity p:
                    Assert.Equal(p.IsClosed, ((PolylineEntity)a).IsClosed);
                    Assert.Equal(p.Vertices.ToArray(), ((PolylineEntity)a).Vertices.ToArray());
                    break;
            }
        }
    }

    [Fact]
    public void Round_trip_preserves_everything_exactly()
    {
        var doc = BuildSample();
        byte[] bytes = HcadSerializer.SaveToBytes(doc);
        var result = HcadSerializer.LoadFromBytes(bytes);

        Assert.Empty(result.Warnings);
        Assert.Equal(HcadSerializer.SchemaVersion, result.SchemaVersion);
        Assert.Null(result.Recovery);
        Assert.False(result.Document.IsDirty);
        AssertSameDocument(doc, result.Document);
    }

    [Fact]
    public void Saving_a_loaded_file_reproduces_it_byte_for_byte()
    {
        byte[] first = HcadSerializer.SaveToBytes(BuildSample());
        byte[] second = HcadSerializer.SaveToBytes(HcadSerializer.LoadFromBytes(first).Document);
        Assert.Equal(Encoding.UTF8.GetString(first), Encoding.UTF8.GetString(second));
    }

    [Fact]
    public void File_declares_format_and_versions()
    {
        string json = Encoding.UTF8.GetString(HcadSerializer.SaveToBytes(new DrawingDocument()));
        Assert.Contains("\"format\": \"HCAD\"", json);
        Assert.Contains($"\"schemaVersion\": {HcadSerializer.SchemaVersion}", json);
        Assert.Contains("\"minReaderVersion\": 1", json);
    }

    [Fact]
    public void Schema_1_files_written_by_the_first_release_still_open()
    {
        string json = Encoding.UTF8.GetString(HcadSerializer.SaveToBytes(BuildLines()))
            .Replace($"\"schemaVersion\": {HcadSerializer.SchemaVersion}", "\"schemaVersion\": 1")
            .Replace("\"snapSpacing\": 1,", "");
        var loaded = HcadSerializer.LoadFromBytes(Encoding.UTF8.GetBytes(json));
        Assert.Empty(loaded.Warnings);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(2, loaded.Document.EntityCount);
        Assert.Equal(1.0, loaded.Document.Settings.SnapSpacing);
    }

    private static DrawingDocument BuildLines()
    {
        var doc = new DrawingDocument();
        AddLine(doc, 0, 0, 10, 0);
        AddCircle(doc, 5, 5, 2);
        return doc;
    }

    [Fact]
    public void Unknown_entity_types_are_preserved_and_reported_not_dropped()
    {
        var doc = new DrawingDocument();
        AddLine(doc, 0, 0, 1, 1);
        string json = Encoding.UTF8.GetString(HcadSerializer.SaveToBytes(doc));
        string future = "{ \"type\": \"rebar\", \"id\": \"" + Guid.NewGuid() + "\", \"barMark\": \"B12\", \"segments\": [[0, 0], [10, 0]] }";
        int at = json.LastIndexOf(']');
        json = json[..at] + ", " + future + json[at..];

        var loaded = HcadSerializer.LoadFromBytes(Encoding.UTF8.GetBytes(json));
        Assert.Equal(1, loaded.Document.EntityCount);
        Assert.Single(loaded.Document.UnknownEntities);
        Assert.Contains("rebar", Assert.Single(loaded.Warnings));

        string saved = Encoding.UTF8.GetString(HcadSerializer.SaveToBytes(loaded.Document));
        Assert.Contains("\"barMark\": \"B12\"", saved);

        var again = HcadSerializer.LoadFromBytes(Encoding.UTF8.GetBytes(saved));
        Assert.Single(again.Document.UnknownEntities);
        Assert.Equal(1, again.Document.EntityCount);
    }

    [Fact]
    public void Files_that_need_a_newer_reader_are_refused()
    {
        string json = Encoding.UTF8.GetString(HcadSerializer.SaveToBytes(new DrawingDocument()))
            .Replace("\"minReaderVersion\": 1", "\"minReaderVersion\": 99")
            .Replace($"\"schemaVersion\": {HcadSerializer.SchemaVersion}", "\"schemaVersion\": 99");
        var ex = Assert.Throws<HcadFormatException>(() => HcadSerializer.LoadFromBytes(Encoding.UTF8.GetBytes(json)));
        Assert.Contains("newer version", ex.Message);
    }

    [Fact]
    public void Newer_schema_that_is_still_readable_loads_with_a_warning()
    {
        string json = Encoding.UTF8.GetString(HcadSerializer.SaveToBytes(new DrawingDocument()))
            .Replace($"\"schemaVersion\": {HcadSerializer.SchemaVersion}", "\"schemaVersion\": 77");
        var loaded = HcadSerializer.LoadFromBytes(Encoding.UTF8.GetBytes(json));
        Assert.Equal(77, loaded.SchemaVersion);
        Assert.Contains("newer", Assert.Single(loaded.Warnings));
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{ \"format\": \"DXF\" }")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"format\": \"HCAD\", \"schemaVersion\": 1 }")]
    [InlineData("{ \"format\": \"HCAD\", \"schemaVersion\": \"one\", \"document\": {} }")]
    [InlineData("")]
    public void Damaged_or_foreign_files_raise_a_format_error(string content)
    {
        Assert.Throws<HcadFormatException>(() => HcadSerializer.LoadFromBytes(Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public void Truncated_file_is_rejected()
    {
        byte[] bytes = HcadSerializer.SaveToBytes(BuildSample());
        byte[] half = bytes.Take(bytes.Length / 2).ToArray();
        Assert.Throws<HcadFormatException>(() => HcadSerializer.LoadFromBytes(half));
    }

    [Fact]
    public void Repairs_are_reported_as_warnings()
    {
        var doc = new DrawingDocument();
        var line = AddLine(doc, 0, 0, 1, 1);
        var circle = AddCircle(doc, 5, 5, 2);
        string json = Encoding.UTF8.GetString(HcadSerializer.SaveToBytes(doc));

        // Point the line at a layer that does not exist and make the circle's radius invalid.
        int lineAt = json.IndexOf(line.Id.ToString(), StringComparison.Ordinal);
        int layerAt = json.IndexOf(doc.CurrentLayerId.ToString(), lineAt, StringComparison.Ordinal);
        json = json[..layerAt] + Guid.NewGuid() + json[(layerAt + 36)..];
        json = json.Replace("\"radius\": 2", "\"radius\": 0");

        var loaded = HcadSerializer.LoadFromBytes(Encoding.UTF8.GetBytes(json));
        Assert.Equal(2, loaded.Warnings.Count);
        Assert.Contains(loaded.Warnings, w => w.Contains("missing layer"));
        Assert.Contains(loaded.Warnings, w => w.Contains("invalid geometry"));

        var kept = Assert.Single(loaded.Document.Entities);
        Assert.Equal(line.Id, kept.Id);
        Assert.Equal(loaded.Document.FindLayer("0")!.Id, kept.LayerId);
        Assert.Contains(circle.Id.ToString(), Assert.Single(loaded.Document.UnknownEntities)); // raw data kept
    }

    [Fact]
    public void Large_drawing_round_trips()
    {
        var doc = new DrawingDocument();
        var random = new Random(7);
        using (doc.BeginBatch())
            for (int i = 0; i < 20000; i++)
                AddLine(doc, random.NextDouble() * 1e5, random.NextDouble() * 1e5, random.NextDouble() * 1e5, random.NextDouble() * 1e5);

        var loaded = HcadSerializer.LoadFromBytes(HcadSerializer.SaveToBytes(doc)).Document;
        Assert.Equal(20000, loaded.EntityCount);
        for (int i = 0; i < 20000; i += 997)
        {
            Assert.Equal(doc.Entities[i].Id, loaded.Entities[i].Id);
            Assert.Equal(((LineEntity)doc.Entities[i]).End, ((LineEntity)loaded.Entities[i]).End);
        }
    }
}

public class HcadFileStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Save_and_load_from_disk()
    {
        var doc = new DrawingDocument();
        AddLine(doc, 0, 0, 12, 0);
        string path = _temp.File("a.hcad");
        Assert.True(doc.IsDirty);

        HcadFileStore.Save(doc, path);
        Assert.False(doc.IsDirty);
        Assert.True(File.Exists(path));

        var loaded = HcadFileStore.Load(path);
        Assert.Equal(1, loaded.Document.EntityCount);
        Assert.Equal(doc.Id, loaded.Document.Id);
    }

    [Fact]
    public void Saving_again_keeps_the_previous_version_as_backup_and_leaves_no_temp_files()
    {
        var doc = new DrawingDocument();
        AddLine(doc, 0, 0, 12, 0);
        string path = _temp.File("b.hcad");
        HcadFileStore.Save(doc, path);
        AddCircle(doc, 5, 5, 1);
        HcadFileStore.Save(doc, path);

        Assert.Equal(2, HcadFileStore.Load(path).Document.EntityCount);
        Assert.Equal(1, HcadFileStore.Load(path + ".bak").Document.EntityCount);
        var names = Directory.GetFiles(_temp.Path).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "b.hcad", "b.hcad.bak" }, names);
    }

    [Fact]
    public void A_failed_save_leaves_the_existing_file_untouched()
    {
        var doc = new DrawingDocument();
        AddLine(doc, 0, 0, 12, 0);
        string path = _temp.File("c.hcad");
        HcadFileStore.Save(doc, path);
        byte[] before = File.ReadAllBytes(path);

        // Hold the target open exclusively so the final swap cannot happen.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AddCircle(doc, 1, 1, 1);
            try { HcadFileStore.Save(doc, path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // Whether or not the platform enforces the lock, the file must be a complete, valid document.
        var loaded = HcadFileStore.Load(path);
        Assert.InRange(loaded.Document.EntityCount, 1, 2);
        if (loaded.Document.EntityCount == 1) Assert.Equal(before, File.ReadAllBytes(path));
        Assert.DoesNotContain(Directory.GetFiles(_temp.Path), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void A_leftover_partial_temp_file_does_not_affect_the_drawing()
    {
        var doc = new DrawingDocument();
        AddLine(doc, 0, 0, 12, 0);
        string path = _temp.File("d.hcad");
        HcadFileStore.Save(doc, path);

        // Simulate a crash in the middle of a later save: half-written temp file next to the drawing.
        File.WriteAllText(_temp.File("~d.hcad.deadbeef.tmp"), "{ \"format\": \"HCAD\", \"docu");
        Assert.Equal(1, HcadFileStore.Load(path).Document.EntityCount);
        HcadFileStore.Save(doc, path);
        Assert.Equal(1, HcadFileStore.Load(path).Document.EntityCount);
    }

    [Fact]
    public void Missing_file_and_missing_folder()
    {
        Assert.Throws<FileNotFoundException>(() => HcadFileStore.Load(_temp.File("nope.hcad")));

        // Saving creates missing folders.
        var doc = new DrawingDocument();
        string nested = Path.Combine(_temp.Path, "jobs", "2026", "e.hcad");
        HcadFileStore.Save(doc, nested);
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Recovery_snapshots_can_be_written_found_and_deleted()
    {
        var store = new RecoveryStore(_temp.File("recovery"));
        Assert.Empty(store.FindSnapshots());

        var doc = new DrawingDocument();
        AddLine(doc, 0, 0, 12, 0);
        store.WriteSnapshot(doc.Id, RecoveryStore.CreateSnapshot(doc, @"C:\Jobs\footing.hcad"));
        Assert.True(doc.IsDirty); // an autosave is not a save

        var found = Assert.Single(store.FindSnapshots());
        Assert.Equal(@"C:\Jobs\footing.hcad", found.OriginalPath);
        Assert.InRange(found.SavedUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));

        var recovered = HcadFileStore.Load(found.SnapshotPath);
        Assert.Equal(doc.Id, recovered.Document.Id);
        Assert.Equal(1, recovered.Document.EntityCount);
        Assert.NotNull(recovered.Recovery);

        // A damaged snapshot is skipped but left on disk.
        string damaged = Path.Combine(store.Directory, "broken" + RecoveryStore.SnapshotExtension);
        File.WriteAllText(damaged, "garbage");
        Assert.Single(store.FindSnapshots());
        Assert.True(File.Exists(damaged));

        store.DeleteSnapshot(doc.Id);
        Assert.Empty(store.FindSnapshots());
        store.DeleteSnapshot(doc.Id); // deleting twice is harmless
    }
}
