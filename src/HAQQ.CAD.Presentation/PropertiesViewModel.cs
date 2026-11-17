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
    private (long Stamp, int Count, Guid First, Guid Last, Guid Document) _signature = (-1, -1, Guid.Empty, Guid.Empty, Guid.Empty);

    internal PropertiesViewModel(EditorSession session) => _session = session;

    /// <summary>
    /// How edits made in the panel are executed. The default runs them immediately; the desktop
    /// shell replaces it with a dispatcher post so an edit never rebuilds the panel re-entrantly.
    /// </summary>
    public Action<Action> Dispatch { get; set; } = action => action();

    public ObservableCollection<PropertyRow> Rows { get; } = new();
    public ObservableCollection<string> LayerNames { get; } = new();

    /// <summary>ByLayer, ByBlock and the drawing's linetypes: the choices for the selected objects.</summary>
    public ObservableCollection<string> LinetypeNames { get; } = new();

    private string? _selectedLinetype;

    /// <summary>Linetype of the selection (null when it varies or nothing is selected); setting it changes the selected objects.</summary>
    public string? SelectedLinetype
    {
        get => _selectedLinetype;
        set
        {
            if (value == _selectedLinetype || string.IsNullOrEmpty(value)) return;
            string? name = Linetypes.IsReserved(value)
                ? (string.Equals(value, Linetypes.ByBlock, StringComparison.OrdinalIgnoreCase) ? Linetypes.ByBlock : Linetypes.ByLayer)
                : _session.Document.FindLinetype(value)?.Name;
            if (name is null || _session.Selection.Count == 0 ||
                !_session.ModifyEntities(_session.Selection.Ids.ToList(), e => e.Linetype = name, "Change linetype"))
                OnPropertyChanged();
        }
    }

    private void SetLinetype(string? name)
    {
        if (_selectedLinetype == name) return;
        _selectedLinetype = name;
        OnPropertyChanged(nameof(SelectedLinetype));
    }

    private PropertyRow LinetypeScaleRow(IReadOnlyList<Entity> entities)
    {
        var scales = entities.Select(e => e.LinetypeScale).Distinct().ToList();
        var ids = entities.Select(e => e.Id).ToList();
        return Editable("Linetype scale", scales.Count == 1 ? scales[0].ToString("0.####", CultureInfo.InvariantCulture) : Varies, text =>
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) || !(scale > 0) || !double.IsFinite(scale))
            {
                _session.Post("The linetype scale must be a number more than 0.");
                return false;
            }
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
                // Nothing selected: the box shows, and sets, the current layer (the layer new objects go on).
                if (!_session.SetCurrentLayer(layer.Id)) OnPropertyChanged();
                return;
            }
            if (!_session.ModifyEntities(_session.Selection.Ids.ToList(), e => e.LayerId = layer.Id, "Change layer"))
                OnPropertyChanged();
        }
    }

    internal void Refresh()
    {
        var doc = _session.Document;
        var ids = _session.Selection.Ids;
        var signature = (doc.ChangeStamp, ids.Count, ids.Count > 0 ? ids[0] : Guid.Empty, ids.Count > 0 ? ids[^1] : Guid.Empty, doc.Id);
        if (signature == _signature) return;
        _signature = signature;

        var names = doc.Layers.Select(l => l.Name).ToList();
        if (!names.SequenceEqual(LayerNames))
        {
            LayerNames.Clear();
            foreach (string n in names) LayerNames.Add(n);
        }

        var linetypeNames = new List<string> { Linetypes.ByLayer, Linetypes.ByBlock };
        linetypeNames.AddRange(doc.LinetypeTable.Select(l => l.Name));
        MainViewModel.SyncNames(LinetypeNames, linetypeNames);

        var entities = _session.GetSelectedEntities();
        Rows.Clear();
        HasSelection = entities.Count > 0;

        if (entities.Count == 0)
        {
            Header = "No selection";
            SetLinetype(null);
            SetLayerName(doc.GetLayer(doc.CurrentLayerId)?.Name);
            return;
        }

        var settings = doc.Settings;
        string F(double v) => UnitFormatter.FormatLength(v, settings);

        var layerIds = entities.Select(e => e.LayerId).Distinct().ToList();
        SetLayerName(layerIds.Count == 1 ? doc.GetLayer(layerIds[0])?.Name : null);

        var linetypes = entities.Select(e => e.Linetype).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SetLinetype(linetypes.Count == 1 ? LinetypeNames.FirstOrDefault(n => string.Equals(n, linetypes[0], StringComparison.OrdinalIgnoreCase)) : null);

        var colors = entities.Select(e => e.Color).Distinct().ToList();
        var idList = entities.Select(e => e.Id).ToList();
        var colorRow = Editable("Color", colors.Count == 1 ? colors[0].ToString() : Varies, text =>
        {
            if (!CadColor.TryParse(text, out var color))
            {
                _session.Post("Color must be ByLayer or #RRGGBB.");
                return false;
            }
            return _session.ModifyEntities(idList, e => e.Color = color, "Change color");
        });

        if (entities.Count > 1)
        {
            Header = $"{entities.Count} objects";
            Rows.Add(new PropertyRow("Count", entities.Count.ToString(CultureInfo.InvariantCulture)));
            foreach (var group in entities.GroupBy(e => e.TypeName).OrderBy(g => g.Key))
                Rows.Add(new PropertyRow(Capitalize(group.Key) + "s", group.Count().ToString(CultureInfo.InvariantCulture)));
            Rows.Add(colorRow);
            Rows.Add(LinetypeScaleRow(entities));
            return;
        }

        var entity = entities[0];
        Guid id = entity.Id;
        Header = Capitalize(entity.TypeName);
        Rows.Add(new PropertyRow("Type", Capitalize(entity.TypeName)));
        Rows.Add(colorRow);
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

    private void SetLayerName(string? name)
    {
        if (_selectedLayerName == name) return;
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
