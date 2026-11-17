using System.Globalization;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Core;

/// <summary>What is drawn at the ends of a dimension line.</summary>
public enum DimensionArrow
{
    /// <summary>A filled arrowhead.</summary>
    Arrow,
    /// <summary>A short slanted stroke, as used on building drawings.</summary>
    Tick,
    /// <summary>A filled dot.</summary>
    Dot,
    None,
}

/// <summary>How a dimension writes a length.</summary>
public enum DimensionUnitFormat
{
    /// <summary>The same way as the rest of the drawing (the UNITS setting).</summary>
    Drawing,
    Decimal,
    /// <summary>Feet and inches with fractions: 5'-6 1/2".</summary>
    Architectural,
    /// <summary>Inches with fractions: 66 1/2".</summary>
    Fractional,
}

/// <summary>
/// A dimension style: how big the text and arrows of a dimension are, what its ends look like and how the
/// measured value is written. A drawing has a table of named styles; every dimension carries the values of the
/// style it was made with, and gets the new values when that style is changed.
/// </summary>
public sealed record DimensionStyle
{
    public const string StandardName = "Standard";

    public string Name { get; init; } = StandardName;

    // ---- text
    /// <summary>Height of the text, in drawing units.</summary>
    public double TextHeight { get; init; } = 4.0;
    /// <summary>Space between the dimension line and the text.</summary>
    public double TextGap { get; init; } = 1.4;

    // ---- ends
    public DimensionArrow Arrow { get; init; } = DimensionArrow.Arrow;
    /// <summary>Length of an arrowhead (or of a tick, or the width of a dot).</summary>
    public double ArrowSize { get; init; } = 4.0;

    // ---- extension lines
    /// <summary>Gap between the measured point and the start of its extension line.</summary>
    public double ExtensionOffset { get; init; } = 1.4;
    /// <summary>How far an extension line runs past the dimension line.</summary>
    public double ExtensionBeyond { get; init; } = 2.0;

    // ---- how lengths are written
    public DimensionUnitFormat Format { get; init; } = DimensionUnitFormat.Drawing;
    /// <summary>Digits after the decimal point (Decimal format).</summary>
    public int Precision { get; init; } = 2;
    /// <summary>Smallest fraction of an inch shown (Architectural and Fractional formats): 1, 2, 4, 8, 16, 32 or 64.</summary>
    public int FractionDenominator { get; init; } = 16;
    /// <summary>The value is rounded to the nearest multiple of this before it is written; 0 = no rounding.</summary>
    public double RoundOff { get; init; }
    /// <summary>The measured length is multiplied by this before it is written (1 = as measured).</summary>
    public double ScaleFactor { get; init; } = 1.0;
    public string Prefix { get; init; } = "";
    public string Suffix { get; init; } = "";
    /// <summary>Architectural: 6" instead of 0'-6".</summary>
    public bool SuppressZeroFeet { get; init; } = true;
    /// <summary>Architectural: 5' instead of 5'-0".</summary>
    public bool SuppressZeroInches { get; init; }

    // ---- angles
    /// <summary>Digits after the decimal point of an angle in degrees.</summary>
    public int AnglePrecision { get; init; } = 2;

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 64 && !name.Any(c => char.IsControl(c) || c == ',');

    /// <summary>Why the values cannot be used, or null when they are fine.</summary>
    public string? Problem
    {
        get
        {
            if (!IsValidName(Name)) return "A style name is 1 to 64 characters, without commas.";
            if (!Positive(TextHeight)) return "The text height must be more than 0.";
            if (!Positive(ArrowSize)) return "The arrow size must be more than 0.";
            if (!NotNegative(TextGap) || !NotNegative(ExtensionOffset) || !NotNegative(ExtensionBeyond)) return "Gaps and extensions cannot be negative.";
            if (!NotNegative(RoundOff)) return "Round off cannot be negative.";
            if (!Positive(ScaleFactor)) return "The scale factor must be more than 0.";
            if (Precision is < 0 or > 8 || AnglePrecision is < 0 or > 8) return "Precision is 0 to 8 decimal places.";
            if (FractionDenominator is not (1 or 2 or 4 or 8 or 16 or 32 or 64 or 128)) return "The fraction must be 1, 1/2, 1/4, 1/8, 1/16, 1/32, 1/64 or 1/128.";
            if ((Prefix + Suffix).Length > 64 || (Prefix + Suffix).Any(char.IsControl)) return "Prefix and suffix together are at most 64 characters.";
            return null;
        }
    }

    public bool IsValid => Problem is null;

    private static bool Positive(double v) => double.IsFinite(v) && v > 0;
    private static bool NotNegative(double v) => double.IsFinite(v) && v >= 0;

    /// <summary>The same style with every size multiplied by <paramref name="factor"/> (the way values are written is untouched).</summary>
    public DimensionStyle Scaled(double factor) => this with
    {
        TextHeight = TextHeight * factor,
        TextGap = TextGap * factor,
        ArrowSize = ArrowSize * factor,
        ExtensionOffset = ExtensionOffset * factor,
        ExtensionBeyond = ExtensionBeyond * factor,
    };

    /// <summary>Writes a measured length the way this style says.</summary>
    public string FormatLength(double value, DrawingSettings settings)
    {
        value *= ScaleFactor;
        if (RoundOff > 0) value = Math.Round(value / RoundOff, MidpointRounding.AwayFromZero) * RoundOff;

        var s = settings.Clone();
        if (Format != DimensionUnitFormat.Drawing)
        {
            s.Format = Format switch
            {
                DimensionUnitFormat.Architectural => LengthFormat.Architectural,
                DimensionUnitFormat.Fractional => LengthFormat.Fractional,
                _ => LengthFormat.Decimal,
            };
            s.DecimalPrecision = Precision;
            s.FractionDenominator = FractionDenominator;
        }

        string text = UnitFormatter.FormatLength(value, s);
        if (s.Format == LengthFormat.Architectural && UnitConverter.IsImperial(s.Unit))
        {
            if (!SuppressZeroFeet && !text.Contains('\'')) text = (text.StartsWith('-') ? "-0'-" + text[1..] : "0'-" + text);
            if (SuppressZeroInches && text.EndsWith("'-0\"", StringComparison.Ordinal)) text = text[..^3];
        }
        return Prefix + text + Suffix;
    }

    /// <summary>Writes an angle (radians) in degrees.</summary>
    public string FormatAngle(double radians)
    {
        string format = AnglePrecision <= 0 ? "0" : "0." + new string('#', AnglePrecision);
        return GeoMath.RadiansToDegrees(radians).ToString(format, CultureInfo.InvariantCulture) + "°";
    }
}
