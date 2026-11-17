using System.Globalization;

namespace HAQQ.CAD.Core;

public enum CadColorKind
{
    /// <summary>The entity takes the colour of its layer.</summary>
    ByLayer,
    /// <summary>The entity takes the colour of the block reference that contains it.</summary>
    ByBlock,
    /// <summary>Explicit true colour.</summary>
    Rgb,
}

/// <summary>An entity or layer colour: ByLayer, ByBlock or an explicit RGB value.</summary>
public readonly record struct CadColor(CadColorKind Kind, byte R, byte G, byte B)
{
    public static readonly CadColor ByLayer = new(CadColorKind.ByLayer, 0, 0, 0);
    public static readonly CadColor ByBlock = new(CadColorKind.ByBlock, 0, 0, 0);
    public static readonly CadColor White = FromRgb(255, 255, 255);

    public static CadColor FromRgb(byte r, byte g, byte b) => new(CadColorKind.Rgb, r, g, b);

    public bool IsExplicit => Kind == CadColorKind.Rgb;

    public override string ToString() => Kind switch
    {
        CadColorKind.ByLayer => "ByLayer",
        CadColorKind.ByBlock => "ByBlock",
        _ => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}"),
    };

    public static bool TryParse(string? text, out CadColor color)
    {
        color = ByLayer;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.Equals("ByLayer", StringComparison.OrdinalIgnoreCase)) { color = ByLayer; return true; }
        if (text.Equals("ByBlock", StringComparison.OrdinalIgnoreCase)) { color = ByBlock; return true; }
        if (text.Length == 7 && text[0] == '#' &&
            byte.TryParse(text.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(text.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(text.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            color = FromRgb(r, g, b);
            return true;
        }
        return false;
    }
}

/// <summary>Well-known linetype names.</summary>
public static class Linetypes
{
    /// <summary>Object setting: use the linetype of the object's layer.</summary>
    public const string ByLayer = "ByLayer";

    /// <summary>
    /// Object setting: use the linetype of the block the object is in. HAQQ CAD has no blocks yet, so an
    /// object set to ByBlock is drawn continuous; the setting is stored for when blocks exist.
    /// </summary>
    public const string ByBlock = "ByBlock";

    // Continuous, Dashed and Center keep their usual drafting names (owner's decision); the others say what the line looks like.
    public const string Solid = "Continuous";
    public const string Dash = "Dashed";
    public const string ShortDash = "Short Dash";
    public const string LongShort = "Center";
    public const string LongShortShort = "Long Short Short";
    public const string DashDot = "Dash Dot";
    public const string Dotted = "Dotted";
    public const string DashDashDot = "Dash Dash Dot";

    public static bool IsReserved(string? name) =>
        string.Equals(name, ByLayer, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, ByBlock, StringComparison.OrdinalIgnoreCase);

    /// <summary>The linetypes every drawing has. Lengths are in drawing units.</summary>
    public static IReadOnlyList<LinetypeDefinition> BuiltIn { get; } = new[]
    {
        new LinetypeDefinition(Solid, "Unbroken line", Array.Empty<double>()),
        new LinetypeDefinition(Dash, "Even dashes", new[] { 0.5, 0.25 }),
        new LinetypeDefinition(ShortDash, "Short dashes, for edges that are out of sight", new[] { 0.25, 0.125 }),
        new LinetypeDefinition(LongShort, "Long dash, short dash, for centerlines", new[] { 1.25, 0.25, 0.25, 0.25 }),
        new LinetypeDefinition(LongShortShort, "Long dash, two short dashes", new[] { 1.25, 0.25, 0.25, 0.25, 0.25, 0.25 }),
        new LinetypeDefinition(DashDot, "Dash, dot", new[] { 0.5, 0.25, 0.0, 0.25 }),
        new LinetypeDefinition(Dotted, "Dots", new[] { 0.0, 0.25 }),
        new LinetypeDefinition(DashDashDot, "Two dashes, dot", new[] { 0.5, 0.25, 0.5, 0.25, 0.0, 0.25 }),
    };

    /// <summary>
    /// Names the built-in linetypes had in development builds of 17 Nov 2026, with the name used now.
    /// Applied when a drawing is opened.
    /// </summary>
    private static readonly Dictionary<string, string> EarlierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Solid"] = Solid, ["Dash"] = Dash, ["Long Short"] = LongShort,
        ["Hidden"] = ShortDash, ["Phantom"] = LongShortShort, ["DashDot"] = DashDot, ["Dot"] = Dotted, ["Border"] = DashDashDot,
    };

    /// <summary>The current name for a linetype name read from a drawing saved by an earlier build.</summary>
    public static string FromEarlierName(string name) =>
        EarlierNames.TryGetValue(name.Trim(), out string? current) ? current : name;

    public static bool IsBuiltIn(string? name) =>
        BuiltIn.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A linetype: a repeating pattern of dashes and gaps. <see cref="Pattern"/> alternates dash length, gap length,
/// dash, gap... in drawing units; a dash of 0 is a dot. An empty pattern is a continuous line.
/// </summary>
public sealed class LinetypeDefinition
{
    public LinetypeDefinition(string name, string description, IEnumerable<double> pattern)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Linetype name is required.", nameof(name));
        Name = name.Trim();
        Description = description ?? "";
        Pattern = pattern.ToArray();
        if (!IsValidPattern(Pattern)) throw new ArgumentException("Invalid linetype pattern.", nameof(pattern));
    }

    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<double> Pattern { get; }

    public bool IsContinuous => Pattern.Count == 0;

    /// <summary>Length of one repeat of the pattern, in drawing units.</summary>
    public double PatternLength => Pattern.Sum();

    /// <summary>Pairs of dash (0 or more) and gap (more than 0); at least one pair, or none for continuous.</summary>
    public static bool IsValidPattern(IReadOnlyList<double> pattern)
    {
        if (pattern.Count == 0) return true;
        if (pattern.Count % 2 != 0 || pattern.Count > 24) return false;
        for (int i = 0; i < pattern.Count; i++)
        {
            double v = pattern[i];
            if (!double.IsFinite(v) || v < 0) return false;
            if (i % 2 == 1 && v <= 0) return false;                 // a gap must have length
        }
        return pattern.Sum() > 0;
    }

    /// <summary>
    /// Reads a pattern typed by the user. Either dash and gap lengths in turn ("0.5, 0.25" or "1 0.25 0 0.25",
    /// 0 = dot), or the classic form where gaps are negative ("0.5,-0.25").
    /// </summary>
    public static bool TryParsePattern(string? text, out double[] pattern)
    {
        pattern = Array.Empty<double>();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var values = new List<double>();
        foreach (string part in text.Split(new[] { ',', ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("A", StringComparison.OrdinalIgnoreCase)) continue;      // alignment code of the classic form
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ||
                !double.IsFinite(v)) return false;
            values.Add(v);
        }
        if (values.Count == 0) return false;

        if (values.Any(v => v < 0))
        {
            // Classic form: positive or zero = dash or dot, negative = gap, strictly in turn, starting with a dash.
            for (int i = 0; i < values.Count; i++)
                if (i % 2 == 0 ? values[i] < 0 : values[i] >= 0) return false;
            for (int i = 1; i < values.Count; i += 2) values[i] = -values[i];
        }
        if (values.Count % 2 != 0 || !IsValidPattern(values)) return false;
        pattern = values.ToArray();
        return true;
    }

    public override string ToString() =>
        IsContinuous ? Name : Name + ": " + string.Join(", ", Pattern.Select(v => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
}

/// <summary>A drawing layer.</summary>
public sealed class Layer
{
    public const string DefaultLayerName = "0";
    public const double DefaultLineweightMm = 0.25;

    public Layer(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Layer name is required.", nameof(name));
        Id = id;
        Name = name.Trim();
    }

    public Guid Id { get; }
    public string Name { get; set; }

    /// <summary>Layer colour. Always an explicit RGB colour.</summary>
    public CadColor Color { get; set; } = CadColor.White;
    public string Linetype { get; set; } = Linetypes.Solid;
    public double LineweightMm { get; set; } = DefaultLineweightMm;

    /// <summary>On/off switch. Hidden layers are not drawn or selectable.</summary>
    public bool IsVisible { get; set; } = true;
    /// <summary>Locked layers are drawn and snappable but their entities cannot be selected for editing.</summary>
    public bool IsLocked { get; set; }
    /// <summary>Frozen layers are not drawn, not selectable and not snappable.</summary>
    public bool IsFrozen { get; set; }

    public bool IsDisplayed => IsVisible && !IsFrozen;
    public bool IsEditable => IsDisplayed && !IsLocked;

    public Layer Clone() => new(Id, Name)
    {
        Color = Color,
        Linetype = Linetype,
        LineweightMm = LineweightMm,
        IsVisible = IsVisible,
        IsLocked = IsLocked,
        IsFrozen = IsFrozen,
    };
}
