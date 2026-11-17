using System.Globalization;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>At which end of a grid line its label is shown.</summary>
public enum GridLabelEnds
{
    /// <summary>The start: the bottom of lines that run up, the left of lines that run across, the inner end of a fan, the first line of a sector.</summary>
    Start,
    /// <summary>The end: top, right, outer end, last line.</summary>
    End,
    Both,
    None,
}

/// <summary>A rectangular grid of lines: spacings and labels along X and along Y.</summary>
public sealed class RectangularGridSpec
{
    /// <summary>Name of the grid system; its lines go on a layer of this name.</summary>
    public string Name { get; set; } = "Grid 1";
    public Point2D Origin { get; set; }

    /// <summary>Distances between the grid lines counted along X (the first line is at the origin).</summary>
    public IReadOnlyList<double> XSpacings { get; set; } = Array.Empty<double>();
    public IReadOnlyList<double> YSpacings { get; set; } = Array.Empty<double>();

    /// <summary>Labels of the lines counted along X, one per line (spacings + 1).</summary>
    public IReadOnlyList<string> XLabels { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> YLabels { get; set; } = Array.Empty<string>();

    /// <summary>Labels of the lines counted along X: Start = bottom, End = top.</summary>
    public GridLabelEnds XLabelEnds { get; set; } = GridLabelEnds.End;

    /// <summary>Labels of the lines counted along Y: Start = left, End = right.</summary>
    public GridLabelEnds YLabelEnds { get; set; } = GridLabelEnds.Start;

    /// <summary>The whole grid is turned by this angle about its origin, counter-clockwise.</summary>
    public double RotationDegrees { get; set; }

    /// <summary>How far every line runs past the outermost crossing.</summary>
    public double Extension { get; set; } = 24;
    public double BubbleDiameter { get; set; } = 12;

    /// <summary>Linetype given to the layer when the layer is made for this grid.</summary>
    public string Linetype { get; set; } = Linetypes.DashDot;
}

/// <summary>A sector (radial) grid: straight lines fanning out from a centre and arcs across them.</summary>
public sealed class SectorGridSpec
{
    public string Name { get; set; } = "Grid 1";
    public Point2D Center { get; set; }

    /// <summary>Radius of the first arc, then the distances between the arcs.</summary>
    public IReadOnlyList<double> RingSpacings { get; set; } = Array.Empty<double>();

    /// <summary>Angles between the straight lines, in degrees.</summary>
    public IReadOnlyList<double> AngleSpacings { get; set; } = Array.Empty<double>();

    /// <summary>Direction of the first straight line, in degrees counter-clockwise from the X axis.</summary>
    public double StartAngleDegrees { get; set; }

    public IReadOnlyList<string> RadialLabels { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> RingLabels { get; set; } = Array.Empty<string>();

    /// <summary>Labels of the straight lines: Start = inner end, End = outer end.</summary>
    public GridLabelEnds RadialLabelEnds { get; set; } = GridLabelEnds.End;

    /// <summary>Labels of the arcs: Start = beside the first straight line, End = beside the last.</summary>
    public GridLabelEnds RingLabelEnds { get; set; } = GridLabelEnds.Start;

    public double Extension { get; set; } = 24;
    public double BubbleDiameter { get; set; } = 12;
    public string Linetype { get; set; } = Linetypes.DashDot;
}

/// <summary>Reads what is typed into the grid line wizard, and makes the lines, circles and labels of a grid.</summary>
public static class GridLines
{
    public const int MaxLines = 200;

    /// <summary>
    /// Reads spacings separated by commas: "20', 15'-6, 20'". A count and a length give that many equal spaces:
    /// "4x20'" (also 4*20' or 4@20'). Empty text gives no spacings (a single line).
    /// </summary>
    public static bool TryParseSpacings(string? text, DrawingSettings settings, out double[] spacings, out string error) =>
        TryParseList(text, t => UnitFormatter.TryParseLength(t, settings, out double v) ? v : (double?)null, "length", out spacings, out error);

    /// <summary>Reads angles in degrees separated by commas, with the same "count x angle" short form.</summary>
    public static bool TryParseAngles(string? text, out double[] angles, out string error) =>
        TryParseList(text, t => double.TryParse(t.TrimEnd('°').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null,
            "angle", out angles, out error);

    private static bool TryParseList(string? text, Func<string, double?> read, string what, out double[] values, out string error)
    {
        values = Array.Empty<double>();
        error = "";
        var result = new List<double>();
        foreach (string raw in (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string item = raw.Trim();
            if (item.Length == 0) continue;

            int count = 1;
            string value = item;
            int mark = item.IndexOfAny(new[] { 'x', 'X', '*', '@' });
            if (mark > 0 && int.TryParse(item[..mark].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                if (n < 1) { error = $"\"{item}\": the count must be 1 or more."; return false; }
                count = n;
                value = item[(mark + 1)..].Trim();
            }

            if (read(value) is not double v || !double.IsFinite(v) || v <= 0)
            {
                error = $"\"{item}\" is not a {what} more than 0.";
                return false;
            }
            if (result.Count + count > MaxLines) { error = $"A grid can have at most {MaxLines} spaces in one direction."; return false; }
            for (int i = 0; i < count; i++) result.Add(v);
        }
        values = result.ToArray();
        return true;
    }

    /// <summary>
    /// The labels of <paramref name="count"/> lines. What was typed is used as far as it goes ("A, B, C" or
    /// "1, 2, 3, 3.5, 4"); after that the last label is counted on: A, B gives C, D...; 1, 2 gives 3, 4...;
    /// Z gives AA; 3.5 gives 4; A9 gives A10. With nothing typed, <paramref name="first"/> starts the count.
    /// </summary>
    public static string[] Labels(string? text, int count, string first)
    {
        var typed = (text ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (typed.Count == 0) typed.Add(first);
        var labels = new string[Math.Max(count, 0)];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = i < typed.Count ? typed[i] : NextLabel(labels[i - 1]);
        return labels;
    }

    /// <summary>The label that follows <paramref name="label"/>.</summary>
    public static string NextLabel(string label)
    {
        if (label.Length == 0) return "1";

        // A number: the next whole number (3 -> 4, 3.5 -> 4).
        if (double.TryParse(label, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double number) && Math.Abs(number) < 1e12)
            return (Math.Floor(number) + 1).ToString("0", CultureInfo.InvariantCulture);

        // Ends with digits: count those on (A9 -> A10, C.1 -> C.2).
        int digits = 0;
        while (digits < label.Length && char.IsAsciiDigit(label[^(digits + 1)])) digits++;
        if (digits > 0 && digits <= 9)
            return label[..^digits] + (long.Parse(label[^digits..], CultureInfo.InvariantCulture) + 1).ToString(new string('0', digits), CultureInfo.InvariantCulture);

        // Ends with letters: the next one, carrying like a spreadsheet column (Z -> AA, AZ -> BA).
        int letters = 0;
        while (letters < label.Length && char.IsAsciiLetter(label[^(letters + 1)])) letters++;
        if (letters > 0)
        {
            char[] tail = label[^letters..].ToCharArray();
            int at = tail.Length - 1;
            while (at >= 0)
            {
                char c = tail[at];
                if (c is 'Z' or 'z') { tail[at] = c == 'Z' ? 'A' : 'a'; at--; }
                else { tail[at] = (char)(c + 1); break; }
            }
            string head = label[..^letters];
            return at < 0 ? head + (char.IsUpper(tail[0]) ? "A" : "a") + new string(tail) : head + new string(tail);
        }

        return label + "1";
    }

    private static double[] Cumulative(IReadOnlyList<double> spacings)
    {
        var at = new double[spacings.Count + 1];
        for (int i = 0; i < spacings.Count; i++) at[i + 1] = at[i] + spacings[i];
        return at;
    }

    private static GridLineEntity Labelled(GridLineEntity line, string label, GridLabelEnds ends, double diameter, Guid group)
    {
        line.Label = label.Trim();
        line.BubbleDiameter = Math.Max(diameter, 0);
        line.LabelAtStart = ends is GridLabelEnds.Start or GridLabelEnds.Both;
        line.LabelAtEnd = ends is GridLabelEnds.End or GridLabelEnds.Both;
        line.GroupId = group;
        return line;
    }

    /// <summary>The grid lines of a rectangular grid, all in one group (without a layer: the caller places them).</summary>
    public static List<Entity> Build(RectangularGridSpec spec)
    {
        var entities = new List<Entity>();
        double[] xs = Cumulative(spec.XSpacings), ys = Cumulative(spec.YSpacings);
        double width = xs[^1], height = ys[^1], ext = Math.Max(spec.Extension, 0);
        var place = Transform2D.Translation(spec.Origin - Point2D.Origin).After(Transform2D.Rotation(GeoMath.DegreesToRadians(spec.RotationDegrees), Point2D.Origin));
        var group = Guid.NewGuid();

        // Lines counted along X run upwards: start at the bottom, end at the top.
        for (int i = 0; i < xs.Length; i++)
            entities.Add(Labelled(new GridLineEntity(place.Apply(new Point2D(xs[i], -ext)), place.Apply(new Point2D(xs[i], height + ext))),
                i < spec.XLabels.Count ? spec.XLabels[i] : "", spec.XLabelEnds, spec.BubbleDiameter, group));

        // Lines counted along Y run across: start at the left, end at the right.
        for (int j = 0; j < ys.Length; j++)
            entities.Add(Labelled(new GridLineEntity(place.Apply(new Point2D(-ext, ys[j])), place.Apply(new Point2D(width + ext, ys[j]))),
                j < spec.YLabels.Count ? spec.YLabels[j] : "", spec.YLabelEnds, spec.BubbleDiameter, group));

        return entities.Where(e => e.IsValid).ToList();
    }

    /// <summary>The grid lines of a sector grid: straight lines fanning out and arcs across them, all in one group.</summary>
    public static List<Entity> Build(SectorGridSpec spec)
    {
        var entities = new List<Entity>();
        double[] radii = Cumulative(spec.RingSpacings).Skip(1).ToArray();              // the first spacing is the first radius
        double[] turns = Cumulative(spec.AngleSpacings);
        if (radii.Length == 0) return entities;

        double ext = Math.Max(spec.Extension, 0);
        double inner = radii[0], outer = radii[^1];
        double first = GeoMath.DegreesToRadians(spec.StartAngleDegrees);
        double sweep = GeoMath.DegreesToRadians(turns[^1]);
        bool fullTurn = turns[^1] >= 360.0 - 1e-9;
        var group = Guid.NewGuid();

        // Straight lines, fanning out: start at the inner end, end at the outer end.
        int lines = fullTurn ? turns.Length - 1 : turns.Length;                          // the last line of a full turn is the first again
        for (int i = 0; i < lines; i++)
        {
            double a = first + GeoMath.DegreesToRadians(turns[i]);
            var u = new Vector2D(Math.Cos(a), Math.Sin(a));
            double from = Math.Max(inner - ext, 0);
            entities.Add(Labelled(new GridLineEntity(spec.Center + u * from, spec.Center + u * (outer + ext)),
                i < spec.RadialLabels.Count ? spec.RadialLabels[i] : "", spec.RadialLabelEnds, spec.BubbleDiameter, group));
        }

        // Arcs across the lines, running a little past the first and the last line.
        if (sweep > 1e-9)
        {
            for (int k = 0; k < radii.Length; k++)
            {
                double past = fullTurn ? 0 : Math.Min(ext / radii[k], (2.0 * Math.PI - sweep) / 2.0 - 1e-6);
                // A full turn is drawn as two half circles (an arc cannot go all the way round), without labels.
                if (fullTurn)
                {
                    entities.Add(Labelled(new GridLineEntity(spec.Center, radii[k], first, first + Math.PI), "", GridLabelEnds.None, spec.BubbleDiameter, group));
                    entities.Add(Labelled(new GridLineEntity(spec.Center, radii[k], first + Math.PI, first), "", GridLabelEnds.None, spec.BubbleDiameter, group));
                    continue;
                }
                entities.Add(Labelled(new GridLineEntity(spec.Center, radii[k], first - past, first + sweep + past),
                    k < spec.RingLabels.Count ? spec.RingLabels[k] : "", spec.RingLabelEnds, spec.BubbleDiameter, group));
            }
        }
        return entities.Where(e => e.IsValid).ToList();
    }
}

/// <summary>Places a grid that was set up in the wizard: the grid follows the cursor until its origin is clicked.</summary>
public sealed class PlaceGridCommand : CadCommandBase
{
    private readonly string _name;
    private readonly string _linetype;
    private readonly Func<Point2D, List<Entity>> _build;

    public PlaceGridCommand(string name, string linetype, Func<Point2D, List<Entity>> build)
    {
        _name = name;
        _linetype = linetype;
        _build = build;
    }

    public override string Name => "GRIDLINES";
    public override string Prompt => $"GRIDLINES  Click where the origin of \"{_name}\" goes (or type its coordinates):";

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.PointInput p) return Invalid("Click a point, or type coordinates such as 0,0.");
        return Session.CreateGrid(_name, _linetype, _build(p.Point)) ? CommandResult.Finished : CommandResult.Continue;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor) => _build(cursor);
}

/// <summary>
/// GRIDADD: one more grid line beside an existing one. Click a grid line, then where the new one goes (or type
/// its distance, taken on the side of the cursor), then its label. The new line is a copy of the one clicked:
/// same length, same label ends, same grid system and layer. It works between two lines and past either end.
/// </summary>
public sealed class AddGridLineCommand : CadCommandBase, IObjectPicker, IPickedObjects, IRawTextCommand
{
    private GridLineEntity? _source;
    private GridLineEntity? _placed;

    public override string Name => "GRIDADD";
    public bool PickingObject => _source is null;
    public bool WantsRawText => _placed is not null;
    public IEnumerable<Guid> PickedObjects => _source is null ? Array.Empty<Guid>() : new[] { _source.Id };

    public override string Prompt =>
        _source is null ? "GRIDADD  Click the grid line to add one beside:"
        : _placed is null ? "GRIDADD  Click where the new grid line goes, or type its distance from the one you clicked:"
        : $"GRIDADD  Label of the new grid line <{_placed.Label}>:";

    public override CommandResult OnInput(CommandInput input)
    {
        if (_placed is not null) return OnRawText("");                    // Enter: the suggested label

        if (input is CommandInput.EnterInput) return CommandResult.Finished;

        if (_source is null)
        {
            if (input is not CommandInput.PointInput pick) return Invalid("Click a grid line.");
            var entity = SelectionService.PickAt(Document, pick.Point, Session.PickTolerance);
            if (entity is not GridLineEntity grid) return Invalid(entity is null ? "No object there. Click a grid line." : "That is not a grid line.");
            _source = grid;
            return CommandResult.Continue;
        }

        GridLineEntity? copy = input switch
        {
            CommandInput.PointInput p => Offset(_source, p.Point, null),
            CommandInput.NumberInput { Value: > 0 } n => Offset(_source, Session.EffectiveCursor, n.Value),
            _ => null,
        };
        if (copy is null) return Invalid("Click beside the grid line, or type a distance more than 0.");
        copy.Label = SuggestLabel(copy);
        _placed = copy;
        return CommandResult.Continue;
    }

    public CommandResult OnRawText(string text)
    {
        if (_placed is null) return CommandResult.Continue;
        string label = text.Trim();
        if (label.Length > GridLineEntity.MaxLabelLength) return Invalid($"A label is at most {GridLineEntity.MaxLabelLength} characters.");
        if (label.Length > 0) _placed.Label = label;
        return Session.Execute(new AddEntitiesAction(Name, new Entity[] { _placed })) ? CommandResult.Finished : CommandResult.Continue;
    }

    /// <summary>
    /// A copy of <paramref name="source"/> moved sideways so that it passes through <paramref name="through"/>, or by
    /// <paramref name="distance"/> on the side of that point. For an arc the radius changes. Null when it cannot be made.
    /// </summary>
    public static GridLineEntity? Offset(GridLineEntity source, Point2D through, double? distance)
    {
        var copy = (GridLineEntity)source.CopyWithNewId();
        if (source.IsArc)
        {
            double reach = through.DistanceTo(source.Center);
            double radius = distance is double d ? (reach >= source.Radius ? source.Radius + d : source.Radius - d) : reach;
            if (!(radius > 1e-9) || Math.Abs(radius - source.Radius) < 1e-9) return null;
            copy.Radius = radius;
        }
        else
        {
            var u = (source.End - source.Start).Normalized();
            var normal = new Vector2D(-u.Y, u.X);
            double side = normal.Dot(through - source.Start);
            double shift = distance is double d ? (side >= 0 ? d : -d) : side;
            if (Math.Abs(shift) < 1e-9) return null;
            copy.Transform(Transform2D.Translation(normal * shift));
        }
        return copy.IsValid ? copy : null;
    }

    /// <summary>
    /// Past the last line of its direction: the label after that line's. Between two lines: the lower label with
    /// ".5". Before the first line: the first label with "-1".
    /// </summary>
    private string SuggestLabel(GridLineEntity added)
    {
        var source = _source!;
        // Lines of the same grid system that run the same way, each with its position across the direction.
        var siblings = new List<(double At, string Label)>();
        foreach (var e in Document.EntitiesUnordered.OfType<GridLineEntity>())
        {
            if (e.GroupId != source.GroupId || e.IsArc != source.IsArc || e.Label.Length == 0) continue;
            if (source.IsArc) { if (e.Center.NearlyEquals(source.Center, 1e-6)) siblings.Add((e.Radius, e.Label)); }
            else
            {
                var u = (source.End - source.Start).Normalized();
                var v = (e.End - e.Start).Normalized();
                if (Math.Abs(u.Cross(v)) < 1e-6) siblings.Add((new Vector2D(-u.Y, u.X).Dot(e.Start - source.Start), e.Label));
            }
        }
        if (siblings.Count == 0) return GridLines.NextLabel(source.Label);
        siblings.Sort((a, b) => a.At.CompareTo(b.At));

        double at = source.IsArc ? added.Radius : new Vector2D(-(source.End - source.Start).Normalized().Y, (source.End - source.Start).Normalized().X).Dot(added.Start - source.Start);
        // The labels run upwards from one side; which side is told by the order of the first and last label.
        if (Before(siblings[^1].Label, siblings[0].Label))
        {
            siblings = siblings.Select(x => (-x.At, x.Label)).Reverse().ToList();
            at = -at;
        }
        if (at > siblings[^1].At) return GridLines.NextLabel(siblings[^1].Label);
        if (at < siblings[0].At) return siblings[0].Label + "-1";
        for (int i = 0; i + 1 < siblings.Count; i++)
            if (at > siblings[i].At && at < siblings[i + 1].At) return siblings[i].Label + ".5";
        return source.Label + ".5";
    }

    /// <summary>True when label <paramref name="a"/> comes before <paramref name="b"/>: by value for numbers, else shorter first, then by letter.</summary>
    private static bool Before(string a, string b)
    {
        var style = System.Globalization.NumberStyles.Float;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (double.TryParse(a, style, culture, out double x) && double.TryParse(b, style, culture, out double y)) return x < y;
        return a.Length != b.Length ? a.Length < b.Length : string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        if (_placed is not null) return new Entity[] { _placed };
        if (_source is null) return Array.Empty<Entity>();
        return Offset(_source, cursor, null) is { } copy ? new Entity[] { copy } : Array.Empty<Entity>();
    }
}

