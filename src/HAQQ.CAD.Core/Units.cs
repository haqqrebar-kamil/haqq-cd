using System.Globalization;
using System.Text.RegularExpressions;

namespace HAQQ.CAD.Core;

/// <summary>The real-world length represented by one model unit.</summary>
public enum LinearUnit
{
    Inch,
    Foot,
    Millimeter,
    Centimeter,
    Meter,
}

/// <summary>How lengths are displayed.</summary>
public enum LengthFormat
{
    /// <summary>Plain decimal number in drawing units, e.g. 66.5</summary>
    Decimal,
    /// <summary>Feet and fractional inches, e.g. 5'-6 1/2"</summary>
    Architectural,
    /// <summary>Fractional inches, e.g. 66 1/2"</summary>
    Fractional,
}

/// <summary>Per-drawing unit settings, stored in the HCAD file.</summary>
public sealed class DrawingSettings
{
    public LinearUnit Unit { get; set; } = LinearUnit.Inch;
    public LengthFormat Format { get; set; } = LengthFormat.Architectural;

    /// <summary>Digits after the decimal point for <see cref="LengthFormat.Decimal"/>.</summary>
    public int DecimalPrecision { get; set; } = 4;

    /// <summary>Smallest displayed fraction of an inch (2, 4, 8, 16, 32, 64...).</summary>
    public int FractionDenominator { get; set; } = 16;

    /// <summary>Grid spacing in model units.</summary>
    public double GridSpacing { get; set; } = 12.0;

    /// <summary>Increment the cursor jumps to when SNAP mode is on, in model units.</summary>
    public double SnapSpacing { get; set; } = 1.0;

    public DrawingSettings Clone() => (DrawingSettings)MemberwiseClone();
}

public static class UnitConverter
{
    public static double MillimetersPerUnit(LinearUnit unit) => unit switch
    {
        LinearUnit.Inch => 25.4,
        LinearUnit.Foot => 304.8,
        LinearUnit.Millimeter => 1.0,
        LinearUnit.Centimeter => 10.0,
        LinearUnit.Meter => 1000.0,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    public static double Convert(double value, LinearUnit from, LinearUnit to) =>
        from == to ? value : value * MillimetersPerUnit(from) / MillimetersPerUnit(to);

    public static bool IsImperial(LinearUnit unit) => unit is LinearUnit.Inch or LinearUnit.Foot;
}

/// <summary>Formats and parses lengths, including feet and fractional inches.</summary>
public static partial class UnitFormatter
{
    // [sign] [feet'] [-] [ inches | inches n/d | inches-n/d | n/d ] ["]
    [GeneratedRegex(@"^\s*(?<sign>[+-])?\s*(?:(?<ft>\d+(?:\.\d+)?|\.\d+)\s*')?\s*-?\s*(?:(?<in>\d+(?:\.\d+)?|\.\d+)(?:\s*[- ]\s*(?<n>\d+)\s*/\s*(?<d>\d+))?|(?<n2>\d+)\s*/\s*(?<d2>\d+))?\s*(?<mark>"")?\s*$")]
    private static partial Regex LengthRegex();

    /// <summary>
    /// Parses a length typed by the user into model units. Accepts decimals (<c>66.5</c>),
    /// fractions (<c>2 1/2</c>, <c>3/8</c>) and feet-inches (<c>5'</c>, <c>5'6</c>, <c>5'-6 1/2"</c>, <c>6"</c>).
    /// Values with a foot or inch mark are imperial and are converted to the drawing unit;
    /// unmarked values are already in drawing units.
    /// </summary>
    public static bool TryParseLength(string? text, DrawingSettings settings, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain))
        {
            if (!double.IsFinite(plain)) return false;
            value = plain;
            return true;
        }

        var m = LengthRegex().Match(text);
        if (!m.Success) return false;

        bool hasFeet = m.Groups["ft"].Success;
        bool hasInches = m.Groups["in"].Success || m.Groups["n2"].Success;
        bool hasInchMark = m.Groups["mark"].Success;
        if (!hasFeet && !hasInches) return false;
        if (hasInchMark && !hasInches) return false;

        double feet = hasFeet ? double.Parse(m.Groups["ft"].Value, CultureInfo.InvariantCulture) : 0;
        double inches = m.Groups["in"].Success ? double.Parse(m.Groups["in"].Value, CultureInfo.InvariantCulture) : 0;

        string nText = m.Groups["n"].Success ? m.Groups["n"].Value : m.Groups["n2"].Value;
        string dText = m.Groups["d"].Success ? m.Groups["d"].Value : m.Groups["d2"].Value;
        if (nText.Length > 0)
        {
            if (!double.TryParse(nText, NumberStyles.None, CultureInfo.InvariantCulture, out double num)) return false;
            if (!double.TryParse(dText, NumberStyles.None, CultureInfo.InvariantCulture, out double den) || den == 0) return false;
            inches += num / den;
        }

        double result;
        if (hasFeet || hasInchMark)
        {
            // Imperial input: total inches, then into the drawing unit.
            result = UnitConverter.Convert(feet * 12.0 + inches, LinearUnit.Inch, settings.Unit);
        }
        else
        {
            // Unmarked fraction such as "2 1/2": already in drawing units.
            result = inches;
        }

        if (m.Groups["sign"].Value == "-") result = -result;
        if (!double.IsFinite(result)) return false;
        value = result;
        return true;
    }

    /// <summary>Formats a length in model units according to the drawing settings.</summary>
    public static string FormatLength(double value, DrawingSettings settings)
    {
        if (!double.IsFinite(value)) return "?";
        switch (settings.Format)
        {
            case LengthFormat.Architectural when UnitConverter.IsImperial(settings.Unit):
                return FormatFeetInches(UnitConverter.Convert(value, settings.Unit, LinearUnit.Inch),
                    settings.FractionDenominator, useFeet: true);
            case LengthFormat.Fractional when UnitConverter.IsImperial(settings.Unit):
                return FormatFeetInches(UnitConverter.Convert(value, settings.Unit, LinearUnit.Inch),
                    settings.FractionDenominator, useFeet: false);
            default:
                return FormatDecimal(value, settings.DecimalPrecision);
        }
    }

    public static string FormatDecimal(double value, int precision)
    {
        precision = Math.Clamp(precision, 0, 12);
        double rounded = Math.Round(value, precision, MidpointRounding.AwayFromZero);
        if (rounded == 0) rounded = 0; // avoid "-0"
        return rounded.ToString("F" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>Formats inches as feet-inches-fraction, e.g. 66.5 -> 5'-6 1/2".</summary>
    public static string FormatFeetInches(double totalInches, int denominator, bool useFeet)
    {
        if (denominator < 1) denominator = 1;
        bool negative = totalInches < 0;
        // Work in whole fractions so rounding carries correctly (11 31/32" -> 1'-0").
        double scaled = Math.Round(Math.Abs(totalInches) * denominator, MidpointRounding.AwayFromZero);
        if (scaled > 9.0e15) return FormatDecimal(totalInches, 4) + "\"";
        long units = (long)scaled;
        if (units == 0) return useFeet ? "0\"" : "0\"";

        long feet = useFeet ? units / (12L * denominator) : 0;
        long rest = units - feet * 12L * denominator;
        long wholeInches = rest / denominator;
        long num = rest % denominator;
        long den = denominator;
        if (num != 0)
        {
            long g = Gcd(num, den);
            num /= g;
            den /= g;
        }

        string inchText = num == 0
            ? wholeInches.ToString(CultureInfo.InvariantCulture)
            : wholeInches == 0 && feet == 0
                ? $"{num}/{den}"
                : $"{wholeInches} {num}/{den}";

        string body = feet > 0 ? $"{feet}'-{inchText}\"" : $"{inchText}\"";
        return negative ? "-" + body : body;
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a == 0 ? 1 : a;
    }
}
