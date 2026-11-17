using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Snapping;

[Flags]
public enum SnapModes
{
    None = 0,
    Endpoint = 1,
    Midpoint = 2,
    Center = 4,
    Intersection = 8,
    Quadrant = 16,
    Perpendicular = 32,
    Tangent = 64,
    Nearest = 128,
}

/// <summary>The circle or arc a Tangent snap was taken on.</summary>
public readonly record struct TangentTarget(Point2D Center, double Radius, double Start, double Sweep)
{
    /// <summary>True when <paramref name="point"/> (on the circle) lies on the part that exists.</summary>
    public bool Contains(Point2D point) => ArcMath.ContainsAngle(Start, Sweep, (point - Center).Angle, 1e-7);
}

/// <summary>A resolved object snap.</summary>
/// <param name="Tangent">For a Tangent snap: the circle or arc it was taken on.</param>
/// <param name="Deferred">
/// True for a Tangent snap taken before the line has another point to be tangent from (the first point of a line).
/// <paramref name="Point"/> is then only where the circle was clicked; the real tangent point is worked out by the
/// command once the other end is known.
/// </param>
public readonly record struct SnapResult(Point2D Point, SnapModes Mode, Guid EntityId, TangentTarget? Tangent = null, bool Deferred = false);

/// <summary>Tangent lines to circles.</summary>
public static class TangentMath
{
    /// <summary>The (up to two) points where lines from <paramref name="from"/> touch the circle; none from inside it.</summary>
    public static IEnumerable<Point2D> FromPoint(Point2D center, double radius, Point2D from)
    {
        var v = from - center;
        double d = v.Length;
        if (!(radius > 0) || d <= radius * (1 + 1e-9)) yield break;
        double spread = Math.Acos(radius / d);
        yield return ArcMath.PointAt(center, radius, v.Angle + spread);
        yield return ArcMath.PointAt(center, radius, v.Angle - spread);
    }

    /// <summary>
    /// The (up to four) lines tangent to both circles, each as its touching point on the first and on the second
    /// circle: two outer tangents, and two crossing ones when the circles are apart.
    /// </summary>
    public static IEnumerable<(Point2D OnFirst, Point2D OnSecond)> Common(Point2D c1, double r1, Point2D c2, double r2)
    {
        var d = c2 - c1;
        double distance = d.Length;
        if (distance <= GeoMath.Epsilon || !(r1 > 0) || !(r2 > 0)) yield break;
        var u = d / distance;
        var side = new Vector2D(-u.Y, u.X);

        // A tangent line has a unit normal n with signed distances r1 and +-r2 from the centres (same side = outer).
        foreach (double sign in new[] { 1.0, -1.0 })
        {
            double q = (r2 * sign - r1) / distance;
            if (Math.Abs(q) > 1 + 1e-12) continue;                   // one circle inside the other / overlapping too much
            double h = Math.Sqrt(Math.Max(0.0, 1 - q * q));
            foreach (double turn in h > 1e-12 ? new[] { 1.0, -1.0 } : new[] { 1.0 })
            {
                var n = u * q + side * (h * turn);
                yield return (c1 - n * r1, c2 - n * (r2 * sign));
            }
        }
    }
}

public sealed class SnapSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>The snaps that act automatically while OSNAP is on ("running" snaps).</summary>
    public SnapModes Modes { get; set; } = DefaultModes;

    public const SnapModes DefaultModes = SnapModes.Endpoint | SnapModes.Midpoint | SnapModes.Center | SnapModes.Intersection;

    public const SnapModes AllModes = SnapModes.Endpoint | SnapModes.Midpoint | SnapModes.Center | SnapModes.Intersection |
                                      SnapModes.Quadrant | SnapModes.Perpendicular | SnapModes.Tangent | SnapModes.Nearest;

    /// <summary>The single modes in menu order.</summary>
    public static readonly IReadOnlyList<SnapModes> MenuOrder = new[]
    {
        SnapModes.Endpoint, SnapModes.Midpoint, SnapModes.Intersection, SnapModes.Center,
        SnapModes.Quadrant, SnapModes.Perpendicular, SnapModes.Tangent, SnapModes.Nearest,
    };
}

/// <summary>
/// Object snapping on real model geometry. Candidates come from the document's spatial index,
/// so cost depends on what is near the cursor, not on drawing size.
/// </summary>
public static class SnapEngine
{
    /// <summary>Most curve pieces near the cursor that are examined for intersections and object-type snaps.</summary>
    private const int MaxPieces = 64;

    /// <summary>One straight or curved piece of an entity: a segment, or an arc (a circle is an arc with a full sweep).</summary>
    private readonly record struct Piece(Guid EntityId, bool IsArc, Point2D A, Point2D B, Point2D Center, double Radius, double Start, double Sweep)
    {
        public static Piece Segment(Guid id, Point2D a, Point2D b) => new(id, false, a, b, default, 0, 0, 0);
        public static Piece Arc(Guid id, Point2D center, double radius, double start, double sweep) => new(id, true, default, default, center, radius, start, sweep);

        public Point2D ClosestPoint(Point2D p)
        {
            if (!IsArc) return SegmentMath.ClosestPoint(A, B, p);
            var v = p - Center;
            if (v.LengthSquared > 0 && ArcMath.ContainsAngle(Start, Sweep, v.Angle)) return Center + v.Normalized() * Radius;
            Point2D s = ArcMath.PointAt(Center, Radius, Start), e = ArcMath.PointAt(Center, Radius, Start + Sweep);
            return s.DistanceTo(p) <= e.DistanceTo(p) ? s : e;
        }

        public bool OnArc(Point2D p) => ArcMath.ContainsAngle(Start, Sweep, (p - Center).Angle, 1e-7);
    }

    /// <summary>
    /// Finds the best snap point for the cursor. Entities on hidden or frozen layers are ignored; locked layers still snap.
    /// <para>Order of preference: a definite point (endpoint, midpoint, center, quadrant, intersection) within
    /// <paramref name="aperture"/> of the cursor, nearest first; then, when the cursor is on an object, the
    /// perpendicular or tangent point of that object as seen from <paramref name="from"/> (the previous point of the
    /// command); then the nearest point on the object.</para>
    /// </summary>
    /// <param name="deferTangent">
    /// The command can work out a tangent itself once both ends are known (LINE). A Tangent snap then only
    /// records which circle was clicked, and works for a first point too.
    /// </param>
    public static SnapResult? FindSnap(DrawingDocument document, Point2D cursor, double aperture, SnapModes modes, Point2D? from = null, bool deferTangent = false)
    {
        if (modes == SnapModes.None || aperture <= 0) return null;

        SnapResult? best = null;
        double bestDistance = double.PositiveInfinity;
        var probe = BoundingBox.FromCenter(cursor, aperture);
        const SnapModes pieceModes = SnapModes.Intersection | SnapModes.Perpendicular | SnapModes.Tangent | SnapModes.Nearest;
        List<Piece>? pieces = (modes & pieceModes) != 0 ? new List<Piece>() : null;

        void Offer(Point2D point, SnapModes mode, Guid id)
        {
            double d = point.DistanceTo(cursor);
            if (d > aperture) return;
            // Nearest wins; on a tie the more specific mode (lower flag value) wins.
            if (d < bestDistance - GeoMath.Epsilon ||
                (Math.Abs(d - bestDistance) <= GeoMath.Epsilon && best is { } b && mode < b.Mode))
            {
                bestDistance = d;
                best = new SnapResult(point, mode, id);
            }
        }

        foreach (var entity in document.QueryEntities(probe))
        {
            var layer = document.GetLayer(entity.LayerId);
            if (layer is null || !layer.IsDisplayed) continue;

            foreach (var (point, mode) in GetSnapPoints(entity, modes)) Offer(point, mode, entity.Id);

            if (pieces is not null && pieces.Count < MaxPieces)
                foreach (var piece in GetPieces(entity))
                    if (pieces.Count < MaxPieces && piece.ClosestPoint(cursor).DistanceTo(cursor) <= aperture) pieces.Add(piece);
        }

        if (pieces is not null && modes.HasFlag(SnapModes.Intersection))
            for (int i = 0; i < pieces.Count; i++)
                for (int j = i + 1; j < pieces.Count; j++)
                    foreach (var point in Intersections(pieces[i], pieces[j])) Offer(point, SnapModes.Intersection, pieces[i].EntityId);

        // Candidates are found by bounding box, so a circle's centre is acquired by hovering near
        // the centre itself (the probe lies inside the circle's box).
        if (best is not null || pieces is null || pieces.Count == 0) return best;

        // The cursor is on an object but not near one of its definite points.
        if (deferTangent && modes.HasFlag(SnapModes.Tangent))
        {
            foreach (var piece in pieces)
                if (piece.IsArc)
                    OfferObject(piece.ClosestPoint(cursor), SnapModes.Tangent, piece.EntityId, TargetOf(piece), true);
            if (best is not null) return best;
        }

        if (from is { } origin)
        {
            foreach (var piece in pieces)
            {
                if (modes.HasFlag(SnapModes.Perpendicular))
                    foreach (var point in PerpendicularPoints(piece, origin)) OfferObject(point, SnapModes.Perpendicular, piece.EntityId);
                if (modes.HasFlag(SnapModes.Tangent) && !deferTangent)
                    foreach (var point in TangentPoints(piece, origin)) OfferObject(point, SnapModes.Tangent, piece.EntityId, TargetOf(piece));
            }
            if (best is not null) return best;
        }

        if (modes.HasFlag(SnapModes.Nearest))
            foreach (var piece in pieces) OfferObject(piece.ClosestPoint(cursor), SnapModes.Nearest, piece.EntityId);
        return best;

        // Object-type snaps may lie far from the cursor (the foot of a perpendicular); the closest one to the cursor wins.
        void OfferObject(Point2D point, SnapModes mode, Guid id, TangentTarget? tangent = null, bool deferred = false)
        {
            if (!point.IsFinite) return;
            double d = point.DistanceTo(cursor);
            if (d < bestDistance - GeoMath.Epsilon)
            {
                bestDistance = d;
                best = new SnapResult(point, mode, id, tangent, deferred);
            }
        }

        static TangentTarget TargetOf(Piece piece) => new(piece.Center, piece.Radius, piece.Start, piece.Sweep);
    }

    private static IEnumerable<Piece> GetPieces(Entity entity)
    {
        switch (entity)
        {
            case LineEntity line:
                yield return Piece.Segment(line.Id, line.Start, line.End);
                break;
            case CircleEntity circle:
                yield return Piece.Arc(circle.Id, circle.Center, circle.Radius, 0, 2.0 * Math.PI);
                break;
            case ArcEntity arc:
                yield return Piece.Arc(arc.Id, arc.Center, arc.Radius, arc.StartAngle, arc.Sweep);
                break;
            case GridLineEntity grid:
                // Grid lines snap like lines and arcs (ends, middles, and above all their crossings).
                yield return grid.IsArc
                    ? Piece.Arc(grid.Id, grid.Center, grid.Radius, grid.StartAngle, grid.Sweep)
                    : Piece.Segment(grid.Id, grid.Start, grid.End);
                break;
            case PolylineEntity polyline:
                for (int i = 0; i < polyline.SegmentCount; i++)
                {
                    var (a, b) = polyline.GetSegment(i);
                    yield return polyline.TryGetArc(i, out var c, out double r, out double start, out double sweep)
                        ? Piece.Arc(polyline.Id, c, r, start, sweep)
                        : Piece.Segment(polyline.Id, a, b);
                }
                break;
        }
    }

    private static IEnumerable<Point2D> Intersections(Piece p, Piece q)
    {
        if (!p.IsArc && !q.IsArc)
        {
            if (SegmentMath.TryIntersect(p.A, p.B, q.A, q.B, out var x)) yield return x;
            yield break;
        }

        if (p.IsArc != q.IsArc)
        {
            var segment = p.IsArc ? q : p;
            var arc = p.IsArc ? p : q;
            int n = ArcMath.SegmentCircleIntersections(segment.A, segment.B, arc.Center, arc.Radius, out var first, out var second);
            if (n >= 1 && arc.OnArc(first)) yield return first;
            if (n >= 2 && arc.OnArc(second)) yield return second;
            yield break;
        }

        // Two circles: the intersection points lie on the line perpendicular to the line of centres.
        var between = q.Center - p.Center;
        double d = between.Length;
        if (d <= GeoMath.Epsilon || d > p.Radius + q.Radius || d < Math.Abs(p.Radius - q.Radius)) yield break;
        double along = (d * d + p.Radius * p.Radius - q.Radius * q.Radius) / (2.0 * d);
        double h = Math.Sqrt(Math.Max(0.0, p.Radius * p.Radius - along * along));
        var unit = between / d;
        var foot = p.Center + unit * along;
        var side = new Vector2D(-unit.Y, unit.X) * h;
        var one = foot + side;
        if (p.OnArc(one) && q.OnArc(one)) yield return one;
        if (h > GeoMath.Epsilon)
        {
            var two = foot - side;
            if (p.OnArc(two) && q.OnArc(two)) yield return two;
        }
    }

    /// <summary>Points on the piece where a line from <paramref name="from"/> meets it at a right angle.</summary>
    private static IEnumerable<Point2D> PerpendicularPoints(Piece piece, Point2D from)
    {
        if (!piece.IsArc)
        {
            var d = piece.B - piece.A;
            double length2 = d.LengthSquared;
            if (length2 <= 0) yield break;
            double t = (from - piece.A).Dot(d) / length2;
            if (t < -1e-9 || t > 1 + 1e-9) yield break;              // the foot would be off the end of the segment
            var foot = piece.A + d * Math.Clamp(t, 0, 1);
            if (!foot.NearlyEquals(from)) yield return foot;
            yield break;
        }

        var v = from - piece.Center;
        if (v.Length <= GeoMath.Epsilon) yield break;
        var u = v.Normalized() * piece.Radius;
        foreach (var point in new[] { piece.Center + u, piece.Center - u })
            if (piece.OnArc(point) && !point.NearlyEquals(from)) yield return point;
    }

    /// <summary>Points on a circle or arc where a line from <paramref name="from"/> just touches it.</summary>
    private static IEnumerable<Point2D> TangentPoints(Piece piece, Point2D from)
    {
        if (!piece.IsArc) yield break;
        var v = from - piece.Center;
        double d = v.Length;
        if (d <= piece.Radius * (1 + 1e-9)) yield break;             // inside or on the circle: no tangent
        double spread = Math.Acos(piece.Radius / d);
        foreach (double angle in new[] { v.Angle + spread, v.Angle - spread })
        {
            var point = ArcMath.PointAt(piece.Center, piece.Radius, angle);
            if (piece.OnArc(point)) yield return point;
        }
    }

    /// <summary>Display name of a single snap mode.</summary>
    public static string NameOf(SnapModes mode) => mode switch
    {
        SnapModes.Endpoint => "Endpoint",
        SnapModes.Midpoint => "Midpoint",
        SnapModes.Center => "Center",
        SnapModes.Intersection => "Intersection",
        SnapModes.Quadrant => "Quadrant",
        SnapModes.Perpendicular => "Perpendicular",
        SnapModes.Tangent => "Tangent",
        SnapModes.Nearest => "Nearest",
        _ => mode.ToString(),
    };

    private static IEnumerable<(Point2D, SnapModes)> Quadrants(Point2D center, double radius, double start, double sweep)
    {
        for (int k = 0; k < 4; k++)
        {
            double angle = k * Math.PI / 2.0;
            if (ArcMath.ContainsAngle(start, sweep, angle, 1e-7)) yield return (ArcMath.PointAt(center, radius, angle), SnapModes.Quadrant);
        }
    }

    /// <summary>Enumerates the snap points an entity offers for the requested modes.</summary>
    public static IEnumerable<(Point2D Point, SnapModes Mode)> GetSnapPoints(Entity entity, SnapModes modes)
    {
        switch (entity)
        {
            case LineEntity line:
                if (modes.HasFlag(SnapModes.Endpoint))
                {
                    yield return (line.Start, SnapModes.Endpoint);
                    yield return (line.End, SnapModes.Endpoint);
                }
                if (modes.HasFlag(SnapModes.Midpoint)) yield return (line.Midpoint, SnapModes.Midpoint);
                break;

            case PolylineEntity polyline:
                if (modes.HasFlag(SnapModes.Endpoint))
                    foreach (var v in polyline.Vertices) yield return (v, SnapModes.Endpoint);
                if (modes.HasFlag(SnapModes.Midpoint))
                    for (int i = 0; i < polyline.SegmentCount; i++)
                    {
                        var (a, b) = polyline.GetSegment(i);
                        yield return polyline.TryGetArc(i, out var mc, out double mr, out double ms, out double msweep)
                            ? (ArcMath.PointAt(mc, mr, ms + msweep / 2.0), SnapModes.Midpoint)
                            : (Point2D.Midpoint(a, b), SnapModes.Midpoint);
                    }
                if (modes.HasFlag(SnapModes.Center))
                    for (int i = 0; i < polyline.SegmentCount; i++)
                        if (polyline.TryGetArc(i, out var cc, out _, out _, out _)) yield return (cc, SnapModes.Center);
                if (modes.HasFlag(SnapModes.Quadrant))
                    for (int i = 0; i < polyline.SegmentCount; i++)
                        if (polyline.TryGetArc(i, out var qc, out double qr, out double qs, out double qsweep))
                            foreach (var q in Quadrants(qc, qr, qs, qsweep)) yield return q;
                break;

            case CircleEntity circle:
                if (modes.HasFlag(SnapModes.Center)) yield return (circle.Center, SnapModes.Center);
                if (modes.HasFlag(SnapModes.Quadrant))
                    foreach (var q in Quadrants(circle.Center, circle.Radius, 0, 2.0 * Math.PI)) yield return q;
                break;

            case GridLineEntity grid:
                if (modes.HasFlag(SnapModes.Endpoint))
                {
                    yield return (grid.StartPoint, SnapModes.Endpoint);
                    yield return (grid.EndPoint, SnapModes.Endpoint);
                }
                if (modes.HasFlag(SnapModes.Midpoint)) yield return (grid.MidPoint, SnapModes.Midpoint);
                if (grid.IsArc && modes.HasFlag(SnapModes.Center)) yield return (grid.Center, SnapModes.Center);
                break;

            case ArcEntity arc:
                if (modes.HasFlag(SnapModes.Endpoint))
                {
                    yield return (arc.StartPoint, SnapModes.Endpoint);
                    yield return (arc.EndPoint, SnapModes.Endpoint);
                }
                if (modes.HasFlag(SnapModes.Midpoint)) yield return (arc.MidPoint, SnapModes.Midpoint);
                if (modes.HasFlag(SnapModes.Center)) yield return (arc.Center, SnapModes.Center);
                if (modes.HasFlag(SnapModes.Quadrant))
                    foreach (var q in Quadrants(arc.Center, arc.Radius, arc.StartAngle, arc.Sweep)) yield return q;
                break;
        }
    }
}
