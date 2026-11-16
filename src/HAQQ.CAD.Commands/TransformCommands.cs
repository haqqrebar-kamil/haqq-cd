using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>Shared behaviour of commands that apply one transform to the selection (ROTATE, SCALE, MIRROR).</summary>
public abstract class TransformCommandBase : SelectionCommandBase
{
    private const int MaxPreviewEntities = 400;

    protected List<Entity> Targets { get; private set; } = new();

    protected override CommandResult OnSelectionReady()
    {
        Targets = Session.GetSelectedEntities();
        return Targets.Count == 0 ? CommandResult.Finished : CommandResult.Continue;
    }

    /// <summary>Replaces the selected entities with transformed copies as one undo step.</summary>
    protected void ApplyTransform(Transform2D transform)
    {
        var before = new List<Entity>();
        var after = new List<Entity>();
        foreach (var target in Targets)
        {
            if (Document.GetEntity(target.Id) is not { } live) continue;
            var copy = live.Clone();
            copy.Transform(transform);
            before.Add(live);
            after.Add(copy);
        }
        if (before.Count > 0) Session.Execute(new ModifyEntitiesAction(Name, before, after));
    }

    /// <summary>Adds transformed copies (new ids) and leaves the originals in place.</summary>
    protected void AddTransformedCopies(Transform2D transform)
    {
        var copies = new List<Entity>();
        foreach (var target in Targets)
        {
            if (Document.GetEntity(target.Id) is not { } live) continue;
            var copy = live.CopyWithNewId();
            copy.Transform(transform);
            copies.Add(copy);
        }
        if (copies.Count > 0) Session.Execute(new AddEntitiesAction(Name, copies));
    }

    protected IReadOnlyList<Entity> PreviewOf(Transform2D transform, Entity? helper = null)
    {
        var preview = new List<Entity>(Math.Min(Targets.Count, MaxPreviewEntities) + 1);
        foreach (var target in Targets)
        {
            if (preview.Count >= MaxPreviewEntities) break;
            if (Document.GetEntity(target.Id) is not { } live) continue;
            var copy = live.Clone();
            copy.Transform(transform);
            preview.Add(copy);
        }
        if (helper is not null) preview.Add(helper);
        return preview;
    }
}

/// <summary>ROTATE: turns the selection about a base point by a typed angle or toward a picked point.</summary>
public sealed class RotateCommand : TransformCommandBase
{
    private Point2D? _base;

    public override string Name => "ROTATE";
    public override Point2D? LastPoint => _base;
    public override string Prompt => Selecting ? SelectPrompt
        : _base is null ? "ROTATE  Specify base point:" : "ROTATE  Specify rotation angle (degrees, counter-clockwise) or pick a direction:";

    protected override CommandResult OnInputAfterSelection(CommandInput input)
    {
        switch (input)
        {
            case CommandInput.PointInput p when _base is null:
                _base = p.Point;
                return CommandResult.Continue;

            case CommandInput.PointInput p:
                if (p.Point.NearlyEquals(_base!.Value)) return Invalid("Pick a point away from the base point.");
                ApplyTransform(Transform2D.Rotation((p.Point - _base.Value).Angle, _base.Value));
                return CommandResult.Finished;

            case CommandInput.NumberInput n when _base is not null:
                ApplyTransform(Transform2D.Rotation(GeoMath.DegreesToRadians(n.Value), _base.Value));
                return CommandResult.Finished;

            case CommandInput.EnterInput:
                return CommandResult.Finished;

            default:
                return Invalid(_base is null ? "Specify the base point." : "Enter an angle in degrees or pick a point.");
        }
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        if (_base is not { } b || cursor.NearlyEquals(b)) return Array.Empty<Entity>();
        return PreviewOf(Transform2D.Rotation((cursor - b).Angle, b), new LineEntity(b, cursor));
    }
}

/// <summary>
/// SCALE: enlarges or reduces the selection about a base point. After the base point the scaled result
/// follows the cursor: the size is 1:1 when the cursor is as far from the base point as the farthest corner
/// of the selection, bigger further out and smaller closer in. Click to accept, or type a factor.
/// </summary>
public sealed class ScaleCommand : TransformCommandBase
{
    private Point2D? _base;
    private double _reference = 1.0;

    public override string Name => "SCALE";
    public override Point2D? LastPoint => _base;
    public override string Prompt => Selecting ? SelectPrompt
        : _base is null ? "SCALE  Specify base point:"
        : "SCALE  Move the cursor and click, or type a scale factor (2 doubles the size, 0.5 halves it):";

    /// <summary>The scale factor a click at <paramref name="cursor"/> would give, or null before the base point is set.</summary>
    public double? FactorAt(Point2D cursor) =>
        _base is { } origin ? origin.DistanceTo(cursor) / _reference : null;

    protected override CommandResult OnInputAfterSelection(CommandInput input)
    {
        switch (input)
        {
            case CommandInput.PointInput p when _base is null:
                _base = p.Point;
                _reference = ReferenceLength(p.Point);
                return CommandResult.Continue;

            case CommandInput.PointInput p:
                double picked = FactorAt(p.Point) ?? 0;
                if (!(picked > 1e-6) || !double.IsFinite(picked)) return Invalid("Click away from the base point, or type a scale factor.");
                ApplyTransform(Transform2D.Scaling(picked, _base!.Value));
                Session.Post($"Scaled by {picked:0.####}.");
                return CommandResult.Finished;

            case CommandInput.NumberInput n when _base is not null:
                if (!(n.Value > GeoMath.Epsilon) || !double.IsFinite(n.Value)) return Invalid("The scale factor must be positive.");
                ApplyTransform(Transform2D.Scaling(n.Value, _base.Value));
                return CommandResult.Finished;

            case CommandInput.EnterInput:
                return CommandResult.Finished;

            default:
                return Invalid(_base is null ? "Specify the base point." : "Click a point or type a scale factor.");
        }
    }

    /// <summary>Distance from the base point to the farthest corner of the selection: the cursor distance that means "same size".</summary>
    private double ReferenceLength(Point2D origin)
    {
        double farthest = 0;
        foreach (var target in Targets)
        {
            if (Document.GetEntity(target.Id) is not { } live) continue;
            var box = live.Bounds;
            foreach (var corner in new[] { new Point2D(box.MinX, box.MinY), new Point2D(box.MaxX, box.MinY), new Point2D(box.MaxX, box.MaxY), new Point2D(box.MinX, box.MaxY) })
                farthest = Math.Max(farthest, origin.DistanceTo(corner));
        }
        return farthest > 1e-9 && double.IsFinite(farthest) ? farthest : 1.0;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        if (_base is not { } origin || FactorAt(cursor) is not { } factor || !(factor > 1e-6) || !double.IsFinite(factor))
            return Array.Empty<Entity>();
        return PreviewOf(Transform2D.Scaling(factor, origin), new LineEntity(origin, cursor));
    }
}

/// <summary>MIRROR: reflects the selection across a line given by two points; keeps or erases the originals.</summary>
public sealed class MirrorCommand : TransformCommandBase
{
    private Point2D? _first;
    private Point2D? _second;

    public override string Name => "MIRROR";
    public override Point2D? LastPoint => _second is null ? _first : null;
    public override string Prompt => Selecting ? SelectPrompt
        : _first is null ? "MIRROR  Specify first point of mirror line:"
        : _second is null ? "MIRROR  Specify second point of mirror line:"
        : "MIRROR  Erase source objects? [Yes/No] <No>:";

    public override IReadOnlyList<string> Options => _second is not null ? new[] { "Yes", "No" } : Array.Empty<string>();

    /// <summary>Reflection across the line through <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static Transform2D Reflection(Point2D a, Point2D b)
    {
        var u = (b - a).Normalized();
        double m11 = u.X * u.X - u.Y * u.Y, m12 = 2 * u.X * u.Y, m22 = u.Y * u.Y - u.X * u.X;
        return new Transform2D(m11, m12, m12, m22,
            a.X - (m11 * a.X + m12 * a.Y),
            a.Y - (m12 * a.X + m22 * a.Y));
    }

    protected override CommandResult OnInputAfterSelection(CommandInput input)
    {
        if (_second is null)
        {
            switch (input)
            {
                case CommandInput.PointInput p when _first is null:
                    _first = p.Point;
                    return CommandResult.Continue;
                case CommandInput.PointInput p:
                    if (p.Point.NearlyEquals(_first!.Value)) return Invalid("The mirror line needs two different points.");
                    _second = p.Point;
                    return CommandResult.Continue;
                case CommandInput.EnterInput:
                    return CommandResult.Finished;
                default:
                    return Invalid("Specify a point on the mirror line.");
            }
        }

        var transform = Reflection(_first!.Value, _second.Value);
        switch (input)
        {
            case CommandInput.KeywordInput k when Matches(k.Keyword, "YES"):
                ApplyTransform(transform);
                return CommandResult.Finished;
            case CommandInput.KeywordInput k when Matches(k.Keyword, "NO"):
            case CommandInput.EnterInput:
                AddTransformedCopies(transform);
                return CommandResult.Finished;
            default:
                return Invalid("Enter Yes or No.");
        }
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        if (_first is not { } a) return Array.Empty<Entity>();
        var b = _second ?? cursor;
        if (b.NearlyEquals(a)) return Array.Empty<Entity>();
        return PreviewOf(Reflection(a, b), new LineEntity(a, b));
    }
}
