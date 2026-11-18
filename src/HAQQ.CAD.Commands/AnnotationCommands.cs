using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>How the next text is made: remembered from the last text placed or edited.</summary>
public sealed record TextSettings(double Height, string Font, bool Bold, bool Italic, bool Underline, TextJustify Justify, double AngleDegrees);

/// <summary>
/// Places an object that was made ready elsewhere (the text or the table from its window): it follows the
/// cursor until a click, or typed coordinates, says where it goes.
/// </summary>
public sealed class PlaceObjectCommand : CadCommandBase, ICreatesObjects
{
    private readonly string _name;
    private readonly string _prompt;
    private readonly Func<Point2D, Entity> _build;

    public PlaceObjectCommand(string name, string prompt, Func<Point2D, Entity> build)
    {
        _name = name;
        _prompt = prompt;
        _build = build;
    }

    public override string Name => _name;
    public override string Prompt => $"{_name}  {_prompt}";

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.PointInput p) return Invalid("Click a point, or type coordinates such as 0,0.");
        var entity = Prepare(_build(p.Point));
        if (!entity.IsValid) return Invalid("That cannot be placed there.");
        return Session.Execute(new AddEntitiesAction(_name, new[] { entity })) ? CommandResult.Finished : CommandResult.Continue;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor) => new[] { _build(cursor) };
}

/// <summary>
/// -TEXT: one line of text from the command line, without the text window. A point, a height (Enter keeps the
/// height in use) and the words. Font, bold and the rest are those last used.
/// </summary>
public sealed class TypedTextCommand : CadCommandBase, IRawTextCommand, ICreatesObjects
{
    private Point2D? _at;
    private double? _height;

    public override string Name => "-TEXT";
    public bool WantsRawText => _height is not null;

    public override string Prompt =>
        _at is null ? "-TEXT  Click where the text starts:"
        : _height is null ? $"-TEXT  Letter height <{UnitFormatter.FormatLength(Session.TextDefaults.Height, Document.Settings)}>:"
        : "-TEXT  Type the text:";

    public override CommandResult OnInput(CommandInput input)
    {
        if (_at is null)
        {
            if (input is CommandInput.EnterInput) return CommandResult.Finished;
            if (input is not CommandInput.PointInput p) return Invalid("Click a point, or type coordinates such as 0,0.");
            _at = p.Point;
            return CommandResult.Continue;
        }
        if (_height is null)
        {
            switch (input)
            {
                case CommandInput.EnterInput:
                    _height = Session.TextDefaults.Height;
                    return CommandResult.Continue;
                case CommandInput.NumberInput n when n.Value > GeoMath.Epsilon:
                    _height = n.Value;
                    return CommandResult.Continue;
                case CommandInput.PointInput second when second.Point.DistanceTo(_at.Value) > GeoMath.Epsilon:
                    _height = second.Point.DistanceTo(_at.Value);          // shown by a second click
                    return CommandResult.Continue;
                default:
                    return Invalid("Type a height more than 0, or press Enter to keep the one shown.");
            }
        }
        return CommandResult.Finished;                                    // Enter with nothing typed
    }

    public CommandResult OnRawText(string text)
    {
        if (text.Trim().Length == 0) return CommandResult.Finished;
        var defaults = Session.TextDefaults with { Height = _height!.Value };
        var entity = Prepare(EditorSession.MakeText(_at!.Value, text.Trim(), defaults with { Justify = TextJustify.Left, AngleDegrees = 0 }));
        if (!entity.IsValid) return Invalid("That text cannot be used.");
        if (!Session.Execute(new AddEntitiesAction("TEXT", new[] { entity }))) return CommandResult.Continue;
        Session.TextDefaults = defaults;
        return CommandResult.Finished;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor) => Array.Empty<Entity>();
}

/// <summary>TEXTEDIT: click a text or a table to open it for editing (the same as a double-click on it).</summary>
public sealed class EditAnnotationCommand : CadCommandBase, IObjectPicker
{
    public override string Name => "TEXTEDIT";
    public bool PickingObject => true;
    public override string Prompt => "TEXTEDIT  Click the text or the table to edit:";

    protected override CommandResult OnStart()
    {
        // Something editable is already selected: open it straight away.
        var selected = Session.GetSelectedEntities();
        if (selected.Count == 1 && selected[0] is TextEntity or TableEntity && Session.RequestEdit(selected[0].Id)) return CommandResult.Finished;
        return CommandResult.Continue;
    }

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.PointInput p) return Invalid("Click a text or a table.");
        var picked = SelectionService.PickAt(Document, p.Point, Session.PickTolerance);
        if (picked is not (TextEntity or TableEntity)) return Invalid("That is not a text or a table.");
        return Session.RequestEdit(picked.Id) ? CommandResult.Finished : CommandResult.Continue;
    }
}
