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
    public const int SchemaVersion = 9;

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
        bool needsSchema3 = document.AllEntities.Any(e => e is PolylineEntity p && (p.HasArcs || p.HasWidths));
        // A reader that does not know layouts would show the objects of every sheet piled on the model.
        bool needsSchema8 = document.Layouts.Count > 0;
        // Without the block table a reader could not show, or keep, the places where blocks are used.
        bool needsSchema9 = document.Blocks.Count > 0;
        w.WriteNumber("minReaderVersion", needsSchema9 ? 9 : needsSchema8 ? 8 : needsSchema3 ? 3 : MinReaderVersion);
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
        w.WriteNumber("linetypeScale", s.LinetypeScale);
        w.WriteEndObject();

        // Linetypes defined in this drawing (the built-in ones are not stored).
        w.WriteStartArray("linetypes");
        foreach (var linetype in document.LinetypeTable)
        {
            if (Linetypes.IsBuiltIn(linetype.Name)) continue;
            w.WriteStartObject();
            w.WriteString("name", linetype.Name);
            w.WriteString("description", linetype.Description);
            w.WriteStartArray("pattern");
            foreach (double v in linetype.Pattern) w.WriteNumberValue(v);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

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

        // The tab that was open when the drawing was saved (left out for the model).
        if (document.ActiveLayout is { } openLayout) w.WriteString("activeLayout", openLayout.Id);

        // Layouts (sheets). Their objects are in "entities" with "space" set to the layout's id.
        w.WriteStartArray("layouts");
        foreach (var layout in document.Layouts)
        {
            w.WriteStartObject();
            w.WriteString("id", layout.Id);
            w.WriteString("name", layout.Name);
            w.WriteNumber("paperWidth", layout.PaperWidth);
            w.WriteNumber("paperHeight", layout.PaperHeight);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // Dimension styles
        w.WriteString("currentDimensionStyle", document.CurrentDimensionStyleName);
        w.WriteStartArray("dimensionStyles");
        foreach (var style in document.DimensionStyles)
        {
            w.WriteStartObject();
            WriteDimensionStyle(w, style);
            w.WriteEndObject();
        }
        w.WriteEndArray();

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
            w.WriteBoolean("plot", layer.IsPlotted);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // Blocks: each with its objects about the base point 0,0. Placed by entities of type "insert".
        w.WriteStartArray("blocks");
        foreach (var block in document.Blocks)
        {
            w.WriteStartObject();
            w.WriteString("name", block.Name);
            w.WriteString("description", block.Description);
            w.WriteString("category", block.Category);
            w.WriteStartArray("entities");
            foreach (var inner in block.Entities) WriteEntity(w, inner);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("entities");
        foreach (var entity in document.AllEntitiesOrdered) WriteEntity(w, entity);
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
        if (entity.LinetypeScale != 1.0) w.WriteNumber("linetypeScale", entity.LinetypeScale);
        if (entity.LineweightMm is double lw) w.WriteNumber("lineweightMm", lw); else w.WriteNull("lineweightMm");
        if (entity.GroupId is Guid group) w.WriteString("groupId", group);
        if (entity.SpaceId != Guid.Empty) w.WriteString("space", entity.SpaceId);
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
            case ViewportEntity viewport:
                WritePoint(w, "center", viewport.Center);
                w.WriteNumber("width", viewport.Width);
                w.WriteNumber("height", viewport.Height);
                WritePoint(w, "modelCenter", viewport.ModelCenter);
                w.WriteNumber("scale", viewport.Scale);
                if (viewport.Locked) w.WriteBoolean("locked", true);
                if (viewport.HasTitle)
                {
                    w.WriteString("title", viewport.Title);
                    w.WriteNumber("titleHeight", viewport.TitleHeight);
                }
                break;
            case GridLineEntity grid:
                if (grid.IsArc)
                {
                    WritePoint(w, "center", grid.Center);
                    w.WriteNumber("radius", grid.Radius);
                    w.WriteNumber("startAngle", grid.StartAngle);
                    w.WriteNumber("endAngle", grid.EndAngle);
                }
                else
                {
                    WritePoint(w, "start", grid.Start);
                    WritePoint(w, "end", grid.End);
                }
                w.WriteString("label", grid.Label);
                w.WriteNumber("bubbleDiameter", grid.BubbleDiameter);
                w.WriteBoolean("labelAtStart", grid.LabelAtStart);
                w.WriteBoolean("labelAtEnd", grid.LabelAtEnd);
                break;
            case TextEntity text:
                WritePoint(w, "position", text.Position);
                w.WriteNumber("height", text.Height);
                w.WriteNumber("angle", text.Angle);
                w.WriteString("text", text.Text);
                w.WriteString("font", text.Font);
                w.WriteBoolean("bold", text.Bold);
                w.WriteBoolean("italic", text.Italic);
                w.WriteBoolean("underline", text.Underline);
                w.WriteString("justify", text.Justify.ToString());
                break;
            case BlockReferenceEntity reference:
                w.WriteString("block", reference.BlockName);
                WritePoint(w, "position", reference.Position);
                w.WriteNumber("scale", reference.Scale);
                w.WriteNumber("rotation", reference.Rotation);
                break;
            case HatchEntity hatch:
                w.WriteString("pattern", hatch.Pattern);
                w.WriteNumber("scale", hatch.Scale);
                w.WriteNumber("angle", hatch.Angle);
                WritePoint(w, "origin", hatch.Origin);
                w.WriteString("islands", hatch.Islands.ToString());
                w.WriteBoolean("associative", hatch.Associative);
                if (hatch.BoundaryLost) w.WriteBoolean("boundaryLost", true);
                if (hatch.GapTolerance > 0) w.WriteNumber("gapTolerance", hatch.GapTolerance);
                if (hatch.Seed is { } seed) WritePoint(w, "seed", seed);
                if (hatch.SourceIds.Count > 0)
                {
                    w.WriteStartArray("sources");
                    foreach (var source in hatch.SourceIds) w.WriteStringValue(source.ToString("D"));
                    w.WriteEndArray();
                }
                w.WriteStartArray("loops");
                foreach (var loop in hatch.Loops)
                {
                    w.WriteStartArray();                          // x, y, x, y ... of one closed loop
                    foreach (var point in loop)
                    {
                        w.WriteNumberValue(point.X);
                        w.WriteNumberValue(point.Y);
                    }
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                break;
            case TableEntity table:
                WritePoint(w, "position", table.Position);
                w.WriteNumber("textHeight", table.TextHeight);
                w.WriteString("font", table.Font);
                w.WriteString("title", table.Title);
                w.WriteBoolean("headerRow", table.HeaderRow);
                w.WriteString("justify", table.Justify.ToString());
                w.WriteStartArray("columnWidths");
                foreach (double width in table.ColumnWidths) w.WriteNumberValue(width);
                w.WriteEndArray();
                w.WriteStartArray("rowHeights");
                foreach (double height in table.RowHeights) w.WriteNumberValue(height);
                w.WriteEndArray();
                w.WriteStartArray("cells");
                for (int r = 0; r < table.RowCount; r++)
                {
                    w.WriteStartArray();
                    for (int c = 0; c < table.ColumnCount; c++) w.WriteStringValue(table.GetCell(r, c));
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                break;
            case DimensionEntity dimension:
                w.WriteString("kind", dimension.Kind.ToString());
                WritePoint(w, "p1", dimension.P1);
                WritePoint(w, "p2", dimension.P2);
                WritePoint(w, "center", dimension.Center);
                WritePoint(w, "linePoint", dimension.LinePoint);
                w.WriteNumber("angle", dimension.Angle);
                w.WriteStartObject("style");
                WriteDimensionStyle(w, dimension.Style);
                w.WriteEndObject();
                w.WriteString("text", dimension.TextOverride);
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
            if (units.TryGetProperty("linetypeScale", out var lts) && lts.ValueKind == JsonValueKind.Number &&
                lts.GetDouble() > 0 && double.IsFinite(lts.GetDouble())) s.LinetypeScale = lts.GetDouble();
            if (units.TryGetProperty("dimensionTextHeight", out var dth) && dth.ValueKind == JsonValueKind.Number &&
                dth.GetDouble() > 0 && double.IsFinite(dth.GetDouble()))
                document.ReplaceDimensionStyle(new DimensionStyle().Scaled(dth.GetDouble() / new DimensionStyle().TextHeight));   // schema 5
        }

        if (doc.TryGetProperty("linetypes", out var linetypes) && linetypes.ValueKind == JsonValueKind.Array)
        {
            foreach (var lt in linetypes.EnumerateArray())
            {
                string name = lt.ValueKind == JsonValueKind.Object ? GetString(lt, "name") : "";
                try
                {
                    var pattern = new List<double>();
                    if (lt.TryGetProperty("pattern", out var pat) && pat.ValueKind == JsonValueKind.Array)
                        foreach (var v in pat.EnumerateArray()) pattern.Add(v.GetDouble());
                    document.AddLinetype(new LinetypeDefinition(name, GetString(lt, "description"), pattern));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
                {
                    warnings.Add($"Linetype \"{name}\" could not be read and was skipped ({ex.Message})");
                }
            }
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
                if (linetype.Length > 0) layer.Linetype = document.CurrentLinetypeName(linetype);
                if (le.TryGetProperty("lineweightMm", out var lw) && lw.ValueKind == JsonValueKind.Number) layer.LineweightMm = lw.GetDouble();
                layer.IsVisible = GetBool(le, "visible", true);
                layer.IsLocked = GetBool(le, "locked", false);
                layer.IsFrozen = GetBool(le, "frozen", false);
                layer.IsPlotted = GetBool(le, "plot", true);
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

        if (doc.TryGetProperty("dimensionStyles", out var dimensionStyles) && dimensionStyles.ValueKind == JsonValueKind.Array)
        {
            foreach (var se in dimensionStyles.EnumerateArray())
            {
                string styleName = se.ValueKind == JsonValueKind.Object ? GetString(se, "name") : "";
                try
                {
                    var style = ReadDimensionStyle(se);
                    if (document.FindDimensionStyle(style.Name) is null) document.AddDimensionStyle(style);
                    else document.ReplaceDimensionStyle(style);                       // "Standard" is already there
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException)
                {
                    warnings.Add($"Dimension style \"{styleName}\" could not be read and was skipped ({ex.Message})");
                }
            }
        }
        if (doc.TryGetProperty("currentDimensionStyle", out var cds) && cds.ValueKind == JsonValueKind.String &&
            document.FindDimensionStyle(cds.GetString()) is { } currentStyle)
            document.CurrentDimensionStyleName = currentStyle.Name;

        if (doc.TryGetProperty("currentLayerId", out var cur) && cur.TryGetGuid(out var curId) && document.GetLayer(curId) is not null)
            document.CurrentLayerId = curId;
        else
            document.CurrentLayerId = layer0.Id;

        if (doc.TryGetProperty("layouts", out var layouts) && layouts.ValueKind == JsonValueKind.Array)
        {
            foreach (var le in layouts.EnumerateArray())
            {
                string layoutName = le.ValueKind == JsonValueKind.Object ? GetString(le, "name") : "";
                try
                {
                    document.AddLayout(new Layout(le.GetProperty("id").GetGuid(), layoutName.Trim(),
                        le.GetProperty("paperWidth").GetDouble(), le.GetProperty("paperHeight").GetDouble()));
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException)
                {
                    warnings.Add($"Layout \"{layoutName}\" could not be read and was skipped ({ex.Message})");
                }
            }
        }

        if (doc.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var be in blocks.EnumerateArray())
            {
                string blockName = be.ValueKind == JsonValueKind.Object ? GetString(be, "name") : "";
                try
                {
                    var contents = new List<Entity>();
                    int skipped = 0;
                    foreach (var ie in be.GetProperty("entities").EnumerateArray())
                    {
                        var inner = ie.ValueKind == JsonValueKind.Object ? ReadEntity(ie, GetString(ie, "type"), out _) : null;
                        if (inner is null) skipped++; else contents.Add(inner);
                    }
                    if (skipped > 0) warnings.Add($"Block \"{blockName}\": {skipped} object(s) inside it could not be read and were left out.");
                    document.AddBlock(new BlockDefinition(blockName, contents, GetString(be, "description"), GetString(be, "category")));
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException)
                {
                    warnings.Add($"Block \"{blockName}\" could not be read and was skipped ({ex.Message})");
                }
            }
        }

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
                    if (type == BlockReferenceEntity.Type)
                    {
                        // A place where a block is used: it needs the block from the table read above.
                        if (document.FindBlock(GetString(ee, "block")) is not { } definition) problem = $"block \"{GetString(ee, "block")}\" is not in the file";
                        else
                        {
                            var reference = new BlockReferenceEntity(ReadId(ee), definition, ReadPoint(ee.GetProperty("position")))
                            {
                                Scale = ee.TryGetProperty("scale", out var bs) && bs.ValueKind == JsonValueKind.Number ? bs.GetDouble() : 1.0,
                                Rotation = ee.TryGetProperty("rotation", out var br) && br.ValueKind == JsonValueKind.Number ? br.GetDouble() : 0.0,
                            };
                            if (!reference.IsValid) problem = "invalid geometry";
                            else
                            {
                                ReadCommon(ee, reference);
                                entity = reference;
                            }
                        }
                    }
                    else entity = ReadEntity(ee, type, out problem);
                    if (entity is not null) entity.Linetype = document.CurrentLinetypeName(entity.Linetype);
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

                if (entity.SpaceId != Guid.Empty && document.GetLayout(entity.SpaceId) is null)
                {
                    // On a layout that is not in the file: it must not land on the model. Kept so nothing is lost.
                    document.UnknownEntities.Add(ee.GetRawText());
                    warnings.Add($"Object #{index} ({type}) is on a layout that is not in the file. It was kept in the file but is not shown.");
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

        // Open on the tab the drawing was saved on.
        if (doc.TryGetProperty("activeLayout", out var open) && open.ValueKind == JsonValueKind.String && open.TryGetGuid(out var openId) &&
            document.GetLayout(openId) is not null) document.ActiveSpaceId = openId;

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
            case ViewportEntity.Type:
                entity = new ViewportEntity(ReadId(e), ReadPoint(e.GetProperty("center")), e.GetProperty("width").GetDouble(), e.GetProperty("height").GetDouble())
                {
                    ModelCenter = ReadPoint(e.GetProperty("modelCenter")),
                    Scale = e.GetProperty("scale").GetDouble(),
                    Title = GetString(e, "title"),
                    Locked = GetBool(e, "locked", false),
                    TitleHeight = e.TryGetProperty("titleHeight", out var vth) && vth.ValueKind == JsonValueKind.Number ? vth.GetDouble() : 0,
                };
                break;
            case GridLineEntity.Type:
            {
                var grid = e.TryGetProperty("center", out var gridCenter)
                    ? new GridLineEntity(ReadId(e), ReadPoint(gridCenter), e.GetProperty("radius").GetDouble(),
                        e.GetProperty("startAngle").GetDouble(), e.GetProperty("endAngle").GetDouble())
                    : new GridLineEntity(ReadId(e), ReadPoint(e.GetProperty("start")), ReadPoint(e.GetProperty("end")));
                grid.Label = GetString(e, "label");
                if (e.TryGetProperty("bubbleDiameter", out var bubble) && bubble.ValueKind == JsonValueKind.Number) grid.BubbleDiameter = bubble.GetDouble();
                grid.LabelAtStart = GetBool(e, "labelAtStart", false);
                grid.LabelAtEnd = GetBool(e, "labelAtEnd", true);
                entity = grid;
                break;
            }
            case TextEntity.Type:
                entity = new TextEntity(ReadId(e), ReadPoint(e.GetProperty("position")), e.GetProperty("height").GetDouble(), GetString(e, "text"))
                {
                    Angle = e.TryGetProperty("angle", out var textAngle) && textAngle.ValueKind == JsonValueKind.Number ? textAngle.GetDouble() : 0,
                    // Text written before schema 9 has no font and is centred on its position.
                    Font = GetString(e, "font") is { Length: > 0 } textFont ? textFont : TextMeasure.DefaultFont,
                    Bold = GetBool(e, "bold", false),
                    Italic = GetBool(e, "italic", false),
                    Underline = GetBool(e, "underline", false),
                    Justify = Enum.TryParse(GetString(e, "justify"), ignoreCase: true, out TextJustify textJustify) && Enum.IsDefined(textJustify) ? textJustify : TextJustify.Center,
                };
                break;
            case HatchEntity.Type:
            {
                var loops = new List<Point2D[]>();
                foreach (var loopElement in e.GetProperty("loops").EnumerateArray())
                {
                    var numbers = loopElement.EnumerateArray().Select(v => v.GetDouble()).ToList();
                    if (numbers.Count < 6 || numbers.Count % 2 != 0) throw new FormatException("a hatch loop needs at least three points");
                    var loop = new Point2D[numbers.Count / 2];
                    for (int k = 0; k < loop.Length; k++) loop[k] = new Point2D(numbers[2 * k], numbers[2 * k + 1]);
                    loops.Add(loop);
                }
                var hatch = new HatchEntity(ReadId(e), loops)
                {
                    Pattern = GetString(e, "pattern") is { Length: > 0 } patternName ? patternName : HatchPatterns.Default,
                    Scale = e.TryGetProperty("scale", out var hatchScale) && hatchScale.ValueKind == JsonValueKind.Number ? hatchScale.GetDouble() : 1.0,
                    Angle = e.TryGetProperty("angle", out var hatchAngle) && hatchAngle.ValueKind == JsonValueKind.Number ? hatchAngle.GetDouble() : 0.0,
                    Origin = e.TryGetProperty("origin", out var hatchOrigin) ? ReadPoint(hatchOrigin) : Point2D.Origin,
                    Islands = Enum.TryParse(GetString(e, "islands"), ignoreCase: true, out HatchIslands hatchIslands) && Enum.IsDefined(hatchIslands) ? hatchIslands : HatchIslands.Normal,
                    Associative = GetBool(e, "associative", false),
                    BoundaryLost = GetBool(e, "boundaryLost", false),
                    GapTolerance = e.TryGetProperty("gapTolerance", out var hatchGap) && hatchGap.ValueKind == JsonValueKind.Number && hatchGap.GetDouble() > 0 ? hatchGap.GetDouble() : 0,
                    Seed = e.TryGetProperty("seed", out var hatchSeed) ? ReadPoint(hatchSeed) : null,
                };
                if (e.TryGetProperty("sources", out var hatchSources) && hatchSources.ValueKind == JsonValueKind.Array)
                    foreach (var source in hatchSources.EnumerateArray())
                        if (Guid.TryParse(source.GetString(), out Guid sourceId)) hatch.SourceIds.Add(sourceId);
                entity = hatch;
                break;
            }
            case TableEntity.Type:
            {
                var widths = e.GetProperty("columnWidths").EnumerateArray().Select(v => v.GetDouble()).ToList();
                var heights = e.GetProperty("rowHeights").EnumerateArray().Select(v => v.GetDouble()).ToList();
                if (widths.Count < 1 || widths.Count > TableEntity.MaxColumns || heights.Count < 1 || heights.Count > TableEntity.MaxRows)
                    throw new FormatException("a table needs 1 to 200 rows and 1 to 50 columns");
                var table = new TableEntity(ReadId(e), ReadPoint(e.GetProperty("position")), heights.Count, widths.Count, 1, 1, e.GetProperty("textHeight").GetDouble())
                {
                    Font = GetString(e, "font") is { Length: > 0 } tableFont ? tableFont : TextMeasure.DefaultFont,
                    Title = GetString(e, "title"),
                    HeaderRow = GetBool(e, "headerRow", true),
                    Justify = Enum.TryParse(GetString(e, "justify"), ignoreCase: true, out TextJustify tableJustify) && Enum.IsDefined(tableJustify) ? tableJustify : TextJustify.Left,
                };
                for (int c = 0; c < widths.Count; c++) table.SetColumnWidth(c, widths[c]);
                for (int r = 0; r < heights.Count; r++) table.SetRowHeight(r, heights[r]);
                if (e.TryGetProperty("cells", out var cellRows) && cellRows.ValueKind == JsonValueKind.Array)
                {
                    int r = 0;
                    foreach (var cellRow in cellRows.EnumerateArray())
                    {
                        if (r >= heights.Count) break;
                        int c = 0;
                        foreach (var cell in cellRow.EnumerateArray())
                        {
                            if (c >= widths.Count) break;
                            table.SetCell(r, c++, cell.ValueKind == JsonValueKind.String ? cell.GetString() : "");
                        }
                        r++;
                    }
                }
                entity = table;
                break;
            }
            case DimensionEntity.Type:
                if (!Enum.TryParse(GetString(e, "kind"), ignoreCase: true, out DimensionKind kind) || !Enum.IsDefined(kind))
                    throw new FormatException("unknown kind of dimension");
                entity = new DimensionEntity(ReadId(e), kind)
                {
                    P1 = ReadPoint(e.GetProperty("p1")),
                    P2 = ReadPoint(e.GetProperty("p2")),
                    Center = ReadPoint(e.GetProperty("center")),
                    LinePoint = ReadPoint(e.GetProperty("linePoint")),
                    Angle = e.GetProperty("angle").GetDouble(),
                    TextOverride = GetString(e, "text"),
                    // Schema 5 stored only a text height; everything else was in proportion to it.
                    Style = e.TryGetProperty("style", out var styleElement) && styleElement.ValueKind == JsonValueKind.Object
                        ? ReadDimensionStyle(styleElement)
                        : new DimensionStyle().Scaled(e.GetProperty("textHeight").GetDouble() / new DimensionStyle().TextHeight),
                };
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
        ReadCommon(e, entity);
        return entity;
    }

    /// <summary>The properties every kind of object has: layer, colour, linetype, lineweight, group, space, custom properties.</summary>
    private static void ReadCommon(JsonElement e, Entity entity)
    {
        if (e.TryGetProperty("layerId", out var lid) && lid.TryGetGuid(out var layerId)) entity.LayerId = layerId;
        if (CadColor.TryParse(GetString(e, "color"), out var color)) entity.Color = color;
        string linetype = GetString(e, "linetype");
        if (linetype.Length > 0) entity.Linetype = linetype;
        if (e.TryGetProperty("linetypeScale", out var els) && els.ValueKind == JsonValueKind.Number &&
            els.GetDouble() > 0 && double.IsFinite(els.GetDouble())) entity.LinetypeScale = els.GetDouble();
        if (e.TryGetProperty("lineweightMm", out var lw) && lw.ValueKind == JsonValueKind.Number) entity.LineweightMm = lw.GetDouble();
        // Always given a space here (the model when the file says nothing), whatever space is active while loading.
        entity.SpaceId = e.TryGetProperty("space", out var space) && space.ValueKind == JsonValueKind.String && space.TryGetGuid(out var spaceId) ? spaceId : Guid.Empty;
        if (e.TryGetProperty("groupId", out var gid) && gid.ValueKind == JsonValueKind.String && gid.TryGetGuid(out var groupId)) entity.GroupId = groupId;
        ReadProperties(e, "properties", entity.CustomProperties);
    }

    private static void WriteDimensionStyle(Utf8JsonWriter w, DimensionStyle style)
    {
        w.WriteString("name", style.Name);
        w.WriteNumber("textHeight", style.TextHeight);
        w.WriteNumber("textGap", style.TextGap);
        w.WriteString("arrow", style.Arrow.ToString());
        w.WriteNumber("arrowSize", style.ArrowSize);
        w.WriteNumber("extensionOffset", style.ExtensionOffset);
        w.WriteNumber("extensionBeyond", style.ExtensionBeyond);
        w.WriteString("format", style.Format.ToString());
        w.WriteNumber("precision", style.Precision);
        w.WriteNumber("fractionDenominator", style.FractionDenominator);
        w.WriteNumber("roundOff", style.RoundOff);
        w.WriteNumber("scaleFactor", style.ScaleFactor);
        w.WriteString("prefix", style.Prefix);
        w.WriteString("suffix", style.Suffix);
        w.WriteBoolean("suppressZeroFeet", style.SuppressZeroFeet);
        w.WriteBoolean("suppressZeroInches", style.SuppressZeroInches);
        w.WriteNumber("anglePrecision", style.AnglePrecision);
    }

    /// <summary>Reads a dimension style. A field that is missing or cannot be used keeps its standard value.</summary>
    private static DimensionStyle ReadDimensionStyle(JsonElement e)
    {
        var d = new DimensionStyle();
        double Number(string name, double fallback) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble()) ? v.GetDouble() : fallback;
        int Whole(string name, int fallback) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;
        string name = GetString(e, "name");
        var style = d with
        {
            Name = DimensionStyle.IsValidName(name) ? name.Trim() : d.Name,
            TextHeight = Number("textHeight", d.TextHeight),
            TextGap = Number("textGap", d.TextGap),
            Arrow = Enum.TryParse(GetString(e, "arrow"), true, out DimensionArrow arrow) && Enum.IsDefined(arrow) ? arrow : d.Arrow,
            ArrowSize = Number("arrowSize", d.ArrowSize),
            ExtensionOffset = Number("extensionOffset", d.ExtensionOffset),
            ExtensionBeyond = Number("extensionBeyond", d.ExtensionBeyond),
            Format = Enum.TryParse(GetString(e, "format"), true, out DimensionUnitFormat format) && Enum.IsDefined(format) ? format : d.Format,
            Precision = Whole("precision", d.Precision),
            FractionDenominator = Whole("fractionDenominator", d.FractionDenominator),
            RoundOff = Number("roundOff", d.RoundOff),
            ScaleFactor = Number("scaleFactor", d.ScaleFactor),
            Prefix = e.TryGetProperty("prefix", out var pre) && pre.ValueKind == JsonValueKind.String ? pre.GetString()! : "",
            Suffix = e.TryGetProperty("suffix", out var suf) && suf.ValueKind == JsonValueKind.String ? suf.GetString()! : "",
            SuppressZeroFeet = GetBool(e, "suppressZeroFeet", d.SuppressZeroFeet),
            SuppressZeroInches = GetBool(e, "suppressZeroInches", d.SuppressZeroInches),
            AnglePrecision = Whole("anglePrecision", d.AnglePrecision),
        };
        if (style.Problem is { } problem) throw new FormatException("dimension style: " + problem);
        return style;
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
