using System.Collections.ObjectModel;
using System.Globalization;
using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Presentation;

/// <summary>One name/value line of the properties panel.</summary>
public sealed class PropertyRow : ObservableObject
{
    private readonly string _value;
    private readonly Func<string, bool>? _commit;
    private readonly Action<Action> _dispatch;

    internal PropertyRow(string label, string value, Func<string, bool>? commit = null, Action<Action>? dispatch = null)
    {
        Label = label;
        _value = value;
        _commit = commit;
        _dispatch = dispatch ?? (action => action());
    }

    public string Label { get; }
    public bool IsReadOnly => _commit is null;
    public bool IsEditable => _commit is not null;

    public string Value
    {
        get => _value;
        set
        {
            if (_commit is null || value == _value) return;
            string text = value ?? "";
            if (text == _pendingText) return; // the same edit is already on its way
            _pendingText = text;
            // A successful commit rebuilds the panel (and removes this row), so the shell may
            // defer it until the control's own binding update has finished.
            _dispatch(() => Apply(text));
        }
    }

    private string? _pendingText;

    private void Apply(string text)
    {
        if (_commit is null) return;
        if (!_commit(text))
        {
            // Rejected: allow another attempt and tell the view to show the stored value again.
            _pendingText = null;
            OnPropertyChanged(nameof(Value));
        }
    }
}

/// <summary>Properties of the current selection. Edits are undoable.</summary>
public sealed class PropertiesViewModel : ObservableObject
{
    private const string Varies = "*VARIES*";

    private readonly EditorSession _session;
    private string _header = "No selection";
    private string? _selectedLayerName;
    private bool _hasSelection;
    private bool _refreshed;

    internal PropertiesViewModel(EditorSession session) => _session = session;

    /// <summary>
    /// How edits made in the panel are executed. The default runs them immediately; the desktop
    /// shell replaces it with a dispatcher post so an edit never rebuilds the panel re-entrantly.
    /// </summary>
    public Action<Action> Dispatch { get; set; } = action => action();

    public ObservableCollection<PropertyRow> Rows { get; } = new();
    public ObservableCollection<string> LayerNames { get; } = new();

    /// <summary>ByLayer, ByBlock and the drawing's linetypes.</summary>
    public ObservableCollection<string> LinetypeNames { get; } = new();

    /// <summary>ByLayer, ByBlock and the standard colours by name (plus the present colour when it is not a standard one).</summary>
    public ObservableCollection<string> ColorNames { get; } = new();

    /// <summary>ByLayer and the standard lineweights (plus the present one when it is not standard).</summary>
    public ObservableCollection<string> LineweightNames { get; } = new();

    private static readonly double[] StandardLineweights = { 0.13, 0.18, 0.25, 0.35, 0.5, 0.7, 1.0, 1.4, 2.0 };

    private string? _selectedLinetype;
    private string? _selectedColor;
    private string? _selectedLineweight;
    private string _hint = "";
    private bool _canSave;
    private bool _isNewObject;
    private Entity? _saveSource;

    /// <summary>Changes the selected objects, or (with nothing selected) the properties of new objects.</summary>
    private bool Change(Action<Entity> forObjects, Action<NewObjectProperties> forNew, string undoName)
    {
        if (_session.Selection.Count > 0)
            return _session.ModifyEntities(_session.Selection.Ids.ToList(), forObjects, undoName);
        _session.ChangeNewObject(forNew);
        return true;
    }

    /// <summary>
    /// Linetype of the selection (null when it varies). With nothing selected: the linetype of the object being
    /// drawn, or of new objects.
    /// </summary>
    public string? SelectedLinetype
    {
        get => _selectedLinetype;
        set
        {
            if (value == _selectedLinetype || string.IsNullOrEmpty(value)) return;
            string? name = Linetypes.IsReserved(value)
                ? (string.Equals(value, Linetypes.ByBlock, StringComparison.OrdinalIgnoreCase) ? Linetypes.ByBlock : Linetypes.ByLayer)
                : _session.Document.FindLinetype(value)?.Name;
            if (name is null || !Change(e => e.Linetype = name, p => p.Linetype = name, "Change linetype"))
                OnPropertyChanged();
        }
    }

    private void SetLinetype(string? name)
    {
        if (_selectedLinetype == name) return;
        _selectedLinetype = name;
        OnPropertyChanged(nameof(SelectedLinetype));
    }

    /// <summary>Colour by name ("ByLayer", "Red") or as #RRGGBB; null when the selection has several.</summary>
    public string? SelectedColor
    {
        get => _selectedColor;
        set
        {
            if (value == _selectedColor || string.IsNullOrEmpty(value)) return;
            if (!CadColor.TryParse(LayerViewModel.ColorFromName(value) ?? value, out var color))
            {
                _session.Post("Color must be ByLayer, ByBlock, a colour name or #RRGGBB.");
                OnPropertyChanged();
                return;
            }
            if (!Change(e => e.Color = color, p => p.Color = color, "Change color")) OnPropertyChanged();
        }
    }

    private static string ColorText(CadColor color) => color.IsExplicit ? LayerViewModel.ColorNameOf(color.ToString()) : color.ToString();

    /// <summary>Lineweight: "ByLayer" or millimetres ("0.35 mm"); null when the selection has several.</summary>
    public string? SelectedLineweight
    {
        get => _selectedLineweight;
        set
        {
            if (value == _selectedLineweight || string.IsNullOrEmpty(value)) return;
            double? mm = null;
            if (!value.Trim().Equals("ByLayer", StringComparison.OrdinalIgnoreCase))
            {
                string number = value.Replace("mm", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || parsed < 0 || parsed > 10 || !double.IsFinite(parsed))
                {
                    _session.Post("Lineweight must be ByLayer or millimetres from 0 to 10.");
                    OnPropertyChanged();
                    return;
                }
                mm = parsed;
            }
            if (!Change(e => e.LineweightMm = mm, p => p.LineweightMm = mm, "Change lineweight")) OnPropertyChanged();
        }
    }

    private static string LineweightText(double? mm) =>
        mm is double v ? v.ToString("0.00", CultureInfo.InvariantCulture) + " mm" : "ByLayer";

    private void SetChoice(ref string? field, string? value, ObservableCollection<string> names, IReadOnlyList<string> standard, string property)
    {
        var wanted = new List<string>(standard);
        if (value is not null && !wanted.Contains(value)) wanted.Add(value);
        bool listChanged = !names.SequenceEqual(wanted);
        MainViewModel.SyncNames(names, wanted);
        // A list that changed makes the drop-down drop its choice, so it is told again even when the value is the same.
        if (field == value && !listChanged) return;
        field = value;
        OnPropertyChanged(property);
    }

    /// <summary>One line under the title saying what the panel is showing and what a change will affect.</summary>
    public string Hint
    {
        get => _hint;
        private set => SetProperty(ref _hint, value);
    }

    /// <summary>True while the panel shows the properties of an object that is being drawn or will be drawn next.</summary>
    public bool IsNewObject
    {
        get => _isNewObject;
        private set => SetProperty(ref _isNewObject, value);
    }

    /// <summary>True when Save has something to do.</summary>
    public bool CanSave
    {
        get => _canSave;
        private set => SetProperty(ref _canSave, value);
    }

    /// <summary>
    /// Save: the properties shown become the settings for every object drawn from now on. Without it, a change
    /// made while drawing applies to that one object, and a change to a selected object only to that object.
    /// </summary>
    public bool Save()
    {
        if (!CanSave) return false;
        if (_saveSource is { } source) _session.UseForNewObjects(source);
        else if (!_session.SaveNewObjectChanges()) return false;
        return true;
    }

    private PropertyRow LinetypeScaleRow(IReadOnlyList<Entity> entities)
    {
        var scales = entities.Select(e => e.LinetypeScale).Distinct().ToList();
        var ids = entities.Select(e => e.Id).ToList();
        string shown = entities.Count == 0
            ? _session.EffectiveNewObject.LinetypeScale.ToString("0.####", CultureInfo.InvariantCulture)
            : scales.Count == 1 ? scales[0].ToString("0.####", CultureInfo.InvariantCulture) : Varies;
        return Editable("Linetype scale", shown, text =>
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) || !(scale > 0) || !double.IsFinite(scale))
            {
                _session.Post("The linetype scale must be a number more than 0.");
                return false;
            }
            if (ids.Count == 0) { _session.ChangeNewObject(p => p.LinetypeScale = scale); return true; }
            return _session.ModifyEntities(ids, e => e.LinetypeScale = scale, "Change linetype scale");
        });
    }

    public string Header
    {
        get => _header;
        private set => SetProperty(ref _header, value);
    }

    public bool HasSelection
    {
        get => _hasSelection;
        private set => SetProperty(ref _hasSelection, value);
    }

    /// <summary>Layer of the selection; setting it moves the selected objects to that layer. With no selection: the current layer.</summary>
    public string? SelectedLayerName
    {
        get => _selectedLayerName;
        set
        {
            if (value == _selectedLayerName || string.IsNullOrEmpty(value)) return;
            var layer = _session.Document.FindLayer(value);
            if (layer is null) { OnPropertyChanged(); return; }
            if (_session.Selection.Count == 0)
            {
                // Nothing selected: the layer of the object being drawn (that one only), or the current layer.
                _session.ChangeNewObject(p => p.LayerId = layer.Id);
                return;
            }
            if (!_session.ModifyEntities(_session.Selection.Ids.ToList(), e => e.LayerId = layer.Id, "Change layer"))
                OnPropertyChanged();
        }
    }

    private (long Stamp, int Count, Guid First, Guid Last, Guid Document, int NewObject, string Command, bool Unsaved) _state;

    internal void Refresh()
    {
        var doc = _session.Document;
        var ids = _session.Selection.Ids;
        string command = _session.IsCreatingObjects ? _session.ActiveCommand!.Name : "";
        var state = (doc.ChangeStamp, ids.Count, ids.Count > 0 ? ids[0] : Guid.Empty, ids.Count > 0 ? ids[^1] : Guid.Empty, doc.Id,
            _session.NewObjectVersion, command, _session.HasUnsavedNewObjectChanges);
        if (_refreshed && state == _state) return;
        _refreshed = true;
        _state = state;

        var layerNames = doc.Layers.Select(l => l.Name).ToList();
        bool layersChanged = !layerNames.SequenceEqual(LayerNames);
        MainViewModel.SyncNames(LayerNames, layerNames);

        var linetypeNames = new List<string> { Linetypes.ByLayer, Linetypes.ByBlock };
        linetypeNames.AddRange(doc.LinetypeTable.Select(l => l.Name));
        bool linetypesChanged = !linetypeNames.SequenceEqual(LinetypeNames);
        MainViewModel.SyncNames(LinetypeNames, linetypeNames);

        var colorChoices = new List<string> { "ByLayer", "ByBlock" };
        colorChoices.AddRange(LayerViewModel.StandardColors.Select(LayerViewModel.ColorNameOf));
        var weightChoices = new List<string> { "ByLayer" };
        weightChoices.AddRange(StandardLineweights.Select(w => LineweightText(w)));

        var entities = _session.GetSelectedEntities();
        Rows.Clear();
        HasSelection = entities.Count > 0;
        IsNewObject = entities.Count == 0;
        _saveSource = null;

        if (entities.Count == 0)
        {
            // Nothing selected: the properties of the object being drawn, or of the next new object.
            var next = _session.EffectiveNewObject;
            bool drawing = _session.IsCreatingObjects;
            Header = drawing ? command + " (new object)" : "No selection";
            Hint = !drawing
                ? "New objects get these properties."
                : _session.HasUnsavedNewObjectChanges
                    ? "Changed for this " + command + " only. Press Save to use it for every new object."
                    : "A change here applies to this " + command + " only, unless you press Save.";
            SetLayerName(doc.GetLayer(_session.EffectiveNewObjectLayerId)?.Name, layersChanged);
            string nextLinetype = LinetypeNames.FirstOrDefault(n => string.Equals(n, next.Linetype, StringComparison.OrdinalIgnoreCase)) ?? Linetypes.ByLayer;
            if (linetypesChanged) _selectedLinetype = null;
            SetLinetype(nextLinetype);
            SetChoice(ref _selectedColor, ColorText(next.Color), ColorNames, colorChoices, nameof(SelectedColor));
            SetChoice(ref _selectedLineweight, LineweightText(next.LineweightMm), LineweightNames, weightChoices, nameof(SelectedLineweight));
            Rows.Add(LinetypeScaleRow(entities));
            CanSave = _session.HasUnsavedNewObjectChanges;
            return;
        }

        var settings = doc.Settings;
        string F(double v) => UnitFormatter.FormatLength(v, settings);

        var layerIds = entities.Select(e => e.LayerId).Distinct().ToList();
        SetLayerName(layerIds.Count == 1 ? doc.GetLayer(layerIds[0])?.Name : null, layersChanged);

        var linetypes = entities.Select(e => e.Linetype).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (linetypesChanged) _selectedLinetype = null;
        SetLinetype(linetypes.Count == 1 ? LinetypeNames.FirstOrDefault(n => string.Equals(n, linetypes[0], StringComparison.OrdinalIgnoreCase)) : null);

        var colors = entities.Select(e => e.Color).Distinct().ToList();
        SetChoice(ref _selectedColor, colors.Count == 1 ? ColorText(colors[0]) : null, ColorNames, colorChoices, nameof(SelectedColor));
        var weights = entities.Select(e => e.LineweightMm).Distinct().ToList();
        SetChoice(ref _selectedLineweight, weights.Count == 1 ? LineweightText(weights[0]) : null, LineweightNames, weightChoices, nameof(SelectedLineweight));

        // Save copies the selection's properties to new objects; it needs one value for each of them.
        bool uniform = layerIds.Count == 1 && linetypes.Count == 1 && colors.Count == 1 && weights.Count == 1 &&
                       entities.Select(e => e.LinetypeScale).Distinct().Count() == 1;
        _saveSource = uniform ? entities[0] : null;
        CanSave = uniform;
        Hint = entities.Count == 1
            ? "A change here applies to this object only. Save makes these the properties of new objects too."
            : "A change here applies to the selected objects only. Save makes these the properties of new objects too.";

        if (entities.Count > 1)
        {
            Header = $"{entities.Count} objects";
            Rows.Add(new PropertyRow("Count", entities.Count.ToString(CultureInfo.InvariantCulture)));
            foreach (var group in entities.GroupBy(e => e.TypeName).OrderBy(g => g.Key))
                Rows.Add(new PropertyRow(Capitalize(group.Key) + "s", group.Count().ToString(CultureInfo.InvariantCulture)));
            Rows.Add(LinetypeScaleRow(entities));
            return;
        }

        var entity = entities[0];
        Guid id = entity.Id;
        Header = Capitalize(entity.TypeName);
        Rows.Add(new PropertyRow("Type", Capitalize(entity.TypeName)));
        Rows.Add(LinetypeScaleRow(entities));

        switch (entity)
        {
            case LineEntity line:
                Rows.Add(Length("Start X", line.Start.X, id, (e, v) => ((LineEntity)e).Start = new Point2D(v, ((LineEntity)e).Start.Y)));
                Rows.Add(Length("Start Y", line.Start.Y, id, (e, v) => ((LineEntity)e).Start = new Point2D(((LineEntity)e).Start.X, v)));
                Rows.Add(Length("End X", line.End.X, id, (e, v) => ((LineEntity)e).End = new Point2D(v, ((LineEntity)e).End.Y)));
                Rows.Add(Length("End Y", line.End.Y, id, (e, v) => ((LineEntity)e).End = new Point2D(((LineEntity)e).End.X, v)));
                Rows.Add(new PropertyRow("Length", F(line.Length)));
                Rows.Add(new PropertyRow("Angle", FormatAngle((line.End - line.Start).Angle)));
                break;

            case CircleEntity circle:
                Rows.Add(Length("Center X", circle.Center.X, id, (e, v) => ((CircleEntity)e).Center = new Point2D(v, ((CircleEntity)e).Center.Y)));
                Rows.Add(Length("Center Y", circle.Center.Y, id, (e, v) => ((CircleEntity)e).Center = new Point2D(((CircleEntity)e).Center.X, v)));
                Rows.Add(Length("Radius", circle.Radius, id, (e, v) => ((CircleEntity)e).Radius = v));
                Rows.Add(Length("Diameter", circle.Radius * 2, id, (e, v) => ((CircleEntity)e).Radius = v / 2.0));
                Rows.Add(new PropertyRow("Circumference", F(circle.Circumference)));
                Rows.Add(new PropertyRow("Area", FormatArea(circle.Area, settings)));
                break;

            case ArcEntity arc:
                Rows.Add(Length("Center X", arc.Center.X, id, (e, v) => ((ArcEntity)e).Center = new Point2D(v, ((ArcEntity)e).Center.Y)));
                Rows.Add(Length("Center Y", arc.Center.Y, id, (e, v) => ((ArcEntity)e).Center = new Point2D(((ArcEntity)e).Center.X, v)));
                Rows.Add(Length("Radius", arc.Radius, id, (e, v) => ((ArcEntity)e).Radius = v));
                Rows.Add(new PropertyRow("Start angle", FormatAngle(arc.StartAngle)));
                Rows.Add(new PropertyRow("End angle", FormatAngle(arc.EndAngle)));
                Rows.Add(new PropertyRow("Included angle", FormatAngle(arc.Sweep)));
                Rows.Add(new PropertyRow("Length", F(arc.Length)));
                break;

            case PolylineEntity polyline:
                Rows.Add(new PropertyRow("Vertices", polyline.Vertices.Count.ToString(CultureInfo.InvariantCulture)));
                Rows.Add(new PropertyRow("Closed", polyline.IsClosed ? "Yes" : "No"));
                int arcSegments = 0;
                for (int i = 0; i < polyline.SegmentCount; i++) if (BulgeMath.IsArc(polyline.GetBulge(i))) arcSegments++;
                if (arcSegments > 0) Rows.Add(new PropertyRow("Arc segments", arcSegments.ToString(CultureInfo.InvariantCulture)));
                if (polyline.HasUniformWidth)
                    Rows.Add(Length("Width", polyline.Width, id, (e, v) => ((PolylineEntity)e).Width = v));
                else
                    Rows.Add(Editable("Width", Varies, text =>
                    {
                        // Typing a value here gives every segment that one width.
                        if (!UnitFormatter.TryParseLength(text, settings, out double all))
                        {
                            _session.Post($"\"{text}\" is not a valid length.");
                            return false;
                        }
                        return _session.ModifyEntities(new[] { id }, e => ((PolylineEntity)e).Width = all, "Edit width");
                    }));
                Rows.Add(new PropertyRow("Length", F(polyline.Length)));
                if (polyline.IsClosed) Rows.Add(new PropertyRow("Area", FormatArea(polyline.Area, settings)));
                var b = polyline.Bounds;
                Rows.Add(new PropertyRow("Width", F(b.Width)));
                Rows.Add(new PropertyRow("Height", F(b.Height)));
                break;
        }

        Rows.Add(new PropertyRow("Id", entity.Id.ToString()));
    }

    private PropertyRow Editable(string label, string value, Func<string, bool> commit) =>
        new(label, value, commit, action => Dispatch(action));

    private PropertyRow Length(string label, double value, Guid id, Action<Entity, double> apply)
    {
        var settings = _session.Document.Settings;
        return Editable(label, UnitFormatter.FormatLength(value, settings), text =>
        {
            if (!UnitFormatter.TryParseLength(text, settings, out double parsed))
            {
                _session.Post($"\"{text}\" is not a valid length.");
                return false;
            }
            return _session.ModifyEntities(new[] { id }, e => apply(e, parsed), "Edit " + label.ToLowerInvariant());
        });
    }

    private void SetLayerName(string? name, bool listChanged = false)
    {
        // A list that changed makes the drop-down drop its choice, so it is told again even when the name is the same.
        if (_selectedLayerName == name && !listChanged) return;
        _selectedLayerName = name;
        OnPropertyChanged(nameof(SelectedLayerName));
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string FormatAngle(double radians)
    {
        double degrees = GeoMath.RadiansToDegrees(radians);
        if (degrees < 0) degrees += 360.0;
        return degrees.ToString("0.###", CultureInfo.InvariantCulture) + "°";
    }

    private static string FormatArea(double area, DrawingSettings settings)
    {
        string unit = settings.Unit switch
        {
            LinearUnit.Inch => "sq in",
            LinearUnit.Foot => "sq ft",
            LinearUnit.Millimeter => "mm²",
            LinearUnit.Centimeter => "cm²",
            LinearUnit.Meter => "m²",
            _ => "",
        };
        return UnitFormatter.FormatDecimal(area, Math.Min(settings.DecimalPrecision, 4)) + " " + unit;
    }
}
