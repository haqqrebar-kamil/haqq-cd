using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Core;

/// <summary>
/// Base class of every drawing object. Entities have a persistent identifier that survives
/// save/open and edits.
/// </summary>
/// <remarks>
/// An entity that belongs to a <see cref="DrawingDocument"/> must not be mutated in place:
/// clone it, change the clone and hand it to <see cref="DrawingDocument.ReplaceEntity"/> (normally
/// through an undoable action) so the spatial index and change notifications stay correct.
/// </remarks>
public abstract class Entity
{
    protected Entity(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Entity id must not be empty.", nameof(id));
        Id = id;
    }

    public Guid Id { get; }
    public Guid LayerId { get; set; }
    public CadColor Color { get; set; } = CadColor.ByLayer;
    public string Linetype { get; set; } = Linetypes.ByLayer;

    /// <summary>Scales this object's linetype pattern, on top of the drawing's linetype scale.</summary>
    public double LinetypeScale { get; set; } = 1.0;

    /// <summary>Lineweight in millimetres; null means ByLayer.</summary>
    public double? LineweightMm { get; set; }

    /// <summary>
    /// Objects with the same group id belong together (the lines of one grid system): a click on one selects
    /// them all, a double-click selects only the one clicked. Null = not in a group.
    /// </summary>
    public Guid? GroupId { get; set; }

    private Guid? _space;

    /// <summary>
    /// The space the object is in: <see cref="Guid.Empty"/> for the model, or the id of the layout it is drawn
    /// on. A new object gets the space that is active when it is first added to a drawing.
    /// </summary>
    public Guid SpaceId
    {
        get => _space ?? Guid.Empty;
        set => _space = value;
    }

    /// <summary>False until the object has been given a space (see <see cref="SpaceId"/>).</summary>
    public bool HasSpace => _space.HasValue;

    /// <summary>Extensible custom properties, preserved by the HCAD format.</summary>
    public Dictionary<string, string> CustomProperties { get; } = new(StringComparer.Ordinal);

    /// <summary>Stable type name used in the HCAD format and the UI.</summary>
    public abstract string TypeName { get; }

    public abstract BoundingBox Bounds { get; }

    /// <summary>Shortest distance from <paramref name="point"/> to the entity's curve.</summary>
    public abstract double DistanceTo(Point2D point);

    /// <summary>True when the entity's curve touches or crosses <paramref name="box"/>.</summary>
    public abstract bool IntersectsBox(BoundingBox box);

    /// <summary>Applies a similarity transform (translation, rotation, uniform scale) in place.</summary>
    public abstract void Transform(in Transform2D transform);

    /// <summary>True when the geometry is valid (finite and non-degenerate).</summary>
    public abstract bool IsValid { get; }

    /// <summary>Deep copy that keeps the same id (used for undo snapshots and edits).</summary>
    public Entity Clone() => CopyCommon(CloneGeometry(Id));

    /// <summary>Deep copy with a new persistent id (used by COPY).</summary>
    public Entity CopyWithNewId() => CopyCommon(CloneGeometry(Guid.NewGuid()));

    protected abstract Entity CloneGeometry(Guid id);

    private Entity CopyCommon(Entity target)
    {
        target.LayerId = LayerId;
        target.Color = Color;
        target.Linetype = Linetype;
        target.LinetypeScale = LinetypeScale;
        target.LineweightMm = LineweightMm;
        target.GroupId = GroupId;
        target._space = _space;
        foreach (var pair in CustomProperties) target.CustomProperties[pair.Key] = pair.Value;
        return target;
    }
}

public sealed class LineEntity : Entity
{
    public const string Type = "line";

    public LineEntity(Guid id, Point2D start, Point2D end) : base(id)
    {
        Start = start;
        End = end;
    }

    public LineEntity(Point2D start, Point2D end) : this(Guid.NewGuid(), start, end) { }

    public Point2D Start { get; set; }
    public Point2D End { get; set; }

    public double Length => Start.DistanceTo(End);
    public Point2D Midpoint => Point2D.Midpoint(Start, End);

    public override string TypeName => Type;
    public override BoundingBox Bounds => BoundingBox.FromCorners(Start, End);
    public override bool IsValid => Start.IsFinite && End.IsFinite && !Start.NearlyEquals(End);
    public override double DistanceTo(Point2D point) => SegmentMath.DistanceToPoint(Start, End, point);
    public override bool IntersectsBox(BoundingBox box) => SegmentMath.IntersectsBox(Start, End, box);

    public override void Transform(in Transform2D transform)
    {
        Start = transform.Apply(Start);
        End = transform.Apply(End);
    }

    protected override Entity CloneGeometry(Guid id) => new LineEntity(id, Start, End);
}

public sealed class CircleEntity : Entity
{
    public const string Type = "circle";

    public CircleEntity(Guid id, Point2D center, double radius) : base(id)
    {
        Center = center;
        Radius = radius;
    }

    public CircleEntity(Point2D center, double radius) : this(Guid.NewGuid(), center, radius) { }

    public Point2D Center { get; set; }
    public double Radius { get; set; }

    public double Circumference => 2.0 * Math.PI * Radius;
    public double Area => Math.PI * Radius * Radius;

    public override string TypeName => Type;
    public override BoundingBox Bounds => BoundingBox.FromCenter(Center, Radius);
    public override bool IsValid => Center.IsFinite && GeoMath.IsFinite(Radius) && Radius > GeoMath.Epsilon;
    public override double DistanceTo(Point2D point) => CircleMath.DistanceToCircumference(Center, Radius, point);
    public override bool IntersectsBox(BoundingBox box) => CircleMath.CircumferenceIntersectsBox(Center, Radius, box);

    public override void Transform(in Transform2D transform)
    {
        Center = transform.Apply(Center);
        Radius *= transform.ScaleFactor;
    }

    protected override Entity CloneGeometry(Guid id) => new CircleEntity(id, Center, Radius);
}

/// <summary>
/// A chain of straight and arc segments, optionally closed, with an optional uniform width.
/// RECTANG produces a closed polyline. Arc segments are stored as bulges (see <see cref="BulgeMath"/>).
/// </summary>
public sealed class PolylineEntity : Entity
{
    public const string Type = "polyline";

    private Point2D[] _vertices;
    private double[] _bulges;
    private BoundingBox? _bounds;
    private double[] _startWidths;
    private double[] _endWidths;

    public PolylineEntity(Guid id, IEnumerable<Point2D> vertices, bool isClosed, IEnumerable<double>? bulges = null) : base(id)
    {
        _vertices = vertices.ToArray();
        _bulges = Normalize(bulges, _vertices.Length);
        _startWidths = new double[_vertices.Length];
        _endWidths = new double[_vertices.Length];
        IsClosed = isClosed;
    }

    public PolylineEntity(IEnumerable<Point2D> vertices, bool isClosed, IEnumerable<double>? bulges = null)
        : this(Guid.NewGuid(), vertices, isClosed, bulges) { }

    private static double[] Normalize(IEnumerable<double>? bulges, int count)
    {
        var result = new double[count];
        if (bulges is null) return result;
        int i = 0;
        foreach (double b in bulges)
        {
            if (i >= count) break;
            result[i++] = b;
        }
        return result;
    }

    public IReadOnlyList<Point2D> Vertices => _vertices;

    /// <summary>One bulge per vertex: the curvature of the segment that starts at that vertex (0 = straight).</summary>
    public IReadOnlyList<double> Bulges => _bulges;

    public bool IsClosed { get; set; }

    /// <summary>
    /// Width in model units. Setting it gives every segment that constant width. Reading it returns the
    /// common width when all segments share one, otherwise the largest width in the polyline.
    /// </summary>
    public double Width
    {
        get => MaxWidth;
        set
        {
            for (int i = 0; i < _startWidths.Length; i++) _startWidths[i] = _endWidths[i] = value;
            _bounds = null;
        }
    }

    /// <summary>Largest width anywhere along the polyline (0 for a hairline).</summary>
    public double MaxWidth
    {
        get
        {
            double max = 0;
            for (int i = 0; i < SegmentCount; i++) max = Math.Max(max, Math.Max(_startWidths[i], _endWidths[i]));
            return max;
        }
    }

    /// <summary>True when any segment has a width.</summary>
    public bool HasWidths => MaxWidth > 0;

    /// <summary>True when every segment has the same constant width (including all zero).</summary>
    public bool HasUniformWidth
    {
        get
        {
            if (SegmentCount == 0) return true;
            double w = _startWidths[0];
            for (int i = 0; i < SegmentCount; i++)
                if (_startWidths[i] != w || _endWidths[i] != w) return false;
            return true;
        }
    }

    /// <summary>Width at the start and at the end of segment <paramref name="index"/>; they differ for a tapered segment.</summary>
    public (double Start, double End) GetSegmentWidth(int index)
    {
        if (index < 0 || index >= SegmentCount) throw new ArgumentOutOfRangeException(nameof(index));
        return (_startWidths[index], _endWidths[index]);
    }

    public void SetSegmentWidth(int index, double start, double end)
    {
        if (index < 0 || index >= _startWidths.Length) throw new ArgumentOutOfRangeException(nameof(index));
        _startWidths[index] = start;
        _endWidths[index] = end;
        _bounds = null;
    }

    public bool HasArcs
    {
        get
        {
            for (int i = 0; i < SegmentCount; i++) if (BulgeMath.IsArc(_bulges[i])) return true;
            return false;
        }
    }

    /// <summary>Number of segments, including the closing segment.</summary>
    public int SegmentCount => _vertices.Length < 2 ? 0 : (IsClosed && _vertices.Length > 2 ? _vertices.Length : _vertices.Length - 1);

    public (Point2D A, Point2D B) GetSegment(int index)
    {
        if (index < 0 || index >= SegmentCount) throw new ArgumentOutOfRangeException(nameof(index));
        return (_vertices[index], _vertices[(index + 1) % _vertices.Length]);
    }

    public double GetBulge(int index)
    {
        if (index < 0 || index >= SegmentCount) throw new ArgumentOutOfRangeException(nameof(index));
        return _bulges[index];
    }

    /// <summary>The arc of segment <paramref name="index"/>, or false when that segment is straight.</summary>
    public bool TryGetArc(int index, out Point2D center, out double radius, out double start, out double sweep)
    {
        var (a, b) = GetSegment(index);
        return BulgeMath.TryGetArc(a, b, _bulges[index], out center, out radius, out start, out sweep);
    }

    public void SetVertices(IEnumerable<Point2D> vertices, IEnumerable<double>? bulges = null)
    {
        _vertices = vertices.ToArray();
        _bulges = Normalize(bulges, _vertices.Length);
        Array.Resize(ref _startWidths, _vertices.Length);
        Array.Resize(ref _endWidths, _vertices.Length);
        _bounds = null;
    }

    public double Length
    {
        get
        {
            double total = 0;
            for (int i = 0; i < SegmentCount; i++)
            {
                var (a, b) = GetSegment(i);
                total += BulgeMath.Length(a, b, _bulges[i]);
            }
            return total;
        }
    }

    public double Area
    {
        get
        {
            if (!IsClosed) return 0;
            double area = PolylineMath.SignedArea(_vertices);
            for (int i = 0; i < SegmentCount; i++)
            {
                var (a, b) = GetSegment(i);
                area += BulgeMath.SegmentArea(a, b, _bulges[i]);
            }
            return Math.Abs(area);
        }
    }

    public override string TypeName => Type;

    public override BoundingBox Bounds
    {
        get
        {
            if (_bounds is { } cached) return cached;
            var box = BoundingBox.FromPoints(_vertices);
            for (int i = 0; i < SegmentCount; i++)
                if (TryGetArc(i, out var c, out double r, out double start, out double sweep))
                    box = box.Union(ArcMath.ArcBounds(c, r, start, sweep));
            double widest = MaxWidth;
            if (widest > 0) box = box.Inflate(widest / 2.0);
            _bounds = box;
            return box;
        }
    }

    public override bool IsValid
    {
        get
        {
            if (_vertices.Length < 2) return false;
            for (int i = 0; i < _startWidths.Length; i++)
                if (!(_startWidths[i] >= 0) || !(_endWidths[i] >= 0) || !double.IsFinite(_startWidths[i]) || !double.IsFinite(_endWidths[i])) return false;
            foreach (var v in _vertices) if (!v.IsFinite) return false;
            foreach (double b in _bulges) if (!double.IsFinite(b)) return false;
            return Length > GeoMath.Epsilon;
        }
    }

    public override double DistanceTo(Point2D point)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i < SegmentCount; i++)
        {
            var (a, b) = GetSegment(i);
            double d = TryGetArc(i, out var c, out double r, out double start, out double sweep)
                ? ArcMath.DistanceToArc(c, r, start, sweep, point)
                : SegmentMath.DistanceToPoint(a, b, point);
            // A wide segment can be picked anywhere on its band (its wider end is used for a taper).
            d = Math.Max(0, d - Math.Max(_startWidths[i], _endWidths[i]) / 2.0);
            best = Math.Min(best, d);
        }
        return best;
    }

    public override bool IntersectsBox(BoundingBox box)
    {
        if (!Bounds.Intersects(box)) return false;
        for (int i = 0; i < SegmentCount; i++)
        {
            var (a, b) = GetSegment(i);
            bool hit = TryGetArc(i, out var c, out double r, out double start, out double sweep)
                ? ArcMath.ArcIntersectsBox(c, r, start, sweep, box)
                : SegmentMath.IntersectsBox(a, b, box);
            if (hit) return true;
        }
        return false;
    }

    public override void Transform(in Transform2D transform)
    {
        var result = new Point2D[_vertices.Length];
        for (int i = 0; i < result.Length; i++) result[i] = transform.Apply(_vertices[i]);
        _vertices = result;
        if (transform.Determinant < 0)
        {
            // A mirror turns counter-clockwise arcs into clockwise ones.
            var flipped = new double[_bulges.Length];
            for (int i = 0; i < flipped.Length; i++) flipped[i] = -_bulges[i];
            _bulges = flipped;
        }
        double factor = transform.ScaleFactor;
        if (factor != 1.0)
            for (int i = 0; i < _startWidths.Length; i++)
            {
                _startWidths[i] *= factor;
                _endWidths[i] *= factor;
            }
        _bounds = null;
    }

    protected override Entity CloneGeometry(Guid id)
    {
        var copy = new PolylineEntity(id, _vertices, IsClosed, _bulges);
        Array.Copy(_startWidths, copy._startWidths, _startWidths.Length);
        Array.Copy(_endWidths, copy._endWidths, _endWidths.Length);
        return copy;
    }
}

/// <summary>A circular arc running counter-clockwise from <see cref="StartAngle"/> to <see cref="EndAngle"/> (radians).</summary>
public sealed class ArcEntity : Entity
{
    public const string Type = "arc";

    public ArcEntity(Guid id, Point2D center, double radius, double startAngle, double endAngle) : base(id)
    {
        Center = center;
        Radius = radius;
        StartAngle = ArcMath.NormalizeAngle(startAngle);
        EndAngle = ArcMath.NormalizeAngle(endAngle);
    }

    public ArcEntity(Point2D center, double radius, double startAngle, double endAngle)
        : this(Guid.NewGuid(), center, radius, startAngle, endAngle) { }

    public Point2D Center { get; set; }
    public double Radius { get; set; }
    public double StartAngle { get; set; }
    public double EndAngle { get; set; }

    /// <summary>Included angle in radians, in [0, 2*pi).</summary>
    public double Sweep => ArcMath.Sweep(StartAngle, EndAngle);
    public double Length => Radius * Sweep;
    public Point2D StartPoint => ArcMath.PointAt(Center, Radius, StartAngle);
    public Point2D EndPoint => ArcMath.PointAt(Center, Radius, StartAngle + Sweep);
    public Point2D MidPoint => ArcMath.PointAt(Center, Radius, StartAngle + Sweep / 2.0);

    public override string TypeName => Type;
    public override BoundingBox Bounds => ArcMath.ArcBounds(Center, Radius, StartAngle, Sweep);
    public override bool IsValid =>
        Center.IsFinite && GeoMath.IsFinite(Radius) && Radius > GeoMath.Epsilon &&
        GeoMath.IsFinite(StartAngle) && GeoMath.IsFinite(EndAngle) && Sweep > 1e-9;

    public override double DistanceTo(Point2D point) => ArcMath.DistanceToArc(Center, Radius, StartAngle, Sweep, point);
    public override bool IntersectsBox(BoundingBox box) => ArcMath.ArcIntersectsBox(Center, Radius, StartAngle, Sweep, box);

    public override void Transform(in Transform2D transform)
    {
        bool pureTranslation = transform.M11 == 1 && transform.M12 == 0 && transform.M21 == 0 && transform.M22 == 1;
        if (pureTranslation)
        {
            Center = transform.Apply(Center); // angles are untouched, so repeated moves never drift
            return;
        }

        var start = transform.Apply(StartPoint);
        var end = transform.Apply(EndPoint);
        Center = transform.Apply(Center);
        Radius *= transform.ScaleFactor;
        double a1 = (start - Center).Angle, a2 = (end - Center).Angle;
        if (transform.Determinant < 0) (a1, a2) = (a2, a1); // a mirror reverses the direction of travel
        StartAngle = ArcMath.NormalizeAngle(a1);
        EndAngle = ArcMath.NormalizeAngle(a2);
    }

    protected override Entity CloneGeometry(Guid id) => new ArcEntity(id, Center, Radius, StartAngle, EndAngle);
}
