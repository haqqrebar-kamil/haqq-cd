using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>One straight or curved piece of an entity: a segment from A to B, or an arc (a circle is an arc with a full sweep).</summary>
public readonly record struct Curve(bool IsArc, Point2D A, Point2D B, Point2D Center, double Radius, double Start, double Sweep)
{
    public const double TwoPi = 2.0 * Math.PI;

    public static Curve Segment(Point2D a, Point2D b) => new(false, a, b, default, 0, 0, 0);
    public static Curve Arc(Point2D center, double radius, double start, double sweep) => new(true, default, default, center, radius, start, sweep);

    public bool IsFullCircle => IsArc && Sweep >= TwoPi - 1e-9;

    public bool OnArc(Point2D p) => ArcMath.ContainsAngle(Start, Sweep, (p - Center).Angle, 1e-7);

    /// <summary>The pieces of an entity, in drawing order.</summary>
    public static IEnumerable<Curve> PiecesOf(Entity entity)
    {
        switch (entity)
        {
            case LineEntity line:
                yield return Segment(line.Start, line.End);
                break;
            case CircleEntity circle:
                yield return Arc(circle.Center, circle.Radius, 0, TwoPi);
                break;
            case ArcEntity arc:
                yield return Arc(arc.Center, arc.Radius, arc.StartAngle, arc.Sweep);
                break;
            case PolylineEntity polyline:
                for (int i = 0; i < polyline.SegmentCount; i++)
                {
                    var (a, b) = polyline.GetSegment(i);
                    yield return polyline.TryGetArc(i, out var c, out double r, out double start, out double sweep)
                        ? Arc(c, r, start, sweep)
                        : Segment(a, b);
                }
                break;
        }
    }

    /// <summary>Points of this (bounded) piece that lie on the infinite line through <paramref name="p"/> along <paramref name="direction"/>.</summary>
    public IEnumerable<Point2D> MeetLine(Point2D p, Vector2D direction)
    {
        double length = direction.Length;
        if (length <= GeoMath.Epsilon) yield break;
        var u = direction / length;

        if (!IsArc)
        {
            var d = B - A;
            double denom = u.Cross(d);
            if (Math.Abs(denom) <= 1e-12 * Math.Max(1.0, d.Length)) yield break;      // parallel
            double s = (A - p).Cross(u) / denom;                                       // position along this segment, 0..1
            if (s < -1e-9 || s > 1 + 1e-9) yield break;
            yield return A + d * Math.Clamp(s, 0, 1);
            yield break;
        }

        // Line and circle: foot of the perpendicular from the centre, then half the chord either way.
        double along = (Center - p).Dot(u);
        var foot = p + u * along;
        double off = foot.DistanceTo(Center);
        if (off > Radius + 1e-9) yield break;
        double half = Math.Sqrt(Math.Max(0.0, Radius * Radius - off * off));
        var first = foot + u * half;
        if (OnArc(first)) yield return first;
        if (half > 1e-9)
        {
            var second = foot - u * half;
            if (OnArc(second)) yield return second;
        }
    }

    /// <summary>Points of this (bounded) piece that lie on the full circle with the given centre and radius.</summary>
    public IEnumerable<Point2D> MeetCircle(Point2D center, double radius)
    {
        if (!IsArc)
        {
            int n = ArcMath.SegmentCircleIntersections(A, B, center, radius, out var first, out var second);
            if (n >= 1) yield return first;
            if (n >= 2) yield return second;
            yield break;
        }

        var between = Center - center;
        double d = between.Length;
        if (d <= GeoMath.Epsilon || d > radius + Radius + 1e-9 || d < Math.Abs(radius - Radius) - 1e-9) yield break;
        double along = (d * d + radius * radius - Radius * Radius) / (2.0 * d);
        double h = Math.Sqrt(Math.Max(0.0, radius * radius - along * along));
        var unit = between / d;
        var foot = center + unit * along;
        var side = new Vector2D(-unit.Y, unit.X) * h;
        if (OnArc(foot + side)) yield return foot + side;
        if (h > 1e-9 && OnArc(foot - side)) yield return foot - side;
    }
}

/// <summary>OFFSET geometry for polylines.</summary>
public static class PolylineOffset
{
    /// <summary>
    /// A copy of <paramref name="source"/> moved sideways by <paramref name="distance"/>: positive to the left of
    /// the direction the polyline was drawn in, negative to the right. Corners are joined where the offset
    /// segments meet. Returns null when an arc would shrink to nothing or neighbouring segments no longer meet.
    /// </summary>
    public static PolylineEntity? TryOffset(PolylineEntity source, double distance)
    {
        int count = source.SegmentCount;
        if (count == 0 || !double.IsFinite(distance)) return null;

        var pieces = new (bool IsArc, Point2D Start, Point2D End, Point2D Center, double Radius, bool Ccw)[count];
        for (int i = 0; i < count; i++)
        {
            var (a, b) = source.GetSegment(i);
            if (source.TryGetArc(i, out var c, out double r, out _, out _))
            {
                bool ccw = source.GetBulge(i) > 0;
                double radius = ccw ? r - distance : r + distance;     // left of a counter-clockwise arc is its inside
                if (radius <= 1e-9) return null;
                pieces[i] = (true, c + (a - c) * (radius / r), c + (b - c) * (radius / r), c, radius, ccw);
            }
            else
            {
                var along = b - a;
                if (along.Length <= GeoMath.Epsilon) return null;
                var u = along.Normalized();
                var shift = new Vector2D(-u.Y, u.X) * distance;
                pieces[i] = (false, a + shift, b + shift, default, 0, false);
            }
        }

        int vertexCount = source.Vertices.Count;
        var vertices = new Point2D[vertexCount];
        bool closed = source.IsClosed && vertexCount > 2;

        for (int v = 0; v < vertexCount; v++)
        {
            int after = v;                                  // segment that starts at this vertex
            int before = v - 1;                             // segment that ends at this vertex
            if (closed) before = (v - 1 + count) % count;

            bool hasBefore = before >= 0 && before < count;
            bool hasAfter = after < count;
            if (hasBefore && hasAfter)
            {
                if (Join(pieces[before], pieces[after]) is not { } corner) return null;
                vertices[v] = corner;
            }
            else if (hasAfter) vertices[v] = pieces[after].Start;
            else if (hasBefore) vertices[v] = pieces[before].End;
            else return null;
        }

        var bulges = new double[vertexCount];
        for (int i = 0; i < count; i++)
        {
            if (!pieces[i].IsArc) continue;
            Point2D s = vertices[i], e = vertices[(i + 1) % vertexCount], c = pieces[i].Center;
            double a0 = (s - c).Angle, a1 = (e - c).Angle;
            double sweep = pieces[i].Ccw ? ArcMath.Sweep(a0, a1) : ArcMath.Sweep(a1, a0);
            if (sweep <= 1e-9 || sweep >= Curve.TwoPi - 1e-9) return null;
            bulges[i] = Math.Tan(sweep / 4.0) * (pieces[i].Ccw ? 1.0 : -1.0);
        }

        var result = new PolylineEntity(vertices, source.IsClosed, bulges)
        {
            LayerId = source.LayerId,
            Color = source.Color,
            Linetype = source.Linetype,
            LineweightMm = source.LineweightMm,
        };
        for (int i = 0; i < count; i++)
        {
            var (ws, we) = source.GetSegmentWidth(i);
            if (ws != 0 || we != 0) result.SetSegmentWidth(i, ws, we);
        }
        return result.IsValid ? result : null;
    }

    private static Point2D? Join(
        (bool IsArc, Point2D Start, Point2D End, Point2D Center, double Radius, bool Ccw) first,
        (bool IsArc, Point2D Start, Point2D End, Point2D Center, double Radius, bool Ccw) second)
    {
        Point2D a = first.End, b = second.Start;
        double scale = Math.Max(1.0, Math.Max(Math.Abs(a.X) + Math.Abs(a.Y), Math.Abs(b.X) + Math.Abs(b.Y)));
        if (a.DistanceTo(b) <= 1e-9 * scale) return a;                 // tangent or straight-through joint

        // Where the two offset pieces, extended, cross; the crossing nearest the old joint is the corner.
        IEnumerable<Point2D> candidates;
        if (!first.IsArc && !second.IsArc)
        {
            var d1 = first.End - first.Start;
            var d2 = second.End - second.Start;
            double denom = d1.Cross(d2);
            if (Math.Abs(denom) <= 1e-12 * d1.Length * d2.Length) return null;   // parallel but apart
            double t = (second.Start - first.Start).Cross(d2) / denom;
            return first.Start + d1 * t;
        }
        if (first.IsArc && second.IsArc)
            candidates = Curve.Arc(second.Center, second.Radius, 0, Curve.TwoPi).MeetCircle(first.Center, first.Radius);
        else
        {
            var arc = first.IsArc ? first : second;
            var line = first.IsArc ? second : first;
            candidates = Curve.Arc(arc.Center, arc.Radius, 0, Curve.TwoPi).MeetLine(line.Start, line.End - line.Start);
        }

        Point2D? best = null;
        double bestDistance = double.PositiveInfinity;
        foreach (var p in candidates)
        {
            double d = p.DistanceTo(a) + p.DistanceTo(b);
            if (d < bestDistance) { bestDistance = d; best = p; }
        }
        return best;
    }
}
