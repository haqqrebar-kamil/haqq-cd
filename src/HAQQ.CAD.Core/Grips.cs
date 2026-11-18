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
            case DimensionEntity dimension:
                return DimensionGrips(dimension);
            case TextEntity text:
                return new[] { text.Position };
            case ViewportEntity viewport:
            {
                // The four corners resize the window; the middle moves it.
                var f = viewport.Frame;
                return new[] { new Point2D(f.MinX, f.MinY), new Point2D(f.MaxX, f.MinY), new Point2D(f.MaxX, f.MaxY), new Point2D(f.MinX, f.MaxY), viewport.Center };
            }
            case GridLineEntity grid:
                // Start, middle, end. Stretching an end takes its label circle along.
                return new[] { grid.StartPoint, grid.MidPoint, grid.EndPoint };
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
            case DimensionEntity dimension:
                return StretchDimension(dimension, gripIndex, target);
            case TextEntity text:
                if (gripIndex != 0) return false;
                text.Position = target;
                return true;
            case GridLineEntity grid:
                return StretchGridLine(grid, gripIndex, target);
            case ViewportEntity viewport:
            {
                if (gripIndex == 4) { viewport.Center = target; return true; }
                if (gripIndex is < 0 or > 3) return false;
                // The opposite corner stays where it is; what is seen through the window does not move on the sheet.
                var f = viewport.Frame;
                var opposite = gripIndex switch
                {
                    0 => new Point2D(f.MaxX, f.MaxY),
                    1 => new Point2D(f.MinX, f.MaxY),
                    2 => new Point2D(f.MinX, f.MinY),
                    _ => new Point2D(f.MaxX, f.MinY),
                };
                var frame = BoundingBox.FromCorners(opposite, target);
                if (!(frame.Width > GeoMath.Epsilon) || !(frame.Height > GeoMath.Epsilon)) return false;
                viewport.SetFrame(frame);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Straight: an end goes to the target, the middle moves the whole line. Arc: an end turns to the direction of
    /// the target (the radius stays), the middle changes the radius.
    /// </summary>
    private static bool StretchGridLine(GridLineEntity grid, int gripIndex, Point2D target)
    {
        if (!grid.IsArc)
        {
            switch (gripIndex)
            {
                case 0:
                    if (target.NearlyEquals(grid.End)) return false;
                    grid.Start = target;
                    return true;
                case 1:
                    grid.Transform(Transform2D.Translation(target - grid.MidPoint));
                    return true;
                case 2:
                    if (target.NearlyEquals(grid.Start)) return false;
                    grid.End = target;
                    return true;
            }
            return false;
        }

        var toward = target - grid.Center;
        if (toward.Length <= GeoMath.Epsilon) return false;
        double start = grid.StartAngle, end = grid.EndAngle, radius = grid.Radius;
        if (gripIndex == 0) start = ArcMath.NormalizeAngle(toward.Angle);
        else if (gripIndex == 2) end = ArcMath.NormalizeAngle(toward.Angle);
        else if (gripIndex == 1) radius = toward.Length;
        else return false;
        if (ArcMath.Sweep(start, end) <= 1e-9) return false;
        (grid.StartAngle, grid.EndAngle, grid.Radius) = (start, end, radius);
        return true;
    }

    // Dimension grips.
    //   Linear, Aligned:   0 = first point, 1 = second point, 2 = middle of the dimension line (moves the line)
    //   Radius, Diameter:  0 = far end of the line (turns the dimension round the circle and sets its reach), 1 = centre (moves it)
    //   Angle:             0 = first point, 1 = second point, 2 = middle of the arc (sets its radius and side)
    //   Arc length:        0 = middle of the dimension arc
    private static IReadOnlyList<Point2D> DimensionGrips(DimensionEntity d)
    {
        switch (d.Kind)
        {
            case DimensionKind.Linear:
            case DimensionKind.Aligned:
            {
                var dir = d.Direction;
                var d1 = d.LinePoint + dir * (d.P1 - d.LinePoint).Dot(dir);
                var d2 = d.LinePoint + dir * (d.P2 - d.LinePoint).Dot(dir);
                return new[] { d.P1, d.P2, Point2D.Midpoint(d1, d2) };
            }
            case DimensionKind.Radius:
            case DimensionKind.Diameter:
            {
                var u = (d.P1 - d.Center).Normalized();
                return new[] { d.Center + u * (d.LinePoint - d.Center).Length, d.Center };
            }
            default:
            {
                var g = d.BuildGeometry();
                var arc = g.Arcs[0];
                var mid = ArcMath.PointAt(arc.Center, arc.Radius, arc.StartAngle + arc.Sweep / 2.0);
                return d.Kind == DimensionKind.Angle ? new[] { d.P1, d.P2, mid } : new[] { mid };
            }
        }
    }

    private static bool StretchDimension(DimensionEntity d, int gripIndex, Point2D target)
    {
        var before = (d.P1, d.P2, d.Center, d.LinePoint);
        switch (d.Kind)
        {
            case DimensionKind.Linear:
            case DimensionKind.Aligned:
                if (gripIndex == 0) d.P1 = target;
                else if (gripIndex == 1) d.P2 = target;
                else if (gripIndex == 2) d.LinePoint = target;
                else return false;
                break;
            case DimensionKind.Radius:
            case DimensionKind.Diameter:
                if (gripIndex == 0)
                {
                    var u = (target - d.Center).Normalized();
                    if (u == Vector2D.Zero) return false;
                    d.P1 = d.Center + u * d.P1.DistanceTo(d.Center);
                    d.LinePoint = target;
                }
                else if (gripIndex == 1) d.Transform(Transform2D.Translation(target - d.Center));
                else return false;
                break;
            case DimensionKind.Angle:
                if (gripIndex == 0) d.P1 = target;
                else if (gripIndex == 1) d.P2 = target;
                else if (gripIndex == 2) d.LinePoint = target;
                else return false;
                break;
            default:
                if (gripIndex != 0) return false;
                d.LinePoint = target;
                break;
        }
        if (d.IsValid) return true;
        (d.P1, d.P2, d.Center, d.LinePoint) = before;
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
