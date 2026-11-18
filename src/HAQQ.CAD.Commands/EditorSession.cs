using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Snapping;

namespace HAQQ.CAD.Commands;

public enum CommandResult
{
    /// <summary>The command needs more input.</summary>
    Continue,
    /// <summary>The command has ended.</summary>
    Finished,
}

/// <summary>An interactive, cancellable CAD command driven by points, numbers, keywords and Enter.</summary>
public interface ICadCommand
{
    string Name { get; }

    /// <summary>Text shown on the command line while the command waits for input.</summary>
    string Prompt { get; }

    /// <summary>True while the command is collecting a selection (clicks select instead of giving points).</summary>
    bool WantsSelection { get; }

    /// <summary>Reference point for relative coordinates, ORTHO and direct distance entry.</summary>
    Point2D? LastPoint { get; }

    /// <summary>Option keywords that are valid right now (shown in the right-click menu).</summary>
    IReadOnlyList<string> Options { get; }

    CommandResult Start(EditorSession session);
    CommandResult OnInput(CommandInput input);

    /// <summary>Called when the user presses Esc. The command may keep work that was already completed.</summary>
    void OnCancel();

    /// <summary>Transient geometry to draw as a preview for the given cursor position.</summary>
    IReadOnlyList<Entity> GetPreview(Point2D cursor);
}

/// <summary>
/// Implemented by commands for which "straight" does not mean horizontal or vertical. While Shift is
/// held the command shapes the cursor point itself (RECTANG makes a square); ORTHO and polar
/// tracking are not applied to it.
/// </summary>
public interface IShiftConstraint
{
    Point2D ConstrainWithShift(Point2D cursor);
}

/// <summary>
/// Implemented by commands that sometimes need the typed line exactly as it was typed (a name, a list of numbers)
/// instead of having it read as coordinates, a distance or an option.
/// </summary>
public interface IRawTextCommand
{
    bool WantsRawText { get; }
    CommandResult OnRawText(string text);
}

/// <summary>
/// Implemented by commands that can make a line tangent to a circle themselves once both ends are known
/// (LINE). While <see cref="WantsDeferredTangent"/> is true the Tangent snap works on a first point too.
/// </summary>
public interface ITangentAware
{
    bool WantsDeferredTangent { get; }
}

/// <summary>
/// Implemented by commands that ask the user to click on an object (TRIM, OFFSET, FILLET...). While
/// <see cref="PickingObject"/> is true the click is taken where the cursor is: object snap is not applied
/// and the pick box is shown at the crosshair.
/// </summary>
public interface IObjectPicker
{
    bool PickingObject { get; }
}

/// <summary>
/// A command that has taken an object by a click and still needs something else (OFFSET waiting for the side,
/// FILLET waiting for the second line). The object is shown highlighted so the user can see it was taken.
/// </summary>
public interface IPickedObjects
{
    IEnumerable<Guid> PickedObjects { get; }
}

/// <summary>A command that draws new objects (LINE, PLINE, RECTANG, CIRCLE, ARC): the Properties panel shows what the new object will get.</summary>
public interface ICreatesObjects { }

/// <summary>The properties a newly drawn object is given.</summary>
public sealed class NewObjectProperties
{
    /// <summary>Layer for the object; null means the drawing's current layer.</summary>
    public Guid? LayerId { get; set; }
    public CadColor Color { get; set; } = CadColor.ByLayer;
    public string Linetype { get; set; } = Linetypes.ByLayer;
    public double LinetypeScale { get; set; } = 1.0;
    /// <summary>Lineweight in millimetres; null means ByLayer.</summary>
    public double? LineweightMm { get; set; }

    public NewObjectProperties Clone() => (NewObjectProperties)MemberwiseClone();
}

/// <summary>
/// An active object snap tracking alignment: the cursor is locked onto a line through a point that was acquired
/// by hovering over an object snap, or onto the crossing of two such lines (or of one and the polar ray).
/// </summary>
/// <param name="Point">Where a click would land.</param>
/// <param name="Lines">The tracking lines in use: the acquired point each one starts at, and its angle in degrees.</param>
public sealed record ObjectTrack(Point2D Point, IReadOnlyList<(Point2D From, double AngleDegrees)> Lines);

/// <summary>An active polar tracking alignment: the cursor is locked onto a ray from <paramref name="From"/>.</summary>
public readonly record struct PolarTrack(Point2D From, double AngleDegrees, Point2D Point);

/// <summary>Maps command names and aliases to command factories.</summary>
/// <summary>One command as the Shortcuts window lists it.</summary>
/// <param name="Name">The full command name; it can always be typed.</param>
/// <param name="Description">What the command does, in a few words.</param>
/// <param name="DefaultShortcuts">The short names it comes with.</param>
public sealed record CommandInfo(string Name, string Description, IReadOnlyList<string> DefaultShortcuts);

public sealed class CommandRegistry
{
    private sealed record Entry(CommandInfo Info, Func<ICadCommand>? Factory);

    private readonly Dictionary<string, Entry> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();
    private readonly Dictionary<string, string> _shortcuts = new(StringComparer.OrdinalIgnoreCase);      // shortcut -> command name
    private readonly Dictionary<string, IReadOnlyList<string>> _custom = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a command. A null <paramref name="factory"/> is a command the session carries out itself (UNDO, ZOOM...).</summary>
    public void Register(string name, Func<ICadCommand>? factory, params string[] aliases) => Register(name, "", factory, aliases);

    public void Register(string name, string description, Func<ICadCommand>? factory, params string[] aliases)
    {
        if (!_commands.ContainsKey(name)) _order.Add(name);
        _commands[name] = new Entry(new CommandInfo(name, description, aliases), factory);
        Rebuild();
    }

    /// <summary>Every command, in the order of the ribbon tabs.</summary>
    public IReadOnlyList<CommandInfo> Commands => _order.Select(n => _commands[n].Info).ToList();

    /// <summary>The short names a command answers to now (its own, or the user's).</summary>
    public IReadOnlyList<string> ShortcutsOf(string name) =>
        _custom.TryGetValue(name, out var custom) ? custom
        : _commands.TryGetValue(name, out var entry) ? entry.Info.DefaultShortcuts : Array.Empty<string>();

    /// <summary>The user's own short names, by command, for the commands that differ from the defaults.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CustomShortcuts => _custom;

    /// <summary>The full command name for what was typed (a full name or a short name), or null.</summary>
    public string? Resolve(string nameOrAlias)
    {
        string key = nameOrAlias.Trim();
        if (_commands.TryGetValue(key, out var entry)) return entry.Info.Name;
        return _shortcuts.TryGetValue(key, out string? name) ? name : null;
    }

    /// <summary>
    /// Replaces the short names of the commands in <paramref name="custom"/> (a command that is not in it keeps its
    /// defaults). Nothing changes and the reason is returned when a short name is not usable or is given twice.
    /// </summary>
    public string? SetShortcuts(IReadOnlyDictionary<string, IReadOnlyList<string>> custom)
    {
        var wanted = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in custom)
        {
            if (!_commands.TryGetValue(pair.Key, out var entry)) continue;         // a command of another version: ignore
            var list = pair.Value.Select(a => a.Trim().ToUpperInvariant()).Where(a => a.Length > 0).Distinct().ToList();
            if (!list.SequenceEqual(entry.Info.DefaultShortcuts, StringComparer.OrdinalIgnoreCase)) wanted[entry.Info.Name] = list;
        }

        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in _order)
        {
            var list = wanted.TryGetValue(name, out var mine) ? mine : _commands[name].Info.DefaultShortcuts;
            foreach (string shortcut in list)
            {
                if (!IsValidShortcut(shortcut)) return $"\"{shortcut}\" cannot be a shortcut. Use letters, digits and - only, up to 12, with no spaces.";
                if (_commands.TryGetValue(shortcut, out var other) && !other.Info.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return $"\"{shortcut}\" is the name of the {other.Info.Name} command and cannot be a shortcut for {name}.";
                if (owner.TryGetValue(shortcut, out string? taken) && !taken.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return $"\"{shortcut}\" is given to both {taken} and {name}. A shortcut can start only one command.";
                owner[shortcut] = name;
            }
        }

        _custom.Clear();
        foreach (var pair in wanted) _custom[pair.Key] = pair.Value;
        Rebuild();
        return null;
    }

    public static bool IsValidShortcut(string text) =>
        text.Length is >= 1 and <= 12 && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private void Rebuild()
    {
        _shortcuts.Clear();
        foreach (string name in _order)
            foreach (string shortcut in ShortcutsOf(name))
                _shortcuts.TryAdd(shortcut, name);
    }

    public bool TryCreate(string nameOrAlias, out ICadCommand? command, out string canonicalName)
    {
        if (Resolve(nameOrAlias) is { } name && _commands[name].Factory is { } factory)
        {
            command = factory();
            canonicalName = name;
            return true;
        }
        command = null;
        canonicalName = "";
        return false;
    }

    public IEnumerable<string> CommandNames => _order.OrderBy(n => n);

    public static CommandRegistry CreateDefault()
    {
        var r = new CommandRegistry();
        // Draw
        r.Register("LINE", "Draw a line", () => new LineCommand(), "L");
        r.Register("PLINE", "Draw a polyline", () => new PolylineCommand(), "PL", "POLYLINE");
        r.Register("RECTANG", "Draw a rectangle", () => new RectangleCommand(), "REC", "RECTANGLE");
        r.Register("CIRCLE", "Draw a circle", () => new CircleCommand(), "C");
        r.Register("ARC", "Draw an arc", () => new ArcCommand(), "A");
        r.Register("GRIDLINES", "Grid line wizard: rectangular or sector, with labels", null, "GL", "GRIDWIZARD");
        r.Register("GRIDADD", "Add a grid line beside an existing one", () => new AddGridLineCommand(), "GA");
        // Modify
        r.Register("MOVE", "Move objects", () => new MoveCommand(), "M");
        r.Register("COPY", "Copy objects", () => new CopyCommand(), "CO", "CP");
        r.Register("ERASE", "Delete objects", () => new EraseCommand(), "E", "DELETE", "DEL");
        r.Register("ROTATE", "Rotate objects", () => new RotateCommand(), "RO");
        r.Register("SCALE", "Make objects larger or smaller", () => new ScaleCommand(), "SC");
        r.Register("MIRROR", "Mirror objects", () => new MirrorCommand(), "MI");
        r.Register("OFFSET", "Parallel copy at a distance", () => new OffsetCommand(), "O");
        r.Register("TRIM", "Cut an object back to where it meets another", () => new TrimCommand(), "TR");
        r.Register("EXTEND", "Lengthen an object to meet another", () => new ExtendCommand(), "EX");
        r.Register("FILLET", "Round a corner between two lines", () => new FilletCommand(), "F");
        r.Register("CHAMFER", "Cut a corner between two lines", () => new ChamferCommand(), "CHA");
        r.Register("EXPLODE", "Break a polyline into lines and arcs, or a block into its objects", () => new ExplodeCommand(), "X");
        r.Register("JOIN", "Join touching objects into a polyline", () => new JoinCommand(), "J");
        // Dimension
        r.Register("DIM", "Horizontal or vertical dimension", () => new LinearDimensionCommand(aligned: false), "DL", "DIMLIN");
        r.Register("DIMALIGN", "Aligned dimension", () => new LinearDimensionCommand(aligned: true), "DA");
        r.Register("DIMRAD", "Radius dimension", () => new RadialDimensionCommand(diameter: false), "DR");
        r.Register("DIMDIA", "Diameter dimension", () => new RadialDimensionCommand(diameter: true), "DD");
        r.Register("DIMANGLE", "Angle dimension", () => new AngularDimensionCommand(arcLength: false), "DG");
        r.Register("DIMARCLEN", "Arc length dimension", () => new AngularDimensionCommand(arcLength: true), "DARC");
        r.Register("DIMCHAIN", "Carry on dimensions end to end", () => new RunningDimensionCommand(stack: false), "DC");
        r.Register("DIMSTACK", "More dimensions from the same first point", () => new RunningDimensionCommand(stack: true), "DS");
        r.Register("DIMSTYLE", "Dimension settings and styles", null, "DST", "DIMSETTINGS");
        r.Register("DIMSIZE", "Text height of the current dimension style", () => new DimensionSizeCommand(), "DSIZE");
        // Layers and linetypes
        r.Register("LAYER", "Open the layer table", null, "LA", "LAYERS");
        r.Register("LINETYPE", "Open the Linetype Manager", null, "LT", "LTYPE");
        r.Register("-LINETYPE", "Linetypes on the command line", () => new LinetypeCommand(), "-LT", "-LTYPE");
        r.Register("LTSCALE", "Linetype scale of the drawing", () => new LinetypeScaleCommand(), "LTS");
        // View, undo and settings
        r.Register("ZOOM", "Zoom: All, Window, Center, In, Out", () => new ZoomCommand(), "Z");
        r.Register("ZOOMALL", "Zoom to show the whole drawing", null, "ZA", "ZE");
        r.Register("ZOOMWINDOW", "Zoom to a window you pick", () => new ZoomCommand("W"), "ZW");
        r.Register("ZOOMIN", "Zoom in one step", () => new ZoomCommand("I"), "ZI");
        r.Register("ZOOMOUT", "Zoom out one step", () => new ZoomCommand("O"), "ZO");
        r.Register("PAN", "Move the view by dragging", () => new PanCommand(), "P");
        // Copying drawings from other files
        r.Register("COPYPDF", "Copy a drawing out of a PDF as editable lines, arcs, circles and text", null, "PDFIMPORT");
        r.Register("CALIBRATE", "Give objects their true size from one known length", () => new CalibrateCommand(), "CAL");
        r.Register("CLEANUP", "Tidy lines: straighten, remove doubles, merge and join", () => new CleanupCommand(), "CLEAN");
        // Blocks
        r.Register("BLOCK", "Make a block from selected objects", () => new BlockCommand(), "B", "BMAKE");
        r.Register("INSERT", "Block library: place a block of the drawing, of your library or of the HAQQ library", null, "I", "BLOCKS", "LIBRARY");
        r.Register("-INSERT", "Place a block by typing its name", () => new TypedInsertCommand());
        // Text and tables
        r.Register("TEXT", "Text: words in a font, with height, bold, italic, underline and angle", null, "T", "MTEXT", "DTEXT");
        r.Register("-TEXT", "One line of text typed on the command line", () => new TypedTextCommand());
        r.Register("TABLE", "Table: rows and columns of text, with a title and a header", null, "TB");
        r.Register("TEXTEDIT", "Edit a text or a table (also a double-click on it)", () => new EditAnnotationCommand(), "ED", "TABLEDIT");
        // Layouts and plotting
        r.Register("MODEL", "Go to the Model tab", null, "MS", "MSPACE");
        r.Register("LAYOUT", "New layout straight away: standard sheet, title block and a viewport", null, "LO");
        r.Register("LAYOUTRENAME", "Rename the layout being looked at", () => new RenameLayoutCommand());
        r.Register("LAYOUTDELETE", "Delete the layout being looked at", () => new DeleteLayoutCommand());
        r.Register("PAGESETUP", "Paper size of the layout", () => new PageSetupCommand(), "PAPER");
        r.Register("VIEWPORT", "New viewport on the layout", () => new ViewportCommand(), "VP", "MV", "MVIEW");
        r.Register("VPSCALE", "Scale of a viewport", () => new ViewportScaleCommand(fit: false), "VS");
        r.Register("COMPANY", "Your company's name, for the title block of new layouts", () => new CompanyNameCommand(), "COMPANYNAME");
        r.Register("VPLOCK", "Lock or unlock the view and scale of a viewport", () => new ViewportLockCommand(), "VL");
        r.Register("NEWLAYOUT", "New layout: choose the paper, the border and the title block", null, "LAYOUTNEW");
        r.Register("PDF", "Save the open layout as a PDF file, the page exactly the size of the sheet", null, "SAVEPDF");
        r.Register("EXPORTPDF", "Export layouts as a vector PDF, one sheet per page", null, "PDFOUT");
        r.Register("VPFIT", "Show the whole model in a viewport", () => new ViewportScaleCommand(fit: true), "VF");
        r.Register("PLOT", "Print the layout or the model, or make a PDF", null, "PRINT");
        r.Register("UNDO", "Undo the last change", null, "U");
        r.Register("REDO", "Redo what was undone", null);
        r.Register("UNITS", "Drawing units and precision", () => new UnitsCommand(), "UN");
        r.Register("NEWUNITS", "The unit new drawings start in (inches unless changed)", () => new NewDrawingUnitsCommand(), "DEFAULTUNITS");
        r.Register("GRID", "Spacing of the background grid", () => new SpacingCommand(snap: false), "GRIDSPACING");
        r.Register("CURSORSIZE", "Size of the crosshair", () => new CursorSizeCommand(), "CS", "CROSSHAIR");
        r.Register("LINEMODE", "LINE ends at the second point, or carries on", () => new LineModeCommand());
        r.Register("SHORTCUTS", "Change the shortcuts of commands", null, "KEYS");
        r.Register("QUIT", "Close HAQQ CAD", null, "EXIT");
        return r;
    }
}

/// <summary>
/// The editing state of one open drawing, independent of any UI toolkit: document, undo history,
/// selection, active command, snap and ortho settings. The desktop shell forwards mouse and
/// keyboard input here; tests drive it directly.
/// </summary>
public sealed class EditorSession
{
    private readonly CommandRegistry _registry;
    private string? _lastCommandName;

    public EditorSession(DrawingDocument? document = null, CommandRegistry? registry = null)
    {
        _registry = registry ?? CommandRegistry.CreateDefault();
        Document = document ?? new DrawingDocument();
        Document.Changed += OnDocumentChanged;
        Selection.Changed += (_, _) => RaiseStateChanged();
        UndoStack.Changed += (_, _) => RaiseStateChanged();
    }

    public DrawingDocument Document { get; private set; }
    public UndoStack UndoStack { get; } = new();
    public SelectionSet Selection { get; } = new();
    public SnapSettings Snap { get; } = new();
    public bool OrthoEnabled { get; set; }

    /// <summary>Temporary override while Shift is held: flips <see cref="OrthoEnabled"/> for as long as it is set.</summary>
    public bool OrthoOverride { get; set; }

    /// <summary>True when cursor points are currently restricted to horizontal / vertical.</summary>
    public bool OrthoActive => OrthoEnabled != OrthoOverride;
    public ICadCommand? ActiveCommand { get; private set; }

    /// <summary>Grid snap: cursor points jump to multiples of the drawing's snap spacing. Nothing in the program switches it on any more.</summary>
    public bool GridSnapEnabled { get; set; }

    private int _crosshairSizePercent = 8;

    /// <summary>Crosshair size as a percentage of the drawing area, 1 to 100. A program preference, not part of the drawing.</summary>
    public int CrosshairSizePercent
    {
        get => _crosshairSizePercent;
        set
        {
            int clamped = Math.Clamp(value, 1, 100);
            if (clamped == _crosshairSizePercent) return;
            _crosshairSizePercent = clamped;
            RaiseStateChanged();
        }
    }

    /// <summary>The last point given to any command; <c>@</c> coordinates at a first prompt are relative to it.</summary>
    public Point2D? LastPoint { get; private set; }

    /// <summary>
    /// LINE behaviour: false (default) = one line per command, ending at the second point;
    /// true = a chain of lines until Enter, with Close and Undo. Changed with the LINEMODE command.
    /// </summary>
    public bool ContinuousLines { get; set; }

    // ------------------------------------------------- properties of new objects

    private bool _overrideSaved;
    private Guid? _layerAfterCommand;

    /// <summary>What every new object gets (the layer is the drawing's current layer). Lasts for the session.</summary>
    public NewObjectProperties NewObjectDefaults { get; private set; } = new();

    /// <summary>Changes made in the Properties panel while a drawing command runs: they apply to that command only.</summary>
    public NewObjectProperties? NewObjectOverride { get; private set; }

    /// <summary>True while a command that draws new objects is running.</summary>
    public bool IsCreatingObjects => ActiveCommand is ICreatesObjects;

    /// <summary>What the object being drawn (or the next one) will get.</summary>
    public NewObjectProperties EffectiveNewObject => NewObjectOverride ?? NewObjectDefaults;

    /// <summary>Layer the object being drawn (or the next one) goes on.</summary>
    public Guid EffectiveNewObjectLayerId =>
        EffectiveNewObject.LayerId is Guid id && Document.GetLayer(id) is not null ? id : Document.CurrentLayerId;

    /// <summary>True when the running command has changes that were not saved for the objects that follow.</summary>
    public bool HasUnsavedNewObjectChanges => NewObjectOverride is not null && !_overrideSaved;

    /// <summary>Counts every change to the properties of new objects, so views know when to refresh.</summary>
    public int NewObjectVersion { get; private set; }

    /// <summary>
    /// Changes a property of new objects. While a drawing command runs the change is for that command only
    /// (see <see cref="SaveNewObjectChanges"/>); otherwise it is for every object drawn from now on.
    /// </summary>
    public void ChangeNewObject(Action<NewObjectProperties> change)
    {
        if (IsCreatingObjects)
        {
            NewObjectOverride ??= NewObjectDefaults.Clone();
            change(NewObjectOverride);
            _overrideSaved = false;
        }
        else
        {
            change(NewObjectDefaults);
            if (NewObjectDefaults.LayerId is Guid layerId)
            {
                NewObjectDefaults.LayerId = null;
                if (Document.GetLayer(layerId) is not null) SetCurrentLayer(layerId);
            }
        }
        NewObjectVersion++;
        RaiseStateChanged();
    }

    /// <summary>Makes the changes of the running command the settings for every object drawn after it as well.</summary>
    public bool SaveNewObjectChanges()
    {
        if (NewObjectOverride is not { } changes) return false;
        NewObjectDefaults = changes.Clone();
        NewObjectDefaults.LayerId = null;
        // The current layer is switched when the command ends, so the command's own undo step stays whole.
        _layerAfterCommand = changes.LayerId;
        _overrideSaved = true;
        NewObjectVersion++;
        Post("Saved: new objects will use these properties.");
        RaiseStateChanged();
        return true;
    }

    /// <summary>Makes the properties of an existing object the settings for new objects (its layer becomes current).</summary>
    public void UseForNewObjects(Entity source)
    {
        NewObjectDefaults = new NewObjectProperties
        {
            Color = source.Color,
            Linetype = source.Linetype,
            LinetypeScale = source.LinetypeScale,
            LineweightMm = source.LineweightMm,
        };
        if (ActiveCommand is null) { if (Document.GetLayer(source.LayerId) is not null) SetCurrentLayer(source.LayerId); }
        else _layerAfterCommand = source.LayerId;
        NewObjectVersion++;
        Post("Saved: new objects will use these properties.");
        RaiseStateChanged();
    }

    /// <summary>Gives a new object its layer, colour, linetype, linetype scale and lineweight.</summary>
    public void ApplyNewObjectProperties(Entity entity)
    {
        var p = EffectiveNewObject;
        entity.LayerId = EffectiveNewObjectLayerId;
        entity.Color = p.Color;
        entity.Linetype = Linetypes.IsReserved(p.Linetype) ? p.Linetype : Document.FindLinetype(p.Linetype)?.Name ?? Linetypes.ByLayer;
        entity.LinetypeScale = p.LinetypeScale > 0 && double.IsFinite(p.LinetypeScale) ? p.LinetypeScale : 1.0;
        entity.LineweightMm = p.LineweightMm;
    }

    /// <summary>A command has ended: its one-off property changes end with it.</summary>
    private void EndNewObjectChanges()
    {
        bool had = NewObjectOverride is not null;
        NewObjectOverride = null;
        _overrideSaved = false;
        if (had) NewObjectVersion++;
        if (_layerAfterCommand is Guid layerId)
        {
            _layerAfterCommand = null;
            if (Document.GetLayer(layerId) is not null) SetCurrentLayer(layerId);
        }
    }

    /// <summary>True for objects to draw highlighted: the selection, and whatever the active command has picked.</summary>
    public bool IsHighlighted(Guid id) =>
        Selection.Contains(id) || (ActiveCommand is IPickedObjects picked && picked.PickedObjects.Contains(id));

    /// <summary>True while the active command wants a click on an object rather than a point.</summary>
    public bool PickingObject => ActiveCommand is IObjectPicker { PickingObject: true };

    /// <summary>Last OFFSET distance, offered as the default next time.</summary>
    public double? OffsetDistance { get; set; }

    /// <summary>FILLET radius; stays until changed (0 makes a sharp corner).</summary>
    public double FilletRadius { get; set; }

    /// <summary>CHAMFER distances along the first and second line; stay until changed.</summary>
    public (double First, double Second) ChamferDistances { get; set; }

    /// <summary>Radius of the last circle drawn, offered as the default for the next one.</summary>
    public double? LastCircleRadius { get; set; }

    /// <summary>
    /// End point and direction of travel (radians) of the last line or arc drawn. ARC's Continue
    /// option starts a new arc there, tangent to it.
    /// </summary>
    public (Point2D Point, double Angle)? ContinueFrom { get; set; }

    /// <summary>Name of the command that Enter would repeat, if any.</summary>
    public string? LastCommandName => _lastCommandName;

    /// <summary>Raw cursor position in model space.</summary>
    public Point2D Cursor { get; private set; }

    /// <summary>Object snap currently acquired under the cursor, if any.</summary>
    public SnapResult? CurrentSnap { get; private set; }

    public string Prompt => ActiveCommand?.Prompt ?? "Command:";

    /// <summary>True when a click should select objects rather than supply a point.</summary>
    public bool WantsSelection => ActiveCommand is null || ActiveCommand.WantsSelection;

    /// <summary>True when the active command is waiting for a point.</summary>
    public bool WantsPoint => ActiveCommand is { WantsSelection: false };

    public event Action<string>? MessagePosted;
    public event EventHandler? StateChanged;
    public event EventHandler? DocumentReplaced;

    /// <summary>Raised for view commands the shell must carry out (currently "ZoomExtents").</summary>
    public event Action<string>? ViewCommandRequested;

    /// <summary>A command asks for another view (zoom window, zoom in...). The shell that owns the view carries it out.</summary>
    public event Action<ViewChange>? ViewChangeRequested;

    public void RequestView(ViewChange change)
    {
        // "All" goes the way it always went, so everything listening for it still hears it.
        if (change.Kind == ViewChangeKind.Extents) ViewCommandRequested?.Invoke("ZoomExtents");
        else ViewChangeRequested?.Invoke(change);
    }

    /// <summary>True while PAN runs: a left-button drag moves the view instead of giving a point.</summary>
    public bool IsPanning => ActiveCommand is PanCommand;

    public void Post(string message)
    {
        if (!_quiet) MessagePosted?.Invoke(message);
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        Selection.Prune(id => Document.GetEntity(id) is { } entity && SelectionService.IsSelectable(Document, entity));
        if (ActiveViewportId is Guid viewportId && (Document.GetEntity(viewportId) is not ViewportEntity v || v.SpaceId != Document.ActiveSpaceId))
        {
            ActiveViewportId = null;                // the viewport, or its layout, is gone (an undo)
            _viewportBefore = null;
        }
        RaiseStateChanged();
    }

    /// <summary>Switches to another document (New/Open). Clears undo history and selection.</summary>
    public void LoadDocument(DrawingDocument document)
    {
        if (ActiveCommand is not null) Cancel();
        Document.Changed -= OnDocumentChanged;
        Document = document;
        Document.Changed += OnDocumentChanged;
        Selection.Clear();
        UndoStack.Clear();
        CurrentSnap = null;
        LastPoint = null;
        LastCircleRadius = null;
        ContinueFrom = null;
        OffsetDistance = null;
        FilletRadius = 0;
        ChamferDistances = (0, 0);
        _lastCopyIds = new List<Guid>();
        _lastCopyKey = "";
        _textDefaults = null;
        _viewportsAwaitingModel.Clear();
        ActiveViewportId = null;
        _viewportBefore = null;
        LastDimensionId = null;
        NewObjectDefaults = new NewObjectProperties();
        NewObjectOverride = null;
        _overrideSaved = false;
        _layerAfterCommand = null;
        NewObjectVersion++;
        DocumentReplaced?.Invoke(this, EventArgs.Empty);
        RaiseStateChanged();
    }

    // --------------------------------------------------------------- cursor

    /// <summary>Updates the cursor position and the acquired object snap.</summary>
    /// <param name="aperture">Snap aperture in model units (pixels divided by zoom).</param>
    public void UpdateCursor(Point2D modelPoint, double aperture)
    {
        Cursor = modelPoint;
        _aperture = aperture;
        var modes = SnapOverride ?? (Snap.Enabled ? Snap.Modes : SnapModes.None);
        if (PickingObject) modes = SnapModes.None;
        CurrentSnap = WantsPoint && modes != SnapModes.None
            ? SnapEngine.FindSnap(Document, modelPoint, aperture, modes, ActiveCommand?.LastPoint,
                ActiveCommand is ITangentAware { WantsDeferredTangent: true })
            : null;
        AcquireTrackingPoint();
    }

    // ------------------------------------------------- object snap tracking

    /// <summary>How many acquired points are kept; hovering over one more drops the oldest.</summary>
    public const int MaxTrackingPoints = 3;

    private readonly List<Point2D> _trackingPoints = new();
    private Point2D? _hovered;

    /// <summary>OTRACK: hovering over an object snap acquires it, and the cursor then locks onto lines through it.</summary>
    public bool TrackingEnabled { get; set; } = true;

    /// <summary>Points acquired for tracking while the current point is being chosen (marked with a small cross).</summary>
    public IReadOnlyList<Point2D> TrackingPoints => _trackingPoints;

    private static bool IsTrackable(SnapModes mode) =>
        mode is SnapModes.Endpoint or SnapModes.Midpoint or SnapModes.Center or SnapModes.Intersection or SnapModes.Quadrant;

    /// <summary>The snap under the cursor becomes a tracking point the moment the cursor reaches it.</summary>
    private void AcquireTrackingPoint()
    {
        if (!TrackingEnabled || !WantsPoint || PickingObject)
        {
            _trackingPoints.Clear();
            _hovered = null;
            return;
        }
        if (CurrentSnap is not { Deferred: false } snap || !IsTrackable(snap.Mode))
        {
            _hovered = null;
            return;
        }
        if (_hovered is { } same && same.NearlyEquals(snap.Point)) return;          // still on the same point
        _hovered = snap.Point;
        if (ActiveCommand?.LastPoint is { } from && from.NearlyEquals(snap.Point)) return;   // polar already tracks from there
        if (_trackingPoints.Any(p => p.NearlyEquals(snap.Point))) return;
        _trackingPoints.Add(snap.Point);
        if (_trackingPoints.Count > MaxTrackingPoints) _trackingPoints.RemoveAt(0);
    }

    private void ClearTracking()
    {
        _trackingPoints.Clear();
        _hovered = null;
    }

    /// <summary>
    /// The tracking alignment the cursor is locked onto, or null. Object snaps and ORTHO come first. With two
    /// lines in reach (two acquired points, or one and the polar ray from the previous point) the cursor goes to
    /// where they cross.
    /// </summary>
    public ObjectTrack? CurrentObjectTrack
    {
        get
        {
            if (!TrackingEnabled || _trackingPoints.Count == 0 || CurrentSnap is not null || OrthoActive || _aperture <= 0) return null;
            if (!WantsPoint || PickingObject || ActiveCommand is IShiftConstraint) return null;
            double step = PolarIncrementDegrees;
            if (!(step >= 1) || step > 360) return null;

            // Every tracking line the cursor is close to: (acquired point, direction, angle, distance from the line).
            var near = new List<(Point2D From, Vector2D Direction, double Degrees, double Across)>();
            foreach (var from in _trackingPoints)
            {
                var delta = Cursor - from;
                if (delta.Length <= _aperture) continue;
                double degrees = GeoMath.RadiansToDegrees(delta.Angle);
                if (degrees < 0) degrees += 360.0;
                double nearest = Math.Round(degrees / step) * step % 360.0;
                var direction = Vector2D.FromPolar(1.0, GeoMath.DegreesToRadians(nearest));
                double across = Math.Abs(delta.Cross(direction));
                if (delta.Dot(direction) > 0 && across <= _aperture) near.Add((from, direction, nearest, across));
            }
            if (near.Count == 0) return null;
            near.Sort((x, y) => x.Across.CompareTo(y.Across));
            var first = near[0];

            // Where it crosses the polar ray from the previous point.
            if (CurrentPolarTrack is { } polar)
            {
                var polarDirection = Vector2D.FromPolar(1.0, GeoMath.DegreesToRadians(polar.AngleDegrees));
                return TryCross(first.From, first.Direction, polar.From, polarDirection, out var corner) && corner.DistanceTo(Cursor) <= 2.0 * _aperture
                    ? new ObjectTrack(corner, new[] { (first.From, first.Degrees) })
                    : null;                                    // the polar ray alone wins
            }

            // Where two tracking lines cross.
            foreach (var second in near.Skip(1))
            {
                if (TryCross(first.From, first.Direction, second.From, second.Direction, out var corner) && corner.DistanceTo(Cursor) <= 2.0 * _aperture)
                    return new ObjectTrack(corner, new[] { (first.From, first.Degrees), (second.From, second.Degrees) });
            }

            return new ObjectTrack(OnLine(first.From, first.Direction, first.Degrees, (Cursor - first.From).Dot(first.Direction)),
                new[] { (first.From, first.Degrees) });
        }
    }

    /// <summary>The point <paramref name="along"/> a tracking line, exact on the axes so a horizontal line stays horizontal.</summary>
    private static Point2D OnLine(Point2D from, Vector2D direction, double degrees, double along) => degrees switch
    {
        0 => new Point2D(from.X + along, from.Y),
        90 => new Point2D(from.X, from.Y + along),
        180 => new Point2D(from.X - along, from.Y),
        270 => new Point2D(from.X, from.Y - along),
        _ => from + direction * along,
    };

    private static bool TryCross(Point2D a, Vector2D u, Point2D b, Vector2D v, out Point2D point)
    {
        point = default;
        double cross = u.Cross(v);
        if (Math.Abs(cross) < 1e-9) return false;
        double t = (b - a).Cross(v) / cross;
        point = a + u * t;
        // Exact where a line is horizontal or vertical.
        if (Math.Abs(u.Y) < 1e-12) point = new Point2D(point.X, a.Y);
        if (Math.Abs(u.X) < 1e-12) point = new Point2D(a.X, point.Y);
        if (Math.Abs(v.Y) < 1e-12) point = new Point2D(point.X, b.Y);
        if (Math.Abs(v.X) < 1e-12) point = new Point2D(b.X, point.Y);
        return point.IsFinite;
    }

    /// <summary>
    /// A snap chosen for the next point only (right-click, Snap Overrides). While it is set, only that snap
    /// is used, whether OSNAP is on or off. <see cref="SnapModes.None"/> means "no snap for the next point".
    /// </summary>
    public SnapModes? SnapOverride { get; private set; }

    private static readonly Dictionary<string, SnapModes> SnapCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["END"] = SnapModes.Endpoint, ["MID"] = SnapModes.Midpoint, ["INT"] = SnapModes.Intersection, ["CEN"] = SnapModes.Center,
        ["QUA"] = SnapModes.Quadrant, ["PER"] = SnapModes.Perpendicular, ["TAN"] = SnapModes.Tangent, ["NEA"] = SnapModes.Nearest,
        ["NON"] = SnapModes.None,
    };

    public void SetSnapOverride(SnapModes? mode)
    {
        SnapOverride = WantsPoint ? mode : null;
        UpdateCursor(Cursor, _aperture);
        RaiseStateChanged();
    }

    /// <summary>The point a click would produce: the acquired snap, else the cursor with ORTHO applied.</summary>
    public Point2D EffectiveCursor
    {
        get
        {
            if (CurrentSnap is { } snap) return snap.Point;
            if (OrthoActive || ActiveCommand is IShiftConstraint) return ApplyOrtho(ApplyGridSnap(Cursor));
            if (CurrentObjectTrack is { } track) return track.Point;
            return CurrentPolarTrack?.Point ?? ApplyGridSnap(Cursor);
        }
    }

    /// <summary>POLAR tracking: near a tracking angle from the previous point, the cursor locks onto that direction.</summary>
    public bool PolarEnabled { get; set; } = true;

    /// <summary>Angle between polar tracking directions, in degrees (90 tracks horizontal and vertical).</summary>
    public double PolarIncrementDegrees { get; set; } = 90.0;

    private double _aperture;

    /// <summary>Pick tolerance in model units from the last cursor update (the snap aperture at the current zoom).</summary>
    public double PickTolerance => _aperture > 0 ? _aperture : 1e-6;

    /// <summary>
    /// The polar alignment the cursor is currently locked onto, or null. Object snaps and ORTHO take
    /// precedence; the cursor must be within the snap aperture of the tracking ray.
    /// </summary>
    public PolarTrack? CurrentPolarTrack
    {
        get
        {
            if (!PolarEnabled || OrthoActive || CurrentSnap is not null || _aperture <= 0) return null;
            if (ActiveCommand is IShiftConstraint) return null;
            if (ActiveCommand is not { WantsSelection: false, LastPoint: { } from }) return null;
            double step = PolarIncrementDegrees;
            if (!(step >= 1) || step > 360) return null;

            var delta = Cursor - from;
            double length = delta.Length;
            if (length <= _aperture) return null; // too close to the base point to tell a direction

            double degrees = GeoMath.RadiansToDegrees(delta.Angle);
            if (degrees < 0) degrees += 360.0;
            double nearest = Math.Round(degrees / step) * step;
            var direction = Vector2D.FromPolar(1.0, GeoMath.DegreesToRadians(nearest));
            double along = delta.Dot(direction);
            double across = Math.Abs(delta.Cross(direction));
            if (along <= 0 || across > _aperture) return null;

            // Exact axis values for the common cases, so a horizontal line is exactly horizontal.
            double normalized = nearest % 360.0;
            Point2D point = normalized switch
            {
                0 => new Point2D(from.X + along, from.Y),
                90 => new Point2D(from.X, from.Y + along),
                180 => new Point2D(from.X - along, from.Y),
                270 => new Point2D(from.X, from.Y - along),
                _ => from + direction * along,
            };
            return new PolarTrack(from, normalized, point);
        }
    }

    public Point2D ApplyGridSnap(Point2D point)
    {
        double spacing = Document.Settings.SnapSpacing;
        if (!GridSnapEnabled || !(spacing > 0) || !double.IsFinite(spacing)) return point;
        return new Point2D(Math.Round(point.X / spacing) * spacing, Math.Round(point.Y / spacing) * spacing);
    }

    public Point2D ApplyOrtho(Point2D point)
    {
        if (ActiveCommand is IShiftConstraint shaped) return OrthoOverride ? shaped.ConstrainWithShift(point) : point;
        if (!OrthoActive || ActiveCommand?.LastPoint is not { } from) return point;
        var delta = point - from;
        return Math.Abs(delta.X) >= Math.Abs(delta.Y)
            ? new Point2D(point.X, from.Y)
            : new Point2D(from.X, point.Y);
    }

    /// <summary>Direct distance entry: the point at <paramref name="distance"/> from <paramref name="from"/> toward the cursor.</summary>
    public Point2D PointFromDistance(Point2D from, double distance)
    {
        var direction = (EffectiveCursor - from).Normalized();
        if (direction == Vector2D.Zero) direction = new Vector2D(1, 0);
        return from + direction * distance;
    }

    public IReadOnlyList<Entity> GetPreview() =>
        ActiveCommand?.GetPreview(EffectiveCursor) ?? Array.Empty<Entity>();

    // ------------------------------------------------------------- commands

    /// <summary>Starts a command by name or alias. Returns false when the name is unknown.</summary>
    public bool StartCommand(string nameOrAlias)
    {
        string name = nameOrAlias.Trim();
        if (name.Length == 0) return false;
        if (ActiveCommand is not null) Cancel();

        // Commands the session carries out itself. What was typed may be the full name or one of its shortcuts.
        switch (_registry.Resolve(name))
        {
            case "UNDO":
                Undo();
                return true;
            case "REDO":
                Redo();
                return true;
            case "QUIT":
                ViewCommandRequested?.Invoke("Quit");
                return true;
            case "LAYER":
                Post("Command: LAYER");
                ViewCommandRequested?.Invoke("Layers");
                return true;
            case "LINETYPE":
                Post("Command: LINETYPE");
                ViewCommandRequested?.Invoke("Linetypes");
                return true;
            case "ZOOMALL":
                Post("Command: ZOOM (all)");
                ViewCommandRequested?.Invoke("ZoomExtents");
                return true;
            case "DIMSTYLE":
                Post("Command: DIMSTYLE");
                ViewCommandRequested?.Invoke("DimensionStyles");
                return true;
            case "GRIDLINES":
                Post("Command: GRIDLINES");
                ViewCommandRequested?.Invoke("GridLines");
                return true;
            case "COPYPDF":
                Post("Command: COPYPDF");
                ViewCommandRequested?.Invoke("CopyPdf");
                return true;
            case "INSERT":
                Post("Command: INSERT");
                ViewCommandRequested?.Invoke("Blocks");
                return true;
            case "TEXT":
                Post("Command: TEXT");
                ViewCommandRequested?.Invoke("Text");
                return true;
            case "TABLE":
                Post("Command: TABLE");
                ViewCommandRequested?.Invoke("Table");
                return true;
            case "NEWLAYOUT":
                Post("Command: NEWLAYOUT");
                ViewCommandRequested?.Invoke("NewLayout");
                return true;
            case "PDF":
                Post("Command: PDF");
                ViewCommandRequested?.Invoke("Pdf");
                return true;
            case "EXPORTPDF":
                Post("Command: EXPORTPDF");
                ViewCommandRequested?.Invoke("ExportPdf");
                return true;
            case "MODEL":
                SwitchSpace(Guid.Empty);
                return true;
            case "LAYOUT":
                Post("Command: LAYOUT");
                AddLayout();
                return true;
            case "PLOT":
                Post("Command: PLOT");
                ViewCommandRequested?.Invoke("Plot");
                return true;
            case "SHORTCUTS":
                Post("Command: SHORTCUTS");
                ViewCommandRequested?.Invoke("Shortcuts");
                return true;
        }

        if (!_registry.TryCreate(name, out var command, out string canonical) || command is null)
        {
            Post($"Unknown command \"{name}\".");
            return false;
        }

        _lastCommandName = canonical;
        Post("Command: " + canonical);
        ActiveCommand = command;
        Complete(command.Start(this));
        return true;
    }

    private void Complete(CommandResult result)
    {
        if (ActiveCommand?.LastPoint is { } last) LastPoint = last;
        if (result == CommandResult.Finished) ActiveCommand = null;
        if (ActiveCommand is null) EndNewObjectChanges();
        if (ActiveCommand is null) ClearTracking();
        if (ActiveCommand is null) SnapOverride = null;
        if (ActiveCommand is null) CurrentSnap = null;
        RaiseStateChanged();
    }

    /// <summary>Esc: cancels the active command, or clears the selection when idle.</summary>
    public void Cancel()
    {
        if (ActiveCommand is { } command)
        {
            command.OnCancel();
            ActiveCommand = null;
            EndNewObjectChanges();
            ClearTracking();
            SnapOverride = null;
            CurrentSnap = null;
            Post("*Cancel*");
            RaiseStateChanged();
        }
        else if (Selection.Count == 0 && ActiveViewportId is not null)
        {
            ExitViewport();                         // Esc with nothing else to cancel leaves the entered viewport
        }
        else
        {
            Selection.Clear();
        }
    }

    /// <summary>Supplies a final model point (already snapped / constrained) to the active command.</summary>
    public void SubmitPoint(Point2D point)
    {
        if (ActiveCommand is not { } command) return;
        LastPoint = point;
        SnapOverride = null;
        ClearTracking();                      // the next point starts with nothing acquired
        // A point taken with the Tangent snap carries the circle it was taken on.
        var tangent = CurrentSnap is { Mode: SnapModes.Tangent, Tangent: { } target } snap && snap.Point.NearlyEquals(point)
            ? target
            : (TangentTarget?)null;
        Complete(command.OnInput(new CommandInput.PointInput(point, tangent)));
    }

    /// <summary>Chooses one of the active command's options (for example from the right-click menu).</summary>
    public void SubmitKeyword(string keyword)
    {
        if (ActiveCommand is not { } command || string.IsNullOrWhiteSpace(keyword)) return;
        Complete(command.OnInput(new CommandInput.KeywordInput(keyword.Trim().ToUpperInvariant())));
    }

    /// <summary>Enter / Space / right-click: confirms the current step, or repeats the last command when idle.</summary>
    public void SubmitEnter()
    {
        if (ActiveCommand is { } command)
        {
            Complete(command.OnInput(new CommandInput.EnterInput()));
        }
        else if (_lastCommandName is not null)
        {
            StartCommand(_lastCommandName);
        }
    }

    /// <summary>Handles a line of text typed on the command line.</summary>
    public void SubmitText(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) { SubmitEnter(); return; }

        if (ActiveCommand is not { } command)
        {
            StartCommand(trimmed);
            return;
        }

        if (command is IRawTextCommand { WantsRawText: true } raw)
        {
            Complete(raw.OnRawText(trimmed));
            return;
        }

        if (command.WantsSelection && trimmed.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            SelectAll();
            return;
        }

        // END, MID, INT, CEN, QUA, PER, TAN, NEA (or NON) while a point is wanted: that snap for the next point only.
        // An option of the command with the same letters wins (ARC has End).
        if (WantsPoint && !PickingObject && (trimmed.Equals("NOD", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("INS", StringComparison.OrdinalIgnoreCase)) &&
            !command.Options.Any(o => o.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            // Known AutoCAD codes whose objects do not exist in HAQQ CAD yet: say so instead of "invalid input".
            Post(trimmed.Equals("NOD", StringComparison.OrdinalIgnoreCase)
                ? "Node snap needs point objects, which HAQQ CAD does not have yet."
                : "Insertion snap needs blocks or text, which HAQQ CAD does not have yet.");
            return;
        }

        if (WantsPoint && !PickingObject && SnapCodes.TryGetValue(trimmed, out var snapCode) &&
            !command.Options.Any(o => o.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            SetSnapOverride(snapCode);
            Post(snapCode == SnapModes.None ? "Snap override: none (next point only)" : $"Snap override: {SnapEngine.NameOf(snapCode)} (next point only)");
            return;
        }

        if (!CommandInputParser.TryParse(trimmed, command.LastPoint ?? LastPoint, Document.Settings, out var input, out string? error) || input is null)
        {
            Post(error ?? "Invalid input.");
            return;
        }
        // A distance typed while the cursor is on one tracking line, when the command has no previous point to
        // measure from: the point that far from the acquired point, along the line.
        if (input is CommandInput.NumberInput { Value: > 0 } distance && command.LastPoint is null && WantsPoint &&
            CurrentObjectTrack is { Lines.Count: 1 } tracked)
        {
            var (from, degrees) = tracked.Lines[0];
            var direction = Vector2D.FromPolar(1.0, GeoMath.DegreesToRadians(degrees));
            SubmitPoint(OnLine(from, direction, degrees, distance.Value));
            return;
        }
        if (input is CommandInput.PointInput typed) { LastPoint = typed.Point; ClearTracking(); }
        Complete(command.OnInput(input));
    }

    // ------------------------------------------------------------ undo/redo

    /// <summary>Runs an undoable action. Returns false (and posts the reason) when the document rejects it.</summary>
    public bool Execute(IUndoableAction action)
    {
        try
        {
            UndoStack.Execute(Document, action);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            Post(ex.Message);
            return false;
        }
    }

    public void Undo()
    {
        if (ActiveCommand is not null) Cancel();
        CommitActiveViewport();                 // what was changed in the entered viewport is the first thing to undo
        var action = UndoStack.Undo(Document);
        Post(action is null ? "Nothing to undo." : "Undo: " + action.Name);
    }

    public void Redo()
    {
        if (ActiveCommand is not null) Cancel();
        var action = UndoStack.Redo(Document);
        Post(action is null ? "Nothing to redo." : "Redo: " + action.Name);
    }

    // ------------------------------------------------------------ selection

    /// <summary>Selects the entity under the point. With <paramref name="toggle"/>, an already selected entity is deselected.</summary>
    public bool SelectAt(Point2D point, double tolerance, bool toggle = false)
    {
        var entity = SelectionService.PickAt(Document, point, tolerance);
        if (entity is null) return false;
        // An object in a group (a grid system) brings the whole group with it.
        var picked = GroupOf(entity);
        if (toggle && Selection.Contains(entity.Id)) foreach (var e in picked) Selection.Remove(e.Id);
        else foreach (var e in picked) Selection.Add(e.Id);
        return true;
    }

    /// <summary>Double-click: selects only the object under the click, also when it belongs to a group.</summary>
    public bool SelectSingleAt(Point2D point, double tolerance)
    {
        var entity = SelectionService.PickAt(Document, point, tolerance);
        if (entity is null) return false;
        Selection.Clear();
        Selection.Add(entity.Id);
        return true;
    }

    /// <summary>The selectable objects in the same group as <paramref name="entity"/> (just itself when it is in no group).</summary>
    public IReadOnlyList<Entity> GroupOf(Entity entity)
    {
        if (entity.GroupId is not Guid group) return new[] { entity };
        return Document.EntitiesUnordered
            .Where(e => e.GroupId == group && SelectionService.IsSelectable(Document, e))
            .ToList();
    }

    /// <summary>
    /// Gives copies made in one go their own groups: copies of objects that were grouped together stay grouped
    /// with each other, not with the originals.
    /// </summary>
    public static void RegroupCopies(IEnumerable<Entity> copies)
    {
        var map = new Dictionary<Guid, Guid>();
        foreach (var copy in copies)
        {
            if (copy.GroupId is not Guid old) continue;
            if (!map.TryGetValue(old, out Guid fresh)) map[old] = fresh = Guid.NewGuid();
            copy.GroupId = fresh;
        }
    }

    /// <summary>Most selected entities whose grips are searched for a grip click (matches the number drawn).</summary>
    private const int MaxGripEntities = 2000;

    /// <summary>
    /// When idle with a selection, a click on a grip starts stretching it: the next point given is where the
    /// grip goes. Returns false when no grip of a selected entity is within <paramref name="tolerance"/>.
    /// </summary>
    public bool TryStartGripEdit(Point2D point, double tolerance)
    {
        if (ActiveCommand is not null || Selection.Count == 0 || !(tolerance > 0)) return false;

        var candidates = new List<(GripRef Grip, Point2D Point)>();
        Point2D? nearest = null;
        double best = tolerance;
        int searched = 0;
        foreach (var id in Selection.Ids)
        {
            if (++searched > MaxGripEntities) break;
            if (Document.GetEntity(id) is not { } entity || !SelectionService.IsSelectable(Document, entity)) continue;
            var grips = EntityGrips.GetGripPoints(entity);
            for (int i = 0; i < grips.Count; i++)
            {
                // Box test, like the square that is drawn.
                double d = Math.Max(Math.Abs(grips[i].X - point.X), Math.Abs(grips[i].Y - point.Y));
                if (d > tolerance) continue;
                candidates.Add((new GripRef(id, i), grips[i]));
                if (d <= best) { best = d; nearest = grips[i]; }
            }
        }
        if (nearest is not { } hot) return false;

        var chosen = candidates.Where(c => c.Point.NearlyEquals(hot)).Select(c => c.Grip).ToList();
        Post("** STRETCH **");
        ActiveCommand = new GripStretchCommand(chosen, hot);
        Complete(ActiveCommand.Start(this));
        return true;
    }

    public int SelectBox(BoundingBox box, bool crossing) =>
        Selection.AddRange(SelectionService.SelectInBox(Document, box, crossing).Select(e => e.Id));

    public int SelectAll() =>
        Selection.AddRange(Document.Entities.Where(e => SelectionService.IsSelectable(Document, e)).Select(e => e.Id));

    public void ClearSelection() => Selection.Clear();

    public List<Entity> GetSelectedEntities()
    {
        var list = new List<Entity>(Selection.Count);
        foreach (var id in Selection.Ids)
            if (Document.GetEntity(id) is { } entity) list.Add(entity);
        return list;
    }

    // --------------------------------------------------------------- layers

    public Layer? AddLayer(string? name = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            int n = 1;
            do { name = "Layer " + n++; } while (Document.FindLayer(name) is not null);
        }
        var layer = new Layer(Guid.NewGuid(), name);
        return Execute(new AddLayerAction(layer)) ? Document.GetLayer(layer.Id) : null;
    }

    public bool DeleteLayer(Guid layerId)
    {
        string? blocker = Document.GetLayerRemovalBlocker(layerId);
        if (blocker is not null) { Post(blocker); return false; }
        return Execute(new RemoveLayerAction(layerId));
    }

    /// <summary>Defines a new linetype in the drawing. Returns false (with a message) when the name or pattern is not usable.</summary>
    public bool AddLinetype(string name, string description, IReadOnlyList<double> pattern)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 64 || name.Any(c => char.IsControl(c) || c == ','))
        {
            Post("A linetype name is 1 to 64 characters, without commas.");
            return false;
        }
        if (!LinetypeDefinition.IsValidPattern(pattern) || pattern.Count == 0)
        {
            Post("A pattern is pairs of dash and gap lengths, for example 0.5, 0.25. Gaps must be more than 0.");
            return false;
        }
        if (!Execute(new AddLinetypeAction(new LinetypeDefinition(name, description, pattern)))) return false;
        Post($"Linetype \"{name}\" created.");
        return true;
    }

    /// <summary>Changes the dashes and description of a custom linetype (its name stays). One undo step.</summary>
    public bool ModifyLinetype(string name, string description, IReadOnlyList<double> pattern)
    {
        var existing = Document.FindLinetype(name);
        if (existing is null) { Post($"There is no linetype named \"{name.Trim()}\"."); return false; }
        if (Linetypes.IsBuiltIn(existing.Name)) { Post($"\"{existing.Name}\" is a built-in linetype and cannot be changed."); return false; }
        if (!LinetypeDefinition.IsValidPattern(pattern) || pattern.Count == 0)
        {
            Post("A pattern is pairs of dash and gap lengths, for example 0.5, 0.25. Gaps must be more than 0.");
            return false;
        }
        if (!Execute(new ReplaceLinetypeAction(new LinetypeDefinition(existing.Name, description, pattern)))) return false;
        Post($"Linetype \"{existing.Name}\" changed.");
        return true;
    }

    public bool DeleteLinetype(string name)
    {
        string? blocker = Document.GetLinetypeRemovalBlocker(name);
        if (blocker is not null) { Post(blocker); return false; }
        if (!Execute(new RemoveLinetypeAction(name.Trim()))) return false;
        Post($"Linetype \"{name.Trim()}\" deleted.");
        return true;
    }

    // ------------------------------------------------------------ shortcuts

    /// <summary>Every command with its description and the shortcuts it comes with.</summary>
    public IReadOnlyList<CommandInfo> CommandCatalog => _registry.Commands;

    /// <summary>The shortcuts a command answers to now.</summary>
    public IReadOnlyList<string> ShortcutsOf(string commandName) => _registry.ShortcutsOf(commandName);

    /// <summary>The user's own shortcuts, for the commands that differ from the defaults.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CustomShortcuts => _registry.CustomShortcuts;

    /// <summary>Sets the user's shortcuts. Returns the reason when they cannot be used (nothing changes then), or null.</summary>
    public string? SetShortcuts(IReadOnlyDictionary<string, IReadOnlyList<string>> custom) => _registry.SetShortcuts(custom);

    /// <summary>The dimension placed last in this session: DIMCHAIN and DIMSTACK carry on from it.</summary>
    public Guid? LastDimensionId { get; set; }

    // ------------------------------------------------------------------ units

    /// <summary>The unit of the drawing was changed with UNITS.</summary>
    public event Action<LinearUnit>? UnitsChanged;

    /// <summary>The unit new drawings start in: inches unless the user chose another (NEWUNITS). Kept by the shell between runs.</summary>
    public LinearUnit NewDrawingUnit { get; private set; } = LinearUnit.Inch;

    /// <summary>The unit for new drawings was chosen (the shell saves it with the user's settings).</summary>
    public event Action<LinearUnit>? NewDrawingUnitChanged;

    /// <summary>Sets the unit new drawings start in. The open drawing is not changed.</summary>
    public void SetNewDrawingUnit(LinearUnit unit, bool announce = true)
    {
        if (!Enum.IsDefined(unit)) return;
        bool changed = unit != NewDrawingUnit;
        NewDrawingUnit = unit;
        if (announce)
        {
            Post($"New drawings will start in {UnitsCommand.UnitName(unit)}. This drawing stays in {UnitsCommand.UnitName(Document.Settings.Unit)} (UNITS changes it).");
            if (changed) NewDrawingUnitChanged?.Invoke(unit);
        }
    }

    /// <summary>
    /// Puts a drawing that was just made into a unit: how lengths are written, the grid, and dimension text,
    /// arrows and dashes at round sizes of that unit. Not an undo step; for new, empty drawings.
    /// </summary>
    public static void ApplyUnitToNewDrawing(DrawingDocument document, LinearUnit unit)
    {
        if (document.Settings.Unit == unit) return;
        foreach (var action in UnitChangeActions(document, document.Settings.ForUnit(unit), keepRealSize: true)) action.Do(document);
    }

    /// <summary>
    /// What it takes to put a drawing into other settings. With <paramref name="keepRealSize"/> everything keeps
    /// the size it really has and its numbers change (a 10 ft wall becomes 3048 mm): objects on the model and on
    /// every layout, blocks, sheets, dimension styles and the linetype scale. Without it only the name of the
    /// unit changes and every number stays as typed. In a drawing with nothing in it the dimension styles and
    /// the linetype scale are taken to round sizes of the new unit.
    /// </summary>
    public static List<IUndoableAction> UnitChangeActions(DrawingDocument document, DrawingSettings target, bool keepRealSize)
    {
        var actions = new List<IUndoableAction>();
        var from = document.Settings;
        double exact = UnitConverter.Convert(1.0, from.Unit, target.Unit);
        bool empty = document.AllEntities.Count == 0 && document.Blocks.Count == 0;
        var after = target.Clone();

        if (from.Unit != target.Unit && (keepRealSize || empty))
        {
            // Nothing drawn yet: round sizes (text 4 in becomes 100 mm, not 101.6).
            double factor = empty ? RoundToTwoDigits(exact) : exact;
            after.LinetypeScale = from.LinetypeScale * factor;

            if (!empty)
            {
                var scale = Transform2D.Scaling(factor, Point2D.Origin);
                var before = new List<Entity>();
                var changed = new List<Entity>();
                foreach (var entity in document.AllEntitiesOrdered)
                {
                    var copy = entity.Clone();
                    switch (copy)
                    {
                        case BlockReferenceEntity reference:
                            // Its block is made larger below; the place it stands moves, its own scale stays.
                            reference.Position = scale.Apply(reference.Position);
                            break;
                        case ViewportEntity viewport:
                            // Sheet and model grow together, so the scale between them stays.
                            double viewScale = viewport.Scale;
                            viewport.Transform(scale);
                            viewport.Scale = viewScale;
                            viewport.ModelCenter = scale.Apply(viewport.ModelCenter);
                            break;
                        default:
                            copy.Transform(scale);
                            break;
                    }
                    before.Add(entity);
                    changed.Add(copy);
                }
                if (before.Count > 0) actions.Add(new ModifyEntitiesAction("UNITS", before, changed));
                foreach (var block in document.Blocks) actions.Add(new ReplaceBlockAction(block.Scaled(factor)));
                foreach (var layout in document.Layouts)
                    actions.Add(new ReplaceLayoutAction("UNITS", layout with { PaperWidth = layout.PaperWidth * factor, PaperHeight = layout.PaperHeight * factor }));
            }
            foreach (var style in document.DimensionStyles) actions.Add(new ReplaceDimensionStyleAction(style.Scaled(factor)));
        }

        actions.Insert(0, new ModifySettingsAction("UNITS", from, after));
        return actions;
    }

    private static double RoundToTwoDigits(double value)
    {
        if (!(value > 0) || !double.IsFinite(value)) return 1.0;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)) - 1);
        return Math.Round(value / magnitude) * magnitude;
    }

    /// <summary>Changes the drawing's unit, format and precision as one undo step (see <see cref="UnitChangeActions"/>).</summary>
    public bool ChangeUnits(DrawingSettings target, bool keepRealSize)
    {
        var from = Document.Settings.Unit;
        var actions = UnitChangeActions(Document, target, keepRealSize);
        if (!Execute(actions.Count == 1 ? actions[0] : new CompositeAction("UNITS", actions.ToArray()))) return false;
        if (from != target.Unit)
        {
            _textDefaults = null;                   // the next text takes the height of the dimension text again
            UnitsChanged?.Invoke(target.Unit);
        }
        return true;
    }

    // ------------------------------------------------------- text and tables

    private TextSettings? _textDefaults;

    /// <summary>
    /// How the next text is made. Until a text has been placed: the height of the current dimension style's
    /// text, Arial, plain, starting at the point clicked.
    /// </summary>
    public TextSettings TextDefaults
    {
        get => _textDefaults ?? new TextSettings(Document.CurrentDimensionStyle.TextHeight, TextMeasure.DefaultFont, false, false, false, TextJustify.Left, 0);
        set => _textDefaults = value;
    }

    /// <summary>A text at a point with the given settings.</summary>
    public static TextEntity MakeText(Point2D position, string text, TextSettings settings) =>
        new(position, settings.Height, text)
        {
            Font = settings.Font,
            Bold = settings.Bold,
            Italic = settings.Italic,
            Underline = settings.Underline,
            Justify = settings.Justify,
            Angle = settings.AngleDegrees * Math.PI / 180.0,
        };

    /// <summary>A text or a table is to be opened for editing (a double-click on it, or TEXTEDIT).</summary>
    public event Action<Guid>? ObjectEditRequested;

    /// <summary>Asks the shell to open a text or a table for editing. False when it is something else or on a locked layer.</summary>
    public bool RequestEdit(Guid id)
    {
        if (Document.GetEntity(id) is not { } entity || entity is not (TextEntity or TableEntity)) return false;
        if (entity.SpaceId != Document.ActiveSpaceId) return false;
        if (Document.GetLayer(entity.LayerId) is not { IsEditable: true }) { Post("That object is on a locked layer."); return false; }
        ObjectEditRequested?.Invoke(id);
        return true;
    }

    /// <summary>Starts placing a text by a click: it follows the cursor until its position is given.</summary>
    public bool BeginTextPlacement(string text, TextSettings settings)
    {
        var sample = MakeText(Point2D.Origin, text, settings);
        if (!sample.IsValid) { Post("That text cannot be used: it needs some words, a height more than 0 and a font."); return false; }
        TextDefaults = settings;
        if (ActiveCommand is not null) Cancel();
        _lastCommandName = "TEXT";
        ActiveCommand = new PlaceObjectCommand("TEXT", "Click where the text goes (or type coordinates):", at => MakeText(at, text, settings));
        Complete(ActiveCommand.Start(this));
        return true;
    }

    /// <summary>Changes the words and the look of a text; its position stays. One undo step.</summary>
    public bool UpdateText(Guid id, string text, TextSettings settings)
    {
        if (Document.GetEntity(id) is not TextEntity) { Post("That text is no longer in the drawing."); return false; }
        var edited = MakeText(Point2D.Origin, text, settings);
        if (!ModifyEntities(new[] { id }, e => ((TextEntity)e).CopyContentFrom(edited), "Edit text")) return false;
        TextDefaults = settings;
        return true;
    }

    /// <summary>Starts placing a table by a click: its top-left corner follows the cursor.</summary>
    public bool BeginTablePlacement(TableEntity table)
    {
        if (!table.IsValid) { Post("That table cannot be used: sizes must be more than 0."); return false; }
        if (ActiveCommand is not null) Cancel();
        _lastCommandName = "TABLE";
        ActiveCommand = new PlaceObjectCommand("TABLE", "Click where the top-left corner of the table goes (or type coordinates):", at =>
        {
            var placed = (TableEntity)table.CopyWithNewId();
            placed.Position = at;
            return placed;
        });
        Complete(ActiveCommand.Start(this));
        return true;
    }

    /// <summary>Replaces the cells, sizes and look of a table; its position stays. One undo step.</summary>
    public bool UpdateTable(Guid id, TableEntity edited)
    {
        if (Document.GetEntity(id) is not TableEntity) { Post("That table is no longer in the drawing."); return false; }
        return ModifyEntities(new[] { id }, e => ((TableEntity)e).CopyContentFrom(edited), "Edit table");
    }

    // ---------------------------------------------------------- drawing copy

    private List<Guid> _lastCopyIds = new();
    private string _lastCopyKey = "";

    /// <summary>The objects of the drawing that was copied in last (from a PDF), for CALIBRATE and for moving it.</summary>
    public IReadOnlyList<Guid> LastCopyIds => _lastCopyIds;

    /// <summary>The copied drawing was given its true size with CALIBRATE: where it came from, and by how much it was scaled.</summary>
    public event Action<string, double>? CopyCalibrated;

    internal void NoteCopyCalibrated(double factor)
    {
        if (_lastCopyKey.Length > 0) CopyCalibrated?.Invoke(_lastCopyKey, factor);
    }

    /// <summary>How far CLEANUP may move a point; the value last used, or a thousandth of a foot / a tenth of a millimetre to begin with.</summary>
    public double CleanupTolerance
    {
        get => _cleanupTolerance ?? UnitConverter.Convert(0.1, LinearUnit.Millimeter, Document.Settings.Unit);
        set => _cleanupTolerance = value;
    }

    private double? _cleanupTolerance;

    /// <summary>Starts placing a copied drawing by a click (see <see cref="PlaceCopyCommand"/>).</summary>
    public bool BeginCopyPlacement(IReadOnlyList<CopyLayer> layers, string sourceKey)
    {
        if (layers.Sum(l => l.Entities.Count) == 0) { Post("There is nothing to place: every group is switched off or empty."); return false; }
        if (ActiveCommand is not null) Cancel();
        _lastCommandName = "COPYPDF";
        ActiveCommand = new PlaceCopyCommand(layers, sourceKey);
        Complete(ActiveCommand.Start(this));
        return true;
    }

    /// <summary>
    /// Puts a copied drawing into the open space, moved by <paramref name="offset"/>: missing layers are made
    /// (with the colour and linetype given), the objects go on them. One undo step. The objects are left selected.
    /// </summary>
    public bool PlaceCopy(IReadOnlyList<CopyLayer> layers, Vector2D offset, string sourceKey)
    {
        var actions = new List<IUndoableAction>();
        var entities = new List<Entity>();
        var move = Transform2D.Translation(offset);
        var made = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers)
        {
            if (layer.Entities.Count == 0) continue;
            string name = (layer.Name ?? "").Trim();
            if (name.Length == 0 || name.Length > 64 || name.Any(char.IsControl)) { Post($"\"{layer.Name}\" cannot be used as a layer name (1 to 64 characters)."); return false; }
            Guid layerId;
            if (Document.FindLayer(name) is { } existing)
            {
                if (!existing.IsEditable) { Post($"Layer \"{existing.Name}\" is locked: unlock it or give that group another layer."); return false; }
                layerId = existing.Id;
            }
            else if (!made.TryGetValue(name, out layerId))
            {
                var created = new Layer(Guid.NewGuid(), name) { Color = layer.Color, Linetype = Document.FindLinetype(layer.Linetype)?.Name ?? Linetypes.Solid };
                layerId = created.Id;
                made[name] = layerId;
                actions.Add(new AddLayerAction(created));
            }
            foreach (var source in layer.Entities)
            {
                var copy = source.CopyWithNewId();
                copy.Transform(move);
                copy.LayerId = layerId;
                if (copy.IsValid) entities.Add(copy);
            }
        }
        if (entities.Count == 0) { Post("There is nothing to place."); return false; }
        actions.Add(new AddEntitiesAction("Copy drawing", entities));
        if (!Execute(actions.Count == 1 ? actions[0] : new CompositeAction("Copy drawing", actions.ToArray()))) return false;

        _lastCopyIds = entities.Select(e => e.Id).ToList();
        _lastCopyKey = sourceKey ?? "";
        Selection.Clear();
        Selection.AddRange(_lastCopyIds);
        Post($"{entities.Count} object(s) copied onto {layers.Count(l => l.Entities.Count > 0)} layer(s). They are selected: MOVE puts them somewhere else, CALIBRATE gives them their true size from one known length.");
        RaiseStateChanged();
        return true;
    }

    // ----------------------------------------------------------------- blocks

    /// <summary>
    /// Makes a block from objects of the drawing and puts the block in their place. Blocks among them are taken
    /// apart first, so a block never holds another. One undo step.
    /// </summary>
    public bool CreateBlock(string name, Point2D basePoint, IReadOnlyList<Entity> sources)
    {
        name = (name ?? "").Trim();
        if (!BlockDefinition.IsValidName(name)) { Post("A block name is 1 to 64 characters."); return false; }
        if (Document.FindBlock(name) is not null) { Post($"There is already a block named \"{name}\". Type another name."); return false; }
        if (StandardBlocks.All.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { Post($"\"{name}\" is the name of a block in the HAQQ library. Type another name."); return false; }
        var usable = sources.Where(e => e is not ViewportEntity && Document.GetLayer(e.LayerId) is { IsEditable: true }).ToList();
        if (usable.Count == 0) { Post("Select the objects for the block first (objects on locked layers cannot be used)."); return false; }

        var contents = new List<Entity>();
        var toBase = Transform2D.Translation(new Vector2D(-basePoint.X, -basePoint.Y));
        foreach (var source in usable)
        {
            foreach (var part in source is BlockReferenceEntity nested ? nested.Resolved : new[] { source })
            {
                var copy = part.Clone();
                copy.Transform(toBase);
                contents.Add(copy);
            }
        }
        if (contents.Count > BlockDefinition.MaxEntities) { Post($"A block holds at most {BlockDefinition.MaxEntities} objects."); return false; }
        var block = new BlockDefinition(name, contents);
        if (!block.IsValid) { Post("Those objects cannot be made into a block."); return false; }

        var reference = new BlockReferenceEntity(block, basePoint) { LayerId = usable[0].LayerId, SpaceId = Document.ActiveSpaceId };
        if (!Execute(new CompositeAction("BLOCK",
                new AddBlockAction(block),
                new RemoveEntitiesAction("BLOCK", usable.Select(e => e.Id).ToList()),
                new AddEntitiesAction("BLOCK", new Entity[] { reference })))) return false;
        Selection.Clear();
        Selection.Add(reference.Id);
        Post($"Block \"{name}\" made from {contents.Count} object(s). INSERT places more of it.");
        RaiseStateChanged();
        return true;
    }

    /// <summary>Starts placing a block: one from the drawing, or one from a library that is brought in with its first use.</summary>
    public bool BeginBlockInsert(BlockDefinition block, double scale, double rotationDegrees)
    {
        if (!block.IsValid || !(scale > 1e-6) || !double.IsFinite(scale) || !double.IsFinite(rotationDegrees)) { Post("That block cannot be placed."); return false; }
        if (ActiveCommand is TypedInsertCommand) ActiveCommand = null;           // -INSERT hands over to the placing
        else if (ActiveCommand is not null) Cancel();
        _lastCommandName = "INSERT";
        ActiveCommand = new InsertBlockCommand(block, scale, rotationDegrees);
        Complete(ActiveCommand.Start(this));
        return true;
    }

    /// <summary>Removes a block that is not placed anywhere in the drawing.</summary>
    public bool DeleteBlock(string name)
    {
        if (Document.FindBlock(name) is not { } block) { Post($"There is no block named \"{name}\"."); return false; }
        int used = Document.CountBlockReferences(block.Name);
        if (used > 0) { Post($"Block \"{block.Name}\" is placed {used} time(s) in the drawing. Erase or explode those first."); return false; }
        return Execute(new RemoveBlockAction(block.Name));
    }

    // -------------------------------------------------- layouts and viewports

    /// <summary>Name of the layer new viewports are placed on. Its Plot switch starts off, so viewport frames are not printed.</summary>
    public const string ViewportLayerName = "Viewport";

    /// <summary>The space being worked in: <see cref="Guid.Empty"/> for the model, or a layout's id.</summary>
    public Guid ActiveSpaceId => Document.ActiveSpaceId;

    /// <summary>The viewport that was entered with a double-click: the wheel and panning change what it shows.</summary>
    public Guid? ActiveViewportId { get; private set; }

    private ViewportEntity? _viewportBefore;

    /// <summary>Shows the model (<see cref="Guid.Empty"/>) or a layout. Ends the running command and clears the selection.</summary>
    public bool SwitchSpace(Guid spaceId)
    {
        if (spaceId != Guid.Empty && Document.GetLayout(spaceId) is null) return false;
        if (spaceId == Document.ActiveSpaceId) return true;
        if (ActiveCommand is not null) Cancel();
        ExitViewport();
        Selection.Clear();
        ClearTracking();
        CurrentSnap = null;
        LastPoint = null;
        Document.ActiveSpaceId = spaceId;
        FitWaitingViewports(spaceId);
        Post(spaceId == Guid.Empty ? "<Model>" : $"<Layout \"{Document.ActiveLayout!.Name}\">");
        RaiseStateChanged();
        return true;
    }

    /// <summary>Viewports made while the model was still empty: there was nothing to aim them at yet.</summary>
    private readonly HashSet<Guid> _viewportsAwaitingModel = new();

    /// <summary>
    /// A layout that was made before anything was drawn (the Layout 1 every new drawing starts with) aims its
    /// viewport at the model the first time it is opened with something drawn: whole model, largest standard
    /// scale that fits. Once only, and not for a viewport the user has locked. Also done before such a layout
    /// is written to a PDF without having been opened.
    /// </summary>
    public void FitWaitingViewports(Guid spaceId)
    {
        if (spaceId == Guid.Empty || _viewportsAwaitingModel.Count == 0) return;
        var model = Document.GetExtents(Guid.Empty);
        if (model.IsEmpty) return;
        foreach (var viewport in Document.AllEntities.OfType<ViewportEntity>().Where(v => v.SpaceId == spaceId && _viewportsAwaitingModel.Contains(v.Id)).ToList())
        {
            _viewportsAwaitingModel.Remove(viewport.Id);
            if (viewport.Locked) continue;
            viewport.Fit(model);
            viewport.Scale = RoundDownToStandardScale(viewport.Scale);
        }
    }

    /// <summary>
    /// Gives a new drawing its first layout, "Layout 1", so the tab is there from the start. The drawing stays
    /// on the Model tab, counts as unchanged, and the layout is not an undo step.
    /// </summary>
    public void AddStartingLayout()
    {
        if (Document.Layouts.Count > 0) return;
        _quiet = true;
        try
        {
            if (AddLayout() is null) return;
            SwitchSpace(Guid.Empty);
        }
        finally { _quiet = false; }
        UndoStack.Clear();
        Document.MarkSaved();
        RaiseStateChanged();
    }

    private bool _quiet;

    private IUndoableAction? ViewportLayerAction(out Guid layerId)
    {
        if (Document.FindLayer(ViewportLayerName) is { } existing)
        {
            layerId = existing.Id;
            return null;
        }
        var layer = new Layer(Guid.NewGuid(), ViewportLayerName) { IsPlotted = false, Color = CadColor.FromRgb(128, 128, 128) };
        layerId = layer.Id;
        return new AddLayerAction(layer);
    }

    /// <summary>What the top line of a new title block says until the user has given their company's name.</summary>
    public const string CompanyPlaceholder = "COMPANY NAME";

    /// <summary>The user's own company, for the top line of new title blocks. Kept by the shell between runs; empty until given.</summary>
    public string CompanyName { get; private set; } = "";

    /// <summary>The company name was given or changed (the shell saves it with the user's settings).</summary>
    public event Action<string>? CompanyNameChanged;

    /// <summary>Sets the company name used in title blocks made from now on. Title blocks already drawn are not changed.</summary>
    public bool SetCompanyName(string? name, bool announce = true)
    {
        name = (name ?? "").Trim();
        if (name.Length > 64 || name.Any(char.IsControl)) { Post("A company name is at most 64 characters."); return false; }
        bool changed = name != CompanyName;
        CompanyName = name;
        if (announce && changed)
        {
            Post(name.Length > 0 ? $"New title blocks will say \"{name}\". Title blocks already drawn keep their words (double-click one to change it)." : "New title blocks will say COMPANY NAME until a name is given.");
            CompanyNameChanged?.Invoke(name);
        }
        return true;
    }

    /// <summary>Name of the layer the sheet border and the title block of new layouts are placed on.</summary>
    public const string SheetLayerName = "Sheet";

    /// <summary>
    /// Adds a layout and opens it. The sheet is the standard one for the drawing's units unless a size is given
    /// (in drawing units). According to the template it gets a border and a title block; with a viewport, one
    /// that shows the whole model at the largest standard scale that fits, with the layout's name and its
    /// scale written under it. One undo step.
    /// </summary>
    public Layout? AddLayout(string? name = null, double? paperWidth = null, double? paperHeight = null,
        LayoutTemplate template = LayoutTemplate.TitleBlock, bool withViewport = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            int n = 1;
            do { name = "Layout " + n++.ToString(System.Globalization.CultureInfo.InvariantCulture); } while (Document.FindLayout(name) is not null);
        }
        name = name.Trim();
        if (!Layout.IsValidName(name)) { Post("A layout name is 1 to 64 characters and cannot be \"Model\"."); return null; }
        if (Document.FindLayout(name) is not null) { Post($"There is already a layout named \"{name}\"."); return null; }

        var (w, h) = LayoutStandards.SizeOf(LayoutStandards.DefaultPaper(Document.Settings), Document.Settings);
        var layout = new Layout(Guid.NewGuid(), name, paperWidth ?? w, paperHeight ?? h);
        if (!layout.IsValid) { Post("The paper size must be more than 0."); return null; }

        var actions = new List<IUndoableAction> { new AddLayoutAction(layout) };
        var entities = new List<Entity>();
        double width = layout.PaperWidth, height = layout.PaperHeight;
        // Everything on the sheet is sized from the sheet: u is 1 inch on an Arch D sheet.
        double u = Math.Min(width, height) / 24.0, margin = 0.5 * u, gap = 0.4 * u;
        var window = new BoundingBox(width * 0.04, height * 0.04, width * 0.96, height * 0.96);
        double titleHeight = 0;

        if (template != LayoutTemplate.Blank)
        {
            Guid sheetLayer;
            if (Document.FindLayer(SheetLayerName) is { } existing) sheetLayer = existing.Id;
            else
            {
                var created = new Layer(Guid.NewGuid(), SheetLayerName);
                sheetLayer = created.Id;
                actions.Add(new AddLayerAction(created));
            }
            entities.Add(new PolylineEntity(new[]
            {
                new Point2D(margin, margin), new Point2D(width - margin, margin),
                new Point2D(width - margin, height - margin), new Point2D(margin, height - margin),
            }, true) { LayerId = sheetLayer, SpaceId = layout.Id });

            double below = 1.2 * u;                                 // room under the viewport for its title and scale
            titleHeight = 0.25 * u;
            if (template == LayoutTemplate.TitleBlock)
            {
                // The title block: a table, so its words are changed by a double-click like any table.
                var block = new TableEntity(Point2D.Origin, 2, 2, 1, 0.7 * u, 0.18 * u)
                {
                    Title = CompanyName.Length > 0 ? CompanyName : CompanyPlaceholder,
                    HeaderRow = false,
                    LayerId = sheetLayer,
                    SpaceId = layout.Id,
                };
                block.SetColumnWidth(0, 5.5 * u);
                block.SetColumnWidth(1, 2.5 * u);
                block.SetCell(0, 0, name.ToUpperInvariant());
                block.SetCell(1, 0, "PROJECT TITLE");
                block.SetCell(1, 1, SheetNumberOf(name, Document.Layouts.Count + 1));
                block.Position = new Point2D(width - margin - block.Width, margin + block.Height);
                entities.Add(block);
                below = block.Height + 0.5 * u;
            }
            window = new BoundingBox(margin + gap, margin + below, width - margin - gap, height - margin - gap);
        }

        if (withViewport && window.Width > 0 && window.Height > 0)
        {
            var viewport = new ViewportEntity(window.Center, window.Width, window.Height)
            {
                SpaceId = layout.Id,
                Title = titleHeight > 0 ? name.ToUpperInvariant() : "",
                TitleHeight = titleHeight,
            };
            var model = Document.GetExtents(Guid.Empty);
            if (model.IsEmpty)
            {
                viewport.Scale = LayoutStandards.DefaultScale(Document.Settings);
                _viewportsAwaitingModel.Add(viewport.Id);
            }
            else
            {
                viewport.Fit(model);
                viewport.Scale = RoundDownToStandardScale(viewport.Scale);
            }
            if (ViewportLayerAction(out Guid layerId) is { } layerAction) actions.Add(layerAction);
            viewport.LayerId = layerId;
            entities.Add(viewport);
        }

        if (entities.Count > 0) actions.Add(new AddEntitiesAction("New layout", entities));
        if (!Execute(actions.Count == 1 ? actions[0] : new CompositeAction("New layout", actions.ToArray()))) return null;
        SwitchSpace(layout.Id);
        return Document.GetLayout(layout.Id);
    }

    /// <summary>The sheet number for the title block: the leading "S-101" of a name like "S-101 Foundation", else the layout's place in the row.</summary>
    private static string SheetNumberOf(string name, int ordinal)
    {
        string first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Any(char.IsDigit) && first.Length <= 12 && first != name ? first : ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Makes a copy of a layout beside it, with everything drawn on it: border, title block, text and the
    /// viewports with their scales. The model is not copied; the new viewports look at the same model. One undo step.
    /// </summary>
    public Layout? DuplicateLayout(Guid layoutId)
    {
        if (Document.GetLayout(layoutId) is not { } source) { Post("That layout does not exist."); return null; }
        string name;
        int n = 2;
        do { name = $"{source.Name} ({n++})"; } while (Document.FindLayout(name) is not null || name.Length > 64);
        if (!Layout.IsValidName(name)) { Post("The copy's name would be too long: rename the layout first."); return null; }
        var copy = source with { Id = Guid.NewGuid(), Name = name };
        int index = Document.Layouts.ToList().FindIndex(l => l.Id == layoutId) + 1;
        var entities = new List<Entity>();
        foreach (var entity in Document.AllEntitiesOrdered.Where(e => e.SpaceId == layoutId))
        {
            var clone = entity.CopyWithNewId();
            clone.SpaceId = copy.Id;
            entities.Add(clone);
        }
        RegroupCopies(entities);
        var actions = new List<IUndoableAction> { new AddLayoutAction(copy, index) };
        if (entities.Count > 0) actions.Add(new AddEntitiesAction("Duplicate layout", entities));
        if (!Execute(actions.Count == 1 ? actions[0] : new CompositeAction("Duplicate layout", actions.ToArray()))) return null;
        SwitchSpace(copy.Id);
        return Document.GetLayout(copy.Id);
    }

    /// <summary>Moves a layout's tab one place left (-1) or right (+1).</summary>
    public bool MoveLayout(Guid layoutId, int delta)
    {
        int index = Document.Layouts.ToList().FindIndex(l => l.Id == layoutId);
        if (index < 0) { Post("That layout does not exist."); return false; }
        int target = Math.Clamp(index + delta, 0, Document.Layouts.Count - 1);
        return target != index && Execute(new MoveLayoutAction(layoutId, target));
    }

    /// <summary>Locks or unlocks viewports (see <see cref="ViewportEntity.Locked"/>).</summary>
    public bool SetViewportLock(IEnumerable<Guid> viewportIds, bool locked)
    {
        CommitActiveViewport();
        bool ok = ModifyEntities(viewportIds, e => { if (e is ViewportEntity v) v.Locked = locked; }, locked ? "Lock viewport" : "Unlock viewport");
        RestartActiveViewport();
        if (ok) Post(locked ? "Viewport locked: its view and scale cannot be changed until it is unlocked." : "Viewport unlocked.");
        return ok;
    }

    /// <summary>The largest standard scale that still shows everything <paramref name="scale"/> shows.</summary>
    private double RoundDownToStandardScale(double scale)
    {
        foreach (double standard in LayoutStandards.Scales(Document.Settings))
            if (standard <= scale * (1 + 1e-9)) return standard;
        return scale;
    }

    public bool RenameLayout(Guid layoutId, string name)
    {
        if (Document.GetLayout(layoutId) is not { } layout) { Post("That layout does not exist."); return false; }
        name = (name ?? "").Trim();
        if (!Layout.IsValidName(name)) { Post("A layout name is 1 to 64 characters and cannot be \"Model\"."); return false; }
        if (name == layout.Name) return true;
        return Execute(new ReplaceLayoutAction("Rename layout", layout with { Name = name }));
    }

    /// <summary>Sets the paper size of a layout, in drawing units. What is drawn on it stays where it is.</summary>
    public bool SetPaperSize(Guid layoutId, double width, double height)
    {
        if (Document.GetLayout(layoutId) is not { } layout) { Post("That layout does not exist."); return false; }
        if (!(width > 0) || !(height > 0) || !double.IsFinite(width) || !double.IsFinite(height)) { Post("The paper width and height must be more than 0."); return false; }
        if (width == layout.PaperWidth && height == layout.PaperHeight) return true;
        if (!Execute(new ReplaceLayoutAction("Page setup", layout with { PaperWidth = width, PaperHeight = height }))) return false;
        Post($"Sheet: {UnitFormatter.FormatLength(width, Document.Settings)} x {UnitFormatter.FormatLength(height, Document.Settings)}.");
        return true;
    }

    /// <summary>Deletes a layout with everything drawn on it. The model is not touched. One undo step.</summary>
    public bool DeleteLayout(Guid layoutId)
    {
        if (Document.GetLayout(layoutId) is not { } layout) { Post("That layout does not exist."); return false; }
        if (Document.ActiveSpaceId == layoutId) SwitchSpace(Guid.Empty);
        var ids = Document.AllEntities.Where(e => e.SpaceId == layoutId).Select(e => e.Id).ToList();
        if (!Execute(new CompositeAction("Delete layout", new RemoveEntitiesByIdAction("Delete layout", ids), new RemoveLayoutAction(layoutId)))) return false;
        Post($"Layout \"{layout.Name}\" deleted.");
        return true;
    }

    /// <summary>Adds a viewport with the given frame to the layout being looked at. It shows the whole model.</summary>
    public bool AddViewport(BoundingBox frame)
    {
        if (Document.ActiveLayout is not { } layout) { Post("Viewports are placed on a layout."); return false; }
        var viewport = new ViewportEntity(frame.Center, frame.Width, frame.Height) { SpaceId = layout.Id };
        if (!viewport.IsValid) { Post("That viewport has no size."); return false; }
        var model = Document.GetExtents(Guid.Empty);
        if (model.IsEmpty) viewport.Scale = LayoutStandards.DefaultScale(Document.Settings);
        else
        {
            viewport.Fit(model);
            viewport.Scale = RoundDownToStandardScale(viewport.Scale);
        }
        var layerAction = ViewportLayerAction(out Guid layerId);
        viewport.LayerId = layerId;
        var add = new AddEntitiesAction("Viewport", new Entity[] { viewport });
        return Execute(layerAction is null ? add : new CompositeAction(add.Name, layerAction, add));
    }

    /// <summary>Sets the scale of viewports; the model point in the middle of each window stays there.</summary>
    public bool SetViewportScale(IEnumerable<Guid> viewportIds, double scale)
    {
        if (!(scale > 1e-9) || !double.IsFinite(scale)) { Post("The scale must be more than 0."); return false; }
        viewportIds = viewportIds.ToList();
        if (viewportIds.Any(id => Document.GetEntity(id) is ViewportEntity { Locked: true })) { Post("That viewport is locked. Unlock it first (the menu button left of the layout tabs > Lock, or VPLOCK)."); return false; }
        CommitActiveViewport();
        bool ok = ModifyEntities(viewportIds, e => { if (e is ViewportEntity v) v.Scale = scale; }, "Viewport scale");
        if (ok) Post("Viewport scale: " + LayoutStandards.ScaleName(scale, Document.Settings));
        RestartActiveViewport();
        return ok;
    }

    /// <summary>Shows the whole model in the viewports, as large as fits.</summary>
    public bool FitViewports(IEnumerable<Guid> viewportIds)
    {
        var model = Document.GetExtents(Guid.Empty);
        if (model.IsEmpty) { Post("The model is empty: there is nothing to fit."); return false; }
        viewportIds = viewportIds.ToList();
        if (viewportIds.Any(id => Document.GetEntity(id) is ViewportEntity { Locked: true })) { Post("That viewport is locked. Unlock it first (the menu button left of the layout tabs > Lock, or VPLOCK)."); return false; }
        CommitActiveViewport();
        bool ok = ModifyEntities(viewportIds, e => { if (e is ViewportEntity v) v.Fit(model); }, "Fit viewport");
        RestartActiveViewport();
        return ok;
    }

    /// <summary>
    /// Double-click inside a viewport: from now on the wheel and panning change what that viewport shows, until
    /// <see cref="ExitViewport"/>. Returns false when the point is not inside a viewport of the layout.
    /// </summary>
    public bool EnterViewportAt(Point2D paperPoint)
    {
        if (Document.ActiveLayout is null) return false;
        ExitViewport();
        // The smallest viewport under the point, so one inside another can be reached.
        var viewport = Document.EntitiesUnordered.OfType<ViewportEntity>()
            .Where(v => v.Contains(paperPoint) && Document.GetLayer(v.LayerId) is { IsEditable: true })
            .OrderBy(v => v.Width * v.Height)
            .FirstOrDefault();
        if (viewport is null) return false;
        ActiveViewportId = viewport.Id;
        _viewportBefore = (ViewportEntity)viewport.Clone();
        Post("<Viewport: the wheel and panning now move the model inside it. Double-click outside it to finish.>");
        RaiseStateChanged();
        return true;
    }

    /// <summary>Leaves the viewport that was entered. What was changed in it becomes one undo step.</summary>
    public void ExitViewport()
    {
        if (ActiveViewportId is null) return;
        CommitActiveViewport();
        ActiveViewportId = null;
        _viewportBefore = null;
        RaiseStateChanged();
    }

    private ViewportEntity? ActiveViewport => ActiveViewportId is Guid id ? Document.GetEntity(id) as ViewportEntity : null;

    /// <summary>Records what has been changed in the entered viewport so far as one undo step.</summary>
    private void CommitActiveViewport()
    {
        if (_viewportBefore is not { } before || ActiveViewport is not { } now) return;
        if (before.Scale == now.Scale && before.ModelCenter == now.ModelCenter) return;
        Execute(new ModifyEntitiesAction("Viewport view", new Entity[] { before }, new Entity[] { now }));
        _viewportBefore = (ViewportEntity)now.Clone();
    }

    private void RestartActiveViewport()
    {
        if (ActiveViewport is { } now) _viewportBefore = (ViewportEntity)now.Clone();
        else if (ActiveViewportId is not null) { ActiveViewportId = null; _viewportBefore = null; }
    }

    /// <summary>Wheel in the entered viewport: the model under <paramref name="paperAnchor"/> stays put while it gets larger or smaller.</summary>
    public bool ZoomActiveViewport(Point2D paperAnchor, double factor)
    {
        if (ActiveViewport is not { Locked: false } viewport || !(factor > 0) || !double.IsFinite(factor)) return false;
        var copy = (ViewportEntity)viewport.Clone();
        var anchorModel = copy.PaperToModel(paperAnchor);
        double scale = copy.Scale * factor;
        if (!(scale > 1e-9) || scale > 1e9) return false;
        copy.Scale = scale;
        copy.ModelCenter = anchorModel - (paperAnchor - copy.Center) / scale;
        if (!copy.IsValid) return false;
        Document.ReplaceEntity(copy);               // the undo step is recorded once, when the viewport is left
        return true;
    }

    /// <summary>Pan in the entered viewport: the model moves with the cursor by <paramref name="paperDelta"/> on the sheet.</summary>
    public bool PanActiveViewport(Vector2D paperDelta)
    {
        if (ActiveViewport is not { Locked: false } viewport) return false;
        var copy = (ViewportEntity)viewport.Clone();
        copy.ModelCenter = copy.ModelCenter - paperDelta / copy.Scale;
        if (!copy.IsValid) return false;
        Document.ReplaceEntity(copy);
        return true;
    }

    // ----------------------------------------------------------- grid lines

    /// <summary>A name for the next grid system that no layer has yet: "Grid 1", "Grid 2"...</summary>
    public string NextGridName()
    {
        for (int i = 1; ; i++)
        {
            string name = "Grid " + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Document.FindLayer(name) is null) return name;
        }
    }

    /// <summary>
    /// Adds the objects of a grid system to the drawing on the layer named after the system. The layer is made
    /// here, with <paramref name="linetype"/>, when it is not there yet. One undo step.
    /// </summary>
    public bool CreateGrid(string name, string linetype, IReadOnlyList<Entity> entities)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 64 || name.Any(char.IsControl)) { Post("Give the grid system a name (up to 64 characters)."); return false; }
        if (entities.Count == 0) { Post("That grid has no lines."); return false; }

        Guid layerId;
        IUndoableAction? layerAction = null;
        if (Document.FindLayer(name) is { } existing) layerId = existing.Id;
        else
        {
            var layer = new Layer(Guid.NewGuid(), name) { Linetype = Document.FindLinetype(linetype)?.Name ?? Linetypes.Solid };
            layerId = layer.Id;
            layerAction = new AddLayerAction(layer);
        }
        foreach (var entity in entities) entity.LayerId = layerId;

        var add = new AddEntitiesAction("Grid lines", entities);
        if (!Execute(layerAction is null ? add : new CompositeAction(add.Name, layerAction, add))) return false;
        Post($"Grid \"{name}\": {entities.Count} grid lines on layer \"{name}\".");
        return true;
    }

    /// <summary>Starts placing a grid by a click: it follows the cursor until its origin is given.</summary>
    public void BeginGridPlacement(string name, string linetype, Func<Point2D, List<Entity>> build)
    {
        if (ActiveCommand is not null) Cancel();
        _lastCommandName = "GRIDLINES";
        ActiveCommand = new PlaceGridCommand(name, linetype, build);
        Complete(ActiveCommand.Start(this));
    }

    // ------------------------------------------------------ dimension styles

    /// <summary>Adds a dimension style. False (with a message) when the name is taken or a value cannot be used.</summary>
    public bool AddDimensionStyle(DimensionStyle style)
    {
        style = style with { Name = style.Name.Trim() };
        if (style.Problem is { } problem) { Post(problem); return false; }
        if (Document.FindDimensionStyle(style.Name) is not null) { Post($"There is already a dimension style named \"{style.Name}\"."); return false; }
        // Every dimension style has a layer of the same name, so dimensions stay apart from the lines.
        var add = new AddDimensionStyleAction(style);
        return Execute(DimensionLayerAction(style.Name, out _) is { } layer ? new CompositeAction(add.Name, add, layer) : add);
    }

    /// <summary>
    /// The layer the dimensions of a style are placed on (see <see cref="DimensionLayerName"/>). When there is no such
    /// layer yet, the action that creates it is returned (to be run together with whatever needs the layer).
    /// </summary>
    public IUndoableAction? DimensionLayerAction(string styleName, out Guid layerId)
    {
        string name = DimensionLayerName(styleName);
        if (Document.FindLayer(name) is { } existing)
        {
            layerId = existing.Id;
            return null;
        }
        var layer = new Layer(Guid.NewGuid(), name);
        layerId = layer.Id;
        return new AddLayerAction(layer);
    }

    /// <summary>The layer of a dimension style, or null when it has not been created yet.</summary>
    public Layer? DimensionLayerOf(string styleName) => Document.FindLayer(DimensionLayerName(styleName));

    /// <summary>
    /// Name of the layer for the dimensions of a style: the style's name followed by "Dimension" ("Standard
    /// Dimension"), so the layer table shows what the layer is for. A style whose own name already says so
    /// ("Rebar dims") keeps its name as the layer name.
    /// </summary>
    public static string DimensionLayerName(string styleName)
    {
        string name = styleName.Trim();
        return name.Contains("dim", StringComparison.OrdinalIgnoreCase) ? name : name + " Dimension";
    }

    /// <summary>Changes the values of the style with that name; the dimensions using it follow. One undo step.</summary>
    public bool ModifyDimensionStyle(DimensionStyle style)
    {
        if (Document.FindDimensionStyle(style.Name) is not { } existing) { Post($"There is no dimension style named \"{style.Name}\"."); return false; }
        if (style.Problem is { } problem) { Post(problem); return false; }
        if (existing == style with { Name = existing.Name }) return true;
        return Execute(new ReplaceDimensionStyleAction(style));
    }

    public bool DeleteDimensionStyle(string name)
    {
        if (Document.GetDimensionStyleRemovalBlocker(name) is { } blocker) { Post(blocker); return false; }
        return Execute(new RemoveDimensionStyleAction(name.Trim()));
    }

    /// <summary>Makes a style the one new dimensions are drawn with.</summary>
    public bool SetCurrentDimensionStyle(string name)
    {
        if (Document.FindDimensionStyle(name) is not { } style) { Post($"There is no dimension style named \"{name}\"."); return false; }
        if (style.Name == Document.CurrentDimensionStyleName) return true;
        return Execute(new SetCurrentDimensionStyleAction(Document.CurrentDimensionStyleName, style.Name));
    }

    /// <summary>Gives the selected dimensions another style.</summary>
    public bool SetDimensionStyleOfSelection(string name)
    {
        if (Document.FindDimensionStyle(name) is not { } style) { Post($"There is no dimension style named \"{name}\"."); return false; }
        var before = GetSelectedEntities().OfType<DimensionEntity>().ToList();
        if (before.Count == 0) return false;
        // The dimensions move to the layer of their new style as well.
        var layerAction = DimensionLayerAction(style.Name, out Guid layerId);
        var after = before.Select(d =>
        {
            var copy = (DimensionEntity)d.Clone();
            copy.Style = style;
            copy.LayerId = layerId;
            return (Entity)copy;
        }).ToList();
        var modify = new ModifyEntitiesAction("Change dimension style", before, after);
        return Execute(layerAction is null ? modify : new CompositeAction(modify.Name, layerAction, modify));
    }

    /// <summary>Sets the drawing's linetype scale (all dash and gap lengths are multiplied by it).</summary>
    public bool SetLinetypeScale(double scale)
    {
        if (!(scale > 0) || !double.IsFinite(scale)) { Post("The linetype scale must be more than 0."); return false; }
        var after = Document.Settings.Clone();
        after.LinetypeScale = scale;
        return Execute(new ModifySettingsAction("Linetype scale", Document.Settings, after));
    }

    /// <summary>
    /// Deletes every layer that can be deleted: no objects on it, not the current layer and not layer 0.
    /// One undo step. Returns how many layers were removed.
    /// </summary>
    public int PurgeLayers()
    {
        var unused = Document.Layers.Where(l => Document.GetLayerRemovalBlocker(l.Id) is null).Select(l => l.Id).ToList();
        if (unused.Count == 0)
        {
            Post("No unused layers to remove.");
            return 0;
        }
        var actions = unused.Select(id => (IUndoableAction)new RemoveLayerAction(id)).ToArray();
        if (!Execute(new CompositeAction("Purge layers", actions))) return 0;
        Post($"{unused.Count} unused layer(s) removed.");
        return unused.Count;
    }

    public bool SetCurrentLayer(Guid layerId)
    {
        if (Document.CurrentLayerId == layerId) return true;
        if (Document.GetLayer(layerId) is null) return false;
        return Execute(new SetCurrentLayerAction(Document.CurrentLayerId, layerId));
    }

    /// <summary>Applies a change to a layer as one undoable step.</summary>
    public bool UpdateLayer(Guid layerId, Action<Layer> change)
    {
        var before = Document.GetLayer(layerId);
        if (before is null) return false;
        var after = before.Clone();
        change(after);
        after.Name = after.Name.Trim();
        if (after.Name.Length == 0) { Post("Layer name is required."); return false; }
        if (!after.Color.IsExplicit) { Post("A layer needs an explicit colour."); return false; }
        if (!(after.LineweightMm >= 0) || !double.IsFinite(after.LineweightMm)) { Post("Invalid lineweight."); return false; }
        return Execute(new ModifyLayerAction(before, after));
    }

    // ------------------------------------------------------------- entities

    /// <summary>Applies a change to copies of the given entities and commits it as one undoable step.</summary>
    public bool ModifyEntities(IEnumerable<Guid> ids, Action<Entity> change, string actionName)
    {
        var before = new List<Entity>();
        var after = new List<Entity>();
        foreach (var id in ids)
        {
            var entity = Document.GetEntity(id);
            if (entity is null) continue;
            var copy = entity.Clone();
            change(copy);
            if (!copy.IsValid) { Post("That value would make the geometry invalid."); return false; }
            if (Document.GetLayer(copy.LayerId) is null) { Post("Layer does not exist."); return false; }
            before.Add(entity);
            after.Add(copy);
        }
        if (before.Count == 0) return false;
        return Execute(new ModifyEntitiesAction(actionName, before, after));
    }
}
