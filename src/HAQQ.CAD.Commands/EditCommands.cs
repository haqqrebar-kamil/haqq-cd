using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>Shared helpers of the commands that work by clicking on objects.</summary>
public abstract class PickCommandBase : CadCommandBase, IObjectPicker
{
    public virtual bool PickingObject => true;

    /// <summary>The editable object under a click, or null (with a message) when there is none.</summary>
    protected Entity? PickEntity(Point2D point)
    {
        var entity = SelectionService.PickAt(Document, point, Session.PickTolerance);
        if (entity is null) Session.Post("No object there. Click on an object, or press Enter to finish.");
        return entity;
    }

    /// <summary>Every piece of every displayed object except <paramref name="except"/>: what TRIM and EXTEND work against.</summary>
    protected List<Curve> BoundaryPieces(Entity except)
    {
        var pieces = new List<Curve>();
        foreach (var entity in Document.EntitiesUnordered)
        {
            if (entity.Id == except.Id) continue;
            if (Document.GetLayer(entity.LayerId) is not { IsDisplayed: true }) continue;
            pieces.AddRange(Curve.PiecesOf(entity));
        }
        return pieces;
    }

    protected static T Inherit<T>(Entity from, T to) where T : Entity
    {
        to.LayerId = from.LayerId;
        to.Color = from.Color;
        to.Linetype = from.Linetype;
        to.LineweightMm = from.LineweightMm;
        return to;
    }
}

/// <summary>OFFSET: a parallel copy of a line, arc, circle or polyline at a set distance, on the side that is clicked.</summary>
public sealed class OffsetCommand : PickCommandBase
{
    private double? _distance;
    private Entity? _source;

    public override string Name => "OFFSET";

    public override bool PickingObject => _distance is not null && _source is null;

    public override string Prompt => _distance is null
        ? Session.OffsetDistance is { } last
            ? $"OFFSET  Specify offset distance <{UnitFormatter.FormatLength(last, Document.Settings)}>:"
            : "OFFSET  Specify offset distance:"
        : _source is null
            ? "OFFSET  Select object to offset, or Enter to finish:"
            : "OFFSET  Click on the side to offset to:";

    public override CommandResult OnInput(CommandInput input)
    {
        if (_distance is null)
        {
            switch (input)
            {
                case CommandInput.NumberInput n when n.Value > 0:
                    _distance = Session.OffsetDistance = n.Value;
                    return CommandResult.Continue;
                case CommandInput.EnterInput when Session.OffsetDistance is { } last:
                    _distance = last;
                    return CommandResult.Continue;
                case CommandInput.EnterInput:
                    return CommandResult.Finished;
                default:
                    return Invalid("Type the offset distance (a positive number).");
            }
        }

        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.PointInput p) return Invalid("Click on an object, or press Enter to finish.");

        if (_source is null)
        {
            var entity = PickEntity(p.Point);
            if (entity is null) return CommandResult.Continue;
            if (entity is not (LineEntity or ArcEntity or CircleEntity or PolylineEntity))
                return Invalid("That kind of object cannot be offset.");
            _source = entity;
            return CommandResult.Continue;
        }

        var copy = Build(_source, p.Point, _distance.Value);
        if (copy is null) return Invalid("The offset does not fit on that side (the distance is too large for this object).");
        Session.Execute(new AddEntitiesAction("OFFSET", new[] { copy }));
        _source = null;                                   // ready for the next object
        return CommandResult.Continue;
    }

    /// <summary>The offset copy of <paramref name="source"/> on the side of <paramref name="side"/>, or null when it cannot exist.</summary>
    public static Entity? Build(Entity source, Point2D side, double distance)
    {
        switch (source)
        {
            case LineEntity line:
            {
                var along = line.End - line.Start;
                if (along.Length <= GeoMath.Epsilon) return null;
                var u = along.Normalized();
                var left = new Vector2D(-u.Y, u.X);
                var shift = left * (left.Dot(side - line.Start) >= 0 ? distance : -distance);
                return Inherit(line, new LineEntity(line.Start + shift, line.End + shift));
            }
            case CircleEntity circle:
            {
                double radius = side.DistanceTo(circle.Center) >= circle.Radius ? circle.Radius + distance : circle.Radius - distance;
                return radius > 1e-9 ? Inherit(circle, new CircleEntity(circle.Center, radius)) : null;
            }
            case ArcEntity arc:
            {
                double radius = side.DistanceTo(arc.Center) >= arc.Radius ? arc.Radius + distance : arc.Radius - distance;
                return radius > 1e-9 ? Inherit(arc, new ArcEntity(arc.Center, radius, arc.StartAngle, arc.EndAngle)) : null;
            }
            case PolylineEntity polyline:
            {
                // Which side of the nearest segment was clicked decides left or right for the whole polyline.
                int nearest = -1;
                double best = double.PositiveInfinity;
                var pieces = Curve.PiecesOf(polyline).ToList();
                for (int i = 0; i < pieces.Count; i++)
                {
                    double d = pieces[i].IsArc
                        ? ArcMath.DistanceToArc(pieces[i].Center, pieces[i].Radius, pieces[i].Start, pieces[i].Sweep, side)
                        : SegmentMath.DistanceToPoint(pieces[i].A, pieces[i].B, side);
                    if (d < best) { best = d; nearest = i; }
                }
                if (nearest < 0) return null;

                bool leftSide;
                if (pieces[nearest].IsArc)
                {
                    bool inside = side.DistanceTo(pieces[nearest].Center) < pieces[nearest].Radius;
                    leftSide = polyline.GetBulge(nearest) > 0 ? inside : !inside;
                }
                else
                {
                    var (a, b) = polyline.GetSegment(nearest);
                    leftSide = (b - a).Cross(side - a) >= 0;
                }
                return PolylineOffset.TryOffset(polyline, leftSide ? distance : -distance);
            }
        }
        return null;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        if (_source is null || _distance is null) return Array.Empty<Entity>();
        return Build(_source, cursor, _distance.Value) is { } copy ? new[] { copy } : Array.Empty<Entity>();
    }
}

/// <summary>
/// TRIM (quick mode): click the part of a line, arc or circle to remove; it is cut back to the nearest
/// crossings with any other object on either side of the click. Each trim is one undo step.
/// </summary>
public sealed class TrimCommand : PickCommandBase
{
    public override string Name => "TRIM";
    public override string Prompt => "TRIM  Click the part to remove, or Enter to finish:";

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.PointInput p) return Invalid("Click on the part to remove, or press Enter to finish.");
        var entity = PickEntity(p.Point);
        if (entity is null) return CommandResult.Continue;

        var boundaries = BoundaryPieces(entity);
        switch (entity)
        {
            case LineEntity line:
                TrimLine(line, p.Point, boundaries);
                break;
            case ArcEntity arc:
                TrimArc(arc, arc.Center, arc.Radius, arc.StartAngle, arc.Sweep, p.Point, boundaries);
                break;
            case CircleEntity circle:
                TrimArc(circle, circle.Center, circle.Radius, 0, Curve.TwoPi, p.Point, boundaries);
                break;
            case PolylineEntity:
                Session.Post("Polylines cannot be trimmed yet. Use EXPLODE (X) to turn the polyline into lines and arcs first.");
                break;
            default:
                Session.Post("That kind of object cannot be trimmed.");
                break;
        }
        return CommandResult.Continue;
    }

    private void TrimLine(LineEntity line, Point2D pick, List<Curve> boundaries)
    {
        var d = line.End - line.Start;
        double length2 = d.LengthSquared;
        if (length2 <= 0) return;
        double At(Point2D q) => (q - line.Start).Dot(d) / length2;
        double tolerance = 1e-9;

        double clicked = Math.Clamp(At(pick), 0, 1);
        double? below = null, above = null;
        foreach (var piece in boundaries)
            foreach (var x in piece.MeetLine(line.Start, d))
            {
                double t = At(x);
                if (t <= tolerance || t >= 1 - tolerance) continue;          // crossings at the very ends do not cut
                if (t <= clicked && (below is null || t > below)) below = t;
                if (t >= clicked && (above is null || t < above)) above = t;
            }

        if (below is null && above is null)
        {
            Session.Post("Nothing crosses that line, so there is nothing to trim it to. Use ERASE to remove it.");
            return;
        }

        var parts = new List<Entity>();
        if (below is { } lo) parts.Add(new LineEntity(line.Id, line.Start, line.Start + d * lo));
        if (above is { } hi) parts.Add(new LineEntity(parts.Count == 0 ? line.Id : Guid.NewGuid(), line.Start + d * hi, line.End));
        Commit(line, parts);
    }

    private void TrimArc(Entity source, Point2D center, double radius, double start, double sweep, Point2D pick, List<Curve> boundaries)
    {
        bool full = sweep >= Curve.TwoPi - 1e-9;
        double Along(Point2D q) => ArcMath.NormalizeAngle((q - center).Angle - start);     // 0..2pi from the start
        double tolerance = 1e-9;

        var cuts = new List<double>();
        foreach (var piece in boundaries)
            foreach (var x in piece.MeetCircle(center, radius))
            {
                double a = Along(x);
                if (full || (a > tolerance && a < sweep - tolerance)) cuts.Add(a);
            }

        double clicked = Along(pick);
        if (!full) clicked = clicked > sweep ? (clicked - sweep < Curve.TwoPi - clicked ? sweep : 0) : clicked;

        if (full)
        {
            var distinct = cuts.OrderBy(a => a).ToList();
            for (int i = distinct.Count - 1; i > 0; i--) if (distinct[i] - distinct[i - 1] < 1e-7) distinct.RemoveAt(i);
            if (distinct.Count >= 2 && distinct[^1] - distinct[0] > Curve.TwoPi - 1e-7) distinct.RemoveAt(distinct.Count - 1);
            if (distinct.Count < 2)
            {
                Session.Post("A circle needs two crossings to be trimmed between.");
                return;
            }
            // The removed part runs from the last cut before the click round to the first cut after it.
            double before = distinct.Where(a => a <= clicked).DefaultIfEmpty(distinct[^1]).Max();
            double after = distinct.Where(a => a >= clicked).DefaultIfEmpty(distinct[0]).Min();
            if (distinct.All(a => a > clicked)) before = distinct[^1];
            if (distinct.All(a => a < clicked)) after = distinct[0];
            var kept = Inherit(source, new ArcEntity(source.Id, center, radius, start + after, start + before));
            if (!kept.IsValid) { Session.Post("That would leave nothing of the circle."); return; }
            Session.Execute(new CompositeAction("TRIM",
                new RemoveEntitiesAction("TRIM", new[] { source.Id }),
                new AddEntitiesAction("TRIM", new Entity[] { kept })));
            return;
        }

        double? below = null, above = null;
        foreach (double a in cuts)
        {
            if (a <= clicked && (below is null || a > below)) below = a;
            if (a >= clicked && (above is null || a < above)) above = a;
        }
        if (below is null && above is null)
        {
            Session.Post("Nothing crosses that arc, so there is nothing to trim it to. Use ERASE to remove it.");
            return;
        }

        var parts = new List<Entity>();
        if (below is { } lo) parts.Add(new ArcEntity(source.Id, center, radius, start, start + lo));
        if (above is { } hi) parts.Add(new ArcEntity(parts.Count == 0 ? source.Id : Guid.NewGuid(), center, radius, start + hi, start + sweep));
        Commit(source, parts);
    }

    /// <summary>Replaces <paramref name="source"/> by its kept parts: the first keeps the id, a second is a new object.</summary>
    private void Commit(Entity source, List<Entity> parts)
    {
        foreach (var part in parts) Inherit(source, part);
        if (parts.Any(part => !part.IsValid)) { Session.Post("That trim would leave a zero-length piece."); return; }

        var modify = new ModifyEntitiesAction("TRIM", new[] { source }, new[] { parts[0] });
        Session.Execute(parts.Count == 1
            ? modify
            : new CompositeAction("TRIM", modify, new AddEntitiesAction("TRIM", new[] { parts[1] })));
    }
}

/// <summary>
/// EXTEND (quick mode): click near the end of a line or arc; that end is lengthened to the first object
/// it meets. Each extension is one undo step.
/// </summary>
public sealed class ExtendCommand : PickCommandBase
{
    public override string Name => "EXTEND";
    public override string Prompt => "EXTEND  Click near the end to extend, or Enter to finish:";

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.PointInput p) return Invalid("Click near the end of a line or arc, or press Enter to finish.");
        var entity = PickEntity(p.Point);
        if (entity is null) return CommandResult.Continue;

        var boundaries = BoundaryPieces(entity);
        Entity? longer = entity switch
        {
            LineEntity line => ExtendLine(line, p.Point, boundaries),
            ArcEntity arc => ExtendArc(arc, p.Point, boundaries),
            CircleEntity => null,
            _ => null,
        };

        if (entity is PolylineEntity)
            Session.Post("Polylines cannot be extended yet. Use EXPLODE (X) to turn the polyline into lines and arcs first.");
        else if (entity is not (LineEntity or ArcEntity))
            Session.Post("Only lines and arcs can be extended.");
        else if (longer is null)
            Session.Post("That end does not meet any object when extended.");
        else
            Session.Execute(new ModifyEntitiesAction("EXTEND", new[] { entity }, new[] { longer }));
        return CommandResult.Continue;
    }

    private static Entity? ExtendLine(LineEntity line, Point2D pick, List<Curve> boundaries)
    {
        var d = line.End - line.Start;
        double length2 = d.LengthSquared;
        if (length2 <= 0) return null;
        double At(Point2D q) => (q - line.Start).Dot(d) / length2;
        bool fromEnd = At(pick) >= 0.5;
        double tolerance = 1e-9;

        double? best = null;
        foreach (var piece in boundaries)
            foreach (var x in piece.MeetLine(line.Start, d))
            {
                double t = At(x);
                if (fromEnd ? t > 1 + tolerance && (best is null || t < best) : t < -tolerance && (best is null || t > best)) best = t;
            }
        if (best is not { } target) return null;

        var result = (LineEntity)line.Clone();
        if (fromEnd) result.End = line.Start + d * target; else result.Start = line.Start + d * target;
        return result.IsValid ? result : null;
    }

    private static Entity? ExtendArc(ArcEntity arc, Point2D pick, List<Curve> boundaries)
    {
        double sweep = arc.Sweep;
        double Along(Point2D q) => ArcMath.NormalizeAngle((q - arc.Center).Angle - arc.StartAngle);
        bool fromEnd = pick.DistanceTo(arc.EndPoint) <= pick.DistanceTo(arc.StartPoint);
        double tolerance = 1e-7;

        // Beyond the end the arc can grow counter-clockwise up to (not including) a full circle;
        // beyond the start it grows clockwise, which is the same range seen from the other side.
        double? best = null;
        foreach (var piece in boundaries)
            foreach (var x in piece.MeetCircle(arc.Center, arc.Radius))
            {
                double a = Along(x);
                if (a <= sweep + tolerance || a >= Curve.TwoPi - tolerance) continue;
                if (fromEnd ? best is null || a < best : best is null || a > best) best = a;
            }
        if (best is not { } target) return null;

        var result = (ArcEntity)arc.Clone();
        if (fromEnd) result.EndAngle = ArcMath.NormalizeAngle(arc.StartAngle + target);
        else result.StartAngle = ArcMath.NormalizeAngle(arc.StartAngle + target);
        return result.IsValid ? result : null;
    }
}

/// <summary>Shared two-line corner logic of FILLET and CHAMFER.</summary>
public abstract class CornerCommandBase : PickCommandBase
{
    private LineEntity? _first;
    private Point2D _firstPick;
    protected int ValueStage;        // 0 = picking lines; otherwise the command is asking for a value

    public override bool PickingObject => ValueStage == 0;

    protected bool HasFirst => _first is not null;

    public override CommandResult OnInput(CommandInput input)
    {
        if (ValueStage != 0) return OnValue(input);
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is CommandInput.KeywordInput k) return OnOption(k.Keyword);
        if (input is not CommandInput.PointInput p) return Invalid("Click on a line.");

        var entity = PickEntity(p.Point);
        if (entity is null) return CommandResult.Continue;
        if (entity is not LineEntity line)
            return Invalid(entity is PolylineEntity
                ? $"{Name} works on lines for now. Use EXPLODE (X) to turn the polyline into lines first."
                : $"{Name} works on two lines for now.");

        if (_first is null)
        {
            _first = line;
            _firstPick = p.Point;
            return CommandResult.Continue;
        }
        if (line.Id == _first.Id) return Invalid("Click on a different line for the second one.");

        var before = new Entity[] { _first, line };
        if (!TryCorner(_first, _firstPick, line, p.Point, out var a, out var b, out var joint, out string? problem))
            return Invalid(problem ?? "Those lines cannot be joined.");

        var modify = new ModifyEntitiesAction(Name, before, new Entity[] { a, b });
        Session.Execute(joint is null
            ? modify
            : new CompositeAction(Name, modify, new AddEntitiesAction(Name, new[] { Inherit(_first, joint) })));
        return CommandResult.Finished;
    }

    protected abstract CommandResult OnOption(string keyword);
    protected abstract CommandResult OnValue(CommandInput input);

    /// <summary>Distances from the corner along the first and the second line at which the joint starts.</summary>
    protected abstract (double First, double Second) Setbacks(double cornerAngle);

    /// <summary>The joining object between the two tangent points (null for a sharp corner).</summary>
    protected abstract Entity? MakeJoint(Point2D corner, Point2D onFirst, Point2D onSecond, Vector2D alongFirst, Vector2D alongSecond);

    private bool TryCorner(LineEntity first, Point2D firstPick, LineEntity second, Point2D secondPick,
        out LineEntity a, out LineEntity b, out Entity? joint, out string? problem)
    {
        a = first; b = second; joint = null; problem = null;
        Vector2D d1 = first.End - first.Start, d2 = second.End - second.Start;
        double denom = d1.Cross(d2);
        if (Math.Abs(denom) <= 1e-12 * d1.Length * d2.Length) { problem = "The lines are parallel."; return false; }
        var corner = first.Start + d1 * ((second.Start - first.Start).Cross(d2) / denom);

        // Each line keeps the side of the corner that was clicked.
        var u1 = KeptDirection(first, firstPick, corner);
        var u2 = KeptDirection(second, secondPick, corner);
        double angle = Math.Acos(Math.Clamp(u1.Dot(u2), -1.0, 1.0));
        if (angle <= 1e-9 || angle >= Math.PI - 1e-9) { problem = "The lines are parallel."; return false; }

        var (s1, s2) = Setbacks(angle);
        Point2D t1 = corner + u1 * s1, t2 = corner + u2 * s2;
        if (!TryReplaceNearEnd(first, corner, u1, s1, t1, out a) || !TryReplaceNearEnd(second, corner, u2, s2, t2, out b))
        {
            problem = "The lines are too short for that size. Set a smaller one first.";
            return false;
        }
        joint = s1 > 1e-12 || s2 > 1e-12 ? MakeJoint(corner, t1, t2, u1, u2) : null;
        return joint is null || joint.IsValid;
    }

    private static Vector2D KeptDirection(LineEntity line, Point2D pick, Point2D corner)
    {
        var d = (line.End - line.Start).Normalized();
        double clicked = (pick - corner).Dot(d);
        if (Math.Abs(clicked) > 1e-9) return clicked > 0 ? d : -d;
        // Clicked right at the corner: keep the longer side.
        return (line.End - corner).Dot(d) >= -(line.Start - corner).Dot(d) ? d : -d;
    }

    /// <summary>Moves the end of <paramref name="line"/> that is nearer the corner to <paramref name="target"/>.</summary>
    private static bool TryReplaceNearEnd(LineEntity line, Point2D corner, Vector2D kept, double setback, Point2D target, out LineEntity result)
    {
        result = (LineEntity)line.Clone();
        double start = (line.Start - corner).Dot(kept), end = (line.End - corner).Dot(kept);
        bool farIsEnd = end >= start;
        double far = farIsEnd ? end : start;
        if (far <= setback + 1e-9) return false;          // nothing of the line would be left
        if (farIsEnd) result.Start = target; else result.End = target;
        return result.IsValid;
    }
}

/// <summary>FILLET: rounds the corner between two lines with an arc of the current radius (0 = sharp corner).</summary>
public sealed class FilletCommand : CornerCommandBase
{
    public override string Name => "FILLET";

    private string RadiusText => UnitFormatter.FormatLength(Session.FilletRadius, Document.Settings);

    public override string Prompt => ValueStage != 0
        ? $"FILLET  Specify fillet radius <{RadiusText}>:"
        : HasFirst ? "FILLET  Select second line:" : $"FILLET  (radius = {RadiusText})  Select first line or [Radius]:";

    public override IReadOnlyList<string> Options => ValueStage == 0 && !HasFirst ? new[] { "Radius" } : Array.Empty<string>();

    protected override CommandResult OnOption(string keyword)
    {
        if (!HasFirst && Matches(keyword, "RADIUS")) { ValueStage = 1; return CommandResult.Continue; }
        return Invalid("Invalid option.");
    }

    protected override CommandResult OnValue(CommandInput input)
    {
        if (input is CommandInput.NumberInput n && n.Value >= 0) Session.FilletRadius = n.Value;
        else if (input is not CommandInput.EnterInput) return Invalid("Type the radius (0 or more).");
        ValueStage = 0;
        return CommandResult.Continue;
    }

    protected override (double First, double Second) Setbacks(double cornerAngle)
    {
        double t = Session.FilletRadius / Math.Tan(cornerAngle / 2.0);
        return (t, t);
    }

    protected override Entity? MakeJoint(Point2D corner, Point2D onFirst, Point2D onSecond, Vector2D alongFirst, Vector2D alongSecond)
    {
        double radius = Session.FilletRadius;
        var bisector = (alongFirst + alongSecond).Normalized();
        double angle = Math.Acos(Math.Clamp(alongFirst.Dot(alongSecond), -1.0, 1.0));
        var center = corner + bisector * (radius / Math.Sin(angle / 2.0));
        double a1 = (onFirst - center).Angle, a2 = (onSecond - center).Angle;
        // The fillet is the short way round between the two tangent points.
        return ArcMath.Sweep(a1, a2) <= Math.PI
            ? new ArcEntity(center, radius, a1, a2)
            : new ArcEntity(center, radius, a2, a1);
    }
}

/// <summary>CHAMFER: cuts the corner between two lines with a straight line at the current distances.</summary>
public sealed class ChamferCommand : CornerCommandBase
{
    public override string Name => "CHAMFER";

    private string DistanceText =>
        UnitFormatter.FormatLength(Session.ChamferDistances.First, Document.Settings) + ", " +
        UnitFormatter.FormatLength(Session.ChamferDistances.Second, Document.Settings);

    public override string Prompt => ValueStage switch
    {
        1 => $"CHAMFER  Specify first chamfer distance <{UnitFormatter.FormatLength(Session.ChamferDistances.First, Document.Settings)}>:",
        2 => $"CHAMFER  Specify second chamfer distance <{UnitFormatter.FormatLength(Session.ChamferDistances.Second, Document.Settings)}>:",
        _ => HasFirst ? "CHAMFER  Select second line:" : $"CHAMFER  (distances = {DistanceText})  Select first line or [Distance]:",
    };

    public override IReadOnlyList<string> Options => ValueStage == 0 && !HasFirst ? new[] { "Distance" } : Array.Empty<string>();

    protected override CommandResult OnOption(string keyword)
    {
        if (!HasFirst && Matches(keyword, "DISTANCE")) { ValueStage = 1; return CommandResult.Continue; }
        return Invalid("Invalid option.");
    }

    protected override CommandResult OnValue(CommandInput input)
    {
        var current = Session.ChamferDistances;
        if (input is CommandInput.NumberInput n && n.Value >= 0)
        {
            // The second distance defaults to the first, as usual for chamfers.
            Session.ChamferDistances = ValueStage == 1 ? (n.Value, n.Value) : (current.First, n.Value);
        }
        else if (input is not CommandInput.EnterInput) return Invalid("Type the distance (0 or more).");
        ValueStage = ValueStage == 1 ? 2 : 0;
        return CommandResult.Continue;
    }

    protected override (double First, double Second) Setbacks(double cornerAngle) => Session.ChamferDistances;

    protected override Entity? MakeJoint(Point2D corner, Point2D onFirst, Point2D onSecond, Vector2D alongFirst, Vector2D alongSecond) =>
        new LineEntity(onFirst, onSecond);
}

/// <summary>EXPLODE: turns the selected polylines into separate lines and arcs (width is lost, as it belongs to polylines).</summary>
public sealed class ExplodeCommand : SelectionCommandBase
{
    public override string Name => "EXPLODE";
    public override string Prompt => SelectPrompt;

    protected override CommandResult OnSelectionReady()
    {
        var polylines = Session.GetSelectedEntities().OfType<PolylineEntity>().ToList();
        if (polylines.Count == 0)
        {
            Session.Post("Nothing to explode: select one or more polylines (rectangles are polylines).");
            return CommandResult.Finished;
        }

        var parts = new List<Entity>();
        foreach (var polyline in polylines)
            for (int i = 0; i < polyline.SegmentCount; i++)
            {
                var (a, b) = polyline.GetSegment(i);
                Entity part;
                if (polyline.TryGetArc(i, out var c, out double r, out double start, out double sweep))
                    part = new ArcEntity(c, r, start, start + sweep);
                else
                {
                    if (a.NearlyEquals(b)) continue;
                    part = new LineEntity(a, b);
                }
                part.LayerId = polyline.LayerId;
                part.Color = polyline.Color;
                part.Linetype = polyline.Linetype;
                part.LineweightMm = polyline.LineweightMm;
                parts.Add(part);
            }

        if (Session.Execute(new CompositeAction("EXPLODE",
                new RemoveEntitiesAction("EXPLODE", polylines.Select(p => p.Id).ToList()),
                new AddEntitiesAction("EXPLODE", parts))))
        {
            Session.ClearSelection();
            Session.Post($"{polylines.Count} polyline(s) exploded into {parts.Count} object(s).");
        }
        return CommandResult.Finished;
    }

    protected override CommandResult OnInputAfterSelection(CommandInput input) => CommandResult.Finished;
}
