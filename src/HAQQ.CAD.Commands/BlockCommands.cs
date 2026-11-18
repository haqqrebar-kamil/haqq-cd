using System.Globalization;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>
/// BLOCK: makes a block from the selected objects. Select them, click the base point (the point the block is
/// held by when it is placed), type a name. The objects are replaced by the block, in the same place.
/// </summary>
public sealed class BlockCommand : SelectionCommandBase, IRawTextCommand
{
    private Point2D? _base;

    public override string Name => "BLOCK";
    public bool WantsRawText => _base is not null;

    public override string Prompt =>
        Selecting ? SelectPrompt
        : _base is null ? "BLOCK  Click the base point of the block (the point it is placed by):"
        : "BLOCK  Type a name for the block:";

    protected override CommandResult OnSelectionReady() => CommandResult.Continue;

    protected override CommandResult OnInputAfterSelection(CommandInput input)
    {
        if (_base is not null) return CommandResult.Finished;                 // Enter with no name: give up
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.PointInput p) return Invalid("Click the base point, or type its coordinates.");
        _base = p.Point;
        return CommandResult.Continue;
    }

    public CommandResult OnRawText(string text)
    {
        if (text.Trim().Length == 0) return CommandResult.Finished;
        return Session.CreateBlock(text, _base!.Value, Session.GetSelectedEntities()) ? CommandResult.Finished : CommandResult.Continue;
    }
}

/// <summary>
/// Places a block, again and again until Enter: it follows the cursor, a click puts one down. Rotate and Scale
/// change the angle and the size of the ones placed after.
/// </summary>
public sealed class InsertBlockCommand : CadCommandBase, ICreatesObjects
{
    private readonly BlockDefinition _block;
    private double _scale, _rotationDegrees;
    private string? _asking;                        // "R" or "S" while a number is wanted
    private int _placed;

    public InsertBlockCommand(BlockDefinition block, double scale, double rotationDegrees)
    {
        _block = block;
        _scale = scale;
        _rotationDegrees = rotationDegrees;
    }

    public override string Name => "INSERT";
    public override IReadOnlyList<string> Options => _asking is null ? new[] { "Rotate", "Scale" } : Array.Empty<string>();

    public override string Prompt => _asking switch
    {
        "R" => $"INSERT  Angle in degrees <{_rotationDegrees.ToString("0.###", CultureInfo.InvariantCulture)}>:",
        "S" => $"INSERT  Scale (1 = the size it was made) <{_scale.ToString("0.###", CultureInfo.InvariantCulture)}>:",
        _ => $"INSERT  Click where \"{_block.Name}\" goes [Rotate/Scale], Enter to finish:",
    };

    private BlockReferenceEntity Build(Point2D at) =>
        new(Document.FindBlock(_block.Name) ?? _block, at) { Scale = _scale, Rotation = _rotationDegrees * Math.PI / 180.0 };

    public override CommandResult OnInput(CommandInput input)
    {
        if (_asking is not null)
        {
            if (input is CommandInput.NumberInput n)
            {
                if (_asking == "S")
                {
                    if (!(n.Value > 1e-6) || !double.IsFinite(n.Value)) return Invalid("The scale must be more than 0.");
                    _scale = n.Value;
                }
                else _rotationDegrees = n.Value;
            }
            else if (input is not CommandInput.EnterInput) return Invalid("Type a number, or press Enter to keep the value shown.");
            _asking = null;
            return CommandResult.Continue;
        }

        switch (input)
        {
            case CommandInput.EnterInput:
                return CommandResult.Finished;
            case CommandInput.KeywordInput k when Matches(k.Keyword, "ROTATE"):
                _asking = "R";
                return CommandResult.Continue;
            case CommandInput.KeywordInput k when Matches(k.Keyword, "SCALE"):
                _asking = "S";
                return CommandResult.Continue;
            case CommandInput.PointInput p:
            {
                var reference = Prepare(Build(p.Point));
                if (!reference.IsValid) return Invalid("The block cannot be placed there.");
                var add = new AddEntitiesAction("INSERT", new Entity[] { reference });
                // The first use of a library block brings the block itself into the drawing, in the same undo step.
                bool known = Document.FindBlock(_block.Name) is not null;
                if (Session.Execute(known ? add : new CompositeAction("INSERT", new AddBlockAction(_block), add))) _placed++;
                return CommandResult.Continue;                                    // and the next one
            }
            default:
                return Invalid("Click a point, or type Rotate or Scale.");
        }
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor) =>
        _asking is null ? new Entity[] { Build(cursor) } : Array.Empty<Entity>();
}

/// <summary>-INSERT: places a block of the drawing, or of the HAQQ library, by typing its name.</summary>
public sealed class TypedInsertCommand : CadCommandBase, IRawTextCommand
{
    public override string Name => "-INSERT";
    public bool WantsRawText => true;
    public override string Prompt => "-INSERT  Type the name of the block:";
    public override CommandResult OnInput(CommandInput input) => CommandResult.Finished;

    public CommandResult OnRawText(string text)
    {
        string name = text.Trim();
        if (name.Length == 0) return CommandResult.Finished;
        var block = Document.FindBlock(name);
        if (block is null && StandardBlocks.All.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } standard)
            block = StandardBlocks.ForUnit(standard, Document.Settings.Unit);
        if (block is null) return Invalid($"There is no block named \"{name}\" in the drawing or in the HAQQ library. INSERT shows them all.");
        // Handing over to the placing command ends this one.
        Session.BeginBlockInsert(block, 1.0, 0.0);
        return CommandResult.Continue;
    }
}
