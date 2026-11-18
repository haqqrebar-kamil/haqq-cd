using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Core;

/// <summary>How closed shapes lying inside the hatched area are treated.</summary>
public enum HatchIslands
{
    /// <summary>Alternate: the area is filled, a shape inside it is left open, a shape inside that is filled again.</summary>
    Normal,
    /// <summary>Only the outermost area is filled; everything enclosed inside it is left open.</summary>
    Outer,
    /// <summary>Shapes inside are ignored: the hatch runs through them.</summary>
    Ignore,
}

/// <summary>
/// A filled area: one CAD object, however many pattern lines it shows. The area is a set of closed loops
/// (straight-sided; curves of the boundary are stored as many short sides) filled by the even-odd rule, so a
/// loop inside a loop is a hole. The pattern lines are not stored: they are worked out from the pattern, scale,
/// angle and origin whenever they are needed, so they can never disagree with the definition.
/// An associative hatch also remembers which objects bound it and follows them when they change.
/// </summary>
public sealed class HatchEntity : Entity
{
    public const string Type = "hatch";

    /// <summary>More pattern segments than this in one hatch and it is drawn as its outline with a warning instead.</summary>
    public const int MaxSegments = 200_000;

    private List<Point2D[]> _loops;
    private string _pattern = HatchPatterns.Default;
    private double _scale = 1.0, _angle;
    private Point2D _origin;
    private BoundingBox? _bounds;
    private List<(Point2D A, Point2D B)>? _lines;
    private bool _tooDense;

    public HatchEntity(Guid id, IEnumerable<IEnumerable<Point2D>> loops) : base(id)
    {
        _loops = loops.Select(l => l.ToArray()).ToList();
    }

    public HatchEntity(IEnumerable<IEnumerable<Point2D>> loops) : this(Guid.NewGuid(), loops) { }

    /// <summary>The closed loops of the area. Filled by the even-odd rule.</summary>
    public IReadOnlyList<Point2D[]> Loops => _loops;

    public void SetLoops(IEnumerable<IEnumerable<Point2D>> loops)
    {
        _loops = loops.Select(l => l.ToArray()).ToList();
        Invalidate();
    }

    /// <summary>Name of the pattern (see <see cref="HatchPatterns"/>); "SOLID" fills the area.</summary>
    public string Pattern { get => _pattern; set { _pattern = value ?? ""; Invalidate(); } }

    /// <summary>Pattern size: pattern units times this are drawing units. Does not change the area.</summary>
    public double Scale { get => _scale; set { _scale = value; Invalidate(); } }

    /// <summary>Rotation of the pattern in degrees, counter-clockwise, about <see cref="Origin"/>. Does not turn the area.</summary>
    public double Angle { get => _angle; set { _angle = value; Invalidate(); } }

    /// <summary>The point the pattern is laid out from (where a brick joint or a tile corner falls).</summary>
    public Point2D Origin { get => _origin; set { _origin = value; Invalidate(); } }

    public HatchIslands Islands { get; set; } = HatchIslands.Normal;

    /// <summary>The largest opening between boundary objects that was allowed when the area was found; used again when it is worked out anew.</summary>
    public double GapTolerance { get; set; }

    /// <summary>Follows its boundary objects (<see cref="SourceIds"/>) when they are moved, stretched or edited.</summary>
    public bool Associative { get; set; }

    /// <summary>The objects that bound the area, for an associative hatch.</summary>
    public List<Guid> SourceIds { get; } = new();

    /// <summary>A point inside the area, when it was found by a click; it tells which area of the boundary objects is meant.</summary>
    public Point2D? Seed { get; set; }

    /// <summary>
    /// The boundary objects no longer enclose an area (one was erased, or a gap opened). The hatch keeps the last
    /// area it had and waits: it is never removed by itself.
    /// </summary>
    public bool BoundaryLost { get; set; }

    public bool IsSolid => string.Equals(_pattern, HatchPatterns.Solid, StringComparison.OrdinalIgnoreCase);

    public override string TypeName => Type;

    public override BoundingBox Bounds => _bounds ??= BoundingBox.FromPoints(_loops.SelectMany(l => l));

    public override bool IsValid =>
        _loops.Count > 0 && _loops.All(l => l.Length >= 3 && l.All(p => p.IsFinite)) &&
        GeoMath.IsFinite(_scale) && _scale > 0 && GeoMath.IsFinite(_angle) && _origin.IsFinite &&
        _pattern.Length is > 0 and <= 64 && Math.Abs(Area) > 0;

    /// <summary>The filled area, holes taken out.</summary>
    public double Area => HatchGeometry.Area(_loops);

    public bool Contains(Point2D point) => HatchGeometry.Contains(_loops, point);

    /// <summary>
    /// The pattern lines, in drawing units, cut off at the boundary. Empty for a solid fill and when the pattern
    /// would need more than <see cref="MaxSegments"/> segments (<see cref="TooDense"/> is then true).
    /// </summary>
    public IReadOnlyList<(Point2D A, Point2D B)> Lines
    {
        get
        {
            if (_lines is null)
            {
                var pattern = HatchPatterns.Find(_pattern);
                _lines = pattern is null || pattern.IsSolid
                    ? new List<(Point2D, Point2D)>()
                    : HatchGeometry.Lines(_loops, pattern, _scale, _angle, _origin, MaxSegments, out _tooDense);
            }
            return _lines;
        }
    }

    /// <summary>The pattern is too fine for the size of the area at this scale; nothing but the outline is drawn.</summary>
    public bool TooDense
    {
        get
        {
            _ = Lines;
            return _tooDense;
        }
    }

    /// <summary>
    /// Distance for picking. A solid fill (and a hatch whose lines cannot be drawn) is picked anywhere inside it;
    /// a patterned one on its lines. The outline does not count, so a click on the boundary takes the boundary.
    /// </summary>
    public override double DistanceTo(Point2D point)
    {
        var lines = Lines;
        if (lines.Count == 0)
            return Contains(point) ? 0 : HatchGeometry.DistanceToLoops(_loops, point);
        double best = double.PositiveInfinity;
        foreach (var (a, b) in lines)
        {
            double d = SegmentMath.DistanceToPoint(a, b, point);
            if (d < best) best = d;
        }
        return best;
    }

    public override bool IntersectsBox(BoundingBox box)
    {
        if (!Bounds.Intersects(box)) return false;
        foreach (var loop in _loops)
            for (int i = 0; i < loop.Length; i++)
                if (SegmentMath.IntersectsBox(loop[i], loop[(i + 1) % loop.Length], box)) return true;
        return Contains(box.Center);
    }

    public override void Transform(in Transform2D transform)
    {
        foreach (var loop in _loops)
            for (int i = 0; i < loop.Length; i++) loop[i] = transform.Apply(loop[i]);
        _origin = transform.Apply(_origin);
        if (Seed is { } seed) Seed = transform.Apply(seed);
        _scale *= transform.ScaleFactor;
        GapTolerance *= transform.ScaleFactor;
        _angle += GeoMath.RadiansToDegrees(Math.Atan2(transform.M21, transform.M11));
        Invalidate();
    }

    private void Invalidate()
    {
        _bounds = null;
        _lines = null;
        _tooDense = false;
    }

    protected override Entity CloneGeometry(Guid id)
    {
        var copy = new HatchEntity(id, _loops)
        {
            _pattern = _pattern,
            _scale = _scale,
            _angle = _angle,
            _origin = _origin,
            Islands = Islands,
            Seed = Seed,
            GapTolerance = GapTolerance,
        };
        // A copy with a new id is a new hatch somewhere else: it must not follow the original's boundary objects.
        if (id == Id)
        {
            copy.Associative = Associative;
            copy.BoundaryLost = BoundaryLost;
            copy.SourceIds.AddRange(SourceIds);
            copy._lines = _lines;                   // the same lines: they are never changed in place
            copy._tooDense = _tooDense;
        }
        return copy;
    }
}

/// <summary>Area, containment and pattern lines of a set of closed loops filled by the even-odd rule.</summary>
public static class HatchGeometry
{
    public static bool Contains(IReadOnlyList<Point2D[]> loops, Point2D p)
    {
        bool inside = false;
        foreach (var loop in loops)
            for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
            {
                var a = loop[i];
                var b = loop[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
        return inside;
    }

    public static bool Contains(Point2D[] loop, Point2D p) => Contains(new[] { loop }, p);

    public static double DistanceToLoops(IReadOnlyList<Point2D[]> loops, Point2D p)
    {
        double best = double.PositiveInfinity;
        foreach (var loop in loops)
            for (int i = 0; i < loop.Length; i++)
                best = Math.Min(best, SegmentMath.DistanceToPoint(loop[i], loop[(i + 1) % loop.Length], p));
        return best;
    }

    /// <summary>Even-odd area: a loop that lies inside an odd number of the others is a hole.</summary>
    public static double Area(IReadOnlyList<Point2D[]> loops)
    {
        double total = 0;
        for (int i = 0; i < loops.Count; i++)
        {
            double area = Math.Abs(PolylineMath.SignedArea(loops[i]));
            if (area <= 0) continue;
            int depth = 0;
            var probe = ProbeOf(loops[i]);
            for (int j = 0; j < loops.Count; j++)
                if (j != i && Contains(loops[j], probe)) depth++;
            total += depth % 2 == 0 ? area : -area;
        }
        return total;
    }

    /// <summary>A point just inside a loop (used to tell which loop is inside which).</summary>
    private static Point2D ProbeOf(Point2D[] loop)
    {
        // Midpoint of the first side of real length, pushed a hair to the inside.
        double sign = PolylineMath.SignedArea(loop) >= 0 ? 1 : -1;
        var box = BoundingBox.FromPoints(loop);
        double nudge = Math.Max(box.Width, box.Height) * 1e-7;
        for (int i = 0; i < loop.Length; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Length];
            double length = a.DistanceTo(b);
            if (length <= nudge * 10) continue;
            var left = new Vector2D(-(b.Y - a.Y) / length, (b.X - a.X) / length);
            var probe = Point2D.Midpoint(a, b) + left * (sign * nudge);
            if (Contains(loop, probe)) return probe;
        }
        return loop[0];
    }

    /// <summary>
    /// A point that is inside the filled area (not in a hole), as far from the sides as a few trial lines find.
    /// Null when the loops enclose nothing.
    /// </summary>
    public static Point2D? InteriorPoint(IReadOnlyList<Point2D[]> loops)
    {
        var box = BoundingBox.FromPoints(loops.SelectMany(l => l));
        if (box.IsEmpty || !(box.Height > 0) || !(box.Width > 0)) return null;
        Point2D? best = null;
        double bestWidth = 0;
        var xs = new List<double>();
        foreach (double f in new[] { 0.5, 0.37, 0.63, 0.21, 0.79, 0.11, 0.89, 0.05, 0.95 })
        {
            double y = box.MinY + box.Height * f;
            xs.Clear();
            foreach (var loop in loops)
                for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
                {
                    var a = loop[i];
                    var b = loop[j];
                    if ((a.Y > y) != (b.Y > y)) xs.Add((b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X);
                }
            xs.Sort();
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                double width = xs[i + 1] - xs[i];
                if (width > bestWidth)
                {
                    bestWidth = width;
                    best = new Point2D((xs[i] + xs[i + 1]) / 2, y);
                }
            }
            if (best is not null && f == 0.5 && bestWidth > box.Width * 0.2) break;
        }
        return best;
    }

    /// <summary>
    /// The lines of a pattern inside the loops. Each family of the pattern is a set of parallel lines; every line
    /// is cut where it crosses the loops and kept between an odd and the next even crossing, then broken into the
    /// family's dashes, which are measured from the pattern origin so they line up from line to line.
    /// </summary>
    public static List<(Point2D A, Point2D B)> Lines(IReadOnlyList<Point2D[]> loops, HatchPattern pattern, double scale,
        double angleDegrees, Point2D origin, int maxSegments, out bool tooDense)
    {
        tooDense = false;
        var result = new List<(Point2D, Point2D)>();
        if (loops.Count == 0 || !(scale > 0) || !double.IsFinite(scale)) return result;
        double rotation = GeoMath.DegreesToRadians(angleDegrees);
        double cosR = Math.Cos(rotation), sinR = Math.Sin(rotation);
        var crossings = new Dictionary<long, List<double>>();

        foreach (var family in pattern.Families)
        {
            double a = rotation + GeoMath.DegreesToRadians(family.AngleDeg);
            double dx = Math.Cos(a), dy = Math.Sin(a);                 // along the lines
            double nx = -dy, ny = dx;                                   // across them
            double spacing = family.Spacing * scale, shift = family.Shift * scale;
            if (!(spacing > 0) || !double.IsFinite(spacing)) continue;

            // Where this family starts: the pattern origin plus the family's own offset, turned with the pattern.
            double ox = origin.X + (family.X * cosR - family.Y * sinR) * scale;
            double oy = origin.Y + (family.X * sinR + family.Y * cosR) * scale;
            double across0 = nx * ox + ny * oy, along0 = dx * ox + dy * oy;

            crossings.Clear();
            long count = 0;
            foreach (var loop in loops)
                for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
                {
                    var p = loop[j];
                    var q = loop[i];
                    double sp = nx * p.X + ny * p.Y, sq = nx * q.X + ny * q.Y;
                    if (sp == sq) continue;
                    double lo = Math.Min(sp, sq), hi = Math.Max(sp, sq);
                    // Line k lies at across0 + k * spacing; this side is crossed by the lines in (lo, hi].
                    double first = Math.Floor((lo - across0) / spacing) + 1, last = Math.Floor((hi - across0) / spacing);
                    if (last - first > maxSegments) { tooDense = true; result.Clear(); return result; }
                    for (long k = (long)first; k <= (long)last; k++)
                    {
                        double v = across0 + k * spacing;
                        double f = (v - sp) / (sq - sp);
                        double t = dx * (p.X + (q.X - p.X) * f) + dy * (p.Y + (q.Y - p.Y) * f);
                        if (!crossings.TryGetValue(k, out var list)) crossings[k] = list = new List<double>(4);
                        list.Add(t);
                        if (++count > maxSegments * 4L) { tooDense = true; result.Clear(); return result; }
                    }
                }

            double period = 0;
            foreach (double dash in family.Dashes) period += Math.Abs(dash) * scale;
            bool dashed = family.Dashes.Length > 0 && period > 0;

            foreach (var (k, ts) in crossings)
            {
                if (ts.Count < 2) continue;
                ts.Sort();
                double v = across0 + k * spacing;
                double start = along0 + k * shift;                      // where this line's dashes are measured from
                for (int i = 0; i + 1 < ts.Count; i += 2)
                {
                    double from = ts[i], to = ts[i + 1];
                    if (!(to > from)) continue;
                    if (!dashed)
                    {
                        result.Add((At(from, v), At(to, v)));
                    }
                    else
                    {
                        double periods = (to - from) / period;
                        if (periods > maxSegments) { tooDense = true; result.Clear(); return result; }
                        double pos = start + Math.Floor((from - start) / period) * period;
                        while (pos < to)
                        {
                            foreach (double dash in family.Dashes)
                            {
                                double length = Math.Abs(dash) * scale;
                                if (dash >= 0)
                                {
                                    double s = Math.Max(pos, from), e = Math.Min(pos + length, to);
                                    if (dash == 0) { if (pos >= from && pos <= to) result.Add((At(pos, v), At(pos, v))); }
                                    else if (e > s) result.Add((At(s, v), At(e, v)));
                                }
                                pos += length;
                                if (pos >= to) break;
                            }
                        }
                    }
                    if (result.Count > maxSegments) { tooDense = true; result.Clear(); return result; }
                }
            }

            Point2D At(double t, double v) => new(dx * t + nx * v, dy * t + ny * v);
        }
        return result;
    }
}
