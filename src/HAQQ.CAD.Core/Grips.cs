using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Core;

/// <summary>
/// Grips: the handles shown on a selected entity, and what dragging each one does.
/// <list type="bullet">
/// <item>Line: start, midpoint (moves the line), end.</item>
/// <item>Polyline: one per vertex. Arc segments keep their bulge, so they follow the moved vertex.</item>
/// <item>Arc: start, midpoint, end (the arc is re-fitted through its three grip points), center (moves the arc).</item>
/// <item>Circle: center (moves it) and four quadrant points (change the radius).</item>
/// </list>
/// </summary>
public static class EntityGrips
{
    public static IReadOnlyList<Point2D> GetGripPoints(Entity entity)
    {
        switch (entity)
        {
            case LineEntity line:
                return new[] { line.Start, line.Midpoint, line.End };
            case PolylineEntity polyline:
                return polyline.Vertices;
            case ArcEntity arc:
                return new[] { arc.StartPoint, arc.MidPoint, arc.EndPoint, arc.Center };
            case CircleEntity circle:
                return new[]
                {
                    circle.Center,
                    new Point2D(circle.Center.X + circle.Radius, circle.Center.Y),
                    new Point2D(circle.Center.X, circle.Center.Y + circle.Radius),
                    new Point2D(circle.Center.X - circle.Radius, circle.Center.Y),
                    new Point2D(circle.Center.X, circle.Center.Y - circle.Radius),
                };
            default:
                return Array.Empty<Point2D>();
        }
    }

    /// <summary>
    /// Moves grip <paramref name="gripIndex"/> of <paramref name="entity"/> to <paramref name="target"/>, changing the
    /// entity in place. Returns false, leaving the entity untouched, when the result would not be a valid entity.
    /// </summary>
    public static bool Stretch(Entity entity, int gripIndex, Point2D target)
    {
        if (!target.IsFinite) return false;
        switch (entity)
        {
            case LineEntity line:
                switch (gripIndex)
                {
                    case 0:
                        if (target.NearlyEquals(line.End)) return false;
                        line.Start = target;
                        return true;
                    case 1:
                        line.Transform(Transform2D.Translation(target - line.Midpoint));
                        return true;
                    case 2:
                        if (target.NearlyEquals(line.Start)) return false;
                        line.End = target;
                        return true;
                }
                return false;

            case PolylineEntity polyline:
                if (gripIndex < 0 || gripIndex >= polyline.Vertices.Count) return false;
                var vertices = polyline.Vertices.ToArray();
                vertices[gripIndex] = target;
                polyline.SetVertices(vertices, polyline.Bulges.ToArray());
                return true;

            case CircleEntity circle:
                if (gripIndex == 0) { circle.Center = target; return true; }
                if (gripIndex is < 1 or > 4) return false;
                double radius = circle.Center.DistanceTo(target);
                if (radius <= GeoMath.Epsilon) return false;
                circle.Radius = radius;
                return true;

            case ArcEntity arc:
                switch (gripIndex)
                {
                    case 0: return Refit(arc, target, arc.MidPoint, arc.EndPoint);
                    case 1: return Refit(arc, arc.StartPoint, target, arc.EndPoint);
                    case 2: return Refit(arc, arc.StartPoint, arc.MidPoint, target);
                    case 3: arc.Center = target; return true;
                }
                return false;
        }
        return false;
    }

    /// <summary>Makes <paramref name="arc"/> the arc that runs from <paramref name="a"/> through <paramref name="b"/> to <paramref name="c"/>.</summary>
    private static bool Refit(ArcEntity arc, Point2D a, Point2D b, Point2D c)
    {
        if (!ArcMath.TryCircumcenter(a, b, c, out var center)) return false;
        double radius = center.DistanceTo(a);
        if (!GeoMath.IsFinite(radius) || radius <= GeoMath.Epsilon) return false;
        bool counterClockwise = (b - a).Cross(c - b) > 0;
        double first = (a - center).Angle, last = (c - center).Angle;
        double start = ArcMath.NormalizeAngle(counterClockwise ? first : last);
        double end = ArcMath.NormalizeAngle(counterClockwise ? last : first);
        if (ArcMath.Sweep(start, end) <= 1e-9) return false;
        arc.Center = center;
        arc.Radius = radius;
        arc.StartAngle = start;
        arc.EndAngle = end;
        return true;
    }
}
