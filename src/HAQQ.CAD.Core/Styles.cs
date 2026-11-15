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

/// <summary>Well-known linetype names. Phase 1 stores linetype names; only continuous lines are rendered.</summary>
public static class Linetypes
{
    public const string ByLayer = "ByLayer";
    public const string ByBlock = "ByBlock";
    public const string Continuous = "Continuous";
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
    public string Linetype { get; set; } = Linetypes.Continuous;
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
