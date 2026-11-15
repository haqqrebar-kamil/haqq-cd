using HAQQ.CAD.Core;
using Xunit;

namespace HAQQ.CAD.Tests;

public class UnitsTests
{
    private static readonly DrawingSettings Inches = new() { Unit = LinearUnit.Inch, Format = LengthFormat.Architectural, FractionDenominator = 16 };

    [Theory]
    [InlineData("66.5", 66.5)]
    [InlineData(".5", 0.5)]
    [InlineData("-12", -12.0)]
    [InlineData("5'", 60.0)]
    [InlineData("5'6", 66.0)]
    [InlineData("5'-6\"", 66.0)]
    [InlineData("5'-6 1/2\"", 66.5)]
    [InlineData("5' 6-1/2\"", 66.5)]
    [InlineData("5'6.25\"", 66.25)]
    [InlineData("6\"", 6.0)]
    [InlineData("1/2\"", 0.5)]
    [InlineData("3/8", 0.375)]
    [InlineData("2 1/2", 2.5)]
    [InlineData("2-1/2", 2.5)]
    [InlineData("-1'6\"", -18.0)]
    [InlineData("  10'-0\"  ", 120.0)]
    [InlineData("0'-0 3/16\"", 0.1875)]
    public void Parses_lengths_into_inches(string text, double expected)
    {
        Assert.True(UnitFormatter.TryParseLength(text, Inches, out double value), text);
        Assert.Equal(expected, value, 10);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("5'x")]
    [InlineData("1/0")]
    [InlineData("\"")]
    [InlineData("5''")]
    [InlineData("1 2")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Rejects_invalid_lengths(string text)
    {
        Assert.False(UnitFormatter.TryParseLength(text, Inches, out _), text);
    }

    [Theory]
    [InlineData(66.5, "5'-6 1/2\"")]
    [InlineData(60.0, "5'-0\"")]
    [InlineData(6.0, "6\"")]
    [InlineData(0.5, "1/2\"")]
    [InlineData(12.1875, "1'-0 3/16\"")]
    [InlineData(11.99, "1'-0\"")]      // rounds up and carries into feet
    [InlineData(-18.0, "-1'-6\"")]
    [InlineData(0.0, "0\"")]
    [InlineData(0.01, "0\"")]          // below half of 1/16
    [InlineData(432.0, "36'-0\"")]
    public void Formats_architectural(double inches, string expected)
    {
        Assert.Equal(expected, UnitFormatter.FormatLength(inches, Inches));
    }

    [Fact]
    public void Fractional_format_keeps_inches()
    {
        var s = new DrawingSettings { Unit = LinearUnit.Inch, Format = LengthFormat.Fractional, FractionDenominator = 8 };
        Assert.Equal("66 1/2\"", UnitFormatter.FormatLength(66.5, s));
        Assert.Equal("66 3/8\"", UnitFormatter.FormatLength(66.4, s));
    }

    [Fact]
    public void Decimal_format_uses_precision()
    {
        var s = new DrawingSettings { Unit = LinearUnit.Millimeter, Format = LengthFormat.Decimal, DecimalPrecision = 2 };
        Assert.Equal("1234.57", UnitFormatter.FormatLength(1234.5678, s));
        Assert.Equal("0.00", UnitFormatter.FormatLength(-0.0001, s));
        // Architectural display is not meaningful for metric drawings: falls back to decimal.
        s.Format = LengthFormat.Architectural;
        Assert.Equal("25.40", UnitFormatter.FormatLength(25.4, s));
    }

    [Fact]
    public void Formatted_values_parse_back_to_the_displayed_precision()
    {
        for (int sixteenths = -400; sixteenths <= 4000; sixteenths += 7)
        {
            double value = sixteenths / 16.0;
            string text = UnitFormatter.FormatLength(value, Inches);
            Assert.True(UnitFormatter.TryParseLength(text, Inches, out double parsed), text);
            Assert.Equal(value, parsed, 10);
        }
    }

    [Fact]
    public void Imperial_input_converts_to_the_drawing_unit()
    {
        var feet = new DrawingSettings { Unit = LinearUnit.Foot };
        Assert.True(UnitFormatter.TryParseLength("6\"", feet, out double v));
        Assert.Equal(0.5, v, 12);
        Assert.True(UnitFormatter.TryParseLength("2'-6\"", feet, out v));
        Assert.Equal(2.5, v, 12);

        var mm = new DrawingSettings { Unit = LinearUnit.Millimeter };
        Assert.True(UnitFormatter.TryParseLength("1'", mm, out v));
        Assert.Equal(304.8, v, 9);
        // Unmarked numbers are always drawing units.
        Assert.True(UnitFormatter.TryParseLength("250", mm, out v));
        Assert.Equal(250.0, v);
    }

    [Fact]
    public void Unit_conversion()
    {
        Assert.Equal(12.0, UnitConverter.Convert(1, LinearUnit.Foot, LinearUnit.Inch), 12);
        Assert.Equal(25.4, UnitConverter.Convert(1, LinearUnit.Inch, LinearUnit.Millimeter), 12);
        Assert.Equal(3.28083989501, UnitConverter.Convert(1, LinearUnit.Meter, LinearUnit.Foot), 9);
        Assert.Equal(7.0, UnitConverter.Convert(7, LinearUnit.Centimeter, LinearUnit.Centimeter));
    }

    [Fact]
    public void Colors_parse_and_format()
    {
        Assert.True(CadColor.TryParse("#FF8000", out var c));
        Assert.Equal(CadColor.FromRgb(255, 128, 0), c);
        Assert.Equal("#FF8000", c.ToString());
        Assert.True(CadColor.TryParse("bylayer", out c));
        Assert.Equal(CadColor.ByLayer, c);
        Assert.Equal("ByLayer", c.ToString());
        Assert.False(CadColor.TryParse("#GG0000", out _));
        Assert.False(CadColor.TryParse("red", out _));
    }
}
