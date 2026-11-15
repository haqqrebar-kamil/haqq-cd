using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>One grip of one entity.</summary>
public readonly record struct GripRef(Guid EntityId, int Index);

/// <summary>
/// Grip stretch: started by clicking a grip of a selected entity; the next point is where that grip goes.
/// Every selected entity with a grip at the clicked position is stretched together, so lines that meet at a
/// corner stay joined. One undo step. Esc or Enter leaves everything unchanged.
/// </summary>
public sealed class GripStretchCommand : CadCommandBase
{
    private readonly IReadOnlyList<GripRef> _grips;
    private readonly Point2D _from;

    public GripStretchCommand(IReadOnlyList<GripRef> grips, Point2D from)
    {
        _grips = grips;
        _from = from;
    }

    public override string Name => "STRETCH";
    public override string Prompt => "** STRETCH **  Specify stretch point:";
    public override Point2D? LastPoint => _from;

    public override CommandResult OnInput(CommandInput input)
    {
        switch (input)
        {
            case CommandInput.PointInput p:
                return Apply(p.Point);
            case CommandInput.NumberInput n:
                if (n.Value <= 0) return Invalid("Distance must be positive.");
                return Apply(Session.PointFromDistance(_from, n.Value));
            case CommandInput.EnterInput:
                return CommandResult.Finished;
            default:
                return Invalid("Specify a point (click or type coordinates).");
        }
    }

    private CommandResult Apply(Point2D target)
    {
        if (target.NearlyEquals(_from)) return CommandResult.Finished;   // clicked the grip again: nothing to do
        var before = new List<Entity>();
        var after = Stretched(target, before);
        if (after.Count == 0) return Invalid("That point does not give a valid object.");
        Session.Execute(new ModifyEntitiesAction("Stretch", before, after));
        return CommandResult.Finished;
    }

    /// <summary>Stretched copies of the gripped entities; entities that cannot take the new point are left out.</summary>
    private List<Entity> Stretched(Point2D target, List<Entity>? originals = null)
    {
        var result = new List<Entity>(_grips.Count);
        foreach (var group in _grips.GroupBy(g => g.EntityId))
        {
            if (Document.GetEntity(group.Key) is not { } entity) continue;
            var copy = entity.Clone();
            bool changed = false;
            foreach (var grip in group) changed |= EntityGrips.Stretch(copy, grip.Index, target);
            if (!changed || !copy.IsValid) continue;
            originals?.Add(entity);
            result.Add(copy);
        }
        return result;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        if (cursor.NearlyEquals(_from)) return Array.Empty<Entity>();
        var preview = Stretched(cursor);
        preview.Add(new LineEntity(_from, cursor));
        return preview;
    }
}
