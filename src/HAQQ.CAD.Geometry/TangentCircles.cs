namespace HAQQ.CAD.Geometry;

/// <summary>One condition a tangent circle must satisfy.</summary>
public abstract record TangentConstraint
{
    /// <summary>Tangent to the infinite line through two points.</summary>
    public sealed record Line(Point2D A, Point2D B) : TangentConstraint;

    /// <summary>Tangent to a circle (also used for arcs).</summary>
    public sealed record Circle(Point2D Center, double Radius) : TangentConstraint;

    /// <summary>The circle's radius is given.</summary>
    public sealed record FixedRadius(double Radius) : TangentConstraint;
}

/// <summary>A circle found by <see cref="TangentCircleSolver"/>.</summary>
public readonly record struct CircleSolution(Point2D Center, double Radius);

/// <summary>
/// Finds circles tangent to lines and circles: tangent-tangent-radius (two objects and a radius) and
/// tangent-tangent-tangent (three objects, the problem of Apollonius). The unknowns are the centre
/// (x, y) and the radius r. A line gives a linear equation a*x + b*y + c = ±r, a fixed radius gives
/// r = R, and a circle gives (x - cx)² + (y - cy)² = (r ± R)². For each choice of signs the linear
/// equations (lines, radius, and differences of circle equations) reduce the problem to at most a
/// quadratic. Every result is checked against the original conditions before it is returned.
/// </summary>
public static class TangentCircleSolver
{
    public static List<CircleSolution> Solve(IReadOnlyList<TangentConstraint> constraints)
    {
        var results = new List<CircleSolution>();
        if (constraints.Count != 3) return results;

        double scale = 1.0;
        foreach (var c in constraints)
        {
            switch (c)
            {
                case TangentConstraint.Line l:
                    if (l.A.NearlyEquals(l.B)) return results;
                    scale = Math.Max(scale, Math.Max(Math.Abs(l.A.X) + Math.Abs(l.A.Y), Math.Abs(l.B.X) + Math.Abs(l.B.Y)));
                    break;
                case TangentConstraint.Circle k:
                    if (!(k.Radius > 0)) return results;
                    scale = Math.Max(scale, Math.Abs(k.Center.X) + Math.Abs(k.Center.Y) + k.Radius);
                    break;
                case TangentConstraint.FixedRadius f:
                    if (!(f.Radius > 0)) return results;
                    scale = Math.Max(scale, f.Radius);
                    break;
            }
        }

        for (int mask = 0; mask < 8; mask++)
        {
            var linear = new List<(double X, double Y, double R, double B)>();
            var circles = new List<(double Cx, double Cy, double Rs)>(); // Rs = sign * radius
            bool skip = false;

            for (int i = 0; i < 3 && !skip; i++)
            {
                double sign = (mask & (1 << i)) == 0 ? 1.0 : -1.0;
                switch (constraints[i])
                {
                    case TangentConstraint.Line l:
                        var dir = (l.B - l.A).Normalized();
                        double a = -dir.Y, b = dir.X, c0 = -(a * l.A.X + b * l.A.Y);
                        linear.Add((a, b, -sign, -c0));           // a*x + b*y - sign*r = -c0
                        break;
                    case TangentConstraint.Circle k:
                        circles.Add((k.Center.X, k.Center.Y, sign * k.Radius));
                        break;
                    case TangentConstraint.FixedRadius f:
                        if (sign < 0) { skip = true; break; }     // no sign to choose: handle it once
                        linear.Add((0, 0, 1, f.Radius));
                        break;
                }
            }
            if (skip) continue;

            // Differences of circle equations are linear in (x, y, r).
            for (int i = 1; i < circles.Count && linear.Count < 2; i++)
            {
                var p = circles[0];
                var q = circles[i];
                linear.Add((2 * (q.Cx - p.Cx), 2 * (q.Cy - p.Cy), 2 * (q.Rs - p.Rs),
                    (q.Cx * q.Cx + q.Cy * q.Cy - q.Rs * q.Rs) - (p.Cx * p.Cx + p.Cy * p.Cy - p.Rs * p.Rs)));
            }

            if (circles.Count == 0)
            {
                if (TrySolve3(linear[0], linear[1], linear[2], out double x, out double y, out double r))
                    Accept(results, constraints, x, y, r, scale);
                continue;
            }

            if (linear.Count < 2) continue;
            var (l1, l2) = (linear[0], linear[1]);

            // Solutions of the two linear equations form a line u = p + t*d in (x, y, r) space.
            double dx = l1.Y * l2.R - l1.R * l2.Y;
            double dy = l1.R * l2.X - l1.X * l2.R;
            double dr = l1.X * l2.Y - l1.Y * l2.X;
            double ax = Math.Abs(dx), ay = Math.Abs(dy), ar = Math.Abs(dr);
            double largest = Math.Max(ax, Math.Max(ay, ar));
            if (largest < 1e-12) continue;

            double px = 0, py = 0, pr = 0;
            if (ar >= ax && ar >= ay) { if (!TrySolve2(l1.X, l1.Y, l1.B, l2.X, l2.Y, l2.B, out px, out py)) continue; }
            else if (ay >= ax) { if (!TrySolve2(l1.X, l1.R, l1.B, l2.X, l2.R, l2.B, out px, out pr)) continue; }
            else { if (!TrySolve2(l1.Y, l1.R, l1.B, l2.Y, l2.R, l2.B, out py, out pr)) continue; }

            var k0 = circles[0];
            double ex = px - k0.Cx, ey = py - k0.Cy, er = pr + k0.Rs;
            double qa = dx * dx + dy * dy - dr * dr;
            double qb = 2.0 * (ex * dx + ey * dy - er * dr);
            double qc = ex * ex + ey * ey - er * er;

            double size = Math.Max(Math.Abs(qa), Math.Max(Math.Abs(qb), Math.Abs(qc)));
            if (size == 0) continue;
            if (Math.Abs(qa) < 1e-12 * size)
            {
                if (Math.Abs(qb) < 1e-12 * size) continue;
                double t = -qc / qb;
                Accept(results, constraints, px + t * dx, py + t * dy, pr + t * dr, scale);
                continue;
            }

            double disc = qb * qb - 4.0 * qa * qc;
            if (disc < 0)
            {
                if (disc < -1e-9 * qb * qb - 1e-18) continue;
                disc = 0; // touching within rounding
            }
            double root = Math.Sqrt(disc);
            // Numerically stable form of the quadratic formula.
            double qq = -0.5 * (qb + (qb >= 0 ? root : -root));
            double t1 = qq / qa;
            Accept(results, constraints, px + t1 * dx, py + t1 * dy, pr + t1 * dr, scale);
            if (Math.Abs(qq) > 0)
            {
                double t2 = qc / qq;
                Accept(results, constraints, px + t2 * dx, py + t2 * dy, pr + t2 * dr, scale);
            }
        }

        return results;
    }

    /// <summary>The point where a solution touches a constraint object (null for a fixed radius).</summary>
    public static Point2D? TangentPoint(TangentConstraint constraint, CircleSolution circle)
    {
        switch (constraint)
        {
            case TangentConstraint.Line l:
                var ab = l.B - l.A;
                double t = (circle.Center - l.A).Dot(ab) / ab.LengthSquared;
                return l.A + ab * t;
            case TangentConstraint.Circle k:
                var u = (circle.Center - k.Center).Normalized();
                if (u == Vector2D.Zero) return null;
                var outer = k.Center + u * k.Radius;
                var inner = k.Center - u * k.Radius;
                return Math.Abs(outer.DistanceTo(circle.Center) - circle.Radius) <= Math.Abs(inner.DistanceTo(circle.Center) - circle.Radius)
                    ? outer
                    : inner;
            default:
                return null;
        }
    }

    /// <summary>
    /// Chooses the solution whose tangent points lie closest to the points the user picked on the
    /// objects (one pick per constraint, null for a fixed radius).
    /// </summary>
    public static CircleSolution? ChooseNearest(IReadOnlyList<TangentConstraint> constraints,
        IReadOnlyList<Point2D?> picks, IReadOnlyList<CircleSolution> solutions)
    {
        CircleSolution? best = null;
        double bestScore = double.PositiveInfinity;
        foreach (var solution in solutions)
        {
            double score = 0;
            for (int i = 0; i < constraints.Count; i++)
            {
                if (picks[i] is not { } pick) continue;
                if (TangentPoint(constraints[i], solution) is { } touch) score += touch.DistanceTo(pick);
            }
            if (score < bestScore)
            {
                bestScore = score;
                best = solution;
            }
        }
        return best;
    }

    private static void Accept(List<CircleSolution> results, IReadOnlyList<TangentConstraint> constraints,
        double x, double y, double r, double scale)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(r) || r <= 1e-9 * scale) return;
        double tol = 1e-7 * Math.Max(scale, r);
        var center = new Point2D(x, y);

        foreach (var c in constraints)
        {
            switch (c)
            {
                case TangentConstraint.Line l:
                    var ab = (l.B - l.A).Normalized();
                    if (Math.Abs(Math.Abs((center - l.A).Cross(ab)) - r) > tol) return;
                    break;
                case TangentConstraint.Circle k:
                    double d = center.DistanceTo(k.Center);
                    if (Math.Abs(d - (r + k.Radius)) > tol && Math.Abs(d - Math.Abs(r - k.Radius)) > tol) return;
                    break;
                case TangentConstraint.FixedRadius f:
                    if (Math.Abs(r - f.Radius) > tol) return;
                    break;
            }
        }

        foreach (var existing in results)
            if (existing.Center.DistanceTo(center) <= tol * 10 && Math.Abs(existing.Radius - r) <= tol * 10) return;
        results.Add(new CircleSolution(center, r));
    }

    private static bool TrySolve2(double a1, double b1, double c1, double a2, double b2, double c2, out double u, out double v)
    {
        u = v = 0;
        double det = a1 * b2 - a2 * b1;
        double size = Math.Abs(a1 * b2) + Math.Abs(a2 * b1);
        if (size == 0 || Math.Abs(det) < 1e-13 * size) return false;
        u = (c1 * b2 - c2 * b1) / det;
        v = (a1 * c2 - a2 * c1) / det;
        return true;
    }

    private static bool TrySolve3((double X, double Y, double R, double B) e1, (double X, double Y, double R, double B) e2,
        (double X, double Y, double R, double B) e3, out double x, out double y, out double r)
    {
        x = y = r = 0;
        double det = e1.X * (e2.Y * e3.R - e2.R * e3.Y) - e1.Y * (e2.X * e3.R - e2.R * e3.X) + e1.R * (e2.X * e3.Y - e2.Y * e3.X);
        double size = (Math.Abs(e1.X) + Math.Abs(e1.Y) + Math.Abs(e1.R)) * (Math.Abs(e2.X) + Math.Abs(e2.Y) + Math.Abs(e2.R)) *
                      (Math.Abs(e3.X) + Math.Abs(e3.Y) + Math.Abs(e3.R));
        if (size == 0 || Math.Abs(det) < 1e-12 * size) return false;
        x = (e1.B * (e2.Y * e3.R - e2.R * e3.Y) - e1.Y * (e2.B * e3.R - e2.R * e3.B) + e1.R * (e2.B * e3.Y - e2.Y * e3.B)) / det;
        y = (e1.X * (e2.B * e3.R - e2.R * e3.B) - e1.B * (e2.X * e3.R - e2.R * e3.X) + e1.R * (e2.X * e3.B - e2.B * e3.X)) / det;
        r = (e1.X * (e2.Y * e3.B - e2.B * e3.Y) - e1.Y * (e2.X * e3.B - e2.B * e3.X) + e1.B * (e2.X * e3.Y - e2.Y * e3.X)) / det;
        return true;
    }
}
