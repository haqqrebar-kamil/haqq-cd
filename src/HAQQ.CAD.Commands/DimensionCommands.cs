using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>Shared parts of the dimension commands.</summary>
public abstract class DimensionCommandBase : CadCommandBase, ICreatesObjects, IObjectPicker
{
    /// <summary>True while the command wants a click on an object (object snap off, pick box shown).</summary>
    public virtual bool PickingObject => false;

    protected DimensionEntity New(DimensionKind kind) => new(kind) { Style = Document.CurrentDimensionStyle };

    /// <summary>Adds the dimension to the drawing as one undo step and remembers it for DIMCHAIN and DIMSTACK.</summary>
    protected bool Add(DimensionEntity dimension)
    {
        if (!dimension.IsValid)
        {
            Session.Post("That does not make a dimension. Pick different points.");
            return false;
        }
        Prepare(dimension);
        if (!Session.Execute(new AddEntitiesAction(Name, new Entity[] { dimension }))) return false;
        Session.LastDimensionId = dimension.Id;
        Session.Post("Dimension: " + dimension.GetText(Document.Settings));
        return true;
    }

    protected Entity? Pick(Point2D point)
    {
        var entity = SelectionService.PickAt(Document, point, Session.PickTolerance);
        if (entity is null) Session.Post("No object there.");
        return entity;
    }

    protected static IReadOnlyList<Entity> PreviewOf(DimensionEntity? dimension) =>
        dimension is { IsValid: true } ? new Entity[] { dimension } : Array.Empty<Entity>();
}

/// <summary>
/// DIM: distance between two points, measured horizontally or vertically. Which one is decided by where the
/// dimension line is placed (above or below the points = horizontal, beside them = vertical), or forced with
/// the Horizontal / Vertical options. DIMALIGN measures the true distance along the two points.
/// </summary>
public sealed class LinearDimensionCommand : DimensionCommandBase
{
    private readonly bool _aligned;
    private Point2D? _p1, _p2;
    private bool? _horizontal;       // null = decided by the cursor

    public LinearDimensionCommand(bool aligned) => _aligned = aligned;

    public override string Name => _aligned ? "DIMALIGN" : "DIM";

    public override string Prompt =>
        _p1 is null ? $"{Name}  Click the first point to measure from:"
        : _p2 is null ? $"{Name}  Click the second point:"
        : _aligned ? $"{Name}  Click where the dimension line goes (or type its distance from the points):"
        : $"{Name}  Click where the dimension line goes or [Horizontal/Vertical] (or type its distance from the points):";

    public override Point2D? LastPoint => _p2 is null ? _p1 : null;

    public override IReadOnlyList<string> Options =>
        _p2 is not null && !_aligned ? new[] { "Horizontal", "Vertical" } : Array.Empty<string>();

    public override CommandResult OnInput(CommandInput input)
    {
        switch (input)
        {
            case CommandInput.EnterInput:
                return CommandResult.Finished;

            case CommandInput.KeywordInput k when _p2 is not null && !_aligned:
                if (Matches(k.Keyword, "HORIZONTAL")) _horizontal = true;
                else if (Matches(k.Keyword, "VERTICAL")) _horizontal = false;
                else return Invalid("Type H for horizontal or V for vertical.");
                return CommandResult.Continue;

            case CommandInput.PointInput p when _p1 is null:
                _p1 = p.Point;
                return CommandResult.Continue;

            case CommandInput.PointInput p when _p2 is null:
                if (p.Point.NearlyEquals(_p1!.Value)) return Invalid("The second point must be a different point.");
                _p2 = p.Point;
                return CommandResult.Continue;

            case CommandInput.PointInput p:
                return Add(Build(p.Point)) ? CommandResult.Finished : CommandResult.Continue;

            case CommandInput.NumberInput n when _p2 is not null:
            {
                if (!(n.Value > 0)) return Invalid("The distance must be more than 0.");
                // The typed distance is taken on the side where the cursor is.
                var draft = Build(Session.EffectiveCursor);
                var normal = new Vector2D(-draft.Direction.Y, draft.Direction.X);
                var farthest = normal.Dot(Session.EffectiveCursor - _p1!.Value) >= 0
                    ? (normal.Dot(_p2!.Value - _p1.Value) > 0 ? _p2.Value : _p1.Value)
                    : (normal.Dot(_p2!.Value - _p1.Value) < 0 ? _p2.Value : _p1.Value);
                double sign = normal.Dot(Session.EffectiveCursor - _p1.Value) >= 0 ? 1 : -1;
                draft.LinePoint = farthest + normal * (sign * n.Value);
                return Add(draft) ? CommandResult.Finished : CommandResult.Continue;
            }

            default:
                return Invalid("Click a point.");
        }
    }

    private DimensionEntity Build(Point2D location)
    {
        Point2D a = _p1!.Value, b = _p2!.Value;
        var d = New(_aligned ? DimensionKind.Aligned : DimensionKind.Linear);
        d.P1 = a;
        d.P2 = b;
        d.LinePoint = location;
        if (!_aligned) d.Angle = IsHorizontal(a, b, location, _horizontal) ? 0 : Math.PI / 2;
        return d;
    }

    /// <summary>
    /// Horizontal when the dimension line is placed above or below the two points, vertical when it is placed
    /// beside them. Points on one horizontal or vertical line can only be measured one way.
    /// </summary>
    public static bool IsHorizontal(Point2D a, Point2D b, Point2D location, bool? forced = null)
    {
        if (forced is bool f) return f;
        if (GeoMath.NearlyEqual(a.Y, b.Y)) return true;
        if (GeoMath.NearlyEqual(a.X, b.X)) return false;
        double outsideX = Math.Max(0, Math.Max(Math.Min(a.X, b.X) - location.X, location.X - Math.Max(a.X, b.X)));
        double outsideY = Math.Max(0, Math.Max(Math.Min(a.Y, b.Y) - location.Y, location.Y - Math.Max(a.Y, b.Y)));
        if (outsideX == 0 && outsideY == 0) return Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y);
        return outsideY >= outsideX;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        if (_p1 is null) return Array.Empty<Entity>();
        if (_p2 is null) return cursor.NearlyEquals(_p1.Value) ? Array.Empty<Entity>() : new Entity[] { new LineEntity(_p1.Value, cursor) };
        return PreviewOf(Build(cursor));
    }
}

/// <summary>DIMRAD and DIMDIA: click a circle or an arc, then where the text goes.</summary>
public sealed class RadialDimensionCommand : DimensionCommandBase
{
    private readonly bool _diameter;
    private Point2D? _center;
    private double _radius;

    public RadialDimensionCommand(bool diameter) => _diameter = diameter;

    public override string Name => _diameter ? "DIMDIA" : "DIMRAD";
    public override bool PickingObject => _center is null;

    public override string Prompt => _center is null
        ? $"{Name}  Click a circle or an arc:"
        : $"{Name}  Click where the dimension goes (inside or outside the circle):";

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.PointInput p) return Invalid("Click a point.");

        if (_center is null)
        {
            switch (Pick(p.Point))
            {
                case CircleEntity circle: (_center, _radius) = (circle.Center, circle.Radius); break;
                case ArcEntity arc: (_center, _radius) = (arc.Center, arc.Radius); break;
                case null: break;
                default: Session.Post("Click a circle or an arc. (For a rounded polyline corner, EXPLODE the polyline first.)"); break;
            }
            return CommandResult.Continue;
        }

        var d = Build(p.Point);
        if (d is null) return Invalid("Click away from the centre.");
        return Add(d) ? CommandResult.Finished : CommandResult.Continue;
    }

    private DimensionEntity? Build(Point2D location)
    {
        var u = (location - _center!.Value).Normalized();
        if (u == Vector2D.Zero) return null;
        var d = New(_diameter ? DimensionKind.Diameter : DimensionKind.Radius);
        d.Center = _center.Value;
        d.P1 = _center.Value + u * _radius;
        d.LinePoint = location;
        return d;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor) =>
        _center is null ? Array.Empty<Entity>() : PreviewOf(Build(cursor));
}

/// <summary>
/// DIMANGLE: the angle between two lines (click each line), or the angle an arc covers (click the arc).
/// DIMARCLEN: the length along an arc. The last click places the dimension arc.
/// </summary>
public sealed class AngularDimensionCommand : DimensionCommandBase
{
    private readonly bool _arcLength;
    private LineEntity? _firstLine;
    private Point2D _firstPick;
    private Point2D? _center, _a, _b;

    public AngularDimensionCommand(bool arcLength) => _arcLength = arcLength;

    public override string Name => _arcLength ? "DIMARCLEN" : "DIMANGLE";
    public override bool PickingObject => _center is null;

    public override string Prompt =>
        _center is not null ? $"{Name}  Click where the dimension arc goes:"
        : _arcLength ? $"{Name}  Click an arc:"
        : _firstLine is null ? $"{Name}  Click the first line, or an arc:"
        : $"{Name}  Click the second line:";

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.PointInput p) return Invalid("Click a point.");

        if (_center is not null)
        {
            var d = Build(p.Point);
            return Add(d) ? CommandResult.Finished : CommandResult.Continue;
        }

        var entity = Pick(p.Point);
        switch (entity)
        {
            case null:
                return CommandResult.Continue;

            case ArcEntity arc when _firstLine is null:
                (_center, _a, _b) = (arc.Center, arc.StartPoint, arc.EndPoint);
                return CommandResult.Continue;

            case LineEntity line when !_arcLength && _firstLine is null:
                _firstLine = line;
                _firstPick = p.Point;
                return CommandResult.Continue;

            case LineEntity line when !_arcLength:
            {
                if (line.Id == _firstLine!.Id) return Invalid("Click a different line for the second one.");
                if (!TryIntersect(_firstLine, line, out var corner)) return Invalid("Those lines are parallel: there is no angle between them.");
                _center = corner;
                _a = RayPoint(_firstLine, _firstPick, corner);
                _b = RayPoint(line, p.Point, corner);
                return CommandResult.Continue;
            }

            default:
                return Invalid(_arcLength ? "Click an arc." : "Click a line or an arc. (For polyline sides, EXPLODE the polyline first.)");
        }
    }

    /// <summary>A point on <paramref name="line"/> on the side of the corner where it was clicked.</summary>
    private static Point2D RayPoint(LineEntity line, Point2D pick, Point2D corner)
    {
        var onLine = SegmentMath.ClosestPoint(line.Start, line.End, pick);
        if (!onLine.NearlyEquals(corner, 1e-9)) return onLine;
        return line.Start.DistanceTo(corner) >= line.End.DistanceTo(corner) ? line.Start : line.End;
    }

    private static bool TryIntersect(LineEntity a, LineEntity b, out Point2D point)
    {
        var r = a.End - a.Start;
        var s = b.End - b.Start;
        double cross = r.Cross(s);
        point = default;
        if (Math.Abs(cross) <= 1e-12 * Math.Max(1.0, r.Length * s.Length)) return false;
        double t = (b.Start - a.Start).Cross(s) / cross;
        point = a.Start + r * t;
        return point.IsFinite;
    }

    private DimensionEntity Build(Point2D location)
    {
        var d = New(_arcLength ? DimensionKind.ArcLength : DimensionKind.Angle);
        d.Center = _center!.Value;
        d.P1 = _a!.Value;
        d.P2 = _b!.Value;
        d.LinePoint = location;
        return d;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor) =>
        _center is null ? Array.Empty<Entity>() : PreviewOf(Build(cursor));
}

/// <summary>
/// DIMCHAIN: carries on from the dimension placed last, each new dimension starting where the one before ended,
/// all on one line. DIMSTACK: every new dimension starts from the first point of the dimension placed last, each
/// on its own line further out. Both repeat until Enter.
/// </summary>
public sealed class RunningDimensionCommand : DimensionCommandBase
{
    private readonly bool _stack;
    private Point2D _from, _first, _linePoint;
    private Vector2D _direction;
    private DimensionStyle _style = new();
    private int _count;
    private bool _ready;

    public RunningDimensionCommand(bool stack) => _stack = stack;

    public override string Name => _stack ? "DIMSTACK" : "DIMCHAIN";
    public override string Prompt => $"{Name}  Click the next point to measure to, or Enter to finish:";
    public override Point2D? LastPoint => _ready ? _from : null;

    protected override CommandResult OnStart()
    {
        if (Session.LastDimensionId is not Guid id || Document.GetEntity(id) is not DimensionEntity { Kind: DimensionKind.Linear or DimensionKind.Aligned } last)
        {
            Session.Post($"{Name} carries on from a straight dimension. Place one with DIM or DIMALIGN first.");
            return CommandResult.Finished;
        }
        _first = last.P1;
        _from = last.P2;
        _direction = last.Direction;
        _linePoint = last.LinePoint;
        _style = last.Style;
        _ready = true;
        return CommandResult.Continue;
    }

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.PointInput p) return Invalid("Click a point, or press Enter to finish.");

        var d = Build(p.Point, _count + 1);
        if (!Add(d)) return CommandResult.Continue;
        _count++;
        _from = p.Point;
        return CommandResult.Continue;
    }

    private DimensionEntity Build(Point2D to, int number)
    {
        var d = New(DimensionKind.Linear);
        d.Style = _style;
        d.Angle = _direction.Angle;
        d.P1 = _stack ? _first : _from;
        d.P2 = to;
        if (_stack)
        {
            // Each stacked dimension sits one step further from the measured points.
            var normal = new Vector2D(-_direction.Y, _direction.X);
            double side = normal.Dot(_linePoint - _first) >= 0 ? 1 : -1;
            d.LinePoint = _linePoint + normal * (side * number * 2.2 * _style.TextHeight);
        }
        else d.LinePoint = _linePoint;
        return d;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor) =>
        _ready ? PreviewOf(Build(cursor, _count + 1)) : Array.Empty<Entity>();
}

/// <summary>
/// DIMSIZE: the text height of the current dimension style. Arrows, gaps and extensions change in proportion,
/// and every dimension drawn with that style follows.
/// </summary>
public sealed class DimensionSizeCommand : CadCommandBase
{
    public override string Name => "DIMSIZE";

    public override string Prompt =>
        $"DIMSIZE  Enter the text height for dimension style \"{Document.CurrentDimensionStyleName}\" <{UnitFormatter.FormatLength(Document.CurrentDimensionStyle.TextHeight, Document.Settings)}>:";

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.NumberInput n || !(n.Value > 0)) return Invalid("Type a height more than 0.");
        var style = Document.CurrentDimensionStyle;
        return Session.ModifyDimensionStyle(style.Scaled(n.Value / style.TextHeight)) ? CommandResult.Finished : CommandResult.Continue;
    }
}
