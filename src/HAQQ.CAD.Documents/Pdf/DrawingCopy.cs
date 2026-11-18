using System.Globalization;
using System.Text.RegularExpressions;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Documents.Pdf;

/// <summary>How a PDF page is turned into drawing objects.</summary>
public sealed class CopyOptions
{
    /// <summary>Drawing units for one PDF point (1/72 inch of paper). 1/72 copies the sheet at its paper size in an inch drawing.</summary>
    public double UnitsPerPoint { get; set; } = 1.0 / 72.0;

    /// <summary>True: tidy the result (see <see cref="GeometryCleanup"/>). False: keep every line exactly where the PDF has it.</summary>
    public bool Clean { get; set; }

    /// <summary>For Clean: how far a point may be moved, in PDF points (so it means the same on paper at any scale).</summary>
    public double TolerancePoints { get; set; } = 0.25;

    public double AngleToleranceDegrees { get; set; } = 0.5;
    public bool JoinIntoPolylines { get; set; } = true;
    public bool IncludeText { get; set; } = true;

    /// <summary>Outlines of filled shapes (arrowheads, solid areas). The page's own white background is always left out.</summary>
    public bool IncludeFills { get; set; } = true;

    /// <summary>Only what lies wholly inside this part of the page (in points); null for the whole page.</summary>
    public BoundingBox? Region { get; set; }
}

/// <summary>The objects that will go on one layer: all lines of one pen, the dashed ones, the text, the fills.</summary>
public sealed class CopyGroup
{
    /// <summary>Stays the same for the same kind of line work in any PDF ("line|heavy|#000000"), so a choice made once can be used again.</summary>
    public string Key { get; init; } = "";

    /// <summary>What the group is, in words.</summary>
    public string Description { get; init; } = "";

    /// <summary>The layer it goes on. Can be changed before placing.</summary>
    public string LayerName { get; set; } = "";
    public bool Include { get; set; } = true;
    public CadColor Color { get; init; }
    public string Linetype { get; init; } = Linetypes.Solid;
    public List<Entity> Entities { get; set; } = new();
}

/// <summary>A PDF page as drawing objects, ready to be looked over and placed.</summary>
public sealed class CopyResult
{
    public List<CopyGroup> Groups { get; } = new();

    /// <summary>A scale found written on the page ("1/4\" = 1'-0\""), and what it is as paper size over real size.</summary>
    public string? ScaleText { get; set; }
    public double? Scale { get; set; }

    /// <summary>Things the user should know: text that could not be read, pictures that were not copied, several scales found.</summary>
    public List<string> Notes { get; } = new();

    public CleanupReport? Cleanup { get; set; }

    public IEnumerable<Entity> Included => Groups.Where(g => g.Include).SelectMany(g => g.Entities);
    public int Count<T>() where T : Entity => Included.OfType<T>().Count();
}

/// <summary>
/// Turns the line work and text of a PDF page into HAQQ CAD objects: lines, polylines (with arc pieces),
/// arcs, circles and text, sorted into groups by pen so each can go on its own layer. Nothing is guessed:
/// every object comes from an outline or a piece of text that is in the PDF.
/// </summary>
public static partial class DrawingCopy
{
    private const double HeavyFrom = 0.9, MediumFrom = 0.35;      // pen widths in points
    private const double SameCircle = 5e-3;                       // arcs whose centres and radii agree to this part of the radius are one circle

    public static CopyResult Convert(PdfPageContent page, CopyOptions options)
    {
        var result = new CopyResult();
        double k = options.UnitsPerPoint;
        if (!(k > 0) || !double.IsFinite(k)) throw new ArgumentException("The scale must be more than 0.");
        var groups = new Dictionary<string, CopyGroup>();

        CopyGroup Group(string kind, string weight, byte r, byte g, byte b, bool dashed)
        {
            // Black ink is white on HAQQ CAD's dark background; so is ink too dark to see.
            bool dark = r < 40 && g < 40 && b < 40;
            var color = dark ? CadColor.FromRgb(255, 255, 255) : CadColor.FromRgb(r, g, b);
            string ink = dark ? "" : $" #{r:X2}{g:X2}{b:X2}";
            string key = $"{kind}|{weight}|{(dark ? "black" : ink.Trim())}";
            if (groups.TryGetValue(key, out var group)) return group;
            string name = kind switch
            {
                "text" => "Copy Text",
                "fill" => "Copy Filled",
                "dashed" => "Copy Dashed",
                _ => "Copy " + weight,
            } + ink;
            string description = kind switch
            {
                "text" => "Text",
                "fill" => "Outlines of filled shapes (arrowheads, solid areas)",
                "dashed" => "Dashed lines",
                _ => weight + " lines",
            } + (dark ? "" : $", colour{ink}");
            group = new CopyGroup { Key = key, Description = description, LayerName = name, Color = color, Linetype = dashed ? Linetypes.Dash : Linetypes.Solid };
            groups[key] = group;
            result.Groups.Add(group);
            return group;
        }

        double pageArea = page.Width * page.Height;
        foreach (var path in page.Paths)
        {
            if (!path.Stroked)
            {
                if (!options.IncludeFills) continue;
                // The sheet's own white background, and white patches that hide what is under them, are not line work.
                if (path.R > 250 && path.G > 250 && path.B > 250) continue;
            }
            foreach (var subpath in path.Subpaths)
            {
                if (subpath.Segments.Count == 0) continue;
                if (options.Region is { } region && !subpath.Segments.All(s => region.Contains(new Point2D(s.X1, s.Y1)) && region.Contains(new Point2D(s.X2, s.Y2)))) continue;
                if (!path.Stroked && pageArea > 0 && CoversPage(subpath, page)) continue;

                string kind = !path.Stroked ? "fill" : path.Dashed ? "dashed" : "line";
                string weight = kind != "line" ? "" : path.LineWidth >= HeavyFrom ? "Heavy" : path.LineWidth >= MediumFrom ? "Medium" : "Light";
                var group = Group(kind, weight, path.R, path.G, path.B, path.Dashed);
                foreach (var entity in ToEntities(subpath, k)) group.Entities.Add(entity);
            }
        }

        if (options.IncludeText)
        {
            foreach (var text in MergeRuns(page.Texts))
            {
                if (options.Region is { } region && !region.Contains(new Point2D(text.X, text.Y))) continue;
                // PDF gives the font size (the height of a whole line of type); a capital is about 0.7 of it.
                double height = text.Height * 0.7 * k;
                if (!(height > 0)) continue;
                double up = text.Height * 0.35;
                var at = new Point2D((text.X - Math.Sin(text.Angle) * up) * k, (text.Y + Math.Cos(text.Angle) * up) * k);
                string words = new string(text.Text.Where(c => !char.IsControl(c)).ToArray()).Trim();
                if (words.Length == 0) continue;
                if (words.Length > TextEntity.MaxLength) words = words[..TextEntity.MaxLength];
                var entity = new TextEntity(at, height, words) { Justify = TextJustify.Left, Angle = text.Angle };
                if (entity.IsValid) Group("text", "", 0, 0, 0, false).Entities.Add(entity);
            }
        }

        if (page.UnreadableTextCount > 0)
            result.Notes.Add($"{page.UnreadableTextCount} piece(s) of text could not be read as letters (the PDF's fonts do not say which letters they are). They were left out.");
        if (page.ImageCount > 0)
            result.Notes.Add(page.LooksScanned
                ? "This page is a picture (a scan), not line work: there is nothing here to copy as lines yet."
                : $"{page.ImageCount} picture(s) on the page were not copied.");
        if (page.Paths.Count == 0 && page.Texts.Count == 0 && page.ImageCount == 0) result.Notes.Add("The page is empty.");

        DetectScale(page, result);

        if (options.Clean)
        {
            double tolerance = Math.Max(options.TolerancePoints, 0) * k;
            int straightened = 0, duplicates = 0, merged = 0, tiny = 0, joined = 0;
            foreach (var group in result.Groups)
            {
                if (group.Key.StartsWith("text", StringComparison.Ordinal)) continue;
                group.Entities = GeometryCleanup.Clean(group.Entities, tolerance, options.AngleToleranceDegrees, options.JoinIntoPolylines, out var report);
                straightened += report.Straightened;
                duplicates += report.DuplicatesRemoved;
                merged += report.Merged;
                tiny += report.TinyRemoved;
                joined += report.Joined;
            }
            result.Cleanup = new CleanupReport(straightened, duplicates, merged, tiny, joined);
        }

        result.Groups.RemoveAll(g => g.Entities.Count == 0);
        return result;
    }

    private static bool CoversPage(PdfSubpath subpath, PdfPageContent page)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var s in subpath.Segments)
        {
            minX = Math.Min(minX, Math.Min(s.X1, s.X2)); maxX = Math.Max(maxX, Math.Max(s.X1, s.X2));
            minY = Math.Min(minY, Math.Min(s.Y1, s.Y2)); maxY = Math.Max(maxY, Math.Max(s.Y1, s.Y2));
        }
        return maxX - minX >= page.Width * 0.98 && maxY - minY >= page.Height * 0.98;
    }

    // ------------------------------------------------------------- outlines

    /// <summary>A piece of an outline once curves have been looked at: a straight line, or a round arc.</summary>
    private readonly record struct Piece(Point2D From, Point2D To, bool IsArc, Point2D Center, double Radius, double Sweep);

    private static IEnumerable<Entity> ToEntities(PdfSubpath subpath, double k)
    {
        var pieces = new List<Piece>();
        foreach (var s in subpath.Segments)
        {
            var a = new Point2D(s.X1 * k, s.Y1 * k);
            var b = new Point2D(s.X2 * k, s.Y2 * k);
            if (!s.IsCurve)
            {
                if (a.DistanceTo(b) > 1e-12) pieces.Add(new Piece(a, b, false, default, 0, 0));
                continue;
            }
            var c1 = new Point2D(s.C1X * k, s.C1Y * k);
            var c2 = new Point2D(s.C2X * k, s.C2Y * k);
            if (TryArc(a, c1, c2, b, out var center, out double radius, out double sweep))
            {
                // Arcs that carry on round the same centre are one arc.
                if (pieces.Count > 0 && pieces[^1] is { IsArc: true } last && last.Center.DistanceTo(center) <= radius * SameCircle &&
                    Math.Abs(last.Radius - radius) <= radius * SameCircle && Math.Sign(last.Sweep) == Math.Sign(sweep) && Math.Abs(last.Sweep + sweep) < 2 * Math.PI - 1e-6)
                    pieces[^1] = last with { To = b, Sweep = last.Sweep + sweep };
                else pieces.Add(new Piece(a, b, true, center, radius, sweep));
            }
            else
            {
                // Not part of a circle: short straight pieces along the curve.
                const int steps = 12;
                var previous = a;
                for (int i = 1; i <= steps; i++)
                {
                    double t = i / (double)steps, u = 1 - t;
                    var p = new Point2D(
                        u * u * u * a.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * b.X,
                        u * u * u * a.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * b.Y);
                    if (previous.DistanceTo(p) > 1e-12) pieces.Add(new Piece(previous, p, false, default, 0, 0));
                    previous = p;
                }
            }
        }
        if (pieces.Count == 0) yield break;

        // A whole circle: arcs round one centre that come back to where they began. Programs write a circle as
        // 4 to 12 curves with rounded numbers, so the centres agree only to a few thousandths of the radius.
        if (pieces.All(p => p.IsArc) && pieces[0].From.DistanceTo(pieces[^1].To) <= pieces[0].Radius * SameCircle &&
            Math.Abs(Math.Abs(pieces.Sum(p => p.Sweep)) - 2 * Math.PI) < 0.02)
        {
            var middle = new Point2D(pieces.Average(p => p.Center.X), pieces.Average(p => p.Center.Y));
            double mean = pieces.Average(p => p.Radius);
            if (pieces.All(p => p.Center.DistanceTo(middle) <= mean * SameCircle && Math.Abs(p.Radius - mean) <= mean * SameCircle))
            {
                var circle = new CircleEntity(middle, mean);
                if (circle.IsValid) yield return circle;
                yield break;
            }
        }

        if (pieces.Count == 1)
        {
            var only = pieces[0];
            Entity single = only.IsArc ? ArcOf(only) : new LineEntity(only.From, only.To);
            if (single.IsValid) yield return single;
            yield break;
        }

        // Several pieces: one polyline, with a bulge where a piece is an arc.
        bool closed = subpath.Closed || pieces[0].From.DistanceTo(pieces[^1].To) <= 1e-9;
        var vertices = new List<Point2D>();
        var bulges = new List<double>();
        foreach (var piece in pieces)
        {
            vertices.Add(piece.From);
            bulges.Add(piece.IsArc ? Math.Tan(piece.Sweep / 4.0) : 0.0);
        }
        if (!closed)
        {
            vertices.Add(pieces[^1].To);
            bulges.Add(0);
        }
        var polyline = new PolylineEntity(vertices, closed, bulges);
        if (polyline.IsValid) yield return polyline;
        else
            foreach (var piece in pieces)
            {
                Entity part = piece.IsArc ? ArcOf(piece) : new LineEntity(piece.From, piece.To);
                if (part.IsValid) yield return part;
            }
    }

    private static ArcEntity ArcOf(Piece piece)
    {
        double start = Math.Atan2(piece.From.Y - piece.Center.Y, piece.From.X - piece.Center.X);
        double end = Math.Atan2(piece.To.Y - piece.Center.Y, piece.To.X - piece.Center.X);
        // HAQQ CAD arcs run counter-clockwise from start to end.
        return piece.Sweep >= 0 ? new ArcEntity(piece.Center, piece.Radius, start, end) : new ArcEntity(piece.Center, piece.Radius, end, start);
    }

    /// <summary>
    /// Is this Bezier curve a piece of a circle? PDF has no arcs, so circles and arcs are written as curves;
    /// one that stays on a circle to within a thousandth of its radius is taken back to an arc.
    /// </summary>
    private static bool TryArc(Point2D p0, Point2D c1, Point2D c2, Point2D p3, out Point2D center, out double radius, out double sweep)
    {
        center = default;
        radius = sweep = 0;
        var t0 = c1 - p0;
        var t3 = p3 - c2;
        double chord = p0.DistanceTo(p3);
        if (t0.Length < chord * 1e-6 || t3.Length < chord * 1e-6 || chord < 1e-12) return false;

        static Point2D At(Point2D a, Point2D b, Point2D c, Point2D d, double t)
        {
            double u = 1 - t;
            return new Point2D(
                u * u * u * a.X + 3 * u * u * t * b.X + 3 * u * t * t * c.X + t * t * t * d.X,
                u * u * u * a.Y + 3 * u * u * t * b.Y + 3 * u * t * t * c.Y + t * t * t * d.Y);
        }

        // The circle through the two ends and the middle of the curve (steadier than working from the
        // directions at the ends, which rounded numbers in the file throw off).
        var mid = At(p0, c1, c2, p3, 0.5);
        double ax = mid.X - p0.X, ay = mid.Y - p0.Y, bx = p3.X - p0.X, by = p3.Y - p0.Y;
        double d2 = 2 * (ax * by - ay * bx);
        if (Math.Abs(d2) < 1e-12 * chord * chord) return false;               // the three points are in a line: not an arc
        double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by;
        center = new Point2D(p0.X + (by * a2 - ay * b2) / d2, p0.Y + (ax * b2 - bx * a2) / d2);
        radius = center.DistanceTo(p0);
        if (!(radius > 1e-12) || radius > chord * 1e4) return false;
        double limit = radius * 1e-3;
        foreach (double t in new[] { 0.125, 0.25, 0.375, 0.625, 0.75, 0.875 })
            if (Math.Abs(center.DistanceTo(At(p0, c1, c2, p3, t)) - radius) > limit) return false;
        // The curve must leave and arrive along the circle, not across it.
        var r0 = p0 - center;
        var r3 = p3 - center;
        if (Math.Abs(r0.Dot(t0)) > 0.05 * r0.Length * t0.Length || Math.Abs(r3.Dot(t3)) > 0.05 * r3.Length * t3.Length) return false;

        double a0 = Math.Atan2(p0.Y - center.Y, p0.X - center.X), a3 = Math.Atan2(p3.Y - center.Y, p3.X - center.X);
        // Which way round: the way the curve leaves its first point.
        bool counterClockwise = (p0.X - center.X) * t0.Y - (p0.Y - center.Y) * t0.X > 0;
        sweep = a3 - a0;
        if (counterClockwise) { while (sweep <= 0) sweep += 2 * Math.PI; }
        else { while (sweep >= 0) sweep -= 2 * Math.PI; }
        return Math.Abs(sweep) < Math.PI * 1.01;
    }

    // ------------------------------------------------------------------ text

    /// <summary>Puts back together text that the PDF wrote in several pieces along one line.</summary>
    private static List<PdfText> MergeRuns(List<PdfText> texts)
    {
        var merged = new List<PdfText>();
        foreach (var text in texts)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                double cos = Math.Cos(last.Angle), sin = Math.Sin(last.Angle);
                double along = (text.X - last.X) * cos + (text.Y - last.Y) * sin, across = -(text.X - last.X) * sin + (text.Y - last.Y) * cos;
                double gap = along - last.Width, h = last.Height;
                bool sameLine = Math.Abs(across) < h * 0.2 && Math.Abs(text.Height - h) < h * 0.05 && Math.Abs(Math.IEEERemainder(text.Angle - last.Angle, 2 * Math.PI)) < 0.01;
                if (sameLine && gap > -h * 0.3 && gap < h * 1.2 && last.Text.Length + text.Text.Length < 400)
                {
                    bool space = gap > h * 0.2 && !last.Text.EndsWith(' ') && !text.Text.StartsWith(' ');
                    merged[^1] = last with { Text = last.Text + (space ? " " : "") + text.Text, Width = along + text.Width };
                    continue;
                }
            }
            merged.Add(text);
        }
        return merged;
    }

    // ----------------------------------------------------------------- scale

    [GeneratedRegex(@"(?<paper>\d+(?:\s+\d+/\d+|/\d+|\.\d+)?)\s*(?:""|''|in\.?|inch)\s*=\s*(?<real>\d+(?:\.\d+)?\s*'(?:\s*-?\s*\d+(?:\s+\d+/\d+|/\d+|\.\d+)?\s*(?:""|''))?)", RegexOptions.IgnoreCase)]
    private static partial Regex ImperialScale();

    [GeneratedRegex(@"(?<![\d.])1\s*:\s*(?<n>\d{1,5}(?:\.\d+)?)(?![\d:])")]
    private static partial Regex RatioScale();

    /// <summary>Looks through the page's text for a written scale: 1/4" = 1'-0", 1" = 20', 1:50.</summary>
    private static void DetectScale(PdfPageContent page, CopyResult result)
    {
        var found = new List<(string Text, double Scale)>();
        var inch = new DrawingSettings { Unit = LinearUnit.Inch };
        foreach (var text in MergeRuns(page.Texts))
        {
            string t = text.Text.Replace('”', '"').Replace('″', '"').Replace('’', '\'').Replace('′', '\'');
            var m = ImperialScale().Match(t);
            if (m.Success)
            {
                string paperText = m.Groups["paper"].Value.Trim(), realText = m.Groups["real"].Value.Replace("''", "\"").Replace(" ", "").Trim();
                if (UnitFormatter.TryParseLength(paperText, inch, out double paper) && UnitFormatter.TryParseLength(realText, inch, out double real) && paper > 0 && real > 0)
                    found.Add((m.Value.Trim(), paper / real));
                continue;
            }
            // A bare ratio only counts where the word "scale" stands with it, so that "grid 1:2" or a time is not taken for one.
            if (t.Contains("scale", StringComparison.OrdinalIgnoreCase) || t.Contains("ölçek", StringComparison.OrdinalIgnoreCase))
            {
                var r = RatioScale().Match(t);
                if (r.Success && double.TryParse(r.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && n > 0)
                    found.Add((r.Value.Replace(" ", ""), 1.0 / n));
            }
        }
        if (found.Count == 0) return;
        result.ScaleText = found[0].Text;
        result.Scale = found[0].Scale;
        var different = found.Select(f => f.Scale).Where(s => Math.Abs(s - found[0].Scale) > found[0].Scale * 1e-6).Distinct().Count();
        if (different > 0)
            result.Notes.Add($"More than one scale is written on this page ({string.Join(", ", found.Select(f => f.Text).Distinct().Take(4))}). The first was taken; a page with several scales needs each part copied at its own.");
    }

    /// <summary>
    /// Drawing units per PDF point for a scale (paper size over real size, 1/48 for 1/4" = 1'-0"): a point is
    /// 1/72 inch of paper, which stands for 1/72/scale inches of the real thing.
    /// </summary>
    public static double UnitsPerPoint(double scale, LinearUnit drawingUnit) =>
        UnitConverter.Convert(1.0 / 72.0 / scale, LinearUnit.Inch, drawingUnit);
}
