using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Core;

/// <summary>What a clean-up changed.</summary>
public sealed record CleanupReport(int Straightened, int DuplicatesRemoved, int Merged, int TinyRemoved, int Joined)
{
    public bool ChangedAnything => Straightened + DuplicatesRemoved + Merged + TinyRemoved + Joined > 0;

    public override string ToString() =>
        $"{Straightened} line(s) made exactly horizontal or vertical, {DuplicatesRemoved} duplicate(s) removed, " +
        $"{Merged} line(s) merged into longer ones, {TinyRemoved} speck(s) removed, {Joined} line(s) joined into polylines";
}

/// <summary>
/// Tidies line work that came from a traced or copied drawing: lines that are a hair off horizontal or
/// vertical are made exact, doubled lines are removed, pieces lying end to end on one line become one line,
/// specks go, and lines that meet end to end become polylines. Every correction stays within the tolerance
/// given; nothing is moved further than that.
/// </summary>
public static class GeometryCleanup
{
    /// <param name="entities">The objects of one layer. They are not changed; the result holds new or the same objects.</param>
    /// <param name="tolerance">How far a point may be moved, and how close ends must be to count as touching (drawing units).</param>
    /// <param name="angleToleranceDegrees">How far off horizontal or vertical a line may be and still be straightened.</param>
    /// <param name="join">Join lines that meet end to end into polylines.</param>
    public static List<Entity> Clean(IReadOnlyList<Entity> entities, double tolerance, double angleToleranceDegrees, bool join, out CleanupReport report)
    {
        tolerance = Math.Max(tolerance, 0);
        int straightened = 0, duplicates = 0, merged = 0, tiny = 0, joined = 0;
        var lines = new List<LineEntity>();
        var others = new List<Entity>();

        foreach (var entity in entities)
        {
            if (entity is LineEntity line) lines.Add((LineEntity)line.Clone());
            else others.Add(entity);
        }

        // 1. Specks.
        tiny = lines.RemoveAll(l => l.Start.DistanceTo(l.End) <= tolerance);

        // 2. A hair off horizontal or vertical: make it exact, when that moves no end further than the tolerance.
        double sine = Math.Sin(Math.Clamp(angleToleranceDegrees, 0, 10) * Math.PI / 180.0);
        foreach (var line in lines)
        {
            double dx = line.End.X - line.Start.X, dy = line.End.Y - line.Start.Y, length = Math.Sqrt(dx * dx + dy * dy);
            if (dy != 0 && Math.Abs(dy) <= length * sine && Math.Abs(dy) / 2 <= tolerance)
            {
                double y = (line.Start.Y + line.End.Y) / 2;
                line.Start = new Point2D(line.Start.X, y);
                line.End = new Point2D(line.End.X, y);
                straightened++;
            }
            else if (dx != 0 && Math.Abs(dx) <= length * sine && Math.Abs(dx) / 2 <= tolerance)
            {
                double x = (line.Start.X + line.End.X) / 2;
                line.Start = new Point2D(x, line.Start.Y);
                line.End = new Point2D(x, line.End.Y);
                straightened++;
            }
        }

        // 3. Pieces on one straight line that overlap or touch become one line (this also removes doubled lines).
        lines = MergeCollinear(lines, tolerance, ref merged, ref duplicates);

        // 4. Doubled circles and arcs.
        var kept = new List<Entity>();
        foreach (var entity in others)
        {
            bool doubled = entity switch
            {
                CircleEntity c => kept.OfType<CircleEntity>().Any(k => SameLook(k, c) && k.Center.DistanceTo(c.Center) <= tolerance && Math.Abs(k.Radius - c.Radius) <= tolerance),
                ArcEntity a => kept.OfType<ArcEntity>().Any(k => SameLook(k, a) && k.Center.DistanceTo(a.Center) <= tolerance && Math.Abs(k.Radius - a.Radius) <= tolerance &&
                                                              k.StartPoint.DistanceTo(a.StartPoint) <= tolerance && k.EndPoint.DistanceTo(a.EndPoint) <= tolerance),
                _ => false,
            };
            if (doubled) duplicates++;
            else kept.Add(entity);
        }

        // 5. Lines that meet end to end, two at a point, become polylines.
        var result = new List<Entity>(kept);
        if (join) result.AddRange(JoinChains(lines, tolerance, ref joined));
        else result.AddRange(lines);

        report = new CleanupReport(straightened, duplicates, merged, tiny, joined);
        return result;
    }

    /// <summary>How far a point is from the endless line through two points.</summary>
    private static double OffLine(Point2D a, Point2D b, Point2D p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
        return length < 1e-300 ? a.DistanceTo(p) : Math.Abs((p.X - a.X) * dy - (p.Y - a.Y) * dx) / length;
    }

    private static bool SameLook(Entity a, Entity b) =>
        a.LayerId == b.LayerId && a.Color.Equals(b.Color) && a.Linetype == b.Linetype;

    private static List<LineEntity> MergeCollinear(List<LineEntity> lines, double tolerance, ref int merged, ref int duplicates)
    {
        // Lines are sorted into families that share a direction and lie on the same infinite line.
        var cells = new Dictionary<(long Angle, long Offset, Guid Layer, string Look), List<List<LineEntity>>>();
        var families = new List<List<LineEntity>>();
        double angleStep = 1e-4;                           // radians: about 0.006 degrees
        double offsetStep = Math.Max(tolerance, 1e-9);

        foreach (var line in lines)
        {
            double dx = line.End.X - line.Start.X, dy = line.End.Y - line.Start.Y;
            double angle = Math.Atan2(dy, dx);
            if (angle < 0) angle += Math.PI;
            if (angle >= Math.PI - angleStep / 2) angle = 0;
            double nx = -Math.Sin(angle), ny = Math.Cos(angle);
            double offset = line.Start.X * nx + line.Start.Y * ny;
            var key = ((long)Math.Round(angle / angleStep), (long)Math.Round(offset / offsetStep), line.LayerId, line.Color + "|" + line.Linetype);

            // The family whose first line this one lies on, to within the tolerance (it may be filed in a neighbouring cell).
            List<LineEntity>? family = null;
            for (long da = -1; da <= 1 && family is null; da++)
                for (long d = -1; d <= 1 && family is null; d++)
                    if (cells.TryGetValue((key.Item1 + da, key.Item2 + d, key.LayerId, key.Item4), out var candidates))
                        family = candidates.FirstOrDefault(c =>
                            OffLine(c[0].Start, c[0].End, line.Start) <= tolerance + 1e-12 &&
                            OffLine(c[0].Start, c[0].End, line.End) <= tolerance + 1e-12);
            if (family is null)
            {
                family = new List<LineEntity>();
                families.Add(family);
                if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<List<LineEntity>>();
                list.Add(family);
            }
            family.Add(line);
        }

        var result = new List<LineEntity>();
        foreach (var family in families)
        {
            if (family.Count == 1) { result.Add(family[0]); continue; }
            // Along the first line's direction.
            var origin = family[0].Start;
            var axis = (family[0].End - family[0].Start).Normalized();
            var spans = family.Select(l =>
            {
                double a = (l.Start - origin).Dot(axis), b = (l.End - origin).Dot(axis);
                return (From: Math.Min(a, b), To: Math.Max(a, b), Line: l, Forward: a <= b);
            }).OrderBy(s => s.From).ToList();

            var current = spans[0];
            int members = 1;
            bool identical = true;
            void Flush()
            {
                if (members == 1) { result.Add(current.Line); return; }
                var mergedLine = (LineEntity)current.Line.Clone();
                var p = origin + axis * current.From;
                var q = origin + axis * current.To;
                mergedLine.Start = current.Forward ? p : q;
                mergedLine.End = current.Forward ? q : p;
                result.Add(mergedLine);
            }
            for (int i = 1; i < spans.Count; i++)
            {
                var next = spans[i];
                if (next.From <= current.To + tolerance)
                {
                    bool same = Math.Abs(next.From - current.From) <= tolerance && Math.Abs(next.To - current.To) <= tolerance;
                    if (same) duplicates++; else { merged++; identical = false; }
                    current = (current.From, Math.Max(current.To, next.To), current.Line, current.Forward);
                    members++;
                }
                else
                {
                    Flush();
                    current = next;
                    members = 1;
                    identical = true;
                }
            }
            Flush();
            _ = identical;
        }
        return result;
    }

    private static IEnumerable<Entity> JoinChains(List<LineEntity> lines, double tolerance, ref int joined)
    {
        if (lines.Count < 2) return lines;
        double cell = Math.Max(tolerance * 2, 1e-9);
        // Ends are gathered into nodes: ends closer than the tolerance are the same node.
        var nodes = new Dictionary<(long, long), List<(int Node, Point2D At)>>();
        var nodePoints = new List<Point2D>();
        var touching = new List<List<int>>();           // lines at each node

        int NodeOf(Point2D p)
        {
            long gx = (long)Math.Floor(p.X / cell), gy = (long)Math.Floor(p.Y / cell);
            for (long x = gx - 1; x <= gx + 1; x++)
                for (long y = gy - 1; y <= gy + 1; y++)
                    if (nodes.TryGetValue((x, y), out var bucket))
                        foreach (var (node, at) in bucket)
                            if (at.DistanceTo(p) <= tolerance) return node;
            int created = nodePoints.Count;
            nodePoints.Add(p);
            touching.Add(new List<int>());
            if (!nodes.TryGetValue((gx, gy), out var list)) nodes[(gx, gy)] = list = new List<(int, Point2D)>();
            list.Add((created, p));
            return created;
        }

        var ends = new (int A, int B)[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            ends[i] = (NodeOf(lines[i].Start), NodeOf(lines[i].End));
            touching[ends[i].A].Add(i);
            if (ends[i].B != ends[i].A) touching[ends[i].B].Add(i);
        }

        bool SameLookAt(int node) =>
            touching[node].Count == 2 && SameLook(lines[touching[node][0]], lines[touching[node][1]]);

        var used = new bool[lines.Count];
        var result = new List<Entity>();

        // Walks from a line through nodes where exactly two lines of the same look meet.
        List<int> Chain(int start, out int firstNode, out int lastNode)
        {
            var chain = new List<int> { start };
            used[start] = true;
            int forward = ends[start].B, backward = ends[start].A;
            while (SameLookAt(forward))
            {
                int next = touching[forward][0] == chain[^1] ? touching[forward][1] : touching[forward][0];
                if (used[next]) break;
                used[next] = true;
                chain.Add(next);
                forward = ends[next].A == forward ? ends[next].B : ends[next].A;
            }
            while (SameLookAt(backward))
            {
                int next = touching[backward][0] == chain[0] ? touching[backward][1] : touching[backward][0];
                if (used[next]) break;
                used[next] = true;
                chain.Insert(0, next);
                backward = ends[next].A == backward ? ends[next].B : ends[next].A;
            }
            firstNode = backward;
            lastNode = forward;
            return chain;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            if (used[i]) continue;
            if (ends[i].A == ends[i].B) { used[i] = true; result.Add(lines[i]); continue; }
            var chain = Chain(i, out int first, out int last);
            if (chain.Count == 1) { result.Add(lines[i]); continue; }

            // The corner points in order, starting from the free end.
            var points = new List<Point2D> { nodePoints[first] };
            int at = first;
            foreach (int index in chain)
            {
                at = ends[index].A == at ? ends[index].B : ends[index].A;
                points.Add(nodePoints[at]);
            }
            bool closed = first == last && chain.Count >= 3;
            if (closed) points.RemoveAt(points.Count - 1);
            var polyline = new PolylineEntity(points, closed)
            {
                LayerId = lines[i].LayerId,
                Color = lines[i].Color,
                Linetype = lines[i].Linetype,
                LinetypeScale = lines[i].LinetypeScale,
                LineweightMm = lines[i].LineweightMm,
                SpaceId = lines[i].SpaceId,
            };
            if (!polyline.IsValid) { foreach (int index in chain) result.Add(lines[index]); continue; }
            result.Add(polyline);
            joined += chain.Count;
        }
        return result;
    }
}
