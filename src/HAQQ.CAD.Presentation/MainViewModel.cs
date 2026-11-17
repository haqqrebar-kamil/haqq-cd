using System.Collections.ObjectModel;
using System.Globalization;
using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Documents;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Snapping;

namespace HAQQ.CAD.Presentation;

/// <summary>
/// View model of the main window: owns the editing session and exposes commands, panels,
/// command-line state and file operations to the view. Contains no WPF types.
/// </summary>
/// <summary>One line of the canvas right-click menu.</summary>
/// <param name="Children">Entries of a sub-menu; when set, <paramref name="Invoke"/> does nothing.</param>
/// <param name="IsChecked">Null for a plain entry; otherwise a check-box entry and its current state.</param>
public sealed record ContextMenuEntry(string Label, Action Invoke, IReadOnlyList<ContextMenuEntry>? Children = null, bool? IsChecked = null);

/// <summary>What the coordinate read-out in the status bar shows. Click it to cycle.</summary>
public enum CoordinateDisplayMode
{
    Absolute,
    RelativePolar,
    Off,
}

/// <summary>Content of the input boxes drawn beside the crosshair.</summary>
/// <param name="From">The previous point, when the fields are a distance and an angle measured from it.</param>
/// <param name="AngleDegrees">Counter-clockwise angle from +X to the cursor, 0 to 360, when <paramref name="From"/> is set.</param>
/// <param name="PolarText">"Polar: distance &lt; angle" while polar tracking has locked the cursor, else empty.</param>
public sealed record DynamicInputInfo(string Prompt, string Field1, string Field2, bool IsTyping,
    Point2D? From = null, Point2D To = default, double AngleDegrees = 0, string PolarText = "", int ActiveField = 0);

public sealed class MainViewModel : ObservableObject
{
    private const int MaxHistoryLines = 500;

    private readonly IUserInteraction _ui;
    private string _commandText = "";
    private string _prompt = "Command:";
    private string _title = "HAQQ CAD";
    private string _coordinateText = "";
    private string _statusText = "";
    private string? _filePath;
    private bool _gridOn = true;
    private LayerViewModel? _selectedLayer;
    private long _autosavedStamp = -1;
    private long _explorerStamp = -1;
    private Guid _explorerDocument;

    private readonly UserSettingsStore _settingsStore;
    private readonly UserSettings _userSettings;

    /// <param name="settings">Where program preferences are kept; omitted, they last only for this session.</param>
    public MainViewModel(IUserInteraction ui, RecoveryStore? recovery = null, UserSettingsStore? settings = null)
    {
        _ui = ui;
        Recovery = recovery ?? new RecoveryStore(RecoveryStore.DefaultDirectory);
        _settingsStore = settings ?? new UserSettingsStore();
        _userSettings = _settingsStore.Load();
        Session = new EditorSession(CreateNewDrawing()) { CrosshairSizePercent = _userSettings.CrosshairPercent };
        if (_userSettings.SnapModes is { } savedSnaps) Session.Snap.Modes = (SnapModes)savedSnaps & SnapSettings.AllModes;
        Properties = new PropertiesViewModel(Session);
        DimensionBar = new DimensionBarViewModel(Session);
        if (_userSettings.Shortcuts.Count > 0 &&
            Session.SetShortcuts(_userSettings.Shortcuts.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value)) is { } shortcutProblem)
            _startupNote = "Your saved shortcuts could not be used (" + shortcutProblem + ") The standard ones are in use.";

        Session.MessagePosted += AddHistory;
        Session.StateChanged += (_, _) => Refresh();
        Session.DocumentReplaced += (_, _) => ZoomExtentsRequested?.Invoke(this, EventArgs.Empty);
        Session.ViewCommandRequested += name =>
        {
            if (name == "ZoomExtents") ZoomExtentsRequested?.Invoke(this, EventArgs.Empty);
            if (name == "Quit") CloseRequested?.Invoke(this, EventArgs.Empty);
            if (name == "Layers") LayersRequested?.Invoke(this, EventArgs.Empty);
            if (name == "Linetypes") LinetypesRequested?.Invoke(this, EventArgs.Empty);
            if (name == "Shortcuts") ShortcutsRequested?.Invoke(this, EventArgs.Empty);
            if (name == "DimensionStyles") DimensionStylesRequested?.Invoke(this, EventArgs.Empty);
            if (name == "GridLines") GridLinesRequested?.Invoke(this, EventArgs.Empty);
        };

        RunCommandCommand = new RelayCommand(p => RunCommand(p as string ?? ""));
        NewCommand = new RelayCommand(() => NewDocument());
        OpenCommand = new RelayCommand(() => Open());
        SaveCommand = new RelayCommand(() => Save());
        SaveAsCommand = new RelayCommand(() => SaveAs());
        UndoCommand = new RelayCommand(() => Session.Undo(), () => Session.UndoStack.CanUndo || Session.ActiveCommand is not null);
        RedoCommand = new RelayCommand(() => Session.Redo(), () => Session.UndoStack.CanRedo);
        ZoomExtentsCommand = new RelayCommand(() => ZoomExtentsRequested?.Invoke(this, EventArgs.Empty));
        AddLayerCommand = new RelayCommand(AddLayer);
        DeleteLayerCommand = new RelayCommand(DeleteSelectedLayer, () => SelectedLayer is not null);
        SetCurrentLayerCommand = new RelayCommand(SetSelectedLayerCurrent, () => SelectedLayer is not null);
        SetLayerColorCommand = new RelayCommand(p => SetSelectedLayerColor(p as string));
        PurgeLayersCommand = new RelayCommand(() => Session.PurgeLayers());
        CycleCoordinateModeCommand = new RelayCommand(CycleCoordinateMode);

        AddHistory("HAQQ CAD ready. Type a command (LINE, PLINE, RECTANG, CIRCLE, ARC, MOVE, COPY, OFFSET, TRIM, EXTEND, FILLET, ERASE) or use the ribbon.");
        if (_startupNote is not null) AddHistory(_startupNote);
        Refresh();
    }

    private readonly string? _startupNote;

    /// <summary>The SHORTCUTS command asked the shell to show the shortcut settings.</summary>
    public event EventHandler? ShortcutsRequested;

    /// <summary>
    /// Sets the user's command shortcuts and keeps them for next time. Returns the reason when they cannot be
    /// used (nothing changes then), or null.
    /// </summary>
    public string? ApplyShortcuts(IReadOnlyDictionary<string, IReadOnlyList<string>> custom)
    {
        if (Session.SetShortcuts(custom) is { } problem) return problem;
        _userSettings.Shortcuts = Session.CustomShortcuts.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        if (!_settingsStore.Save(_userSettings)) AddHistory("The shortcuts could not be saved for next time.");
        AddHistory("Shortcuts updated.");
        return null;
    }

    public EditorSession Session { get; }
    public RecoveryStore Recovery { get; }
    public PropertiesViewModel Properties { get; }

    /// <summary>The dimension style strip of the ribbon.</summary>
    public DimensionBarViewModel DimensionBar { get; }

    /// <summary>The GRIDLINES command asked the shell to show the grid line wizard.</summary>
    public event EventHandler? GridLinesRequested;

    /// <summary>The DIMSTYLE command asked the shell to show the dimension settings.</summary>
    public event EventHandler? DimensionStylesRequested;

    public ObservableCollection<LayerViewModel> Layers { get; } = new();

    /// <summary>Names of the drawing's linetypes (built-in and custom), for the layer table.</summary>
    public ObservableCollection<string> LinetypeNames { get; } = new();

    /// <summary>Brings <paramref name="target"/> in line with <paramref name="wanted"/> with as few changes as possible, so that lists bound to it keep their selection.</summary>
    internal static void SyncNames(ObservableCollection<string> target, IReadOnlyList<string> wanted)
    {
        if (target.SequenceEqual(wanted)) return;
        for (int i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && target[i] == wanted[i]) continue;
            int at = target.IndexOf(wanted[i]);
            if (at >= 0) target.Move(at, i); else target.Insert(i, wanted[i]);
        }
    }
    public ObservableCollection<string> History { get; } = new();
    public ObservableCollection<string> ExplorerItems { get; } = new();

    /// <summary>Standard colours offered for layers (#RRGGBB).</summary>
    public IReadOnlyList<string> LayerColorPalette { get; } = new[]
    {
        "#FFFFFF", "#FF0000", "#FFFF00", "#00FF00", "#00FFFF", "#0000FF", "#FF00FF",
        "#FF7F00", "#808080", "#C0C0C0", "#7FDF7F", "#7FBFFF", "#FF7F7F", "#BF7FFF",
    };

    public RelayCommand RunCommandCommand { get; }
    public RelayCommand NewCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand ZoomExtentsCommand { get; }
    public RelayCommand AddLayerCommand { get; }
    public RelayCommand DeleteLayerCommand { get; }
    public RelayCommand SetCurrentLayerCommand { get; }
    public RelayCommand SetLayerColorCommand { get; }
    public RelayCommand PurgeLayersCommand { get; }
    public RelayCommand CycleCoordinateModeCommand { get; }

    /// <summary>The QUIT command asked the shell to close the window.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The LAYER command asked the shell to show the layer manager.</summary>
    public event EventHandler? LayersRequested;

    /// <summary>The LINETYPE command asked the shell to show the Linetype Manager.</summary>
    public event EventHandler? LinetypesRequested;

    /// <summary>The canvas should repaint.</summary>
    public event EventHandler? RedrawRequested;

    /// <summary>The canvas should fit the drawing in the view.</summary>
    public event EventHandler? ZoomExtentsRequested;

    // ---------------------------------------------------------- bound state

    public string CommandText
    {
        get => _commandText;
        set
        {
            // The canvas shows what is being typed next to the crosshair.
            if (SetProperty(ref _commandText, value ?? "")) RedrawRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// What to show next to the crosshair while a command waits for a point: the prompt and either
    /// the text being typed, the cursor coordinates (first point) or distance and angle from the
    /// previous point. Null when no point is being requested.
    /// </summary>
    public DynamicInputInfo? GetDynamicInput()
    {
        if (!Session.WantsPoint || Session.ActiveCommand is not { } command) return null;

        string prompt = command.Prompt;
        int split = prompt.IndexOf("  ", StringComparison.Ordinal);
        if (split >= 0) prompt = prompt[(split + 2)..];

        var settings = Session.Document.Settings;
        var cursor = Session.EffectiveCursor;

        if (_fieldMode)
        {
            // Two separate boxes (Tab switches): distance and angle, or X and Y for a first point.
            // An empty box shows the live value from the cursor, which is what will be used for it.
            string first = _activeField == 0 ? CommandText : _fields[0];
            string second = _activeField == 1 ? CommandText : _fields[1];
            if (command.LastPoint is { } origin)
            {
                var d = cursor - origin;
                double liveAngle = GeoMath.RadiansToDegrees(d.Angle);
                if (liveAngle < 0) liveAngle += 360.0;
                if (d.Length < GeoMath.Epsilon) liveAngle = 0;
                if (first.Length == 0) first = UnitFormatter.FormatLength(d.Length, settings);
                if (second.Length == 0) second = liveAngle.ToString("0.#", CultureInfo.InvariantCulture);
                return new DynamicInputInfo(prompt, first, "< " + second + "\u00B0", true, origin, cursor, liveAngle, "", _activeField);
            }
            if (first.Length == 0) first = UnitFormatter.FormatLength(cursor.X, settings);
            if (second.Length == 0) second = UnitFormatter.FormatLength(cursor.Y, settings);
            return new DynamicInputInfo(prompt, first, second, true, null, cursor, 0, "", _activeField);
        }

        if (CommandText.Length > 0) return new DynamicInputInfo(prompt, CommandText, "", true, command.LastPoint, cursor);

        if (command.LastPoint is not { } from)
            return new DynamicInputInfo(prompt, UnitFormatter.FormatLength(cursor.X, settings), UnitFormatter.FormatLength(cursor.Y, settings), false);

        var delta = cursor - from;
        double degrees = GeoMath.RadiansToDegrees(delta.Angle);
        if (degrees < 0) degrees += 360.0;
        if (delta.Length < GeoMath.Epsilon) degrees = 0;
        string lengthText = UnitFormatter.FormatLength(delta.Length, settings);
        string angleText = "< " + degrees.ToString("0.#", CultureInfo.InvariantCulture) + "\u00B0";
        string polar = Session.CurrentObjectTrack is not null ? "Track"
            : Session.CurrentPolarTrack is null ? "" : $"Polar: {lengthText} {angleText}";
        return new DynamicInputInfo(prompt, lengthText, angleText, false, from, cursor, degrees, polar);
    }

    public string Prompt
    {
        get => _prompt;
        private set => SetProperty(ref _prompt, value);
    }

    /// <summary>Whether the Properties panel is folded away; starts folded, and the choice is remembered per user.</summary>
    public bool PropertiesCollapsed
    {
        get => _userSettings.PropertiesCollapsed;
        set
        {
            if (_userSettings.PropertiesCollapsed == value) return;
            _userSettings.PropertiesCollapsed = value;
            _settingsStore.Save(_userSettings);
            OnPropertyChanged();
        }
    }

    private string _updateStatus = "";

    /// <summary>What the automatic update is doing right now ("Downloading update... 40%"); empty when idle. Shown in the status bar.</summary>
    public string UpdateStatus
    {
        get => _updateStatus;
        set => SetProperty(ref _updateStatus, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string CoordinateText
    {
        get => _coordinateText;
        private set => SetProperty(ref _coordinateText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? FilePath
    {
        get => _filePath;
        private set
        {
            if (SetProperty(ref _filePath, value)) OnPropertyChanged(nameof(DocumentName));
        }
    }

    public string DocumentName => FilePath is null ? "Untitled" : Path.GetFileName(FilePath);

    public bool OsnapOn
    {
        get => Session.Snap.Enabled;
        set
        {
            if (Session.Snap.Enabled == value) return;
            Session.Snap.Enabled = value;
            OnPropertyChanged();
            AddHistory(value ? "<Osnap on>" : "<Osnap off>");
            RedrawRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>OTRACK: object snap tracking (F11).</summary>
    public bool TrackOn
    {
        get => Session.TrackingEnabled;
        set
        {
            if (Session.TrackingEnabled == value) return;
            Session.TrackingEnabled = value;
            OnPropertyChanged();
            AddHistory(value ? "<Otrack on>" : "<Otrack off>");
            RedrawRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>POLAR tracking on horizontal and vertical directions (F10).</summary>
    public bool PolarOn
    {
        get => Session.PolarEnabled;
        set
        {
            if (Session.PolarEnabled == value) return;
            Session.PolarEnabled = value;
            OnPropertyChanged();
            AddHistory(value ? "<Polar on>" : "<Polar off>");
            RedrawRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public CoordinateDisplayMode CoordinateMode { get; private set; }

    private Point2D _lastCoordinatePoint;

    private void CycleCoordinateMode()
    {
        CoordinateMode = (CoordinateDisplayMode)(((int)CoordinateMode + 1) % 3);
        OnPropertyChanged(nameof(CoordinateMode));
        UpdateCoordinates(_lastCoordinatePoint);
    }

    /// <summary>
    /// Length in pixels of each crosshair arm for a drawing area of the given size.
    /// 0 means the arms run to the edges (crosshair size 100).
    /// </summary>
    public double GetCrosshairArmLength(double viewWidth, double viewHeight)
    {
        int percent = Session.CrosshairSizePercent;
        if (percent >= 100) return 0;
        return Math.Max(8.0, Math.Max(viewWidth, viewHeight) * percent / 200.0);
    }

    /// <summary>Entries for the right-click menu on the canvas, for the current state.</summary>
    public IReadOnlyList<ContextMenuEntry> GetContextMenu()
    {
        var items = new List<ContextMenuEntry>();
        if (Session.ActiveCommand is { } command)
        {
            items.Add(new ContextMenuEntry("Enter", () => Session.SubmitEnter()));
            items.Add(new ContextMenuEntry("Cancel", Cancel));
            foreach (string option in command.Options)
            {
                string keyword = option;
                items.Add(new ContextMenuEntry(keyword, () => Session.SubmitKeyword(keyword)));
            }
            if (Session.WantsPoint)
            {
                // One snap for the next point only, whatever the OSNAP settings are.
                items.Add(new ContextMenuEntry("Snap Overrides", () => { }, GetSnapOverrideEntries()));
            }
            items.Add(new ContextMenuEntry("Object Snap Settings", () => { }, GetSnapSettingsMenu()));
            return items;
        }

        if (Session.LastCommandName is { } last)
            items.Add(new ContextMenuEntry("Repeat " + last, () => Session.StartCommand(last)));
        if (Session.UndoStack.CanUndo) items.Add(new ContextMenuEntry("Undo " + Session.UndoStack.NextUndoName, () => Session.Undo()));
        if (Session.UndoStack.CanRedo) items.Add(new ContextMenuEntry("Redo " + Session.UndoStack.NextRedoName, () => Session.Redo()));
        if (Session.Selection.Count > 0) items.Add(new ContextMenuEntry("Erase", DeleteSelection));
        items.Add(new ContextMenuEntry("Zoom Extents", () => ZoomExtentsRequested?.Invoke(this, EventArgs.Empty)));
        items.Add(new ContextMenuEntry("Object Snap Settings", () => { }, GetSnapSettingsMenu()));
        return items;
    }

    private List<ContextMenuEntry> GetSnapOverrideEntries() =>
        SnapSettings.MenuOrder
            .Select(mode => new ContextMenuEntry(SnapEngine.NameOf(mode), () => SetSnapOverride(mode)))
            .Append(new ContextMenuEntry("None", () => SetSnapOverride(SnapModes.None)))
            .ToList();

    /// <summary>
    /// The object snap menu opened with Shift + right-click: while a command asks for a point, the snaps to use
    /// for that one point; otherwise the check boxes for the automatic snaps.
    /// </summary>
    public IReadOnlyList<ContextMenuEntry> GetObjectSnapMenu()
    {
        if (!Session.WantsPoint || Session.PickingObject) return GetSnapSettingsMenu();
        var items = GetSnapOverrideEntries();
        items.Add(new ContextMenuEntry("Object Snap Settings", () => { }, GetSnapSettingsMenu()));
        return items;
    }

    /// <summary>Check-box entries for the snaps that act automatically while OSNAP is on (also shown by right-clicking OSNAP).</summary>
    public IReadOnlyList<ContextMenuEntry> GetSnapSettingsMenu() =>
        SnapSettings.MenuOrder
            .Select(mode => new ContextMenuEntry(SnapEngine.NameOf(mode), () => SetRunningSnap(mode, !IsRunningSnap(mode)), null, IsRunningSnap(mode)))
            .ToList();

    public bool IsRunningSnap(SnapModes mode) => (Session.Snap.Modes & mode) == mode;

    /// <summary>Turns one automatic object snap on or off and remembers it for next time.</summary>
    public void SetRunningSnap(SnapModes mode, bool on)
    {
        var modes = on ? Session.Snap.Modes | mode : Session.Snap.Modes & ~mode;
        if (modes == Session.Snap.Modes) return;
        Session.Snap.Modes = modes;
        _userSettings.SnapModes = (int)modes;
        if (!_settingsStore.Save(_userSettings)) AddHistory("The object snap settings could not be saved for next time.");
        AddHistory($"<{SnapEngine.NameOf(mode)} snap {(on ? "on" : "off")}>");
        Session.UpdateCursor(Session.Cursor, Session.PickTolerance);
        RedrawRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Uses one snap for the next point only.</summary>
    public void SetSnapOverride(SnapModes mode)
    {
        if (!Session.WantsPoint) return;
        Session.SetSnapOverride(mode);
        AddHistory(mode == SnapModes.None ? "Snap override: none (next point only)" : $"Snap override: {SnapEngine.NameOf(mode)} (next point only)");
        RedrawRequested?.Invoke(this, EventArgs.Empty);
    }

    public bool OrthoOn
    {
        get => Session.OrthoEnabled;
        set
        {
            if (Session.OrthoEnabled == value) return;
            Session.OrthoEnabled = value;
            OnPropertyChanged();
            AddHistory(value ? "<Ortho on>" : "<Ortho off>");
            RedrawRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool GridOn
    {
        get => _gridOn;
        set
        {
            if (!SetProperty(ref _gridOn, value)) return;
            AddHistory(value ? "<Grid on>" : "<Grid off>");
            RedrawRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public LayerViewModel? SelectedLayer
    {
        get => _selectedLayer;
        set
        {
            if (!SetProperty(ref _selectedLayer, value)) return;
            DeleteLayerCommand.RaiseCanExecuteChanged();
            SetCurrentLayerCommand.RaiseCanExecuteChanged();
        }
    }

    public string CurrentLayerName => Session.Document.CurrentLayer.Name;

    /// <summary>Plot switch of the current layer (shown on the HOME tab).</summary>
    public bool CurrentLayerPlots
    {
        get => Session.Document.CurrentLayer.IsPlotted;
        set
        {
            if (value == Session.Document.CurrentLayer.IsPlotted) return;
            if (!Session.UpdateLayer(Session.Document.CurrentLayerId, l => l.IsPlotted = value)) OnPropertyChanged();
        }
    }

    // -------------------------------------------------------- command line

    public void AddHistory(string line)
    {
        History.Add(line);
        while (History.Count > MaxHistoryLines) History.RemoveAt(0);
    }

    public void RunCommand(string name)
    {
        ResetFields();
        if (string.IsNullOrWhiteSpace(name)) return;

        // "CIRCLE 2P" starts the command and then chooses the option, as the ribbon drop-downs do.
        string[] parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!Session.StartCommand(parts[0])) return;
        for (int i = 1; i < parts.Length; i++) Session.SubmitKeyword(parts[i]);
    }

    // ---- Tab between the two input boxes at the crosshair (distance / angle, or X / Y) ----

    private readonly string[] _fields = { "", "" };
    private bool _fieldMode;
    private int _activeField;

    /// <summary>True once Tab has been pressed for the current point, so input is taken from two separate boxes.</summary>
    public bool IsFieldInputActive => _fieldMode;

    /// <summary>
    /// Tab while a command asks for a point: keeps what was typed in the current box and moves to the
    /// other one (distance then angle for a following point, X then Y for a first point).
    /// Returns false when no point is being requested.
    /// </summary>
    public bool SwitchInputField()
    {
        if (!Session.WantsPoint) return false;
        _fields[_activeField] = CommandText.Trim();
        _fieldMode = true;
        _activeField = 1 - _activeField;
        CommandText = _fields[_activeField];
        RedrawRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void ResetFields()
    {
        _fieldMode = false;
        _activeField = 0;
        _fields[0] = _fields[1] = "";
    }

    /// <summary>Turns the two boxes into ordinary coordinate input. A box left empty takes its value from the cursor.</summary>
    private string ComposeFieldInput()
    {
        _fields[_activeField] = CommandText.Trim();
        string first = _fields[0], second = _fields[1];
        if (first.Length == 0 && second.Length == 0) return "";

        var cursor = Session.EffectiveCursor;
        string Exact(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        if (Session.ActiveCommand?.LastPoint is { } from)
        {
            if (second.Length == 0) return first;                       // distance only: direct distance entry
            if (first.Length == 0) first = Exact((cursor - from).Length); // angle only: distance from the cursor
            return "@" + first + "<" + second;
        }

        if (first.Length == 0) first = Exact(cursor.X);
        if (second.Length == 0) second = Exact(cursor.Y);
        return first + "," + second;
    }

    /// <summary>
    /// A click on the canvas while a command asks for a point. Values locked in the input boxes are
    /// honoured (for example a typed distance with the direction taken from the cursor).
    /// </summary>
    public void ClickPoint()
    {
        if (_fieldMode)
        {
            SubmitCommandLine();
            return;
        }
        if (Session.SnapOverride is { } wanted && wanted != SnapModes.None && Session.CurrentSnap is null)
        {
            // An override asks for one particular snap; a click that finds none is not accepted as a free point.
            AddHistory($"No {SnapEngine.NameOf(wanted)} point found there. Snap override cancelled; pick the point again.");
            Session.SetSnapOverride(null);
            return;
        }
        CommandText = "";
        Session.SubmitPoint(Session.EffectiveCursor);
    }

    /// <summary>Enter on the command line: sends the typed text (or a bare Enter) to the session.</summary>
    public void SubmitCommandLine()
    {
        string text = _fieldMode ? ComposeFieldInput() : CommandText;
        ResetFields();
        CommandText = "";
        if (text.Trim().Length > 0) AddHistory(Session.Prompt + " " + text.Trim());
        Session.SubmitText(text);
    }

    public void Cancel()
    {
        ResetFields();
        CommandText = "";
        Session.Cancel();
    }

    /// <summary>Delete key: erases the selection when no command is running.</summary>
    public void DeleteSelection()
    {
        if (Session.ActiveCommand is null && Session.Selection.Count > 0) Session.StartCommand("ERASE");
    }

    public void UpdateCoordinates(Point2D modelPoint)
    {
        _lastCoordinatePoint = modelPoint;
        var settings = Session.Document.Settings;
        string absolute = UnitFormatter.FormatLength(modelPoint.X, settings) + ", " + UnitFormatter.FormatLength(modelPoint.Y, settings);
        switch (CoordinateMode)
        {
            case CoordinateDisplayMode.Off:
                CoordinateText = "(coordinates off)";
                break;
            case CoordinateDisplayMode.RelativePolar when Session.ActiveCommand?.LastPoint is { } from:
                var delta = modelPoint - from;
                double degrees = GeoMath.RadiansToDegrees(delta.Angle);
                if (degrees < 0) degrees += 360.0;
                if (delta.Length < GeoMath.Epsilon) degrees = 0;
                CoordinateText = UnitFormatter.FormatLength(delta.Length, settings) + " < " + degrees.ToString("0.#", CultureInfo.InvariantCulture) + "\u00B0";
                break;
            default:
                CoordinateText = absolute; // also used in polar mode until a command has a previous point
                break;
        }
    }

    // --------------------------------------------------------------- layers

    /// <summary>Name of the layer a new drawing starts on.</summary>
    public const string FirstLayerName = "Layer 1";

    /// <summary>
    /// A new, unmodified drawing. Besides the permanent layer 0 it has "Layer 1", which is the current
    /// layer, so new objects go on Layer 1 and layer 0 stays free (as DWG/DXF exchange expects it to exist).
    /// </summary>
    public static DrawingDocument CreateNewDrawing()
    {
        var document = new DrawingDocument();
        var layer = new Layer(Guid.NewGuid(), FirstLayerName);
        document.AddLayer(layer);
        document.CurrentLayerId = layer.Id;
        document.MarkSaved();
        return document;
    }

    private void AddLayer()
    {
        var layer = Session.AddLayer();
        if (layer is not null) SelectedLayer = Layers.FirstOrDefault(l => l.Id == layer.Id);
    }

    private void DeleteSelectedLayer()
    {
        if (SelectedLayer is { } layer) Session.DeleteLayer(layer.Id);
    }

    private void SetSelectedLayerCurrent()
    {
        if (SelectedLayer is { } layer) Session.SetCurrentLayer(layer.Id);
    }

    private void SetSelectedLayerColor(string? hex)
    {
        if (SelectedLayer is { } layer && hex is not null) layer.ColorHex = hex;
    }

    // ---------------------------------------------------------------- files

    public bool NewDocument()
    {
        if (!ConfirmDiscardChanges()) return false;
        Recovery.DeleteSnapshot(Session.Document.Id);
        FilePath = null;
        Session.LoadDocument(CreateNewDrawing());
        AddHistory("New drawing.");
        return true;
    }

    public bool Open()
    {
        if (!ConfirmDiscardChanges()) return false;
        string? path = _ui.PickOpenFile();
        return path is not null && OpenFile(path);
    }

    /// <summary>Opens an HCAD file without asking about unsaved changes (the caller already has).</summary>
    public bool OpenFile(string path)
    {
        HcadLoadResult result;
        try
        {
            result = HcadFileStore.Load(path);
        }
        catch (Exception ex) when (ex is HcadFormatException or IOException or UnauthorizedAccessException)
        {
            _ui.ShowError($"Could not open \"{path}\".\n\n{ex.Message}", "Open drawing");
            return false;
        }

        Recovery.DeleteSnapshot(Session.Document.Id);
        FilePath = Path.GetFullPath(path);
        Session.LoadDocument(result.Document);
        AddHistory($"Opened {FilePath} ({result.Document.EntityCount} objects).");
        ReportWarnings(result.Warnings);
        return true;
    }

    private void ReportWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return;
        foreach (string w in warnings) AddHistory("Warning: " + w);
        string shown = string.Join("\n", warnings.Take(12));
        if (warnings.Count > 12) shown += $"\n... and {warnings.Count - 12} more (see the command history).";
        _ui.ShowInfo($"The drawing was opened with {warnings.Count} warning(s):\n\n{shown}", "Open drawing");
    }

    public bool Save() => FilePath is null ? SaveAs() : SaveTo(FilePath);

    public bool SaveAs()
    {
        string? path = _ui.PickSaveFile(FilePath is null ? "Drawing1" + HcadFileStore.Extension : Path.GetFileName(FilePath));
        if (path is null) return false;
        if (Path.GetExtension(path).Length == 0) path += HcadFileStore.Extension;
        return SaveTo(path);
    }

    public bool SaveTo(string path)
    {
        // Finish any running command first so in-progress work is part of what is saved.
        if (Session.ActiveCommand is not null) Session.Cancel();
        try
        {
            HcadFileStore.Save(Session.Document, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _ui.ShowError($"Could not save \"{path}\".\n\n{ex.Message}\n\nThe previous file was not changed.", "Save drawing");
            return false;
        }

        FilePath = Path.GetFullPath(path);
        Recovery.DeleteSnapshot(Session.Document.Id);
        AddHistory("Saved " + FilePath);
        Refresh();
        return true;
    }

    /// <summary>Asks what to do with unsaved changes. Returns false when the user cancels.</summary>
    public bool ConfirmDiscardChanges()
    {
        if (!Session.Document.IsDirty) return true;
        return _ui.ConfirmSaveChanges(DocumentName) switch
        {
            SaveChangesChoice.Save => Save(),
            SaveChangesChoice.Discard => true,
            _ => false,
        };
    }

    /// <summary>Call when the window is closing. Returns false to keep the application open.</summary>
    public bool RequestClose()
    {
        if (!ConfirmDiscardChanges()) return false;
        Recovery.DeleteSnapshot(Session.Document.Id);
        return true;
    }

    // ------------------------------------------------- autosave / recovery

    /// <summary>
    /// Writes a recovery snapshot when there are unsaved changes. The document is serialized on the
    /// calling (UI) thread; only the finished bytes are written on a background thread, atomically.
    /// </summary>
    public async Task AutosaveAsync()
    {
        var document = Session.Document;
        if (!document.IsDirty || document.ChangeStamp == _autosavedStamp) return;

        long stamp = document.ChangeStamp;
        Guid id = document.Id;
        byte[] snapshot;
        try
        {
            snapshot = RecoveryStore.CreateSnapshot(document, FilePath);
            await Task.Run(() => Recovery.WriteSnapshot(id, snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AddHistory("Autosave failed: " + ex.Message);
            return;
        }

        _autosavedStamp = stamp;
        // The drawing may have been saved or closed while the snapshot was being written.
        if (!ReferenceEquals(Session.Document, document) || !document.IsDirty) Recovery.DeleteSnapshot(id);
    }

    /// <summary>At startup: offers the newest snapshot left behind by a previous session that did not close cleanly.</summary>
    public bool OfferRecovery()
    {
        var candidates = Recovery.FindSnapshots();
        if (candidates.Count == 0) return false;

        var newest = candidates[0];
        string name = newest.OriginalPath ?? "an unsaved drawing";
        string when = newest.SavedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        if (!_ui.Confirm($"HAQQ CAD did not close cleanly last time.\n\nRecover {name} (autosaved {when})?", "Drawing recovery"))
        {
            if (_ui.Confirm("Delete this recovery file? Choose No to be asked again next time.", "Drawing recovery"))
                Recovery.DeleteSnapshotFile(newest.SnapshotPath);
            return false;
        }

        HcadLoadResult result;
        try
        {
            result = HcadFileStore.Load(newest.SnapshotPath);
        }
        catch (Exception ex) when (ex is HcadFormatException or IOException or UnauthorizedAccessException)
        {
            _ui.ShowError("The recovery file could not be read.\n\n" + ex.Message, "Drawing recovery");
            return false;
        }

        result.Document.MarkDirty(); // recovered work is not on disk under its real name yet
        FilePath = newest.OriginalPath;
        Session.LoadDocument(result.Document);
        AddHistory($"Recovered {name} from autosave. Save it to keep the recovered changes.");
        ReportWarnings(result.Warnings);
        if (candidates.Count > 1) AddHistory($"{candidates.Count - 1} more recovery file(s) will be offered at the next start.");
        return true;
    }

    // -------------------------------------------------------------- refresh

    private void Refresh()
    {
        if (_userSettings.CrosshairPercent != Session.CrosshairSizePercent)
        {
            _userSettings.CrosshairPercent = Session.CrosshairSizePercent;
            if (!_settingsStore.Save(_userSettings)) AddHistory("The crosshair size could not be saved for next time.");
        }

        var doc = Session.Document;
        Prompt = Session.Prompt;
        Title = $"{DocumentName}{(doc.IsDirty ? "*" : "")} - HAQQ CAD";

        SyncLayers(doc);
        OnPropertyChanged(nameof(CurrentLayerName));
        OnPropertyChanged(nameof(CurrentLayerPlots));
        Properties.Refresh();
        DimensionBar.Refresh();
        if (_layersRebuilt)
        {
            // Lists bound to the layer rows lost their choice when the rows were replaced.
            _layersRebuilt = false;
            Properties.RaiseLayerChoice();
        }
        RefreshExplorer(doc);

        string selection = Session.Selection.Count > 0 ? $"   {Session.Selection.Count} selected" : "";
        StatusText = $"{doc.EntityCount} objects{selection}";

        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        RedrawRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool _layersRebuilt;

    private void SyncLayers(DrawingDocument doc)
    {
        SyncNames(LinetypeNames, doc.LinetypeTable.Select(l => l.Name).ToList());

        bool same = Layers.Count == doc.Layers.Count;
        for (int i = 0; same && i < Layers.Count; i++) same = Layers[i].Id == doc.Layers[i].Id;

        if (same)
        {
            for (int i = 0; i < Layers.Count; i++) Layers[i].Sync(doc.Layers[i], doc.Layers[i].Id == doc.CurrentLayerId);
            return;
        }

        Guid? selectedId = SelectedLayer?.Id;
        _layersRebuilt = true;
        Layers.Clear();
        foreach (var layer in doc.Layers) Layers.Add(new LayerViewModel(this, layer, layer.Id == doc.CurrentLayerId));
        SelectedLayer = Layers.FirstOrDefault(l => l.Id == selectedId) ?? Layers.FirstOrDefault(l => l.IsCurrent);
    }

    private void RefreshExplorer(DrawingDocument doc)
    {
        if (doc.ChangeStamp == _explorerStamp && doc.Id == _explorerDocument) return;
        _explorerStamp = doc.ChangeStamp;
        _explorerDocument = doc.Id;

        int lines = 0, polylines = 0, circles = 0, arcs = 0;
        foreach (var e in doc.EntitiesUnordered)
        {
            switch (e)
            {
                case LineEntity: lines++; break;
                case PolylineEntity: polylines++; break;
                case CircleEntity: circles++; break;
                case ArcEntity: arcs++; break;
            }
        }

        var items = new List<string>
        {
            "Model space",
            $"   Lines: {lines}",
            $"   Polylines: {polylines}",
            $"   Circles: {circles}",
            $"   Arcs: {arcs}",
            $"Layers: {doc.Layers.Count}",
            $"Units: {doc.Settings.Unit} ({doc.Settings.Format})",
        };
        if (doc.UnknownEntities.Count > 0) items.Add($"Unsupported objects kept: {doc.UnknownEntities.Count}");

        ExplorerItems.Clear();
        foreach (string item in items) ExplorerItems.Add(item);
    }
}
