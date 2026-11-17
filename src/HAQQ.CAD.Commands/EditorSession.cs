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

/// <summary>An active polar tracking alignment: the cursor is locked onto a ray from <paramref name="From"/>.</summary>
public readonly record struct PolarTrack(Point2D From, double AngleDegrees, Point2D Point);

/// <summary>Maps command names and aliases to command factories.</summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, (string Canonical, Func<ICadCommand> Factory)> _map =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(string name, Func<ICadCommand> factory, params string[] aliases)
    {
        _map[name] = (name, factory);
        foreach (string alias in aliases) _map[alias] = (name, factory);
    }

    public bool TryCreate(string nameOrAlias, out ICadCommand? command, out string canonicalName)
    {
        if (_map.TryGetValue(nameOrAlias.Trim(), out var entry))
        {
            command = entry.Factory();
            canonicalName = entry.Canonical;
            return true;
        }
        command = null;
        canonicalName = "";
        return false;
    }

    public IEnumerable<string> CommandNames => _map.Values.Select(v => v.Canonical).Distinct().OrderBy(n => n);

    public static CommandRegistry CreateDefault()
    {
        var r = new CommandRegistry();
        r.Register("LINE", () => new LineCommand(), "L");
        r.Register("PLINE", () => new PolylineCommand(), "PL", "POLYLINE");
        r.Register("RECTANG", () => new RectangleCommand(), "REC", "RECTANGLE");
        r.Register("CIRCLE", () => new CircleCommand(), "C");
        r.Register("MOVE", () => new MoveCommand(), "M");
        r.Register("COPY", () => new CopyCommand(), "CO", "CP");
        r.Register("ERASE", () => new EraseCommand(), "E", "DELETE", "DEL");
        r.Register("ARC", () => new ArcCommand(), "A");
        r.Register("ROTATE", () => new RotateCommand(), "RO");
        r.Register("SCALE", () => new ScaleCommand(), "SC");
        r.Register("MIRROR", () => new MirrorCommand(), "MI");
        r.Register("LINEMODE", () => new LineModeCommand());
        r.Register("-LINETYPE", () => new LinetypeCommand(), "-LT", "-LTYPE");      // typed version; LINETYPE opens the window
        r.Register("LTSCALE", () => new LinetypeScaleCommand(), "LTS");
        r.Register("OFFSET", () => new OffsetCommand(), "O");
        r.Register("TRIM", () => new TrimCommand(), "TR");
        r.Register("EXTEND", () => new ExtendCommand(), "EX");
        r.Register("FILLET", () => new FilletCommand(), "F");
        r.Register("CHAMFER", () => new ChamferCommand(), "CHA");
        r.Register("EXPLODE", () => new ExplodeCommand(), "X");
        r.Register("JOIN", () => new JoinCommand(), "J");
        r.Register("UNITS", () => new UnitsCommand(), "UN");
        // Grid snap (SNAP, F9) was taken out of the program on the owner's request (17 Nov 2026): object snap covers it.
        r.Register("GRID", () => new SpacingCommand(snap: false));
        r.Register("CURSORSIZE", () => new CursorSizeCommand(), "CS", "CROSSHAIR");
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

    public void Post(string message) => MessagePosted?.Invoke(message);

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        Selection.Prune(id => Document.GetEntity(id) is { } entity && SelectionService.IsSelectable(Document, entity));
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

        switch (name.ToUpperInvariant())
        {
            case "U":
            case "UNDO":
                Undo();
                return true;
            case "REDO":
                Redo();
                return true;
            case "QUIT":
            case "EXIT":
                ViewCommandRequested?.Invoke("Quit");
                return true;
            case "LAYER":
            case "LA":
            case "LAYERS":
                Post("Command: LAYER");
                ViewCommandRequested?.Invoke("Layers");
                return true;
            case "LINETYPE":
            case "LT":
            case "LTYPE":
                Post("Command: LINETYPE");
                ViewCommandRequested?.Invoke("Linetypes");
                return true;
            case "Z":
            case "ZE":
            case "ZA":
            case "ZOOM":
                Post("Command: ZOOM (extents)");
                ViewCommandRequested?.Invoke("ZoomExtents");
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
            SnapOverride = null;
            CurrentSnap = null;
            Post("*Cancel*");
            RaiseStateChanged();
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
        if (input is CommandInput.PointInput typed) LastPoint = typed.Point;
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
        if (toggle && Selection.Contains(entity.Id)) Selection.Remove(entity.Id);
        else Selection.Add(entity.Id);
        return true;
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
