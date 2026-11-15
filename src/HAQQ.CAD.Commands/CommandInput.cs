using System.Globalization;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>One piece of user input delivered to the active command.</summary>
public abstract record CommandInput
{
    /// <summary>A model-space point (clicked, snapped or typed as coordinates).</summary>
    public sealed record PointInput(Point2D Point) : CommandInput;

    /// <summary>A bare number typed on the command line (a distance, radius...), in model units.</summary>
    public sealed record NumberInput(double Value) : CommandInput;

    /// <summary>An option keyword such as "C" for Close.</summary>
    public sealed record KeywordInput(string Keyword) : CommandInput;

    /// <summary>Enter / Space / right-click with nothing typed.</summary>
    public sealed record EnterInput : CommandInput;
}

/// <summary>Parses command-line text into command input.</summary>
public static class CommandInputParser
{
    /// <summary>
    /// Supported forms (lengths accept decimals, fractions and feet-inches):
    /// <list type="bullet">
    /// <item><c>x,y</c> absolute coordinates</item>
    /// <item><c>@dx,dy</c> relative to the last point</item>
    /// <item><c>@dist&lt;angle</c> polar relative to the last point (angle in degrees, counter-clockwise from +X)</item>
    /// <item><c>dist&lt;angle</c> polar from the origin</item>
    /// <item><c>number</c> a distance/radius</item>
    /// <item><c>letters</c> an option keyword</item>
    /// </list>
    /// </summary>
    public static bool TryParse(string text, Point2D? lastPoint, DrawingSettings settings,
        out CommandInput? input, out string? error)
    {
        input = null;
        error = null;
        string t = text.Trim();
        if (t.Length == 0) { input = new CommandInput.EnterInput(); return true; }

        bool relative = t[0] == '@';
        if (relative) t = t[1..].Trim();

        if (relative && lastPoint is null)
        {
            error = "Relative coordinates need a previous point.";
            return false;
        }
        Point2D basePoint = relative ? lastPoint!.Value : Point2D.Origin;

        int angleMark = t.IndexOf('<');
        if (angleMark >= 0)
        {
            string distText = t[..angleMark], angleText = t[(angleMark + 1)..];
            if (!UnitFormatter.TryParseLength(distText, settings, out double dist) ||
                !double.TryParse(angleText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees) ||
                !double.IsFinite(degrees))
            {
                error = "Invalid polar coordinates. Use distance<angle, e.g. @10<45.";
                return false;
            }
            input = new CommandInput.PointInput(basePoint + Vector2D.FromPolar(dist, GeoMath.DegreesToRadians(degrees)));
            return true;
        }

        if (t.Contains(','))
        {
            string[] parts = t.Split(',');
            if (parts.Length != 2 ||
                !UnitFormatter.TryParseLength(parts[0], settings, out double x) ||
                !UnitFormatter.TryParseLength(parts[1], settings, out double y))
            {
                error = "Invalid coordinates. Use x,y or @dx,dy.";
                return false;
            }
            input = new CommandInput.PointInput(new Point2D(basePoint.X + x, basePoint.Y + y));
            return true;
        }

        if (relative)
        {
            error = "Invalid relative coordinates. Use @dx,dy or @distance<angle.";
            return false;
        }

        if (UnitFormatter.TryParseLength(t, settings, out double number))
        {
            input = new CommandInput.NumberInput(number);
            return true;
        }

        // Option keywords are letters, optionally with digits ("3P", "2P").
        if (t.All(char.IsLetterOrDigit) && t.Any(char.IsLetter))
        {
            input = new CommandInput.KeywordInput(t.ToUpperInvariant());
            return true;
        }

        error = $"Invalid input \"{text.Trim()}\".";
        return false;
    }
}
