using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Core;

/// <summary>An area found for a hatch: its loops (even-odd), the objects that bound it, and a point inside it.</summary>
public sealed class HatchRegion
{
    public List<Point2D[]> Loops { get; } = new();
    public List<Guid> SourceIds { get; } = new();
    public Point2D? Seed { get; set; }
}

/// <summary>
/// Finds the closed area for a hatch from ordinary drawing objects, without changing them.
/// The objects are taken as short straight pieces (a circle as <see cref="ArcSides"/> sides), every piece is cut
/// where another crosses or touches it, and the result is walked as a map: each closed "country" of that map is
/// a candidate area. A click picks the smallest country that contains it, so four separate lines that cross
/// like a # give the square in the middle, and a line drawn across a rectangle gives the half that was clicked.
/// </summary>
public static class HatchBoundary
{
    /// <summary>Sides a full circle is taken as (arcs in proportion). The area differs from the true circle by about 0.04%.</summary>
    public const int ArcSides = 128;

    /// <summary>More pieces than this in one search and it is refused instead of freezing the program.</summary>
    public const int MaxPieces = 400_000;

    private readonly record struct Piece(Point2D A, Point2D B, Guid Source)
    {
        public double MinX => Math.Min(A.X, B.X);
        public double MaxX => Math.Max(A.X, B.X);
        public double MinY => Math.Min(A.Y, B.Y);
        public double MaxY => Math.Max(A.Y, B.Y);
    }

    /// <summary>Objects that can be part of a hatch boundary: lines, circles, arcs, polylines and blocks made of them.</summary>
    public static bool CanBound(Entity entity) =>
        entity is LineEntity or CircleEntity or ArcEntity or PolylineEntity or BlockReferenceEntity;

    // ------------------------------------------------------------------ objects to straight pieces

    /// <summary>The straight pieces of a set of objects, each remembering which object it came from.</summary>
    private static List<Piece> PiecesOf(IEnumerable<Entity> entities)
    {
        var simple = new List<(Entity Entity, Guid Source)>();
        foreach (var e in entities) Flatten(e, e.Id, simple, 0);

        // Where another object ends on a circle or an arc, the curve gets a corner exactly there. Without it the
        // straight sides that stand in for the curve would pass just inside that end and leave a hairline gap.
        var ends = new List<Point2D>();
        foreach (var (e, _) in simple)
            switch (e)
            {
                case LineEntity line: ends.Add(line.Start); ends.Add(line.End); break;
                case ArcEntity arc: ends.Add(arc.StartPoint); ends.Add(arc.EndPoint); break;
                case PolylineEntity polyline: ends.AddRange(polyline.Vertices); break;
            }
        int curves = simple.Count(x => x.Entity is CircleEntity or ArcEntity or PolylineEntity);
        if ((long)curves * ends.Count > 40_000_000) ends.Clear();

        var pieces = new List<Piece>();
        foreach (var (e, source) in simple)
            switch (e)
            {
                case LineEntity line:
                    pieces.Add(new Piece(line.Start, line.End, source));
                    break;
                case CircleEntity circle:
                    AddArc(circle.Center, circle.Radius, 0, 2 * Math.PI, true, source, pieces, ends);
                    break;
                case ArcEntity arc:
                    AddArc(arc.Center, arc.Radius, arc.StartAngle, arc.Sweep, false, source, pieces, ends);
                    break;
                case PolylineEntity polyline:
                    for (int i = 0; i < polyline.SegmentCount; i++)
                    {
                        var (a, b) = polyline.GetSegment(i);
                        double bulge = polyline.Bulges[i];
                        if (BulgeMath.TryGetArc(a, b, bulge, out var center, out double radius, out double start, out double sweep))
                        {
                            // The pieces must end exactly on the vertices, so neighbours meet without a gap.
                            int first = pieces.Count;
                            AddArc(center, radius, start, sweep, false, source, pieces, ends);
                            if (pieces.Count > first)
                            {
                                var (from, to) = bulge > 0 ? (a, b) : (b, a);
                                pieces[first] = pieces[first] with { A = from };
                                pieces[^1] = pieces[^1] with { B = to };
                            }
                        }
                        else pieces.Add(new Piece(a, b, source));
                    }
                    break;
            }
        return pieces;
    }

    private static void Flatten(Entity entity, Guid source, List<(Entity, Guid)> simple, int depth)
    {
        if (entity is BlockReferenceEntity block)
        {
            if (depth >= 4) return;
            IReadOnlyList<Entity> inner;
            try { inner = block.Resolved; }
            catch (InvalidOperationException) { return; }
            foreach (var part in inner) Flatten(part, source, simple, depth + 1);
        }
        else if (entity is LineEntity or CircleEntity or ArcEntity or PolylineEntity) simple.Add((entity, source));
    }

    private static void AddArc(Point2D center, double radius, double start, double sweep, bool full, Guid source, List<Piece> pieces, List<Point2D> ends)
    {
        if (!(radius > 0) || !(sweep > 0) || !double.IsFinite(radius)) return;
        int sides = full ? ArcSides : Math.Max(4, (int)Math.Ceiling(ArcSides * sweep / (2 * Math.PI)));
        // Rank: 0 an ordinary corner, 1 where another object ends on the curve, 2 the two ends of the curve itself.
        var corners = new List<(double Angle, Point2D Point, int Rank)>(sides + 1);
        for (int i = 0; i <= sides; i++)
        {
            double at = sweep * i / sides;
            corners.Add((at, ArcMath.PointAt(center, radius, start + at), i == 0 || i == sides ? 2 : 0));
        }
        double near = radius * 1e-7, apart = sweep / sides * 1e-3;
        int regular = corners.Count;
        foreach (var end in ends)
        {
            double dx = end.X - center.X, dy = end.Y - center.Y;
            if (Math.Abs(dx) > radius + near || Math.Abs(dy) > radius + near) continue;
            if (Math.Abs(Math.Sqrt(dx * dx + dy * dy) - radius) > near) continue;
            double at = ArcMath.NormalizeAngle(Math.Atan2(dy, dx) - start);
            if (at > sweep) continue;
            corners.Add((at, end, 1));
        }
        if (corners.Count > regular)
        {
            corners.Sort((x, y) => x.Angle.CompareTo(y.Angle));
            // Two corners almost on top of each other: the exact one stays (the curve's own ends before anything).
            for (int i = corners.Count - 1; i > 0; i--)
                if (corners[i].Angle - corners[i - 1].Angle < apart)
                    corners.RemoveAt(corners[i].Rank >= corners[i - 1].Rank ? i - 1 : i);
        }
        if (full) corners[^1] = corners[0] with { Angle = sweep };
        for (int i = 1; i < corners.Count; i++) pieces.Add(new Piece(corners[i - 1].Point, corners[i].Point, source));
    }

    // ------------------------------------------------------------------ the map

    private sealed class Map
    {
        public readonly List<Point2D> Nodes = new();
        public readonly List<(int U, int V, Guid Source)> Edges = new();
        public readonly List<Face> Faces = new();
        public int[] Degree = Array.Empty<int>();
        public int[] Component = Array.Empty<int>();
    }

    private sealed class Face
    {
        public readonly List<int> Nodes = new();
        public readonly List<int> EdgeIndexes = new();
        public double Area;
        public Point2D[] Polygon = Array.Empty<Point2D>();
    }

    private static Map Build(List<Piece> pieces, double tolerance)
    {
        var map = new Map();
        pieces.RemoveAll(p => p.A.DistanceTo(p.B) <= tolerance || !p.A.IsFinite || !p.B.IsFinite);
        int n = pieces.Count;
        if (n == 0) return map;

        // 1. Where pieces cross or touch (positions along each piece, 0..1).
        var cuts = new List<double>?[n];
        var order = Enumerable.Range(0, n).OrderBy(i => pieces[i].MinX).ToArray();
        for (int a = 0; a < n; a++)
        {
            int i = order[a];
            var p = pieces[i];
            double maxX = p.MaxX + tolerance;
            for (int b = a + 1; b < n; b++)
            {
                int j = order[b];
                var q = pieces[j];
                if (q.MinX > maxX) break;
                if (q.MinY > p.MaxY + tolerance || q.MaxY < p.MinY - tolerance) continue;
                Cross(p, q, i, j, cuts, tolerance);
            }
        }

        // 2. Points that are the same within the tolerance become one node.
        var grid = new Dictionary<(long, long), List<int>>();
        double cell = tolerance > 0 ? tolerance : 1e-12;
        int NodeAt(Point2D point)
        {
            long cx = (long)Math.Floor(point.X / cell), cy = (long)Math.Floor(point.Y / cell);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (grid.TryGetValue((cx + dx, cy + dy), out var near))
                        foreach (int id in near)
                            if (map.Nodes[id].DistanceTo(point) <= tolerance) return id;
            map.Nodes.Add(point);
            if (!grid.TryGetValue((cx, cy), out var list)) grid[(cx, cy)] = list = new List<int>(1);
            list.Add(map.Nodes.Count - 1);
            return map.Nodes.Count - 1;
        }

        // 3. Each piece becomes edges between its cut points; the same edge twice (overlapping lines) counts once.
        var seen = new HashSet<(int, int)>();
        for (int i = 0; i < n; i++)
        {
            var p = pieces[i];
            int previous = NodeAt(p.A);
            if (cuts[i] is { } list)
            {
                list.Sort();
                foreach (double t in list)
                {
                    if (t <= 0 || t >= 1) continue;
                    int node = NodeAt(new Point2D(p.A.X + (p.B.X - p.A.X) * t, p.A.Y + (p.B.Y - p.A.Y) * t));
                    AddEdge(previous, node, p.Source);
                    previous = node;
                }
            }
            AddEdge(previous, NodeAt(p.B), p.Source);
        }
        void AddEdge(int u, int v, Guid source)
        {
            if (u == v) return;
            if (seen.Add((Math.Min(u, v), Math.Max(u, v)))) map.Edges.Add((u, v, source));
        }

        // 4. Around every node, its edges in order of direction.
        int nodeCount = map.Nodes.Count, edgeCount = map.Edges.Count;
        var outgoing = new List<int>[nodeCount];                     // half-edges: 2e runs U to V, 2e+1 runs V to U
        for (int i = 0; i < nodeCount; i++) outgoing[i] = new List<int>(3);
        int Origin(int h) => (h & 1) == 0 ? map.Edges[h >> 1].U : map.Edges[h >> 1].V;
        int Target(int h) => (h & 1) == 0 ? map.Edges[h >> 1].V : map.Edges[h >> 1].U;
        var angle = new double[edgeCount * 2];
        for (int h = 0; h < edgeCount * 2; h++)
        {
            var from = map.Nodes[Origin(h)];
            var to = map.Nodes[Target(h)];
            angle[h] = Math.Atan2(to.Y - from.Y, to.X - from.X);
            outgoing[Origin(h)].Add(h);
        }
        var position = new int[edgeCount * 2];
        map.Degree = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            outgoing[i].Sort((x, y) => angle[x].CompareTo(angle[y]));
            for (int k = 0; k < outgoing[i].Count; k++) position[outgoing[i][k]] = k;
            map.Degree[i] = outgoing[i].Count;
        }

        // 5. Walk: arriving at a node, leave by the next edge clockwise from the one arrived on. That keeps one
        //    country on the left all the way round; closed countries come out counter-clockwise (positive area),
        //    and the outside of each connected group of edges clockwise (negative).
        var used = new bool[edgeCount * 2];
        for (int start = 0; start < edgeCount * 2; start++)
        {
            if (used[start]) continue;
            var face = new Face();
            int h = start;
            do
            {
                used[h] = true;
                face.Nodes.Add(Origin(h));
                face.EdgeIndexes.Add(h >> 1);
                int at = Target(h);
                var around = outgoing[at];
                int k = position[h ^ 1];
                h = around[(k - 1 + around.Count) % around.Count];
            }
            while (h != start && !used[h]);
            face.Polygon = face.Nodes.Select(id => map.Nodes[id]).ToArray();
            face.Area = PolylineMath.SignedArea(face.Polygon);
            map.Faces.Add(face);
        }

        // 6. Which nodes hang together.
        var parent = Enumerable.Range(0, nodeCount).ToArray();
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        foreach (var (u, v, _) in map.Edges) parent[Find(u)] = Find(v);
        map.Component = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++) map.Component[i] = Find(i);
        return map;
    }

    private static void Cross(in Piece p, in Piece q, int i, int j, List<double>?[] cuts, double tolerance)
    {
        var r = p.B - p.A;
        var s = q.B - q.A;
        double rl = r.Length, sl = s.Length;
        var w = q.A - p.A;
        double denominator = r.Cross(s);
        if (Math.Abs(denominator) > 1e-12 * rl * sl)
        {
            double t = w.Cross(s) / denominator, u = w.Cross(r) / denominator;
            double et = tolerance / rl, eu = tolerance / sl;
            if (t < -et || t > 1 + et || u < -eu || u > 1 + eu) return;
            Add(i, t);
            Add(j, u);
            return;
        }
        // Parallel. On the same line? Then each one's ends that fall on the other cut it.
        if (Math.Abs(w.Cross(r)) / rl > tolerance) return;
        Add(i, w.Dot(r) / (rl * rl));
        Add(i, (q.B - p.A).Dot(r) / (rl * rl));
        Add(j, (p.A - q.A).Dot(s) / (sl * sl));
        Add(j, (p.B - q.A).Dot(s) / (sl * sl));

        void Add(int index, double t)
        {
            if (!(t > 0) || !(t < 1)) return;                         // the ends are nodes anyway
            (cuts[index] ??= new List<double>(2)).Add(t);
        }
    }

    private static double ToleranceFor(List<Piece> pieces, double gap)
    {
        double extent = 0;
        foreach (var p in pieces)
            extent = Math.Max(extent, Math.Max(Math.Max(Math.Abs(p.A.X), Math.Abs(p.A.Y)), Math.Max(Math.Abs(p.B.X), Math.Abs(p.B.Y))));
        // Far from the origin a coordinate keeps fewer decimals; the tolerance follows.
        return Math.Max(Math.Max(gap, 0), Math.Max(extent * 1e-10, 1e-9));
    }

    private static bool IsArea(Face face, double tolerance) => Math.Abs(face.Area) > tolerance * tolerance * 100;

    // ------------------------------------------------------------------ what the tools ask for

    /// <summary>
    /// The area around a clicked point. <paramref name="gap"/> is the largest opening between two objects that
    /// is still treated as closed (0 = they must meet). Null with a reason when there is no closed area;
    /// <paramref name="openEnds"/> then lists loose ends near the point, where a boundary is probably open.
    /// </summary>
    public static HatchRegion? AtPoint(IEnumerable<Entity> entities, Point2D point, HatchIslands islands, double gap,
        out string? problem, out List<Point2D> openEnds)
    {
        openEnds = new List<Point2D>();
        var all = entities.Where(CanBound).ToList();
        if (all.Count == 0) { problem = "There is nothing here that could bound an area."; return null; }

        var total = BoundingBox.Empty;
        foreach (var e in all) total = total.Union(e.Bounds);
        if (!total.Contains(point)) { problem = "That point is outside everything that is drawn: there is no closed area around it."; return null; }

        // Start with what is near the click and widen until the area found lies wholly inside the part looked at
        // (then nothing outside that part can cut it), or everything has been looked at.
        double reach = Math.Max(Math.Max(total.Width, total.Height) / 16, 1e-9);
        int lastCount = -1;
        while (true)
        {
            var window = new BoundingBox(point.X - reach, point.Y - reach, point.X + reach, point.Y + reach);
            bool everything = window.Contains(total);
            var near = everything ? all : all.Where(e => e.Bounds.Intersects(window)).ToList();
            if (near.Count != lastCount || everything)
            {
                lastCount = near.Count;
                var pieces = PiecesOf(near);
                if (pieces.Count > MaxPieces) { problem = "There is too much geometry around that point to search. Zoom in on a smaller closed area, or use Select Objects."; return null; }
                var region = Solve(pieces, point, null, islands, gap, out var loose);
                if (region is not null && (everything || window.Contains(BoundingBox.FromPoints(region.Loops[0]))))
                {
                    problem = null;
                    return region;
                }
                if (everything)
                {
                    openEnds = loose.OrderBy(l => l.DistanceTo(point)).Take(6).ToList();
                    problem = openEnds.Count > 0
                        ? "No closed area was found around that point. The boundary seems to be open: loose ends are marked. Close the gap, or allow for it with Gap tolerance."
                        : "No closed area was found around that point.";
                    return null;
                }
            }
            reach *= 2;
        }
    }

    /// <summary>
    /// The area enclosed by the given objects themselves (Select Objects): the outline of every connected group
    /// that encloses something, with groups inside groups treated as islands.
    /// </summary>
    public static HatchRegion? FromObjects(IEnumerable<Entity> entities, HatchIslands islands, double gap, out string? problem)
    {
        var chosen = entities.Where(CanBound).ToList();
        int usable = chosen.Count;
        var pieces = PiecesOf(chosen);
        if (usable == 0) { problem = "Select lines, arcs, circles or polylines that close around an area."; return null; }
        if (pieces.Count > MaxPieces) { problem = "Too many objects were selected to search for an area."; return null; }
        var region = Outlines(pieces, islands, gap);
        problem = region is null ? "The selected objects do not close around an area. Their ends must meet (or be within the gap tolerance)." : null;
        return region;
    }

    /// <summary>
    /// Works the area of an associative hatch out again from its boundary objects. Returns false, and leaves
    /// the hatch as it was with <see cref="HatchEntity.BoundaryLost"/> set, when they no longer close.
    /// </summary>
    public static bool Regenerate(DrawingDocument document, HatchEntity hatch)
    {
        double gap = hatch.GapTolerance > 0 && double.IsFinite(hatch.GapTolerance) ? hatch.GapTolerance : 0;
        var sources = new List<Entity>();
        bool missing = false;
        foreach (var id in hatch.SourceIds)
        {
            if (document.GetEntity(id) is { } e && e.SpaceId == hatch.SpaceId && CanBound(e)) sources.Add(e);
            else missing = true;
        }
        HatchRegion? region = null;
        if (!missing && sources.Count > 0)
        {
            if (hatch.Seed is { } seed)
            {
                var pieces = PiecesOf(sources);
                region = Solve(pieces, seed, (hatch.Bounds.Center, Math.Abs(PolylineMath.SignedArea(hatch.Loops[0]))), hatch.Islands, gap, out _);
            }
            else region = FromObjects(sources, hatch.Islands, gap, out _);
        }
        if (region is null)
        {
            hatch.BoundaryLost = true;
            return false;
        }
        hatch.SetLoops(region.Loops);
        hatch.BoundaryLost = false;
        if (hatch.Seed is not null) hatch.Seed = region.Seed;
        return true;
    }

    private static HatchRegion? Solve(List<Piece> pieces, Point2D point, (Point2D Center, double Area)? fallback, HatchIslands islands, double gap, out List<Point2D> loose)
    {
        loose = new List<Point2D>();
        double tolerance = ToleranceFor(pieces, gap);
        var map = Build(pieces, tolerance);
        for (int i = 0; i < map.Nodes.Count; i++)
            if (map.Degree[i] == 1) loose.Add(map.Nodes[i]);

        Face? outer = null;
        foreach (var face in map.Faces)
            if (face.Area > 0 && IsArea(face, tolerance) && HatchGeometry.Contains(face.Polygon, point) && (outer is null || face.Area < outer.Area))
                outer = face;
        bool moved = false;
        if (outer is null && fallback is { } old)
        {
            // The boundary moved away from the remembered point: take the area most like the one the hatch had
            // (its size first, then how near it is), so a slab is not mistaken for the opening in it.
            double best = double.PositiveInfinity;
            foreach (var face in map.Faces)
            {
                if (!(face.Area > 0) || !IsArea(face, tolerance)) continue;
                var box = BoundingBox.FromPoints(face.Polygon);
                double size = Math.Max(Math.Max(box.Width, box.Height), 1e-12);
                double score = Math.Abs(face.Area - old.Area) / Math.Max(old.Area, 1e-300) + 1e-3 * box.Center.DistanceTo(old.Center) / size;
                if (score < best) { best = score; outer = face; }
            }
            moved = outer is not null;
        }
        if (outer is null) return null;

        var region = new HatchRegion();
        var sources = new HashSet<Guid>();
        Take(outer);
        if (islands != HatchIslands.Ignore)
        {
            int component = map.Component[outer.Nodes[0]];
            var inside = map.Faces
                .Where(f => f.Area < 0 && IsArea(f, tolerance) && map.Component[f.Nodes[0]] != component && HatchGeometry.Contains(outer.Polygon, f.Polygon[0]))
                .ToList();
            foreach (var island in inside)
            {
                if (islands == HatchIslands.Outer && inside.Any(o => o != island && HatchGeometry.Contains(o.Polygon, island.Polygon[0]))) continue;
                Take(island);
            }
        }
        region.SourceIds.AddRange(sources);
        region.Seed = moved || !HatchGeometry.Contains(region.Loops, point) ? HatchGeometry.InteriorPoint(region.Loops) ?? point : point;
        return region;

        void Take(Face face)
        {
            region.Loops.Add(face.Polygon);
            foreach (int e in face.EdgeIndexes) sources.Add(map.Edges[e].Source);
        }
    }

    private static HatchRegion? Outlines(List<Piece> pieces, HatchIslands islands, double gap)
    {
        double tolerance = ToleranceFor(pieces, gap);
        var map = Build(pieces, tolerance);
        var outlines = map.Faces.Where(f => f.Area < 0 && IsArea(f, tolerance)).ToList();
        // A group of edges that encloses nothing (a lone line) has an outline of no area and was dropped above.
        if (outlines.Count == 0) return null;
        var region = new HatchRegion();
        var sources = new HashSet<Guid>();
        foreach (var outline in outlines)
        {
            int depth = outlines.Count(o => o != outline && HatchGeometry.Contains(o.Polygon, outline.Polygon[0]));
            if (islands == HatchIslands.Ignore && depth > 0) continue;
            if (islands == HatchIslands.Outer && depth > 1) continue;
            region.Loops.Add(outline.Polygon);
            foreach (int e in outline.EdgeIndexes) sources.Add(map.Edges[e].Source);
        }
        region.SourceIds.AddRange(sources);
        return region;
    }
}
