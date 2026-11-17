using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>Shared plumbing for commands.</summary>
public abstract class CadCommandBase : ICadCommand
{
    private EditorSession? _session;

    protected EditorSession Session => _session ?? throw new InvalidOperationException("Command has not been started.");
    protected DrawingDocument Document => Session.Document;

    public abstract string Name { get; }
    public abstract string Prompt { get; }
    public virtual bool WantsSelection => false;
    public virtual Point2D? LastPoint => null;
    public virtual IReadOnlyList<string> Options => Array.Empty<string>();

    public CommandResult Start(EditorSession session)
    {
        _session = session;
        return OnStart();
    }

    protected virtual CommandResult OnStart() => CommandResult.Continue;
    public abstract CommandResult OnInput(CommandInput input);
    public virtual void OnCancel() { }
    public virtual IReadOnlyList<Entity> GetPreview(Point2D cursor) => Array.Empty<Entity>();

    /// <summary>True when <paramref name="keyword"/> is a non-empty prefix of <paramref name="option"/> ("C" matches "CLOSE").</summary>
    protected static bool Matches(string keyword, string option) =>
        keyword.Length > 0 && option.StartsWith(keyword, StringComparison.OrdinalIgnoreCase);

    protected CommandResult Invalid(string message)
    {
        Session.Post(message);
        return CommandResult.Continue;
    }

    /// <summary>Gives a new entity the properties set for new objects (layer, colour, linetype, lineweight).</summary>
    protected T Prepare<T>(T entity) where T : Entity
    {
        Session.ApplyNewObjectProperties(entity);
        return entity;
    }
}

/// <summary>LINE: a chain of independent line segments. The whole chain is one undo step.</summary>
public sealed class LineCommand : CadCommandBase, ITangentAware, ICreatesObjects
{
    private readonly List<Point2D> _points = new();
    private readonly List<LineEntity> _segments = new();

    // A first point taken with the Tangent snap: the circle is known, the exact start is not until the other end is.
    private HAQQ.CAD.Snapping.TangentTarget? _startTangent;
    private Point2D _startPick;

    public bool WantsDeferredTangent => _points.Count == 0 || _startTangent is not null;

    public override string Name => "LINE";
    public override string Prompt => _points.Count == 0
        ? "LINE  Specify first point:"
        : !Session.ContinuousLines ? "LINE  Specify second point or [Undo]:"
        : _points.Count < 3 ? "LINE  Specify next point or [Undo]:" : "LINE  Specify next point or [Close/Undo]:";
    public override Point2D? LastPoint => _points.Count > 0 ? _points[^1] : null;
    public override IReadOnlyList<string> Options => _points.Count >= 3 ? new[] { "Close", "Undo" } : _points.Count >= 1 ? new[] { "Undo" } : Array.Empty<string>();

    public override CommandResult OnInput(CommandInput input)
    {
        switch (input)
        {
            case CommandInput.PointInput p when _points.Count == 0 && p.Tangent is { } circle:
                _startTangent = circle;                    // "tangent to this circle": resolved with the next point
                _startPick = p.Point;
                _points.Add(p.Point);
                return CommandResult.Continue;

            case CommandInput.PointInput p:
                return _startTangent is null ? Add(p.Point) : AddTangent(p.Point, p.Tangent);

            case CommandInput.NumberInput n:
                if (_points.Count == 0) return Invalid("Specify a point first (click or type x,y).");
                if (n.Value <= 0) return Invalid("Distance must be positive.");
                var byDistance = Session.PointFromDistance(_points[^1], n.Value);
                return _startTangent is null ? Add(byDistance) : AddTangent(byDistance, null);

            case CommandInput.KeywordInput k when Matches(k.Keyword, "UNDO"):
                UndoPoint();
                return CommandResult.Continue;

            case CommandInput.KeywordInput k when Matches(k.Keyword, "CLOSE"):
                if (_points.Count < 3) return Invalid("Close needs at least two segments.");
                AddPoint(_points[0]);
                Commit();
                return CommandResult.Finished;

            case CommandInput.EnterInput:
                Commit();
                return CommandResult.Finished;

            default:
                return Invalid("Invalid option.");
        }
    }

    private CommandResult Add(Point2D point)
    {
        AddPoint(point);
        return AfterPoint();
    }

    /// <summary>Second point of a line whose first point was "tangent to a circle".</summary>
    private CommandResult AddTangent(Point2D end, HAQQ.CAD.Snapping.TangentTarget? endCircle)
    {
        if (!TryResolveTangent(_startTangent!.Value, _startPick, end, endCircle, out var start, out var finish))
            return Invalid(endCircle is null
                ? "No tangent from that point: it is inside the circle, or the tangent point is not on the arc."
                : "No line is tangent to both of those where you clicked.");
        _points[0] = start;
        _startTangent = null;
        return Add(finish);
    }

    /// <summary>
    /// The line from a tangent point on <paramref name="first"/> to <paramref name="end"/>, or, when the end was
    /// also taken with the Tangent snap, the line tangent to both circles. Of the possible lines, the one whose
    /// touching points are nearest to where the circles were clicked is used.
    /// </summary>
    public static bool TryResolveTangent(HAQQ.CAD.Snapping.TangentTarget first, Point2D firstPick, Point2D end,
        HAQQ.CAD.Snapping.TangentTarget? second, out Point2D start, out Point2D finish)
    {
        start = finish = default;
        double best = double.PositiveInfinity;

        if (second is { } other)
        {
            foreach (var (a, b) in HAQQ.CAD.Snapping.TangentMath.Common(first.Center, first.Radius, other.Center, other.Radius))
            {
                if (!first.Contains(a) || !other.Contains(b) || a.NearlyEquals(b)) continue;
                double score = a.DistanceTo(firstPick) + b.DistanceTo(end);
                if (score < best) { best = score; start = a; finish = b; }
            }
        }
        else
        {
            foreach (var a in HAQQ.CAD.Snapping.TangentMath.FromPoint(first.Center, first.Radius, end))
            {
                if (!first.Contains(a)) continue;
                double score = a.DistanceTo(firstPick);
                if (score < best) { best = score; start = a; finish = end; }
            }
        }
        return best < double.PositiveInfinity;
    }

    /// <summary>
    /// By default LINE draws one line and ends at the second point (Enter or Space starts the next one).
    /// With <see cref="EditorSession.ContinuousLines"/> on it keeps going from point to point until Enter.
    /// </summary>
    private CommandResult AfterPoint()
    {
        if (Session.ContinuousLines || _segments.Count == 0) return CommandResult.Continue;
        Commit();
        return CommandResult.Finished;
    }

    private void AddPoint(Point2D point)
    {
        if (_points.Count > 0)
        {
            if (point.NearlyEquals(_points[^1])) { Session.Post("Zero-length segment ignored."); return; }
            // Segments go into the document immediately so later points can snap to them;
            // the undo record is written once, when the command ends.
            var line = Prepare(new LineEntity(_points[^1], point));
            Document.AddEntity(line);
            _segments.Add(line);
            Session.ContinueFrom = (point, (point - _points[^1]).Angle);
        }
        _points.Add(point);
    }

    private void UndoPoint()
    {
        if (_points.Count == 0) { Session.Post("Nothing to undo."); return; }
        if (_segments.Count > 0)
        {
            Document.RemoveEntity(_segments[^1].Id);
            _segments.RemoveAt(_segments.Count - 1);
        }
        _points.RemoveAt(_points.Count - 1);
        if (_points.Count == 0) _startTangent = null;
    }

    private void Commit()
    {
        if (_segments.Count > 0) Session.UndoStack.Record(new AddEntitiesAction("LINE", _segments));
        _segments.Clear();
        _points.Clear();
    }

    public override void OnCancel() => Commit();

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        if (_points.Count == 0 || cursor.NearlyEquals(_points[^1])) return Array.Empty<Entity>();
        if (_startTangent is { } circle)
        {
            // The start slides round the circle so that the line stays tangent while the cursor moves.
            var endCircle = Session.CurrentSnap is { Mode: HAQQ.CAD.Snapping.SnapModes.Tangent, Tangent: { } t } snap && snap.Point.NearlyEquals(cursor)
                ? t
                : (HAQQ.CAD.Snapping.TangentTarget?)null;
            return TryResolveTangent(circle, _startPick, cursor, endCircle, out var start, out var finish)
                ? new Entity[] { new LineEntity(start, finish) }
                : Array.Empty<Entity>();
        }
        return new Entity[] { new LineEntity(_points[^1], cursor) };
    }
}

/// <summary>
/// PLINE: one polyline entity made of straight and arc segments, with an optional uniform width.
/// Options: Arc (arc segments tangent to the previous segment), Line (back to straight segments),
/// Close, Length (a straight segment of a given length continuing the previous direction),
/// Halfwidth, Width and Undo.
/// </summary>
public sealed class PolylineCommand : CadCommandBase, ICreatesObjects
{
    private enum Ask { Point, Length, StartWidth, EndWidth, StartHalfwidth, EndHalfwidth }

    private readonly List<Point2D> _points = new();
    private readonly List<double> _bulges = new(); // _bulges[i] belongs to the segment from point i to point i+1
    private bool _arcMode;
    private Ask _ask = Ask.Point;
    private readonly List<(double Start, double End)> _widths = new(); // per segment, parallel to _bulges
    private double _startWidth;   // width at the start of the next segment
    private double _endWidth;     // width at its end; later segments continue at this width

    public override string Name => "PLINE";

    public override string Prompt
    {
        get
        {
            switch (_ask)
            {
                case Ask.Length: return "PLINE  Specify length of line:";
                case Ask.StartWidth: return $"PLINE  Specify starting width <{UnitFormatter.FormatLength(_startWidth, Document.Settings)}>:";
                case Ask.EndWidth: return $"PLINE  Specify ending width <{UnitFormatter.FormatLength(_startWidth, Document.Settings)}>:";
                case Ask.StartHalfwidth: return $"PLINE  Specify starting half-width <{UnitFormatter.FormatLength(_startWidth / 2.0, Document.Settings)}>:";
                case Ask.EndHalfwidth: return $"PLINE  Specify ending half-width <{UnitFormatter.FormatLength(_startWidth / 2.0, Document.Settings)}>:";
            }
            if (_points.Count == 0) return "PLINE  Specify start point:";
            string options = string.Join("/", Options);
            return _arcMode ? $"PLINE  Specify endpoint of arc or [{options}]:" : $"PLINE  Specify next point or [{options}]:";
        }
    }

    public override Point2D? LastPoint => _points.Count > 0 ? _points[^1] : null;

    public override IReadOnlyList<string> Options
    {
        get
        {
            if (_ask != Ask.Point || _points.Count == 0) return Array.Empty<string>();
            var list = new List<string> { _arcMode ? "Line" : "Arc" };
            if (_points.Count >= 3) list.Add("Close");
            list.Add("Halfwidth");
            if (!_arcMode) list.Add("Length");
            if (_points.Count >= 2) list.Add("Undo");
            list.Add("Width");
            return list;
        }
    }

    /// <summary>Direction of travel at the end of what has been drawn so far (radians); +X before the first segment.</summary>
    private double EndDirection =>
        _points.Count >= 2 ? BulgeMath.EndTangentAngle(_points[^2], _points[^1], _bulges[^1]) : 0.0;

    public override CommandResult OnInput(CommandInput input)
    {
        if (_ask != Ask.Point) return OnValueInput(input);

        switch (input)
        {
            case CommandInput.PointInput p:
                AddPoint(p.Point);
                return CommandResult.Continue;

            case CommandInput.NumberInput n:
                if (_points.Count == 0) return Invalid("Specify a point first (click or type x,y).");
                if (n.Value <= 0) return Invalid("Distance must be positive.");
                AddPoint(Session.PointFromDistance(_points[^1], n.Value));
                return CommandResult.Continue;

            case CommandInput.EnterInput:
                Commit(closed: false);
                return CommandResult.Finished;

            case CommandInput.KeywordInput k when _points.Count == 0:
                return Invalid("Specify the start point first.");

            case CommandInput.KeywordInput k when Matches(k.Keyword, "ARC") && !_arcMode:
                _arcMode = true;
                return CommandResult.Continue;

            case CommandInput.KeywordInput k when _arcMode && (k.Keyword == "L" || Matches(k.Keyword, "LINE")):
                _arcMode = false;
                return CommandResult.Continue;

            case CommandInput.KeywordInput k when !_arcMode && k.Keyword.Length >= 1 && Matches(k.Keyword, "LENGTH"):
                _ask = Ask.Length;
                return CommandResult.Continue;

            case CommandInput.KeywordInput k when Matches(k.Keyword, "WIDTH"):
                _ask = Ask.StartWidth;
                return CommandResult.Continue;

            case CommandInput.KeywordInput k when Matches(k.Keyword, "HALFWIDTH"):
                _ask = Ask.StartHalfwidth;
                return CommandResult.Continue;

            case CommandInput.KeywordInput k when Matches(k.Keyword, "UNDO"):
                if (_points.Count < 2) return Invalid("Nothing to undo.");
                _points.RemoveAt(_points.Count - 1);
                _bulges.RemoveAt(_bulges.Count - 1);
                _widths.RemoveAt(_widths.Count - 1);
                return CommandResult.Continue;

            case CommandInput.KeywordInput k when Matches(k.Keyword, "CLOSE"):
                if (_points.Count < 3) return Invalid("Close needs at least three points.");
                double closing = 0;
                if (_arcMode)
                {
                    if (BulgeMath.TangentArcBulge(_points[^1], EndDirection, _points[0]) is not { } bulge)
                        return Invalid("An arc cannot close the polyline from here; switch to Line and close.");
                    closing = bulge;
                }
                Commit(closed: true, closing);
                return CommandResult.Finished;

            default:
                return Invalid("Invalid option.");
        }
    }

    private CommandResult OnValueInput(CommandInput input)
    {
        bool enter = input is CommandInput.EnterInput;
        double value = 0;
        if (!enter)
        {
            if (input is not CommandInput.NumberInput n) return Invalid("Enter a number.");
            value = n.Value;
        }

        switch (_ask)
        {
            case Ask.Length:
                if (enter) break;
                if (value <= 0) return Invalid("The length must be positive.");
                _ask = Ask.Point;
                AddStraight(_points[^1] + Vector2D.FromPolar(value, EndDirection));
                return CommandResult.Continue;

            // Width applies to the segments drawn from now on, not to those already drawn.
            // Starting and ending width may differ; the segment then tapers, and the segments
            // after it continue at the ending width.
            case Ask.StartWidth:
            case Ask.StartHalfwidth:
                if (!enter)
                {
                    if (value < 0) return Invalid("The width cannot be negative.");
                    _startWidth = _ask == Ask.StartHalfwidth ? value * 2.0 : value;
                }
                _ask = _ask == Ask.StartHalfwidth ? Ask.EndHalfwidth : Ask.EndWidth;
                return CommandResult.Continue;

            case Ask.EndWidth:
            case Ask.EndHalfwidth:
                if (enter) _endWidth = _startWidth; // default: same as the starting width
                else
                {
                    if (value < 0) return Invalid("The width cannot be negative.");
                    _endWidth = _ask == Ask.EndHalfwidth ? value * 2.0 : value;
                }
                break;
        }
        _ask = Ask.Point;
        return CommandResult.Continue;
    }

    private void AddPoint(Point2D point)
    {
        if (_points.Count == 0) { _points.Add(point); return; }
        if (point.NearlyEquals(_points[^1])) { Session.Post("Zero-length segment ignored."); return; }

        if (!_arcMode) { AddStraight(point); return; }
        if (BulgeMath.TangentArcBulge(_points[^1], EndDirection, point) is not { } bulge)
        {
            Session.Post("That point lies straight behind the current direction; an arc cannot reach it.");
            return;
        }
        AddSegment(point, bulge);
    }

    private void AddStraight(Point2D point) => AddSegment(point, 0);

    private void AddSegment(Point2D point, double bulge)
    {
        _bulges.Add(bulge);
        _widths.Add((_startWidth, _endWidth));
        _points.Add(point);
        _startWidth = _endWidth; // after a taper, carry on at the ending width
    }

    private PolylineEntity Build(IReadOnlyList<Point2D> points, IReadOnlyList<double> segmentBulges,
        IReadOnlyList<(double Start, double End)> segmentWidths, bool closed, double closingBulge = 0)
    {
        var bulges = new double[points.Count];
        for (int i = 0; i < segmentBulges.Count && i < bulges.Length; i++) bulges[i] = segmentBulges[i];
        if (closed) bulges[^1] = closingBulge;
        var polyline = new PolylineEntity(points, closed, bulges);
        for (int i = 0; i < segmentWidths.Count && i < points.Count; i++)
            polyline.SetSegmentWidth(i, segmentWidths[i].Start, segmentWidths[i].End);
        if (closed) polyline.SetSegmentWidth(points.Count - 1, _startWidth, _endWidth); // the closing segment
        return polyline;
    }

    private void Commit(bool closed, double closingBulge = 0)
    {
        if (_points.Count >= 2)
            Session.Execute(new AddEntitiesAction("PLINE", new[] { Prepare(Build(_points, _bulges, _widths, closed, closingBulge)) }));
        _points.Clear();
        _bulges.Clear();
        _widths.Clear();
    }

    public override void OnCancel() => Commit(closed: false);

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        if (_points.Count == 0) return Array.Empty<Entity>();
        var points = new List<Point2D>(_points);
        var bulges = new List<double>(_bulges);
        var widths = new List<(double Start, double End)>(_widths);

        if (_ask == Ask.Point && !cursor.NearlyEquals(_points[^1]))
        {
            double? bulge = _arcMode ? BulgeMath.TangentArcBulge(_points[^1], EndDirection, cursor) : 0.0;
            if (bulge is { } b)
            {
                bulges.Add(b);
                widths.Add((_startWidth, _endWidth));
                points.Add(cursor);
            }
        }
        return points.Count < 2 ? Array.Empty<Entity>() : new Entity[] { Build(points, bulges, widths, false) };
    }
}

/// <summary>
/// RECTANG: a closed polyline from two opposite corners. Before the first corner: Chamfer, Fillet
/// and Width. For the other corner: Area, Dimensions and Rotation. Holding Shift makes a square.
/// </summary>
public sealed class RectangleCommand : CadCommandBase, IShiftConstraint, ICreatesObjects
{
    private enum Stage
    {
        FirstCorner, OtherCorner,
        Fillet, ChamferFirst, ChamferSecond, Width,
        Rotation, Area, AreaBasis, AreaValue, DimLength, DimWidth, DimDirection,
    }

    private Stage _stage = Stage.FirstCorner;
    private Point2D? _first;
    private double _rotation;          // radians
    private double _fillet;
    private double _chamfer1, _chamfer2;
    private double _width;
    private double _area;
    private bool _areaByLength = true;
    private double _dimLength, _dimWidth;

    public override string Name => "RECTANG";
    public override Point2D? LastPoint => _stage is Stage.OtherCorner or Stage.Rotation or Stage.DimDirection ? _first : null;

    private string F(double v) => UnitFormatter.FormatLength(v, Document.Settings);

    public override string Prompt => _stage switch
    {
        Stage.FirstCorner => "RECTANG  Specify first corner or [Chamfer/Fillet/Width]:",
        Stage.OtherCorner => "RECTANG  Specify other corner or [Area/Dimensions/Rotation] (hold Shift for a square):",
        Stage.Fillet => $"RECTANG  Specify fillet radius for rectangles <{F(_fillet)}>:",
        Stage.ChamferFirst => $"RECTANG  Specify first chamfer distance <{F(_chamfer1)}>:",
        Stage.ChamferSecond => $"RECTANG  Specify second chamfer distance <{F(_chamfer1)}>:",
        Stage.Width => $"RECTANG  Specify line width for rectangles <{F(_width)}>:",
        Stage.Rotation => $"RECTANG  Specify rotation angle in degrees or pick a direction <{GeoMath.RadiansToDegrees(_rotation):0.###}>:",
        Stage.Area => "RECTANG  Enter area of rectangle in current units:",
        Stage.AreaBasis => "RECTANG  Calculate rectangle dimensions based on [Length/Width] <Length>:",
        Stage.AreaValue => _areaByLength ? "RECTANG  Enter rectangle length:" : "RECTANG  Enter rectangle width:",
        Stage.DimLength => "RECTANG  Specify length for rectangles:",
        Stage.DimWidth => "RECTANG  Specify width for rectangles:",
        _ => "RECTANG  Click on the side where the rectangle should go (Enter for up and to the right):",
    };

    public override IReadOnlyList<string> Options => _stage switch
    {
        Stage.FirstCorner => new[] { "Chamfer", "Fillet", "Width" },
        Stage.OtherCorner => new[] { "Area", "Dimensions", "Rotation" },
        Stage.AreaBasis => new[] { "Length", "Width" },
        _ => Array.Empty<string>(),
    };

    private Vector2D AxisU => new(Math.Cos(_rotation), Math.Sin(_rotation));
    private Vector2D AxisV => new(-Math.Sin(_rotation), Math.Cos(_rotation));

    /// <summary>Shift held: the other corner is moved so that both sides are equal (a square).</summary>
    public Point2D ConstrainWithShift(Point2D cursor)
    {
        if (_first is not { } a || _stage != Stage.OtherCorner) return cursor;
        double lx = (cursor - a).Dot(AxisU), ly = (cursor - a).Dot(AxisV);
        double side = Math.Max(Math.Abs(lx), Math.Abs(ly));
        return a + AxisU * (lx < 0 ? -side : side) + AxisV * (ly < 0 ? -side : side);
    }

    public override CommandResult OnInput(CommandInput input)
    {
        switch (_stage)
        {
            case Stage.FirstCorner:
                switch (input)
                {
                    case CommandInput.PointInput p:
                        _first = p.Point;
                        _stage = Stage.OtherCorner;
                        return CommandResult.Continue;
                    case CommandInput.KeywordInput k when Matches(k.Keyword, "CHAMFER"):
                        _stage = Stage.ChamferFirst;
                        return CommandResult.Continue;
                    case CommandInput.KeywordInput k when Matches(k.Keyword, "FILLET"):
                        _stage = Stage.Fillet;
                        return CommandResult.Continue;
                    case CommandInput.KeywordInput k when Matches(k.Keyword, "WIDTH"):
                        _stage = Stage.Width;
                        return CommandResult.Continue;
                    case CommandInput.EnterInput:
                        return CommandResult.Finished;
                    default:
                        return Invalid("Specify a corner point (click or type coordinates).");
                }

            case Stage.OtherCorner:
                switch (input)
                {
                    case CommandInput.PointInput p:
                        return Create((p.Point - _first!.Value).Dot(AxisU), (p.Point - _first.Value).Dot(AxisV));
                    case CommandInput.KeywordInput k when Matches(k.Keyword, "AREA"):
                        _stage = Stage.Area;
                        return CommandResult.Continue;
                    case CommandInput.KeywordInput k when Matches(k.Keyword, "DIMENSIONS"):
                        _stage = Stage.DimLength;
                        return CommandResult.Continue;
                    case CommandInput.KeywordInput k when Matches(k.Keyword, "ROTATION"):
                        _stage = Stage.Rotation;
                        return CommandResult.Continue;
                    case CommandInput.EnterInput:
                        return CommandResult.Finished;
                    default:
                        return Invalid("Specify a corner point (click or type coordinates).");
                }

            case Stage.Rotation:
                switch (input)
                {
                    case CommandInput.NumberInput n:
                        _rotation = GeoMath.DegreesToRadians(n.Value);
                        break;
                    case CommandInput.PointInput p when !p.Point.NearlyEquals(_first!.Value):
                        _rotation = (p.Point - _first.Value).Angle;
                        break;
                    case CommandInput.EnterInput:
                        break;
                    default:
                        return Invalid("Enter an angle in degrees or pick a point.");
                }
                _stage = Stage.OtherCorner;
                return CommandResult.Continue;

            case Stage.AreaBasis:
                switch (input)
                {
                    case CommandInput.KeywordInput k when Matches(k.Keyword, "LENGTH"): _areaByLength = true; break;
                    case CommandInput.KeywordInput k when Matches(k.Keyword, "WIDTH"): _areaByLength = false; break;
                    case CommandInput.EnterInput: _areaByLength = true; break;
                    default: return Invalid("Enter Length or Width.");
                }
                _stage = Stage.AreaValue;
                return CommandResult.Continue;

            case Stage.DimDirection:
                switch (input)
                {
                    case CommandInput.PointInput p:
                        double sx = (p.Point - _first!.Value).Dot(AxisU) < 0 ? -1 : 1;
                        double sy = (p.Point - _first.Value).Dot(AxisV) < 0 ? -1 : 1;
                        return Create(sx * _dimLength, sy * _dimWidth);
                    case CommandInput.EnterInput:
                        return Create(_dimLength, _dimWidth);
                    default:
                        return Invalid("Click on the side where the rectangle should go.");
                }
        }

        // The remaining stages each take one number; Enter keeps the value shown in <>.
        bool enter = input is CommandInput.EnterInput;
        double value = 0;
        if (!enter)
        {
            if (input is not CommandInput.NumberInput number) return Invalid("Enter a number.");
            value = number.Value;
            if (value < 0) return Invalid("The value cannot be negative.");
        }

        switch (_stage)
        {
            case Stage.Fillet:
                if (!enter) { _fillet = value; _chamfer1 = _chamfer2 = 0; } // fillet and chamfer exclude each other
                _stage = Stage.FirstCorner;
                break;
            case Stage.ChamferFirst:
                if (!enter) _chamfer1 = value;
                _stage = Stage.ChamferSecond;
                break;
            case Stage.ChamferSecond:
                _chamfer2 = enter ? _chamfer1 : value;
                if (_chamfer1 > 0 || _chamfer2 > 0) _fillet = 0;
                _stage = Stage.FirstCorner;
                break;
            case Stage.Width:
                if (!enter) _width = value;
                _stage = Stage.FirstCorner;
                break;
            case Stage.Area:
                if (enter || value <= 0) return Invalid("Enter an area greater than zero.");
                _area = value;
                _stage = Stage.AreaBasis;
                break;
            case Stage.AreaValue:
                if (enter || value <= 0) return Invalid("Enter a value greater than zero.");
                return _areaByLength ? Create(value, _area / value) : Create(_area / value, value);
            case Stage.DimLength:
                if (enter || value <= 0) return Invalid("Enter a length greater than zero.");
                _dimLength = value;
                _stage = Stage.DimWidth;
                break;
            case Stage.DimWidth:
                if (enter || value <= 0) return Invalid("Enter a width greater than zero.");
                _dimWidth = value;
                _stage = Stage.DimDirection;
                break;
        }
        return CommandResult.Continue;
    }

    private CommandResult Create(double lengthAlongU, double widthAlongV)
    {
        var rectangle = Build(_first!.Value, lengthAlongU, widthAlongV);
        if (rectangle is null) return Invalid("The rectangle needs a width and a height.");
        Session.Execute(new AddEntitiesAction("RECTANG", new[] { Prepare(rectangle) }));
        return CommandResult.Finished;
    }

    /// <summary>
    /// Builds the rectangle from the first corner and signed sizes along its (possibly rotated) axes,
    /// with rounded or cut corners when a fillet radius or chamfer distances are set and fit.
    /// </summary>
    private PolylineEntity? Build(Point2D origin, double lx, double ly)
    {
        if (Math.Abs(lx) <= GeoMath.Epsilon || Math.Abs(ly) <= GeoMath.Epsilon || !double.IsFinite(lx) || !double.IsFinite(ly)) return null;
        double x0 = Math.Min(0, lx), x1 = Math.Max(0, lx), y0 = Math.Min(0, ly), y1 = Math.Max(0, ly);
        double w = x1 - x0, h = y1 - y0;

        var local = new List<(double X, double Y)>();
        double[]? bulges = null;

        if (_fillet > 0 && 2 * _fillet <= w && 2 * _fillet <= h)
        {
            double r = _fillet, quarter = Math.Tan(Math.PI / 8); // each corner is a quarter circle, counter-clockwise
            local.AddRange(new[]
            {
                (x0 + r, y0), (x1 - r, y0), (x1, y0 + r), (x1, y1 - r),
                (x1 - r, y1), (x0 + r, y1), (x0, y1 - r), (x0, y0 + r),
            });
            bulges = new[] { 0, quarter, 0, quarter, 0, quarter, 0, quarter };
        }
        else if ((_chamfer1 > 0 || _chamfer2 > 0) && 2 * _chamfer1 <= w && 2 * _chamfer2 <= h)
        {
            double a = _chamfer1, b = _chamfer2;
            local.AddRange(new[]
            {
                (x0 + a, y0), (x1 - a, y0), (x1, y0 + b), (x1, y1 - b),
                (x1 - a, y1), (x0 + a, y1), (x0, y1 - b), (x0, y0 + b),
            });
        }
        else
        {
            local.AddRange(new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) });
        }

        // Drop coincident neighbours (a chamfer or fillet that uses up a whole side).
        var points = new List<Point2D>();
        var kept = new List<double>();
        for (int i = 0; i < local.Count; i++)
        {
            var p = origin + AxisU * local[i].X + AxisV * local[i].Y;
            if (points.Count > 0 && p.NearlyEquals(points[^1])) continue;
            points.Add(p);
            kept.Add(bulges?[i] ?? 0);
        }
        if (points.Count > 1 && points[0].NearlyEquals(points[^1]))
        {
            points.RemoveAt(points.Count - 1);
            kept.RemoveAt(kept.Count - 1);
        }
        if (points.Count < 3) return null;
        return new PolylineEntity(points, true, kept) { Width = _width };
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        if (_first is not { } a) return Array.Empty<Entity>();
        PolylineEntity? preview = _stage switch
        {
            Stage.OtherCorner => Build(a, (cursor - a).Dot(AxisU), (cursor - a).Dot(AxisV)),
            Stage.DimDirection => Build(a,
                ((cursor - a).Dot(AxisU) < 0 ? -1 : 1) * _dimLength,
                ((cursor - a).Dot(AxisV) < 0 ? -1 : 1) * _dimWidth),
            _ => null,
        };
        if (preview is not null) return new Entity[] { preview };
        return _stage == Stage.Rotation && !cursor.NearlyEquals(a) ? new Entity[] { new LineEntity(a, cursor) } : Array.Empty<Entity>();
    }
}

/// <summary>CIRCLE: centre and radius/diameter, two points on a diameter, or three points on the circle.</summary>
public sealed class CircleCommand : CadCommandBase, ICreatesObjects
{
    private enum Mode { CenterRadius, TwoPoint, ThreePoint, TanTanRadius, TanTanTan }

    private readonly List<TangentConstraint> _tangents = new();
    private readonly List<Point2D?> _picks = new();

    private readonly List<Point2D> _points = new();
    private Mode _mode = Mode.CenterRadius;
    private Point2D? _center;
    private bool _diameter;

    public override string Name => "CIRCLE";

    public override string Prompt
    {
        get
        {
            if (_mode == Mode.TwoPoint)
                return _points.Count == 0 ? "CIRCLE  Specify first end point of diameter:" : "CIRCLE  Specify second end point of diameter:";
            if (_mode == Mode.ThreePoint)
                return _points.Count switch
                {
                    0 => "CIRCLE  Specify first point on circle:",
                    1 => "CIRCLE  Specify second point on circle:",
                    _ => "CIRCLE  Specify third point on circle:",
                };
            if (_mode is Mode.TanTanRadius or Mode.TanTanTan)
            {
                if (_mode == Mode.TanTanRadius && _tangents.Count == 2)
                    return "CIRCLE  Specify radius of circle" +
                           (Session.LastCircleRadius is double last ? " <" + UnitFormatter.FormatLength(last, Document.Settings) + ">" : "") + ":";
                string which = _tangents.Count switch { 0 => "first", 1 => "second", _ => "third" };
                return $"CIRCLE  Specify point on object for {which} tangent of circle:";
            }
            if (_center is null)
                return _diameter ? "CIRCLE  Specify center point (diameter):" : "CIRCLE  Specify center point or [3P/2P/Ttr/TTT/Diameter]:";
            string fallback = Session.LastCircleRadius is double r
                ? " <" + UnitFormatter.FormatLength(_diameter ? r * 2 : r, Document.Settings) + ">"
                : "";
            return _diameter ? $"CIRCLE  Specify diameter{fallback}:" : $"CIRCLE  Specify radius or [Diameter]{fallback}:";
        }
    }

    public override Point2D? LastPoint => _mode switch
    {
        Mode.CenterRadius => _center,
        Mode.TanTanRadius or Mode.TanTanTan => null, // picks on objects are not a base for ortho or polar
        _ => _points.Count > 0 ? _points[^1] : null,
    };

    public override IReadOnlyList<string> Options =>
        _mode != Mode.CenterRadius ? Array.Empty<string>()
        : _center is null ? (_diameter ? Array.Empty<string>() : new[] { "3P", "2P", "Ttr", "TTT", "Diameter" })
        : _diameter ? Array.Empty<string>() : new[] { "Diameter" };

    public override CommandResult OnInput(CommandInput input)
    {
        if (_mode is Mode.TanTanRadius or Mode.TanTanTan) return OnTangentInput(input);
        if (_mode != Mode.CenterRadius) return OnPointModeInput(input);

        switch (input)
        {
            case CommandInput.KeywordInput k when _center is null && k.Keyword == "3P":
                _mode = Mode.ThreePoint;
                return CommandResult.Continue;

            case CommandInput.KeywordInput k when _center is null && k.Keyword == "2P":
                _mode = Mode.TwoPoint;
                return CommandResult.Continue;

            case CommandInput.KeywordInput k when _center is null && k.Keyword == "TTT":
                _mode = Mode.TanTanTan;
                return CommandResult.Continue;

            case CommandInput.KeywordInput k when _center is null && (k.Keyword == "T" || k.Keyword == "TTR"):
                _mode = Mode.TanTanRadius;
                return CommandResult.Continue;

            case CommandInput.KeywordInput k when _center is null && Matches(k.Keyword, "DIAMETER"):
                _diameter = true; // Center, Diameter chosen up front
                return CommandResult.Continue;

            case CommandInput.PointInput p when _center is null:
                _center = p.Point;
                return CommandResult.Continue;

            case CommandInput.PointInput p:
                double picked = _center!.Value.DistanceTo(p.Point);
                return Create(_center.Value, _diameter ? picked / 2.0 : picked);

            case CommandInput.NumberInput n when _center is not null:
                return Create(_center.Value, _diameter ? n.Value / 2.0 : n.Value);

            case CommandInput.KeywordInput k when _center is not null && Matches(k.Keyword, "DIAMETER"):
                _diameter = true;
                return CommandResult.Continue;

            case CommandInput.EnterInput when _center is not null && Session.LastCircleRadius is double previous:
                return Create(_center.Value, previous); // Enter accepts the default shown in <>

            case CommandInput.EnterInput:
                return CommandResult.Finished;

            default:
                return Invalid(_center is null ? "Specify the center point first." : "Invalid option.");
        }
    }

    /// <summary>Tan-Tan-Radius and Tan-Tan-Tan: pick lines, polyline segments, circles or arcs to be tangent to.</summary>
    private CommandResult OnTangentInput(CommandInput input)
    {
        int needed = _mode == Mode.TanTanRadius ? 2 : 3;

        if (_tangents.Count < needed)
        {
            switch (input)
            {
                case CommandInput.PointInput p:
                    var entity = SelectionService.PickAt(Document, p.Point, Session.PickTolerance);
                    var constraint = entity is null ? null : ToConstraint(entity, p.Point);
                    if (constraint is null) return Invalid("Pick a line, polyline segment, arc or circle.");
                    _tangents.Add(constraint);
                    _picks.Add(p.Point);
                    if (_mode == Mode.TanTanTan && _tangents.Count == 3) return CreateTangent();
                    return CommandResult.Continue;
                case CommandInput.EnterInput:
                    return CommandResult.Finished;
                default:
                    return Invalid("Pick an object to be tangent to.");
            }
        }

        // Tan-Tan-Radius: the radius.
        switch (input)
        {
            case CommandInput.NumberInput n:
                if (!(n.Value > GeoMath.Epsilon)) return Invalid("The radius must be positive.");
                return CreateTangent(n.Value);
            case CommandInput.EnterInput when Session.LastCircleRadius is double previous:
                return CreateTangent(previous);
            case CommandInput.EnterInput:
                return CommandResult.Finished;
            default:
                return Invalid("Enter the radius.");
        }
    }

    private CommandResult CreateTangent(double? radius = null)
    {
        var constraints = new List<TangentConstraint>(_tangents);
        var picks = new List<Point2D?>(_picks);
        if (radius is double r)
        {
            constraints.Add(new TangentConstraint.FixedRadius(r));
            picks.Add(null);
        }

        var solutions = TangentCircleSolver.Solve(constraints);
        var best = TangentCircleSolver.ChooseNearest(constraints, picks, solutions);
        if (best is not { } circle)
        {
            if (radius is null)
            {
                // Let the user try a different third object.
                _tangents.RemoveAt(_tangents.Count - 1);
                _picks.RemoveAt(_picks.Count - 1);
            }
            return Invalid(radius is null ? "No circle is tangent to those three objects." : "No circle of that radius is tangent to both objects.");
        }
        return Create(circle.Center, circle.Radius);
    }

    private static TangentConstraint? ToConstraint(Entity entity, Point2D pick)
    {
        switch (entity)
        {
            case LineEntity line:
                return new TangentConstraint.Line(line.Start, line.End);
            case CircleEntity circle:
                return new TangentConstraint.Circle(circle.Center, circle.Radius);
            case ArcEntity arc:
                return new TangentConstraint.Circle(arc.Center, arc.Radius);
            case PolylineEntity polyline:
                // The segment nearest to where the user clicked.
                TangentConstraint? nearest = null;
                double best = double.PositiveInfinity;
                for (int i = 0; i < polyline.SegmentCount; i++)
                {
                    var segment = polyline.GetSegment(i);
                    if (segment.A.NearlyEquals(segment.B)) continue;
                    bool isArc = polyline.TryGetArc(i, out var c, out double r, out double start, out double sweep);
                    double d = isArc
                        ? ArcMath.DistanceToArc(c, r, start, sweep, pick)
                        : SegmentMath.DistanceToPoint(segment.A, segment.B, pick);
                    if (d >= best) continue;
                    best = d;
                    nearest = isArc ? new TangentConstraint.Circle(c, r) : new TangentConstraint.Line(segment.A, segment.B);
                }
                return nearest;
            default:
                return null;
        }
    }

    private CommandResult OnPointModeInput(CommandInput input)
    {
        switch (input)
        {
            case CommandInput.PointInput p:
                if (_points.Any(q => q.NearlyEquals(p.Point))) return Invalid("That point was already given.");
                _points.Add(p.Point);
                if (_mode == Mode.TwoPoint && _points.Count == 2)
                    return Create(Point2D.Midpoint(_points[0], _points[1]), _points[0].DistanceTo(_points[1]) / 2.0);
                if (_mode == Mode.ThreePoint && _points.Count == 3)
                {
                    if (!ArcMath.TryCircumcenter(_points[0], _points[1], _points[2], out var center))
                    {
                        _points.RemoveAt(2);
                        return Invalid("The three points are in a straight line.");
                    }
                    return Create(center, center.DistanceTo(_points[0]));
                }
                return CommandResult.Continue;

            case CommandInput.EnterInput:
                return CommandResult.Finished;

            default:
                return Invalid("Specify a point (click or type coordinates).");
        }
    }

    private CommandResult Create(Point2D center, double radius)
    {
        if (!(radius > GeoMath.Epsilon) || !double.IsFinite(radius)) return Invalid("The radius must be positive.");
        Session.Execute(new AddEntitiesAction("CIRCLE", new[] { Prepare(new CircleEntity(center, radius)) }));
        Session.LastCircleRadius = radius;
        return CommandResult.Finished;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        switch (_mode)
        {
            case Mode.TwoPoint when _points.Count == 1 && !cursor.NearlyEquals(_points[0]):
                return new Entity[]
                {
                    new CircleEntity(Point2D.Midpoint(_points[0], cursor), _points[0].DistanceTo(cursor) / 2.0),
                    new LineEntity(_points[0], cursor),
                };
            case Mode.ThreePoint when _points.Count == 2 && ArcMath.TryCircumcenter(_points[0], _points[1], cursor, out var c3):
                return new Entity[] { new CircleEntity(c3, c3.DistanceTo(cursor)) };
            case Mode.ThreePoint when _points.Count == 1 && !cursor.NearlyEquals(_points[0]):
                return new Entity[] { new LineEntity(_points[0], cursor) };
            case Mode.CenterRadius when _center is { } c:
                double d = c.DistanceTo(cursor);
                double r = _diameter ? d / 2.0 : d;
                if (r <= GeoMath.Epsilon) return Array.Empty<Entity>();
                return new Entity[] { new CircleEntity(c, r), new LineEntity(c, cursor) };
            default:
                return Array.Empty<Entity>();
        }
    }
}
