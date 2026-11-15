using System.Globalization;
using System.Text.Json;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Documents;

/// <summary>Raised when a file is not a valid HCAD document or cannot be read by this version.</summary>
public sealed class HcadFormatException : Exception
{
    public HcadFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Information stored in an autosave/recovery snapshot.</summary>
public sealed record RecoveryInfo(string? OriginalPath, DateTime SavedUtc);

public sealed class HcadLoadResult
{
    public HcadLoadResult(DrawingDocument document, IReadOnlyList<string> warnings, int schemaVersion, RecoveryInfo? recovery)
    {
        Document = document;
        Warnings = warnings;
        SchemaVersion = schemaVersion;
        Recovery = recovery;
    }

    public DrawingDocument Document { get; }

    /// <summary>Non-fatal problems found while reading. Never empty silently: every repair is reported.</summary>
    public IReadOnlyList<string> Warnings { get; }
    public int SchemaVersion { get; }
    public RecoveryInfo? Recovery { get; }
}

/// <summary>
/// Reads and writes the native HCAD format (schema documented in docs/HCAD-FORMAT.md).
/// </summary>
public static class HcadSerializer
{
    public const string FormatName = "HCAD";

    /// <summary>Schema version written by this build.</summary>
    public const int SchemaVersion = 3;

    /// <summary>
    /// Oldest reader that can safely open files written by this build. A file whose
    /// minReaderVersion is greater than <see cref="SchemaVersion"/> is refused.
    /// </summary>
    public const int MinReaderVersion = 1;

    public const string Generator = "HAQQ CAD 0.2.0";

    // ------------------------------------------------------------------ write

    public static byte[] SaveToBytes(DrawingDocument document, RecoveryInfo? recovery = null)
    {
        using var stream = new MemoryStream();
        Save(document, stream, recovery);
        return stream.ToArray();
    }

    public static void Save(DrawingDocument document, Stream stream, RecoveryInfo? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        w.WriteStartObject();
        w.WriteString("format", FormatName);
        w.WriteNumber("schemaVersion", SchemaVersion);
        // Polylines with arc segments or a width would be silently straightened or thinned by a
        // program that only knows schema 2, so such files require a schema 3 reader.
        bool needsSchema3 = document.EntitiesUnordered.Any(e => e is PolylineEntity p && (p.HasArcs || p.HasWidths));
        w.WriteNumber("minReaderVersion", needsSchema3 ? 3 : MinReaderVersion);
        w.WriteString("generator", Generator);

        if (recovery is not null)
        {
            w.WriteStartObject("recovery");
            if (recovery.OriginalPath is null) w.WriteNull("originalPath"); else w.WriteString("originalPath", recovery.OriginalPath);
            w.WriteString("savedUtc", FormatDate(recovery.SavedUtc));
            w.WriteEndObject();
        }

        w.WriteStartObject("document");
        w.WriteString("id", document.Id);

        var s = document.Settings;
        w.WriteStartObject("units");
        w.WriteString("linear", s.Unit.ToString());
        w.WriteString("format", s.Format.ToString());
        w.WriteNumber("decimalPrecision", s.DecimalPrecision);
        w.WriteNumber("fractionDenominator", s.FractionDenominator);
        w.WriteNumber("gridSpacing", s.GridSpacing);
        w.WriteNumber("snapSpacing", s.SnapSpacing);
        w.WriteEndObject();

        var m = document.Metadata;
        w.WriteStartObject("metadata");
        w.WriteString("projectName", m.ProjectName);
        w.WriteString("description", m.Description);
        w.WriteString("author", m.Author);
        w.WriteString("revision", m.Revision);
        w.WriteString("createdUtc", FormatDate(m.CreatedUtc));
        w.WriteString("modifiedUtc", FormatDate(m.ModifiedUtc));
        w.WriteEndObject();

        WriteProperties(w, "customProperties", document.CustomProperties);

        w.WriteString("currentLayerId", document.CurrentLayerId);

        w.WriteStartArray("layers");
        foreach (var layer in document.Layers)
        {
            w.WriteStartObject();
            w.WriteString("id", layer.Id);
            w.WriteString("name", layer.Name);
            w.WriteString("color", layer.Color.ToString());
            w.WriteString("linetype", layer.Linetype);
            w.WriteNumber("lineweightMm", layer.LineweightMm);
            w.WriteBoolean("visible", layer.IsVisible);
            w.WriteBoolean("locked", layer.IsLocked);
            w.WriteBoolean("frozen", layer.IsFrozen);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("entities");
        foreach (var entity in document.Entities) WriteEntity(w, entity);
        foreach (string raw in document.UnknownEntities) w.WriteRawValue(raw, skipInputValidation: false);
        w.WriteEndArray();

        w.WriteEndObject(); // document
        w.WriteEndObject();
        w.Flush();
    }

    private static void WriteEntity(Utf8JsonWriter w, Entity entity)
    {
        w.WriteStartObject();
        w.WriteString("type", entity.TypeName);
        w.WriteString("id", entity.Id);
        w.WriteString("layerId", entity.LayerId);
        w.WriteString("color", entity.Color.ToString());
        w.WriteString("linetype", entity.Linetype);
        if (entity.LineweightMm is double lw) w.WriteNumber("lineweightMm", lw); else w.WriteNull("lineweightMm");
        if (entity.CustomProperties.Count > 0) WriteProperties(w, "properties", entity.CustomProperties);

        switch (entity)
        {
            case LineEntity line:
                WritePoint(w, "start", line.Start);
                WritePoint(w, "end", line.End);
                break;
            case CircleEntity circle:
                WritePoint(w, "center", circle.Center);
                w.WriteNumber("radius", circle.Radius);
                break;
            case ArcEntity arc:
                WritePoint(w, "center", arc.Center);
                w.WriteNumber("radius", arc.Radius);
                w.WriteNumber("startAngle", arc.StartAngle);
                w.WriteNumber("endAngle", arc.EndAngle);
                break;
            case PolylineEntity polyline:
                w.WriteBoolean("closed", polyline.IsClosed);
                if (polyline.HasWidths)
                {
                    if (polyline.HasUniformWidth)
                    {
                        w.WriteNumber("width", polyline.Width);
                    }
                    else
                    {
                        // One [start, end] pair per vertex: the widths of the segment starting there.
                        w.WriteStartArray("widths");
                        for (int i = 0; i < polyline.Vertices.Count; i++)
                        {
                            var (ws, we) = i < polyline.SegmentCount ? polyline.GetSegmentWidth(i) : (0.0, 0.0);
                            w.WriteStartArray();
                            w.WriteNumberValue(ws);
                            w.WriteNumberValue(we);
                            w.WriteEndArray();
                        }
                        w.WriteEndArray();
                    }
                }
                if (polyline.HasArcs)
                {
                    w.WriteStartArray("bulges");
                    foreach (double bulge in polyline.Bulges) w.WriteNumberValue(bulge);
                    w.WriteEndArray();
                }
                w.WriteStartArray("vertices");
                foreach (var v in polyline.Vertices) WritePointValue(w, v);
                w.WriteEndArray();
                break;
            default:
                throw new NotSupportedException($"Entity type '{entity.TypeName}' has no HCAD writer.");
        }
        w.WriteEndObject();
    }

    private static void WritePoint(Utf8JsonWriter w, string name, Point2D p)
    {
        w.WritePropertyName(name);
        WritePointValue(w, p);
    }

    private static void WritePointValue(Utf8JsonWriter w, Point2D p)
    {
        // Doubles are written in shortest round-trippable form: no precision is lost.
        w.WriteStartArray();
        w.WriteNumberValue(p.X);
        w.WriteNumberValue(p.Y);
        w.WriteEndArray();
    }

    private static void WriteProperties(Utf8JsonWriter w, string name, IReadOnlyDictionary<string, string> properties)
    {
        w.WriteStartObject(name);
        foreach (var pair in properties.OrderBy(p => p.Key, StringComparer.Ordinal)) w.WriteString(pair.Key, pair.Value);
        w.WriteEndObject();
    }

    private static string FormatDate(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------- read

    public static HcadLoadResult LoadFromBytes(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        return Load(stream);
    }

    public static HcadLoadResult Load(Stream stream)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            throw new HcadFormatException("The file is not a valid HCAD document (it is damaged or not JSON).", ex);
        }

        using (json)
        {
            try
            {
                return Read(json.RootElement);
            }
            catch (HcadFormatException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException)
            {
                throw new HcadFormatException("The HCAD document is malformed: " + ex.Message, ex);
            }
        }
    }

    private static HcadLoadResult Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("format", out var format) || format.GetString() != FormatName)
            throw new HcadFormatException("The file is not an HCAD document.");

        int schema = root.TryGetProperty("schemaVersion", out var sv) ? sv.GetInt32() : 1;
        int minReader = root.TryGetProperty("minReaderVersion", out var mr) ? mr.GetInt32() : schema;
        if (minReader > SchemaVersion)
            throw new HcadFormatException(
                $"This drawing was saved by a newer version of HAQQ CAD (file schema {schema}, needs reader {minReader}; " +
                $"this version reads up to {SchemaVersion}). Update HAQQ CAD to open it.");

        var warnings = new List<string>();
        if (schema > SchemaVersion)
            warnings.Add($"The drawing was saved by a newer HAQQ CAD (schema {schema}). Objects this version does not understand are kept but not shown.");

        RecoveryInfo? recovery = null;
        if (root.TryGetProperty("recovery", out var rec) && rec.ValueKind == JsonValueKind.Object)
        {
            string? original = rec.TryGetProperty("originalPath", out var op) && op.ValueKind == JsonValueKind.String ? op.GetString() : null;
            DateTime saved = rec.TryGetProperty("savedUtc", out var su) ? ParseDate(su) : DateTime.UtcNow;
            recovery = new RecoveryInfo(original, saved);
        }

        if (!root.TryGetProperty("document", out var doc) || doc.ValueKind != JsonValueKind.Object)
            throw new HcadFormatException("The HCAD file has no document section.");

        Guid docId = doc.TryGetProperty("id", out var idEl) && idEl.TryGetGuid(out var parsedId) ? parsedId : Guid.NewGuid();
        var document = new DrawingDocument(docId, createDefaultLayer: false);

        if (doc.TryGetProperty("units", out var units) && units.ValueKind == JsonValueKind.Object)
        {
            var s = document.Settings;
            if (units.TryGetProperty("linear", out var lin))
            {
                if (Enum.TryParse<LinearUnit>(lin.GetString(), ignoreCase: true, out var unit)) s.Unit = unit;
                else warnings.Add($"Unknown drawing unit \"{lin.GetString()}\"; using {s.Unit}.");
            }
            if (units.TryGetProperty("format", out var fmt))
            {
                if (Enum.TryParse<LengthFormat>(fmt.GetString(), ignoreCase: true, out var lf)) s.Format = lf;
                else warnings.Add($"Unknown length format \"{fmt.GetString()}\"; using {s.Format}.");
            }
            if (units.TryGetProperty("decimalPrecision", out var dp)) s.DecimalPrecision = Math.Clamp(dp.GetInt32(), 0, 12);
            if (units.TryGetProperty("fractionDenominator", out var fd)) s.FractionDenominator = Math.Max(1, fd.GetInt32());
            if (units.TryGetProperty("gridSpacing", out var gs) && gs.GetDouble() > 0) s.GridSpacing = gs.GetDouble();
            if (units.TryGetProperty("snapSpacing", out var ss) && ss.GetDouble() > 0) s.SnapSpacing = ss.GetDouble();
        }

        if (doc.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            var md = document.Metadata;
            md.ProjectName = GetString(meta, "projectName");
            md.Description = GetString(meta, "description");
            md.Author = GetString(meta, "author");
            md.Revision = GetString(meta, "revision");
            if (meta.TryGetProperty("createdUtc", out var c)) md.CreatedUtc = ParseDate(c);
            if (meta.TryGetProperty("modifiedUtc", out var mo)) md.ModifiedUtc = ParseDate(mo);
        }
        DateTime modified = document.Metadata.ModifiedUtc;

        ReadProperties(doc, "customProperties", document.CustomProperties);

        // Layers
        if (doc.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
        {
            foreach (var le in layers.EnumerateArray())
            {
                string name = GetString(le, "name");
                if (!le.TryGetProperty("id", out var lid) || !lid.TryGetGuid(out var layerId) || name.Length == 0)
                {
                    warnings.Add("A layer without a valid id or name was skipped.");
                    continue;
                }
                if (document.GetLayer(layerId) is not null || document.FindLayer(name) is not null)
                {
                    warnings.Add($"Duplicate layer \"{name}\" was skipped.");
                    continue;
                }
                var layer = new Layer(layerId, name);
                if (CadColor.TryParse(GetString(le, "color"), out var color) && color.IsExplicit) layer.Color = color;
                string linetype = GetString(le, "linetype");
                if (linetype.Length > 0) layer.Linetype = linetype;
                if (le.TryGetProperty("lineweightMm", out var lw) && lw.ValueKind == JsonValueKind.Number) layer.LineweightMm = lw.GetDouble();
                layer.IsVisible = GetBool(le, "visible", true);
                layer.IsLocked = GetBool(le, "locked", false);
                layer.IsFrozen = GetBool(le, "frozen", false);
                document.AddLayer(layer);
            }
        }

        var layer0 = document.FindLayer(Layer.DefaultLayerName);
        if (layer0 is null)
        {
            layer0 = new Layer(Guid.NewGuid(), Layer.DefaultLayerName);
            document.AddLayer(layer0, 0);
            warnings.Add("Layer 0 was missing and has been recreated.");
        }

        if (doc.TryGetProperty("currentLayerId", out var cur) && cur.TryGetGuid(out var curId) && document.GetLayer(curId) is not null)
            document.CurrentLayerId = curId;
        else
            document.CurrentLayerId = layer0.Id;

        // Entities
        if (doc.TryGetProperty("entities", out var entities) && entities.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var ee in entities.EnumerateArray())
            {
                index++;
                string type = ee.ValueKind == JsonValueKind.Object ? GetString(ee, "type") : "";
                Entity? entity = null;
                string? problem = null;
                try
                {
                    entity = ReadEntity(ee, type, out problem);
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException)
                {
                    problem = ex.Message;
                }

                if (entity is null)
                {
                    // Unknown or unreadable: keep the raw JSON so saving does not lose it.
                    document.UnknownEntities.Add(ee.GetRawText());
                    warnings.Add(problem is null
                        ? $"Object #{index} of unsupported type \"{type}\" was kept in the file but cannot be shown or edited."
                        : $"Object #{index} ({type}) could not be read and was kept unmodified in the file: {problem}");
                    continue;
                }

                if (document.ContainsEntity(entity.Id))
                {
                    document.UnknownEntities.Add(ee.GetRawText());
                    warnings.Add($"Object #{index} ({type}) has a duplicate id and was kept unmodified in the file.");
                    continue;
                }

                if (document.GetLayer(entity.LayerId) is null)
                {
                    entity.LayerId = layer0.Id;
                    warnings.Add($"Object #{index} ({type}) referenced a missing layer and was moved to layer 0.");
                }

                document.AddEntity(entity);
            }
        }

        document.Metadata.ModifiedUtc = modified;
        document.MarkSaved();
        return new HcadLoadResult(document, warnings, schema, recovery);
    }

    private static Entity? ReadEntity(JsonElement e, string type, out string? problem)
    {
        problem = null;
        if (e.ValueKind != JsonValueKind.Object) { problem = "not a JSON object"; return null; }

        Entity entity;
        switch (type)
        {
            case LineEntity.Type:
                entity = new LineEntity(ReadId(e), ReadPoint(e.GetProperty("start")), ReadPoint(e.GetProperty("end")));
                break;
            case CircleEntity.Type:
                entity = new CircleEntity(ReadId(e), ReadPoint(e.GetProperty("center")), e.GetProperty("radius").GetDouble());
                break;
            case ArcEntity.Type:
                entity = new ArcEntity(ReadId(e), ReadPoint(e.GetProperty("center")), e.GetProperty("radius").GetDouble(),
                    e.GetProperty("startAngle").GetDouble(), e.GetProperty("endAngle").GetDouble());
                break;
            case PolylineEntity.Type:
                var vertices = new List<Point2D>();
                foreach (var v in e.GetProperty("vertices").EnumerateArray()) vertices.Add(ReadPoint(v));
                List<double>? bulges = null;
                if (e.TryGetProperty("bulges", out var bulgeArray) && bulgeArray.ValueKind == JsonValueKind.Array)
                {
                    bulges = new List<double>();
                    foreach (var b in bulgeArray.EnumerateArray()) bulges.Add(b.GetDouble());
                }
                var polylineEntity = new PolylineEntity(ReadId(e), vertices, GetBool(e, "closed", false), bulges);
                if (e.TryGetProperty("width", out var widthValue) && widthValue.ValueKind == JsonValueKind.Number)
                    polylineEntity.Width = widthValue.GetDouble();
                if (e.TryGetProperty("widths", out var widthPairs) && widthPairs.ValueKind == JsonValueKind.Array)
                {
                    int index = 0;
                    foreach (var pair in widthPairs.EnumerateArray())
                    {
                        if (index >= vertices.Count) break;
                        if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2) throw new FormatException("a width must be [start, end]");
                        polylineEntity.SetSegmentWidth(index++, pair[0].GetDouble(), pair[1].GetDouble());
                    }
                }
                entity = polylineEntity;
                break;
            default:
                return null; // unknown type: caller preserves the raw JSON
        }

        if (!entity.IsValid) { problem = "invalid geometry"; return null; }

        if (e.TryGetProperty("layerId", out var lid) && lid.TryGetGuid(out var layerId)) entity.LayerId = layerId;
        if (CadColor.TryParse(GetString(e, "color"), out var color)) entity.Color = color;
        string linetype = GetString(e, "linetype");
        if (linetype.Length > 0) entity.Linetype = linetype;
        if (e.TryGetProperty("lineweightMm", out var lw) && lw.ValueKind == JsonValueKind.Number) entity.LineweightMm = lw.GetDouble();
        ReadProperties(e, "properties", entity.CustomProperties);
        return entity;
    }

    private static Guid ReadId(JsonElement e)
    {
        if (e.TryGetProperty("id", out var id) && id.TryGetGuid(out var guid) && guid != Guid.Empty) return guid;
        throw new FormatException("missing or invalid id");
    }

    private static Point2D ReadPoint(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 2) throw new FormatException("a point must be [x, y]");
        var p = new Point2D(e[0].GetDouble(), e[1].GetDouble());
        if (!p.IsFinite) throw new FormatException("non-finite coordinate");
        return p;
    }

    private static void ReadProperties(JsonElement parent, string name, Dictionary<string, string> target)
    {
        if (!parent.TryGetProperty(name, out var props) || props.ValueKind != JsonValueKind.Object) return;
        foreach (var p in props.EnumerateObject())
            target[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
    }

    private static string GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool GetBool(JsonElement e, string name, bool fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

    private static DateTime ParseDate(JsonElement e) =>
        e.ValueKind == JsonValueKind.String &&
        DateTime.TryParse(e.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : DateTime.UtcNow;
}
