using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Documents.Pdf;

namespace HAQQ.CAD.Presentation;

/// <summary>
/// What HAQQ CAD has learned from the drawings copied so far on this computer: the layer each kind of line
/// was put on, the options used, the scale given to each file and page, and how far that scale was off when
/// the copy was calibrated afterwards. It is used to fill in the next copy; it is a record of the user's own
/// choices, not a trained model.
/// </summary>
public sealed class DrawingCopyMemory
{
    /// <summary>Group key ("line|Heavy|black") to the layer name the user gave it.</summary>
    public Dictionary<string, string> LayerNames { get; set; } = new();

    /// <summary>Group keys the user switched off.</summary>
    public List<string> LeftOut { get; set; } = new();

    /// <summary>"file name|page" to the scale typed for it.</summary>
    public Dictionary<string, string> Scales { get; set; } = new();

    /// <summary>"file name|page" to the correction found by CALIBRATE (true size over copied size).</summary>
    public Dictionary<string, double> Corrections { get; set; } = new();

    public string LastScale { get; set; } = "";
    public bool Clean { get; set; }
    public double ToleranceMm { get; set; } = 0.1;
    public bool IncludeText { get; set; } = true;
    public bool IncludeFills { get; set; } = true;
    public bool Join { get; set; } = true;

    /// <summary>How many drawings have been copied.</summary>
    public int Copies { get; set; }
}

/// <summary>Keeps <see cref="DrawingCopyMemory"/> in a file between runs.</summary>
public sealed class DrawingCopyMemoryStore
{
    private readonly string? _path;

    public DrawingCopyMemoryStore(string? path = null) => _path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HAQQ CAD", "drawing-copy.json");

    public DrawingCopyMemory Load()
    {
        if (_path is null || !File.Exists(_path)) return new DrawingCopyMemory();
        try
        {
            return JsonSerializer.Deserialize<DrawingCopyMemory>(File.ReadAllText(_path)) ?? new DrawingCopyMemory();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new DrawingCopyMemory();
        }
    }

    public bool Save(DrawingCopyMemory memory)
    {
        if (_path is null) return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            // Old entries are let go so the file stays small.
            foreach (var table in new[] { memory.Scales.Keys.ToList(), memory.Corrections.Keys.ToList() })
                if (table.Count > 500)
                    foreach (string key in table.Take(table.Count - 500)) { memory.Scales.Remove(key); memory.Corrections.Remove(key); }
            File.WriteAllText(_path, JsonSerializer.Serialize(memory, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>One kind of line work in the copy, and the layer it will go on.</summary>
public sealed class CopyGroupViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _include;
    private string _layerName;

    public CopyGroupViewModel(CopyGroup group, Action changed)
    {
        Group = group;
        _include = group.Include;
        _layerName = group.LayerName;
        _changed = changed;
    }

    public CopyGroup Group { get; }
    public string Description => Group.Description;
    public string Count => Group.Entities.Count.ToString("N0", CultureInfo.InvariantCulture);
    public string ColorName => Group.Color.ToString();

    public bool Include
    {
        get => _include;
        set
        {
            if (!SetProperty(ref _include, value)) return;
            Group.Include = value;
            _changed();
        }
    }

    public string LayerName
    {
        get => _layerName;
        set
        {
            if (!SetProperty(ref _layerName, value ?? "")) return;
            Group.LayerName = _layerName.Trim();
        }
    }
}

/// <summary>
/// The Drawing Copy window: a PDF is opened, a page chosen, and its line work and text are shown as the
/// HAQQ CAD objects they will become - sorted by pen onto layers that can be renamed or switched off, at the
/// scale written on the sheet or typed. The result is then placed in the drawing by a click.
/// </summary>
public sealed class DrawingCopyViewModel : ObservableObject
{
    private const int PreviewLimit = 30_000;

    private readonly EditorSession _session;
    private readonly DrawingCopyMemory _memory;
    private readonly DrawingCopyMemoryStore _store;
    private PdfFile? _pdf;
    private PdfPageContent? _page;
    private CopyResult? _result;
    private string _fileName = "", _scaleText = "", _scaleHint = "", _toleranceText, _summary = "", _notes = "", _message = "", _pageInfo = "";
    private int _pageIndex = -1;
    private bool _clean, _includeText, _includeFills, _join;
    private IReadOnlyList<Entity> _preview = Array.Empty<Entity>();
    private readonly Dictionary<string, (bool Include, string LayerName)> _edits = new();

    public DrawingCopyViewModel(EditorSession session, DrawingCopyMemoryStore? store = null)
    {
        _session = session;
        _store = store ?? new DrawingCopyMemoryStore();
        _memory = _store.Load();
        _clean = _memory.Clean;
        _includeText = _memory.IncludeText;
        _includeFills = _memory.IncludeFills;
        _join = _memory.Join;
        _toleranceText = _memory.ToleranceMm.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public ObservableCollection<string> Pages { get; } = new();
    public ObservableCollection<CopyGroupViewModel> Groups { get; } = new();

    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }
    public string PageInfo { get => _pageInfo; private set => SetProperty(ref _pageInfo, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string Notes { get => _notes; private set => SetProperty(ref _notes, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>Where the scale in the box came from: written on the sheet, used before for this file, or nowhere.</summary>
    public string ScaleHint { get => _scaleHint; private set => SetProperty(ref _scaleHint, value); }

    /// <summary>The objects to draw in the picture (a share of them when there are very many).</summary>
    public IReadOnlyList<Entity> Preview { get => _preview; private set => SetProperty(ref _preview, value); }

    public bool CanPlace => _result is not null && _result.Included.Any();

    public int PageIndex
    {
        get => _pageIndex;
        set { if (SetProperty(ref _pageIndex, value)) LoadPage(); }
    }

    /// <summary>The scale of the sheet: 1/4"=1', 1:50; empty or 1:1 copies it at its paper size.</summary>
    public string ScaleText
    {
        get => _scaleText;
        set { if (SetProperty(ref _scaleText, value ?? "")) Refresh(); }
    }

    /// <summary>Tidy the copy (true) or keep every line exactly as it is in the PDF (false).</summary>
    public bool Clean { get => _clean; set { if (SetProperty(ref _clean, value)) Refresh(); } }

    /// <summary>For tidying: how far a point may move, in millimetres on the paper.</summary>
    public string ToleranceText { get => _toleranceText; set { if (SetProperty(ref _toleranceText, value ?? "")) Refresh(); } }

    public bool Join { get => _join; set { if (SetProperty(ref _join, value)) Refresh(); } }
    public bool IncludeText { get => _includeText; set { if (SetProperty(ref _includeText, value)) Refresh(); } }
    public bool IncludeFills { get => _includeFills; set { if (SetProperty(ref _includeFills, value)) Refresh(); } }

    private string SourceKey => $"{FileName.ToLowerInvariant()}|{PageIndex + 1}";

    /// <summary>Opens a PDF. False, with <see cref="Message"/> saying why, when it cannot be used.</summary>
    public bool Open(byte[] data, string fileName)
    {
        try
        {
            _pdf = PdfFile.Open(data);
        }
        catch (PdfReadException ex)
        {
            Message = ex.Message;
            return false;
        }
        // The name without its folder, whichever way the path is written.
        string name = fileName ?? "";
        FileName = name[(name.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
        Pages.Clear();
        for (int i = 0; i < _pdf.PageCount; i++) Pages.Add($"Page {i + 1}");
        _pageIndex = -1;
        PageIndex = 0;
        return _page is not null;
    }

    private void LoadPage()
    {
        Groups.Clear();
        _edits.Clear();                             // another page: start from what is remembered
        _page = null;
        _result = null;
        if (_pdf is null || PageIndex < 0 || PageIndex >= _pdf.PageCount) { Refresh(); return; }
        try
        {
            _page = _pdf.ReadPage(PageIndex);
        }
        catch (PdfReadException ex)
        {
            Message = ex.Message;
            Refresh();
            return;
        }
        Message = "";
        var inch = new DrawingSettings { Unit = LinearUnit.Inch, Format = LengthFormat.Decimal, DecimalPrecision = 2 };
        PageInfo = $"Sheet {_page.Width / 72:0.##} x {_page.Height / 72:0.##} in ({_page.Width / 72 * 25.4:0} x {_page.Height / 72 * 25.4:0} mm)";

        // The scale: what was typed for this file and page before, else what is written on the sheet, else the one used last.
        var found = DrawingCopy.Convert(_page, new CopyOptions { IncludeText = false, IncludeFills = false });
        string scale;
        if (_memory.Scales.TryGetValue(SourceKey, out var remembered))
        {
            scale = remembered;
            ScaleHint = _memory.Corrections.ContainsKey(SourceKey)
                ? "The scale you gave this page before, with the correction from your calibration."
                : "The scale you gave this page before.";
        }
        else if (found.ScaleText is not null)
        {
            scale = found.ScaleText;
            ScaleHint = "Found written on the sheet. Check it: a sheet can carry details at other scales.";
        }
        else if (_memory.LastScale.Length > 0)
        {
            scale = _memory.LastScale;
            ScaleHint = "No scale was found on the sheet. This is the one you used last time: check it, or calibrate after placing.";
        }
        else
        {
            scale = "";
            ScaleHint = "No scale was found on the sheet. Type it (1/4\"=1', 1:50), or leave empty and use Calibrate after placing.";
        }
        _scaleText = scale;
        OnPropertyChanged(nameof(ScaleText));
        Refresh();
    }

    private bool TryScale(out double scale)
    {
        scale = 1.0;
        string text = ScaleText.Trim();
        if (text.Length == 0 || text.Equals("full", StringComparison.OrdinalIgnoreCase)) return true;
        return LayoutStandards.TryParseScale(text, new DrawingSettings { Unit = LinearUnit.Inch }, out scale);
    }

    /// <summary>Makes the copy again with the options as they stand, and shows it.</summary>
    public void Refresh()
    {
        // What was changed in the list so far in this window is kept when the copy is made again.
        foreach (var row in Groups) _edits[row.Group.Key] = (row.Include, row.LayerName.Trim());
        Groups.Clear();
        _result = null;
        if (_page is null)
        {
            Preview = Array.Empty<Entity>();
            Summary = Notes = "";
            OnPropertyChanged(nameof(CanPlace));
            return;
        }
        if (!TryScale(out double scale))
        {
            Message = $"\"{ScaleText}\" is not a scale. Type for example 1/4\"=1' or 1:50, or leave it empty.";
            Preview = Array.Empty<Entity>();
            OnPropertyChanged(nameof(CanPlace));
            return;
        }
        if (!double.TryParse(ToleranceText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double toleranceMm) || toleranceMm < 0 || toleranceMm > 5)
        {
            Message = "The tolerance is a number of millimetres on the paper, from 0 to 5.";
            Preview = Array.Empty<Entity>();
            OnPropertyChanged(nameof(CanPlace));
            return;
        }
        Message = "";

        double unitsPerPoint = DrawingCopy.UnitsPerPoint(scale, _session.Document.Settings.Unit);
        if (_memory.Corrections.TryGetValue(SourceKey, out double correction) && correction > 0 &&
            _memory.Scales.TryGetValue(SourceKey, out var same) && same == ScaleText.Trim()) unitsPerPoint *= correction;

        _result = DrawingCopy.Convert(_page, new CopyOptions
        {
            UnitsPerPoint = unitsPerPoint,
            Clean = Clean,
            TolerancePoints = toleranceMm / 25.4 * 72.0,
            JoinIntoPolylines = Join,
            IncludeText = IncludeText,
            IncludeFills = IncludeFills,
        });

        foreach (var group in _result.Groups)
        {
            // The layer this kind of line was put on last time, and whether it was left out.
            if (_memory.LayerNames.TryGetValue(group.Key, out var learned) && learned.Length > 0) group.LayerName = learned;
            if (_memory.LeftOut.Contains(group.Key)) group.Include = false;
            if (_edits.TryGetValue(group.Key, out var edit))
            {
                group.Include = edit.Include;
                if (edit.LayerName.Length > 0) group.LayerName = edit.LayerName;
            }
            Groups.Add(new CopyGroupViewModel(group, Summarize));
        }
        Summarize();
    }

    private void Summarize()
    {
        if (_result is null) return;
        var included = _result.Included.ToList();
        var settings = _session.Document.Settings;
        var box = BoundsOf(included);
        string size = box.IsEmpty ? "" : $"  Size in the drawing: {UnitFormatter.FormatLength(box.Width, settings)} x {UnitFormatter.FormatLength(box.Height, settings)}.";
        Summary = $"{included.OfType<LineEntity>().Count():N0} lines, {included.OfType<PolylineEntity>().Count():N0} polylines, " +
                  $"{included.OfType<ArcEntity>().Count():N0} arcs, {included.OfType<CircleEntity>().Count():N0} circles, {included.OfType<TextEntity>().Count():N0} texts.{size}";
        var notes = new List<string>(_result.Notes);
        if (_result.Cleanup is { } cleanup) notes.Insert(0, cleanup.ChangedAnything ? "Tidied: " + cleanup + "." : "Tidy: nothing needed changing at this tolerance.");
        if (_memory.Copies > 0 && _result.Groups.Any(g => _memory.LayerNames.ContainsKey(g.Key)))
            notes.Add("Layers are named the way you named them in earlier copies.");
        Notes = string.Join("\n", notes);

        // The picture: everything, or every n-th object of a very large page.
        int step = Math.Max(1, included.Count / PreviewLimit);
        Preview = step == 1 ? included : included.Where((_, i) => i % step == 0).ToList();
        OnPropertyChanged(nameof(CanPlace));
    }

    private static HAQQ.CAD.Geometry.BoundingBox BoundsOf(IEnumerable<Entity> entities)
    {
        var box = HAQQ.CAD.Geometry.BoundingBox.Empty;
        foreach (var entity in entities) box = box.Union(entity.Bounds);
        return box;
    }

    /// <summary>
    /// Starts placing the copy by a click (the window then closes), and remembers the choices made for the
    /// next copy. False, with a message, when there is nothing to place or a layer name cannot be used.
    /// </summary>
    public bool Place()
    {
        if (_result is null || !_result.Included.Any()) { Message = "There is nothing to place: open a PDF page with line work, and leave at least one group switched on."; return false; }
        foreach (var group in _result.Groups.Where(g => g.Include))
        {
            string name = group.LayerName.Trim();
            if (name.Length == 0 || name.Length > 64 || name.Any(char.IsControl)) { Message = $"Give \"{group.Description}\" a layer name (1 to 64 characters)."; return false; }
        }
        var layers = _result.Groups.Where(g => g.Include)
            .GroupBy(g => g.LayerName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new CopyLayer(g.Key, g.First().Color, g.First().Linetype, g.SelectMany(x => x.Entities).ToList()))
            .ToList();
        if (!_session.BeginCopyPlacement(layers, SourceKey)) { Message = "The copy could not be placed."; return false; }

        foreach (var group in _result.Groups)
        {
            _memory.LayerNames[group.Key] = group.LayerName.Trim();
            _memory.LeftOut.Remove(group.Key);
            if (!group.Include) _memory.LeftOut.Add(group.Key);
        }
        string scale = ScaleText.Trim();
        if (!_memory.Scales.TryGetValue(SourceKey, out var before) || before != scale) _memory.Corrections.Remove(SourceKey);
        _memory.Scales[SourceKey] = scale;
        if (scale.Length > 0) _memory.LastScale = scale;
        _memory.Clean = Clean;
        _memory.Join = Join;
        _memory.IncludeText = IncludeText;
        _memory.IncludeFills = IncludeFills;
        if (double.TryParse(ToleranceText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double mm)) _memory.ToleranceMm = mm;
        _memory.Copies++;
        if (!_store.Save(_memory)) _session.Post("Your choices for this copy could not be saved for next time.");
        return true;
    }
}
