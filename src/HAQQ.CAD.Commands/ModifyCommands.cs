using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>
/// Base for commands that act on a selection. If objects are already selected when the command
/// starts they are used (noun-verb); otherwise the command collects a selection first (verb-noun).
/// </summary>
public abstract class SelectionCommandBase : CadCommandBase
{
    protected bool Selecting { get; private set; }

    public override bool WantsSelection => Selecting;

    protected override CommandResult OnStart()
    {
        if (Session.Selection.Count > 0) return OnSelectionReady();
        Selecting = true;
        return CommandResult.Continue;
    }

    public sealed override CommandResult OnInput(CommandInput input)
    {
        if (!Selecting) return OnInputAfterSelection(input);

        if (input is CommandInput.EnterInput)
        {
            if (Session.Selection.Count == 0)
            {
                Session.Post("No objects selected.");
                return CommandResult.Finished;
            }
            Selecting = false;
            return OnSelectionReady();
        }
        return Invalid("Select objects, then press Enter (type ALL to select everything).");
    }

    protected string SelectPrompt => $"{Name}  Select objects ({Session.Selection.Count} selected), Enter to continue:";

    protected abstract CommandResult OnSelectionReady();
    protected abstract CommandResult OnInputAfterSelection(CommandInput input);
}

/// <summary>ERASE: deletes the selected objects.</summary>
public sealed class EraseCommand : SelectionCommandBase
{
    public override string Name => "ERASE";
    public override string Prompt => SelectPrompt;

    protected override CommandResult OnSelectionReady()
    {
        var ids = Session.Selection.Ids.ToList();
        if (Session.Execute(new RemoveEntitiesAction("ERASE", ids)))
            Session.Post($"{ids.Count} object(s) erased.");
        return CommandResult.Finished;
    }

    protected override CommandResult OnInputAfterSelection(CommandInput input) => CommandResult.Finished;
}

/// <summary>Shared base-point / second-point logic of MOVE and COPY.</summary>
public abstract class DisplaceCommandBase : SelectionCommandBase
{
    private const int MaxPreviewEntities = 400;

    private List<Entity> _targets = new();
    protected Point2D? BasePoint { get; private set; }

    public override Point2D? LastPoint => BasePoint;

    public override string Prompt => Selecting
        ? SelectPrompt
        : BasePoint is null ? $"{Name}  Specify base point:" : SecondPointPrompt;

    protected abstract string SecondPointPrompt { get; }

    /// <summary>Applies the displacement. Return Finished to end the command.</summary>
    protected abstract CommandResult Apply(IReadOnlyList<Entity> targets, Vector2D displacement);

    protected override CommandResult OnSelectionReady()
    {
        _targets = Session.GetSelectedEntities();
        return _targets.Count == 0 ? CommandResult.Finished : CommandResult.Continue;
    }

    protected override CommandResult OnInputAfterSelection(CommandInput input)
    {
        switch (input)
        {
            case CommandInput.PointInput p when BasePoint is null:
                BasePoint = p.Point;
                return CommandResult.Continue;

            case CommandInput.PointInput p:
                return Displace(p.Point - BasePoint!.Value);

            case CommandInput.NumberInput n when BasePoint is not null:
                if (n.Value <= 0) return Invalid("Distance must be positive.");
                return Displace(Session.PointFromDistance(BasePoint.Value, n.Value) - BasePoint.Value);

            case CommandInput.EnterInput:
                return CommandResult.Finished;

            default:
                return Invalid("Specify a point (click or type coordinates).");
        }
    }

    private CommandResult Displace(Vector2D displacement)
    {
        // Re-read the targets: an earlier step of this command may have replaced them.
        var current = new List<Entity>(_targets.Count);
        foreach (var t in _targets)
            if (Document.GetEntity(t.Id) is { } e) current.Add(e);
        if (current.Count == 0) return CommandResult.Finished;
        return Apply(current, displacement);
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        if (BasePoint is not { } basePoint || _targets.Count == 0) return Array.Empty<Entity>();
        var transform = Transform2D.Translation(cursor - basePoint);
        var preview = new List<Entity>(Math.Min(_targets.Count, MaxPreviewEntities) + 1);
        foreach (var t in _targets)
        {
            if (preview.Count >= MaxPreviewEntities) break;
            if (Document.GetEntity(t.Id) is not { } live) continue;
            var copy = live.Clone();
            copy.Transform(transform);
            preview.Add(copy);
        }
        if (!cursor.NearlyEquals(basePoint)) preview.Add(new LineEntity(basePoint, cursor));
        return preview;
    }
}

/// <summary>MOVE: translates the selection from a base point to a second point.</summary>
public sealed class MoveCommand : DisplaceCommandBase
{
    public override string Name => "MOVE";
    protected override string SecondPointPrompt => "MOVE  Specify second point (or a distance):";

    protected override CommandResult Apply(IReadOnlyList<Entity> targets, Vector2D displacement)
    {
        var transform = Transform2D.Translation(displacement);
        var after = new List<Entity>(targets.Count);
        foreach (var t in targets)
        {
            var copy = t.Clone();
            copy.Transform(transform);
            after.Add(copy);
        }
        Session.Execute(new ModifyEntitiesAction("MOVE", targets, after));
        return CommandResult.Finished;
    }
}

/// <summary>COPY: places translated copies (with new ids) until Enter is pressed.</summary>
public sealed class CopyCommand : DisplaceCommandBase
{
    public override string Name => "COPY";
    protected override string SecondPointPrompt => "COPY  Specify second point (Enter to finish):";

    protected override CommandResult Apply(IReadOnlyList<Entity> targets, Vector2D displacement)
    {
        var transform = Transform2D.Translation(displacement);
        var copies = new List<Entity>(targets.Count);
        foreach (var t in targets)
        {
            var copy = t.CopyWithNewId();
            copy.Transform(transform);
            copies.Add(copy);
        }
        Session.Execute(new AddEntitiesAction("COPY", copies));
        return CommandResult.Continue;
    }
}
