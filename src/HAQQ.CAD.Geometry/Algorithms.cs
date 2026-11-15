namespace HAQQ.CAD.Geometry;

/// <summary>Algorithms on straight segments.</summary>
public static class SegmentMath
{
    /// <summary>Closest point to <paramref name="p"/> on the segment a-b.</summary>
    public static Point2D ClosestPoint(Point2D a, Point2D b, Point2D p)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 <= 0) return a;
        double t = (p - a).Dot(ab) / len2;
        t = Math.Clamp(t, 0.0, 1.0);
        return a + ab * t;
    }

    public static double DistanceToPoint(Point2D a, Point2D b, Point2D p) => ClosestPoint(a, b, p).DistanceTo(p);

    private static int Orientation(Point2D a, Point2D b, Point2D c)
    {
        double cross = (b - a).Cross(c - a);
        // Scale the tolerance with the magnitude of the operands to stay robust for large coordinates.
        double scale = Math.Max(1.0, Math.Max((b - a).Length, (c - a).Length));
        if (Math.Abs(cross) <= GeoMath.Epsilon * scale * scale) return 0;
        return cross > 0 ? 1 : -1;
    }

    private static bool OnSegment(Point2D a, Point2D b, Point2D p) =>
        p.X >= Math.Min(a.X, b.X) - GeoMath.Epsilon && p.X <= Math.Max(a.X, b.X) + GeoMath.Epsilon &&
        p.Y >= Math.Min(a.Y, b.Y) - GeoMath.Epsilon && p.Y <= Math.Max(a.Y, b.Y) + GeoMath.Epsilon;

    /// <summary>True when the closed segments a1-a2 and b1-b2 share at least one point (including collinear overlap).</summary>
    public static bool SegmentsIntersect(Point2D a1, Point2D a2, Point2D b1, Point2D b2)
    {
        int o1 = Orientation(a1, a2, b1), o2 = Orientation(a1, a2, b2);
        int o3 = Orientation(b1, b2, a1), o4 = Orientation(b1, b2, a2);
        if (o1 != o2 && o3 != o4) return true;
        if (o1 == 0 && OnSegment(a1, a2, b1)) return true;
        if (o2 == 0 && OnSegment(a1, a2, b2)) return true;
        if (o3 == 0 && OnSegment(b1, b2, a1)) return true;
        if (o4 == 0 && OnSegment(b1, b2, a2)) return true;
        return false;
    }

    /// <summary>
    /// Computes the single intersection point of two non-parallel segments.
    /// Returns false when the segments are parallel/collinear or do not meet.
    /// </summary>
    public static bool TryIntersect(Point2D a1, Point2D a2, Point2D b1, Point2D b2, out Point2D intersection)
    {
        intersection = default;
        var r = a2 - a1;
        var s = b2 - b1;
        double denom = r.Cross(s);
        if (Math.Abs(denom) <= GeoMath.Epsilon * Math.Max(1.0, r.Length * s.Length)) return false;
        var qp = b1 - a1;
        double t = qp.Cross(s) / denom;
        double u = qp.Cross(r) / denom;
        const double tol = 1e-12;
        if (t < -tol || t > 1 + tol || u < -tol || u > 1 + tol) return false;
        intersection = a1 + r * Math.Clamp(t, 0.0, 1.0);
        return true;
    }

    /// <summary>True when the segment touches or crosses the box (used for crossing selection).</summary>
    public static bool IntersectsBox(Point2D a, Point2D b, BoundingBox box)
    {
        if (box.IsEmpty) return false;
        if (box.Contains(a) || box.Contains(b)) return true;
        if (!box.Intersects(BoundingBox.FromCorners(a, b))) return false;
        var p1 = new Point2D(box.MinX, box.MinY);
        var p2 = new Point2D(box.MaxX, box.MinY);
        var p3 = new Point2D(box.MaxX, box.MaxY);
        var p4 = new Point2D(box.MinX, box.MaxY);
        return SegmentsIntersect(a, b, p1, p2) || SegmentsIntersect(a, b, p2, p3) ||
               SegmentsIntersect(a, b, p3, p4) || SegmentsIntersect(a, b, p4, p1);
    }
}

/// <summary>Algorithms on full circles.</summary>
public static class CircleMath
{
    /// <summary>Distance from <paramref name="p"/> to the circle's circumference (not its interior).</summary>
    public static double DistanceToCircumference(Point2D center, double radius, Point2D p) =>
        Math.Abs(center.DistanceTo(p) - radius);

    /// <summary>True when the circumference touches or crosses the box.</summary>
    public static bool CircumferenceIntersectsBox(Point2D center, double radius, BoundingBox box)
    {
        if (box.IsEmpty) return false;
        // Nearest point of the box to the centre.
        double nx = Math.Clamp(center.X, box.MinX, box.MaxX);
        double ny = Math.Clamp(center.Y, box.MinY, box.MaxY);
        double nearest = center.DistanceTo(new Point2D(nx, ny));
        if (nearest > radius) return false; // box entirely outside the circle
        // Farthest corner of the box from the centre.
        double fx = Math.Max(Math.Abs(center.X - box.MinX), Math.Abs(center.X - box.MaxX));
        double fy = Math.Max(Math.Abs(center.Y - box.MinY), Math.Abs(center.Y - box.MaxY));
        double farthest = Math.Sqrt(fx * fx + fy * fy);
        return farthest >= radius; // otherwise the box is entirely inside the circle
    }
}

/// <summary>Algorithms on point chains (polylines without bulges).</summary>
public static class PolylineMath
{
    public static double Length(IReadOnlyList<Point2D> vertices, bool closed)
    {
        double total = 0;
        for (int i = 1; i < vertices.Count; i++) total += vertices[i - 1].DistanceTo(vertices[i]);
        if (closed && vertices.Count > 2) total += vertices[^1].DistanceTo(vertices[0]);
        return total;
    }

    /// <summary>Signed area by the shoelace formula (positive for counter-clockwise loops).</summary>
    public static double SignedArea(IReadOnlyList<Point2D> vertices)
    {
        if (vertices.Count < 3) return 0;
        double sum = 0;
        for (int i = 0; i < vertices.Count; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2.0;
    }
}

/// <summary>Algorithms on circular arcs. Angles are radians, counter-clockwise from +X.</summary>
public static class ArcMath
{
    public const double TwoPi = 2.0 * Math.PI;

    /// <summary>Maps an angle into [0, 2*pi).</summary>
    public static double NormalizeAngle(double angle)
    {
        angle %= TwoPi;
        if (angle < 0) angle += TwoPi;
        return angle >= TwoPi ? 0 : angle;
    }

    /// <summary>Counter-clockwise angle travelled from <paramref name="start"/> to <paramref name="end"/>, in [0, 2*pi).</summary>
    public static double Sweep(double start, double end) => NormalizeAngle(end - start);

    public static Point2D PointAt(Point2D center, double radius, double angle) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

    /// <summary>True when <paramref name="angle"/> lies on the arc that starts at <paramref name="start"/> and sweeps counter-clockwise.</summary>
    public static bool ContainsAngle(double start, double sweep, double angle, double tolerance = 1e-9)
    {
        double d = NormalizeAngle(angle - start);
        return d <= sweep + tolerance || d >= TwoPi - tolerance;
    }

    public static double DistanceToArc(Point2D center, double radius, double start, double sweep, Point2D p)
    {
        var v = p - center;
        if (v.LengthSquared > 0 && ContainsAngle(start, sweep, v.Angle)) return Math.Abs(v.Length - radius);
        return Math.Min(PointAt(center, radius, start).DistanceTo(p), PointAt(center, radius, start + sweep).DistanceTo(p));
    }

    public static BoundingBox ArcBounds(Point2D center, double radius, double start, double sweep)
    {
        var box = BoundingBox.FromCorners(PointAt(center, radius, start), PointAt(center, radius, start + sweep));
        for (int quadrant = 0; quadrant < 4; quadrant++)
        {
            double a = quadrant * Math.PI / 2.0;
            if (ContainsAngle(start, sweep, a)) box = box.Include(PointAt(center, radius, a));
        }
        return box;
    }

    /// <summary>Intersections of the segment a-b with a full circle. Returns the number found (0, 1 or 2).</summary>
    public static int SegmentCircleIntersections(Point2D a, Point2D b, Point2D center, double radius, out Point2D first, out Point2D second)
    {
        first = second = default;
        var d = b - a;
        var f = a - center;
        double qa = d.LengthSquared;
        if (qa <= 0) return 0;
        double qb = 2.0 * f.Dot(d);
        double qc = f.LengthSquared - radius * radius;
        double disc = qb * qb - 4.0 * qa * qc;
        if (disc < 0) return 0;
        double root = Math.Sqrt(disc);
        const double tol = 1e-12;
        int count = 0;
        double t1 = (-qb - root) / (2.0 * qa), t2 = (-qb + root) / (2.0 * qa);
        if (t1 >= -tol && t1 <= 1 + tol) { first = a + d * Math.Clamp(t1, 0, 1); count++; }
        if (root > 0 && t2 >= -tol && t2 <= 1 + tol)
        {
            var p = a + d * Math.Clamp(t2, 0, 1);
            if (count == 0) first = p; else second = p;
            count++;
        }
        return count;
    }

    /// <summary>True when the arc touches or crosses the box (used for crossing selection).</summary>
    public static bool ArcIntersectsBox(Point2D center, double radius, double start, double sweep, BoundingBox box)
    {
        if (box.IsEmpty) return false;
        if (box.Contains(PointAt(center, radius, start)) || box.Contains(PointAt(center, radius, start + sweep))) return true;
        var c = new[]
        {
            new Point2D(box.MinX, box.MinY), new Point2D(box.MaxX, box.MinY),
            new Point2D(box.MaxX, box.MaxY), new Point2D(box.MinX, box.MaxY),
        };
        for (int i = 0; i < 4; i++)
        {
            int n = SegmentCircleIntersections(c[i], c[(i + 1) % 4], center, radius, out var p1, out var p2);
            if (n >= 1 && ContainsAngle(start, sweep, (p1 - center).Angle)) return true;
            if (n == 2 && ContainsAngle(start, sweep, (p2 - center).Angle)) return true;
        }
        return false;
    }

    /// <summary>Centre of the circle through three points. False when the points are collinear.</summary>
    public static bool TryCircumcenter(Point2D p1, Point2D p2, Point2D p3, out Point2D center)
    {
        center = default;
        double ax = p2.X - p1.X, ay = p2.Y - p1.Y, bx = p3.X - p1.X, by = p3.Y - p1.Y;
        double d = 2.0 * (ax * by - ay * bx);
        double scale = Math.Max(1.0, Math.Max(ax * ax + ay * ay, bx * bx + by * by));
        if (Math.Abs(d) <= 1e-12 * scale) return false;
        double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by;
        center = new Point2D(p1.X + (by * a2 - ay * b2) / d, p1.Y + (ax * b2 - bx * a2) / d);
        return center.IsFinite;
    }
}

/// <summary>
/// Arc segments of polylines. A segment from A to B with bulge b is an arc whose included angle is
/// 4*atan(b): positive bulges curve counter-clockwise, negative clockwise, 0 is a straight segment
/// and 1 is a semicircle.
/// </summary>
public static class BulgeMath
{
    public const double StraightTolerance = 1e-12;

    public static bool IsArc(double bulge) => Math.Abs(bulge) > StraightTolerance;

    /// <summary>
    /// Centre, radius and counter-clockwise span (start angle and sweep) of a bulged segment.
    /// Returns false for a straight or degenerate segment.
    /// </summary>
    public static bool TryGetArc(Point2D a, Point2D b, double bulge, out Point2D center, out double radius, out double start, out double sweep)
    {
        center = default;
        radius = start = sweep = 0;
        double chord = a.DistanceTo(b);
        if (!IsArc(bulge) || chord <= 0 || !double.IsFinite(bulge)) return false;

        double offset = chord / 2.0 * (1.0 - bulge * bulge) / (2.0 * bulge); // signed, to the left of A->B
        var mid = Point2D.Midpoint(a, b);
        var left = new Vector2D(-(b.Y - a.Y) / chord, (b.X - a.X) / chord);
        center = mid + left * offset;
        radius = center.DistanceTo(a);
        sweep = 4.0 * Math.Atan(Math.Abs(bulge));
        // Stored counter-clockwise: a clockwise segment runs from B back to A.
        start = ArcMath.NormalizeAngle(bulge > 0 ? (a - center).Angle : (b - center).Angle);
        return radius > 0 && double.IsFinite(radius);
    }

    /// <summary>Direction of travel (radians) when arriving at B along the segment.</summary>
    public static double EndTangentAngle(Point2D a, Point2D b, double bulge) => (b - a).Angle + 2.0 * Math.Atan(bulge);

    /// <summary>
    /// Bulge of the arc that leaves <paramref name="from"/> in direction <paramref name="tangentAngle"/>
    /// and ends at <paramref name="to"/>. Null when the end point lies straight behind the start direction.
    /// </summary>
    public static double? TangentArcBulge(Point2D from, double tangentAngle, Point2D to)
    {
        var chord = to - from;
        if (chord.LengthSquared <= 0) return null;
        double phi = chord.Angle - tangentAngle;                 // angle from the tangent to the chord
        phi = Math.IEEERemainder(phi, 2.0 * Math.PI);            // (-pi, pi]
        if (Math.Abs(Math.Abs(phi) - Math.PI) < 1e-9) return null;
        return Math.Tan(phi / 2.0);                               // included angle is 2*phi
    }

    public static double Length(Point2D a, Point2D b, double bulge) =>
        TryGetArc(a, b, bulge, out _, out double r, out _, out double sweep) ? r * sweep : a.DistanceTo(b);

    /// <summary>Signed area between the arc and its chord (positive for counter-clockwise bulges).</summary>
    public static double SegmentArea(Point2D a, Point2D b, double bulge)
    {
        if (!TryGetArc(a, b, bulge, out _, out double r, out _, out double sweep)) return 0;
        double area = 0.5 * r * r * (sweep - Math.Sin(sweep));
        return bulge > 0 ? area : -area;
    }
}
