using System.Globalization;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Core;

public enum DimensionKind
{
    /// <summary>Distance between two points measured along a fixed direction (horizontal, vertical or any angle).</summary>
    Linear,
    /// <summary>True distance between two points; the dimension line is parallel to them.</summary>
    Aligned,
    Radius,
    Diameter,
    /// <summary>Angle at <see cref="DimensionEntity.Center"/> between the rays to P1 and P2, on the side of the dimension arc.</summary>
    Angle,
    /// <summary>Length along an arc from P1 to P2 (counter-clockwise about the center).</summary>
    ArcLength,
}

/// <summary>What a dimension is drawn from: straight pieces, arcs, arrowheads and one text.</summary>
public sealed class DimensionGeometry
{
    public List<(Point2D A, Point2D B)> Lines { get; } = new();
    public List<(Point2D Center, double Radius, double StartAngle, double Sweep)> Arcs { get; } = new();

    /// <summary>Arrowheads: the tip and the unit direction the arrow points in.</summary>
    public List<(Point2D Tip, Vector2D Direction)> Arrows { get; } = new();

    /// <summary>Centre of the text.</summary>
    public Point2D TextCenter { get; set; }

    /// <summary>Text direction in radians, always readable from the bottom or the right of the drawing.</summary>
    public double TextAngle { get; set; }
}

/// <summary>
/// A dimension. It stores the points it measures and where its dimension line is; the value shown is worked
/// out from those points every time, so moving, stretching or scaling the dimension keeps it correct. It is
/// not tied to the object it was placed on.
/// </summary>
public sealed class DimensionEntity : Entity
{
    public const string Type = "dimension";

    /// <summary>What appears in <see cref="TextOverride"/> where the measured value should go.</summary>
    public const string MeasuredMark = "<>";

    public DimensionEntity(Guid id, DimensionKind kind) : base(id) => Kind = kind;
    public DimensionEntity(DimensionKind kind) : this(Guid.NewGuid(), kind) { }

    public DimensionKind Kind { get; }

    /// <summary>First measured point. Radius / Diameter: the point on the circle the arrow touches.</summary>
    public Point2D P1 { get; set; }

    /// <summary>Second measured point (not used by Radius and Diameter).</summary>
    public Point2D P2 { get; set; }

    /// <summary>Centre of the circle or arc, or the corner of an angle (not used by Linear and Aligned).</summary>
    public Point2D Center { get; set; }

    /// <summary>A point the dimension line (or dimension arc) passes through.</summary>
    public Point2D LinePoint { get; set; }

    /// <summary>Linear only: direction of the dimension line in radians (0 = horizontal, pi/2 = vertical).</summary>
    public double Angle { get; set; }

    /// <summary>Height of the text in drawing units. Arrowheads and gaps are sized from it.</summary>
    public double TextHeight { get; set; } = 4.0;

    /// <summary>Text to show instead of the measured value; "&lt;&gt;" inside it stands for the measured value. Empty = measured value.</summary>
    public string TextOverride { get; set; } = "";

    public bool MeasuresLength => Kind != DimensionKind.Angle;

    /// <summary>Unit direction of the dimension line (Linear and Aligned).</summary>
    public Vector2D Direction
    {
        get
        {
            if (Kind == DimensionKind.Aligned)
            {
                var d = (P2 - P1).Normalized();
                return d == Vector2D.Zero ? new Vector2D(1, 0) : d;
            }
            return new Vector2D(Math.Cos(Angle), Math.Sin(Angle));
        }
    }

    private (double Start, double Sweep) Sector
    {
        get
        {
            double a1 = (P1 - Center).Angle, a2 = (P2 - Center).Angle;
            if (Kind == DimensionKind.ArcLength) return (a1, ArcMath.Sweep(a1, a2));
            double toLine = ArcMath.Sweep(a1, (LinePoint - Center).Angle);
            double sweep = ArcMath.Sweep(a1, a2);
            return toLine <= sweep ? (a1, sweep) : (a2, ArcMath.Sweep(a2, a1));
        }
    }

    /// <summary>The measured value: a length in drawing units, or an angle in radians for <see cref="DimensionKind.Angle"/>.</summary>
    public double Measurement => Kind switch
    {
        DimensionKind.Linear => Math.Abs((P2 - P1).Dot(Direction)),
        DimensionKind.Aligned => P1.DistanceTo(P2),
        DimensionKind.Radius => P1.DistanceTo(Center),
        DimensionKind.Diameter => 2.0 * P1.DistanceTo(Center),
        DimensionKind.Angle => Sector.Sweep,
        DimensionKind.ArcLength => P1.DistanceTo(Center) * Sector.Sweep,
        _ => 0,
    };

    /// <summary>The measured value as text, in the drawing's units.</summary>
    public string MeasuredText(DrawingSettings settings)
    {
        double m = Measurement;
        return Kind switch
        {
            DimensionKind.Angle => GeoMath.RadiansToDegrees(m).ToString("0.##", CultureInfo.InvariantCulture) + "°",
            DimensionKind.Radius => "R " + UnitFormatter.FormatLength(m, settings),
            DimensionKind.Diameter => "Ø " + UnitFormatter.FormatLength(m, settings),
            DimensionKind.ArcLength => "⌒ " + UnitFormatter.FormatLength(m, settings),
            _ => UnitFormatter.FormatLength(m, settings),
        };
    }

    /// <summary>The text the dimension shows.</summary>
    public string GetText(DrawingSettings settings) =>
        string.IsNullOrWhiteSpace(TextOverride)
            ? MeasuredText(settings)
            : TextOverride.Replace(MeasuredMark, MeasuredText(settings), StringComparison.Ordinal);

    // ------------------------------------------------------------ geometry

    private static double Readable(double angle)
    {
        double a = Math.IEEERemainder(angle, Math.PI);          // (-pi/2, pi/2]
        if (a <= -Math.PI / 2 + 1e-9) a += Math.PI;
        return a;
    }

    public DimensionGeometry BuildGeometry()
    {
        var g = new DimensionGeometry();
        double h = TextHeight, gap = 0.35 * h, beyond = 0.5 * h, arrow = h;

        switch (Kind)
        {
            case DimensionKind.Linear:
            case DimensionKind.Aligned:
            {
                var dir = Direction;
                var d1 = LinePoint + dir * (P1 - LinePoint).Dot(dir);
                var d2 = LinePoint + dir * (P2 - LinePoint).Dot(dir);
                AddExtension(g, P1, d1, gap, beyond);
                AddExtension(g, P2, d2, gap, beyond);

                var along = d2 - d1;
                double length = along.Length;
                var u = length > GeoMath.Epsilon ? along / length : dir;
                if (length >= 3.0 * arrow)
                {
                    g.Lines.Add((d1, d2));
                    g.Arrows.Add((d1, -u));
                    g.Arrows.Add((d2, u));
                }
                else
                {
                    // Too short for arrows inside: they sit outside, pointing in.
                    g.Lines.Add((d1 - u * (2.0 * arrow), d2 + u * (2.0 * arrow)));
                    g.Arrows.Add((d1, u));
                    g.Arrows.Add((d2, -u));
                }
                PlaceText(g, Point2D.Midpoint(d1, d2), u.Angle, gap, h);
                break;
            }

            case DimensionKind.Radius:
            case DimensionKind.Diameter:
            {
                double radius = P1.DistanceTo(Center);
                var u = radius > GeoMath.Epsilon ? (P1 - Center) / radius : new Vector2D(1, 0);
                double reach = (LinePoint - Center).Length;
                bool outside = reach > radius + arrow;
                var far = Center + u * reach;
                var from = Kind == DimensionKind.Diameter ? Center - u * radius : Center;

                if (outside)
                {
                    g.Lines.Add((from, far));
                    g.Arrows.Add((P1, -u));                                  // from outside, pointing at the circle
                    PlaceText(g, Point2D.Midpoint(P1, far), u.Angle, gap, h);
                }
                else
                {
                    g.Lines.Add((from, P1));
                    g.Arrows.Add((P1, u));
                    PlaceText(g, Kind == DimensionKind.Diameter ? Center : Point2D.Midpoint(Center, P1), u.Angle, gap, h);
                }
                if (Kind == DimensionKind.Diameter) g.Arrows.Add((from, -u));

                // Centre mark.
                double mark = 0.4 * h;
                var v = new Vector2D(-u.Y, u.X);
                if (Kind == DimensionKind.Radius) g.Lines.Add((Center - u * mark, Center));
                g.Lines.Add((Center - v * mark, Center + v * mark));
                break;
            }

            case DimensionKind.Angle:
            case DimensionKind.ArcLength:
            {
                var (start, sweep) = Sector;
                double r = Math.Max((LinePoint - Center).Length, GeoMath.Epsilon * 10);
                g.Arcs.Add((Center, r, start, sweep));

                double end = start + sweep;
                var us = new Vector2D(Math.Cos(start), Math.Sin(start));
                var ue = new Vector2D(Math.Cos(end), Math.Sin(end));
                g.Arrows.Add((Center + us * r, new Vector2D(us.Y, -us.X)));        // pointing clockwise, at the start
                g.Arrows.Add((Center + ue * r, new Vector2D(-ue.Y, ue.X)));        // pointing counter-clockwise, at the end

                foreach (var (ray, point) in new[] { (us, Kind == DimensionKind.ArcLength ? P1 : RayPoint(us)), (ue, Kind == DimensionKind.ArcLength ? P2 : RayPoint(ue)) })
                {
                    double at = (point - Center).Length;
                    if (r > at + gap) g.Lines.Add((Center + ray * (at + gap), Center + ray * (r + beyond)));
                    else if (r < at - gap && Kind == DimensionKind.ArcLength) g.Lines.Add((Center + ray * (at - gap), Center + ray * Math.Max(r - beyond, 0)));
                }

                double mid = start + sweep / 2.0;
                var um = new Vector2D(Math.Cos(mid), Math.Sin(mid));
                g.TextCenter = Center + um * (r + gap + h / 2.0);
                g.TextAngle = Readable(mid - Math.PI / 2.0);
                break;
            }
        }
        return g;

        Point2D RayPoint(Vector2D ray)
        {
            // The measured point that lies on this ray (P1 or P2, whichever is closer in direction).
            var a = P1 - Center;
            var b = P2 - Center;
            return a.Normalized().Dot(ray) >= b.Normalized().Dot(ray) ? P1 : P2;
        }
    }

    private static void AddExtension(DimensionGeometry g, Point2D from, Point2D to, double gap, double beyond)
    {
        var v = to - from;
        double length = v.Length;
        if (length <= gap) return;
        var u = v / length;
        g.Lines.Add((from + u * gap, to + u * beyond));
    }

    private static void PlaceText(DimensionGeometry g, Point2D on, double lineAngle, double gap, double height)
    {
        double a = Readable(lineAngle);
        var up = new Vector2D(-Math.Sin(a), Math.Cos(a));
        g.TextAngle = a;
        g.TextCenter = on + up * (gap + height / 2.0);
    }

    /// <summary>The line the text sits on, used for picking and for the size of the dimension (the real width depends on the font).</summary>
    private (Point2D A, Point2D B) TextAxis(DimensionGeometry g)
    {
        var along = new Vector2D(Math.Cos(g.TextAngle), Math.Sin(g.TextAngle)) * (2.5 * TextHeight);
        return (g.TextCenter - along, g.TextCenter + along);
    }

    // -------------------------------------------------------------- entity

    public override string TypeName => Type;

    public override bool IsValid
    {
        get
        {
            if (!P1.IsFinite || !LinePoint.IsFinite || !GeoMath.IsFinite(TextHeight) || TextHeight <= GeoMath.Epsilon) return false;
            switch (Kind)
            {
                case DimensionKind.Linear:
                    return P2.IsFinite && GeoMath.IsFinite(Angle) && !P1.NearlyEquals(P2);
                case DimensionKind.Aligned:
                    return P2.IsFinite && !P1.NearlyEquals(P2);
                case DimensionKind.Radius:
                case DimensionKind.Diameter:
                    return Center.IsFinite && !P1.NearlyEquals(Center);
                default:
                    return Center.IsFinite && P2.IsFinite && !P1.NearlyEquals(Center) && !P2.NearlyEquals(Center) &&
                           !LinePoint.NearlyEquals(Center) && Sector.Sweep > 1e-9;
            }
        }
    }

    public override BoundingBox Bounds
    {
        get
        {
            var g = BuildGeometry();
            var box = BoundingBox.Empty;
            foreach (var (a, b) in g.Lines) box = box.Include(a).Include(b);
            foreach (var arc in g.Arcs) box = box.Union(ArcMath.ArcBounds(arc.Center, arc.Radius, arc.StartAngle, arc.Sweep));
            foreach (var (tip, _) in g.Arrows) box = box.Include(tip);
            var (t1, t2) = TextAxis(g);
            return box.Include(t1).Include(t2).Inflate(TextHeight * 0.5);
        }
    }

    public override double DistanceTo(Point2D point)
    {
        var g = BuildGeometry();
        double best = double.PositiveInfinity;
        foreach (var (a, b) in g.Lines) best = Math.Min(best, SegmentMath.DistanceToPoint(a, b, point));
        foreach (var arc in g.Arcs) best = Math.Min(best, ArcMath.DistanceToArc(arc.Center, arc.Radius, arc.StartAngle, arc.Sweep, point));
        var (t1, t2) = TextAxis(g);
        return Math.Min(best, SegmentMath.DistanceToPoint(t1, t2, point));
    }

    public override bool IntersectsBox(BoundingBox box)
    {
        var g = BuildGeometry();
        foreach (var (a, b) in g.Lines) if (SegmentMath.IntersectsBox(a, b, box)) return true;
        foreach (var arc in g.Arcs) if (ArcMath.ArcIntersectsBox(arc.Center, arc.Radius, arc.StartAngle, arc.Sweep, box)) return true;
        var (t1, t2) = TextAxis(g);
        return SegmentMath.IntersectsBox(t1, t2, box);
    }

    public override void Transform(in Transform2D transform)
    {
        if (Kind == DimensionKind.Linear)
        {
            var moved = transform.Apply(P1 + Direction) - transform.Apply(P1);
            if (moved.Length > GeoMath.Epsilon) Angle = moved.Angle;
        }
        P1 = transform.Apply(P1);
        P2 = transform.Apply(P2);
        Center = transform.Apply(Center);
        LinePoint = transform.Apply(LinePoint);
        TextHeight *= transform.ScaleFactor;
        // An arc runs counter-clockwise from P1 to P2; a mirror reverses that.
        if (Kind == DimensionKind.ArcLength && transform.Determinant < 0) (P1, P2) = (P2, P1);
    }

    protected override Entity CloneGeometry(Guid id) => new DimensionEntity(id, Kind)
    {
        P1 = P1,
        P2 = P2,
        Center = Center,
        LinePoint = LinePoint,
        Angle = Angle,
        TextHeight = TextHeight,
        TextOverride = TextOverride,
    };
}
