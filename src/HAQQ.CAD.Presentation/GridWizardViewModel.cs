using System.Globalization;
using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Presentation;

/// <summary>
/// The grid line wizard: one window for a rectangular or a sector grid, with a picture that follows what is
/// typed. Spacings are typed as a list ("4x20', 15', 20'"); labels as a list that is counted on by itself
/// ("A" gives A, B, C...; "1, 2, 3, 3.5" carries on with 4, 5...).
/// </summary>
public sealed class GridWizardViewModel : ObservableObject
{
    private readonly EditorSession _session;
    private IReadOnlyList<Entity> _preview = Array.Empty<Entity>();
    private string _error = "", _summary = "";

    // What is typed, by property name.
    private readonly Dictionary<string, string> _text = new();

    public GridWizardViewModel(EditorSession session)
    {
        _session = session;
        var unit = session.Document.Settings.Unit;
        // Starting values that make a sensible building grid in the drawing's unit.
        (string bay, string extension, string bubble) = unit switch
        {
            LinearUnit.Inch => ("20'", "4'", "2'"),
            LinearUnit.Foot => ("20", "4", "2"),
            LinearUnit.Millimeter => ("6000", "1200", "600"),
            LinearUnit.Centimeter => ("600", "120", "60"),
            _ => ("6", "1.2", "0.6"),
        };
        _text[nameof(Name)] = session.NextGridName();
        _text[nameof(OriginX)] = "0";
        _text[nameof(OriginY)] = "0";
        _text[nameof(XSpacings)] = "4x" + bay;
        _text[nameof(YSpacings)] = "3x" + bay;
        _text[nameof(XLabels)] = "A";
        _text[nameof(YLabels)] = "1";
        _text[nameof(Rotation)] = "0";
        _text[nameof(Extension)] = extension;
        _text[nameof(BubbleSize)] = bubble;
        _text[nameof(RingSpacings)] = bay + ", 2x" + bay;
        _text[nameof(AngleSpacings)] = "6x15";
        _text[nameof(StartAngle)] = "0";
        _text[nameof(RadialLabels)] = "A";
        _text[nameof(RingLabels)] = "1";
        LinetypeNames = session.Document.LinetypeTable.Select(l => l.Name).ToList();
        _linetype = LinetypeNames.Contains(Linetypes.DashDot) ? Linetypes.DashDot : LinetypeNames[0];
        Rebuild();
    }

    // ---------------------------------------------------------------- kind

    public IReadOnlyList<string> KindNames { get; } = new[] { "Rectangular", "Sector (radial)" };
    private string _kind = "Rectangular";

    public string SelectedKind
    {
        get => _kind;
        set
        {
            if (string.IsNullOrEmpty(value) || !SetProperty(ref _kind, value)) return;
            OnPropertyChanged(nameof(IsRectangular));
            OnPropertyChanged(nameof(IsSector));
            Rebuild();
        }
    }

    public bool IsRectangular => _kind == KindNames[0];
    public bool IsSector => !IsRectangular;

    // --------------------------------------------------------------- fields

    private string Get(string name) => _text[name];

    private void Set(string name, string? value)
    {
        value ??= "";
        if (_text[name] == value) return;
        _text[name] = value;
        OnPropertyChanged(name);
        Rebuild();
    }

    /// <summary>Name of the grid system: its lines go on a layer of this name.</summary>
    public string Name { get => Get(nameof(Name)); set => Set(nameof(Name), value); }
    public string OriginX { get => Get(nameof(OriginX)); set => Set(nameof(OriginX), value); }
    public string OriginY { get => Get(nameof(OriginY)); set => Set(nameof(OriginY), value); }
    public string XSpacings { get => Get(nameof(XSpacings)); set => Set(nameof(XSpacings), value); }
    public string YSpacings { get => Get(nameof(YSpacings)); set => Set(nameof(YSpacings), value); }
    public string XLabels { get => Get(nameof(XLabels)); set => Set(nameof(XLabels), value); }
    public string YLabels { get => Get(nameof(YLabels)); set => Set(nameof(YLabels), value); }
    public string Rotation { get => Get(nameof(Rotation)); set => Set(nameof(Rotation), value); }
    public string Extension { get => Get(nameof(Extension)); set => Set(nameof(Extension), value); }
    public string BubbleSize { get => Get(nameof(BubbleSize)); set => Set(nameof(BubbleSize), value); }
    public string RingSpacings { get => Get(nameof(RingSpacings)); set => Set(nameof(RingSpacings), value); }
    public string AngleSpacings { get => Get(nameof(AngleSpacings)); set => Set(nameof(AngleSpacings), value); }
    public string StartAngle { get => Get(nameof(StartAngle)); set => Set(nameof(StartAngle), value); }
    public string RadialLabels { get => Get(nameof(RadialLabels)); set => Set(nameof(RadialLabels), value); }
    public string RingLabels { get => Get(nameof(RingLabels)); set => Set(nameof(RingLabels), value); }

    public IReadOnlyList<string> LinetypeNames { get; }
    private string _linetype;

    /// <summary>Linetype of the grid's layer (when the layer is made here).</summary>
    public string SelectedLinetype
    {
        get => _linetype;
        set { if (!string.IsNullOrEmpty(value) && SetProperty(ref _linetype, value)) Rebuild(); }
    }

    // Where the labels go. The order of each list is Start, End, Both, None (see GridLabelEnds).
    public IReadOnlyList<string> XLabelEndNames { get; } = new[] { "Bottom", "Top", "Top and bottom", "No labels" };
    public IReadOnlyList<string> YLabelEndNames { get; } = new[] { "Left", "Right", "Left and right", "No labels" };
    public IReadOnlyList<string> RadialLabelEndNames { get; } = new[] { "Inner end", "Outer end", "Both ends", "No labels" };
    public IReadOnlyList<string> RingLabelEndNames { get; } = new[] { "At the first line", "At the last line", "Both ends", "No labels" };

    private GridLabelEnds _xEnds = GridLabelEnds.End, _yEnds = GridLabelEnds.Start, _radialEnds = GridLabelEnds.End, _ringEnds = GridLabelEnds.Start;

    public string SelectedXLabelEnds { get => XLabelEndNames[(int)_xEnds]; set => SetEnds(ref _xEnds, XLabelEndNames, value); }
    public string SelectedYLabelEnds { get => YLabelEndNames[(int)_yEnds]; set => SetEnds(ref _yEnds, YLabelEndNames, value); }
    public string SelectedRadialLabelEnds { get => RadialLabelEndNames[(int)_radialEnds]; set => SetEnds(ref _radialEnds, RadialLabelEndNames, value); }
    public string SelectedRingLabelEnds { get => RingLabelEndNames[(int)_ringEnds]; set => SetEnds(ref _ringEnds, RingLabelEndNames, value); }

    private void SetEnds(ref GridLabelEnds field, IReadOnlyList<string> names, string? value,
        [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        int i = names.ToList().IndexOf(value ?? "");
        if (i < 0 || (GridLabelEnds)i == field) return;
        field = (GridLabelEnds)i;
        OnPropertyChanged(property);
        Rebuild();
    }

    // -------------------------------------------------------------- results

    /// <summary>The grid as typed, at the typed origin: what the picture shows.</summary>
    public IReadOnlyList<Entity> Preview
    {
        get => _preview;
        private set { _preview = value; OnPropertyChanged(); }
    }

    /// <summary>What is wrong with what was typed, or empty.</summary>
    public string Error
    {
        get => _error;
        private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(CanCreate)); }
    }

    /// <summary>How many lines the grid has and how big it is.</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public bool CanCreate => Error.Length == 0;

    private Func<Point2D, List<Entity>>? _build;
    private Point2D _origin;

    private bool Length(string name, string label, bool allowZero, out double value)
    {
        if (UnitFormatter.TryParseLength(Get(name), _session.Document.Settings, out value) && double.IsFinite(value) && (allowZero ? value >= 0 : value > 0)) return true;
        Error = $"{label}: \"{Get(name)}\" is not a length{(allowZero ? "" : " more than 0")}.";
        return false;
    }

    private bool Coordinate(string name, string label, out double value)
    {
        if (UnitFormatter.TryParseLength(Get(name), _session.Document.Settings, out value) && double.IsFinite(value)) return true;
        Error = $"{label}: \"{Get(name)}\" is not a coordinate.";
        return false;
    }

    private bool Degrees(string name, string label, out double value)
    {
        if (double.TryParse(Get(name).TrimEnd('°').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value)) return true;
        Error = $"{label}: \"{Get(name)}\" is not an angle in degrees.";
        return false;
    }

    private void Rebuild()
    {
        _build = null;
        Error = "";
        var settings = _session.Document.Settings;
        string F(double v) => UnitFormatter.FormatLength(v, settings);

        string name = Name.Trim();
        if (name.Length == 0 || name.Length > 64) { Fail("Give the grid system a name (up to 64 characters)."); return; }
        if (!Coordinate(nameof(OriginX), IsSector ? "Center X" : "Origin X", out double ox) || !Coordinate(nameof(OriginY), IsSector ? "Center Y" : "Origin Y", out double oy) ||
            !Length(nameof(Extension), "Line overrun", true, out double extension) || !Length(nameof(BubbleSize), "Label circle size", true, out double bubble))
        { Fail(Error); return; }
        _origin = new Point2D(ox, oy);

        if (IsRectangular)
        {
            if (!GridLines.TryParseSpacings(XSpacings, settings, out double[] xs, out string xe)) { Fail("Spacings along X: " + xe); return; }
            if (!GridLines.TryParseSpacings(YSpacings, settings, out double[] ys, out string ye)) { Fail("Spacings along Y: " + ye); return; }
            if (xs.Length == 0 && ys.Length == 0) { Fail("Type the spacings along X, along Y, or both."); return; }
            if (!Degrees(nameof(Rotation), "Rotation", out double rotation)) { Fail(Error); return; }

            string[] xl = GridLines.Labels(XLabels, xs.Length + 1, "A"), yl = GridLines.Labels(YLabels, ys.Length + 1, "1");
            var (xEnds, yEnds, linetype) = (_xEnds, _yEnds, _linetype);
            _build = origin => GridLines.Build(new RectangularGridSpec
            {
                Name = name, Origin = origin, XSpacings = xs, YSpacings = ys, XLabels = xl, YLabels = yl,
                XLabelEnds = xEnds, YLabelEnds = yEnds, RotationDegrees = rotation, Extension = extension, BubbleDiameter = bubble, Linetype = linetype,
            });
            Summary = $"{xs.Length + 1} lines {xl[0]} to {xl[^1]}, {ys.Length + 1} lines {yl[0]} to {yl[^1]}.  Size {F(xs.Sum())} by {F(ys.Sum())}.";
        }
        else
        {
            if (!GridLines.TryParseSpacings(RingSpacings, settings, out double[] rings, out string re)) { Fail("Radius of the arcs: " + re); return; }
            if (rings.Length == 0) { Fail("Type the radius of the first arc, then the spacings of the others."); return; }
            if (!GridLines.TryParseAngles(AngleSpacings, out double[] angles, out string ae)) { Fail("Angles between the lines: " + ae); return; }
            if (angles.Length == 0) { Fail("Type the angles between the straight lines, for example 6x15."); return; }
            if (angles.Sum() > 360.0 + 1e-9) { Fail("The angles add up to more than a full turn (360 degrees)."); return; }
            if (!Degrees(nameof(StartAngle), "First line at", out double start)) { Fail(Error); return; }

            bool full = angles.Sum() >= 360.0 - 1e-9;
            string[] al = GridLines.Labels(RadialLabels, full ? angles.Length : angles.Length + 1, "A"), rl = GridLines.Labels(RingLabels, rings.Length, "1");
            var (radialEnds, ringEnds, linetype) = (_radialEnds, _ringEnds, _linetype);
            _build = center => GridLines.Build(new SectorGridSpec
            {
                Name = name, Center = center, RingSpacings = rings, AngleSpacings = angles, StartAngleDegrees = start, RadialLabels = al, RingLabels = rl,
                RadialLabelEnds = radialEnds, RingLabelEnds = ringEnds, Extension = extension, BubbleDiameter = bubble, Linetype = linetype,
            });
            Summary = $"{al.Length} straight lines {al[0]} to {al[^1]}, {rl.Length} arcs {rl[0]} to {rl[^1]}.  Radius {F(rings[0])} to {F(rings.Sum())}, {angles.Sum().ToString("0.##", CultureInfo.InvariantCulture)}° in all.";
        }

        var built = _build(_origin);
        if (built.Count == 0) { Fail("That makes no grid lines."); return; }
        if (_session.Document.FindLayer(name) is not null) Summary += $"  Layer \"{name}\" is already there: the lines are added to it.";
        Preview = built;

        void Fail(string message)
        {
            _build = null;
            Error = message;
            Summary = "";
            Preview = Array.Empty<Entity>();
        }
    }

    /// <summary>Adds the grid to the drawing at the typed origin (or centre). One undo step.</summary>
    public bool Create()
    {
        if (_build is null) return false;
        return _session.CreateGrid(Name.Trim(), _linetype, _build(_origin));
    }

    /// <summary>Lets the origin (or centre) be clicked in the drawing: the grid follows the cursor until then.</summary>
    public bool PlaceByClick()
    {
        if (_build is null) return false;
        _session.BeginGridPlacement(Name.Trim(), _linetype, _build);
        return true;
    }
}
