using System.Collections.ObjectModel;
using System.Globalization;
using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Presentation;

/// <summary>One pattern in the Hatch window's list, with a small sample of it to show.</summary>
public sealed class HatchPatternItem : ObservableObject
{
    private bool _isFavorite;

    internal HatchPatternItem(HatchPattern pattern, bool favorite)
    {
        Name = pattern.Name;
        Title = pattern.Title;
        Category = pattern.Category;
        _isFavorite = favorite;
        Entities = HatchViewModel.Sample(pattern.Name, 0);
    }

    public string Name { get; }
    public string Title { get; }
    public string Category { get; }
    public IReadOnlyList<Entity> Entities { get; }
    public bool IsFavorite { get => _isFavorite; internal set => SetProperty(ref _isFavorite, value); }
}

/// <summary>
/// The Hatch window: the pattern (from a list with pictures, searchable, with favourites and the ones used
/// lately), its scale and angle, how openings inside the area are treated, and whether the hatch follows its
/// boundary. For a new hatch it then hands over to clicking inside areas or selecting objects; for an existing
/// hatch (double-click) it changes that hatch.
/// </summary>
public sealed class HatchViewModel : ObservableObject
{
    public const string All = "All patterns", RecentCategory = "Recently used", FavoritesCategory = "Favorites";
    public const string AutoScale = "Auto";

    private readonly EditorSession _session;
    private readonly Guid? _editing;
    private readonly IList<string> _favorites;
    private readonly Action? _favoritesChanged;
    private readonly List<HatchPatternItem> _all;
    private HatchPatternItem? _selected;
    private string _category = All, _filter = "", _scaleText, _angleText, _gapText, _islands, _message = "";
    private bool _associative, _originAtCorner;
    private IReadOnlyList<Entity> _preview = Array.Empty<Entity>();

    public HatchViewModel(EditorSession session, Guid? editing = null, IList<string>? favorites = null, Action? favoritesChanged = null)
    {
        _session = session;
        _favorites = favorites ?? new List<string>();
        _favoritesChanged = favoritesChanged;
        _all = HatchPatterns.All.Select(p => new HatchPatternItem(p, IsFavoriteName(p.Name))).ToList();
        IslandNames = new[] { "Normal (leave openings, fill what is inside them)", "Outer (leave everything inside open)", "Ignore (hatch through openings)" };
        Categories = new[] { All, RecentCategory, FavoritesCategory }.Concat(HatchPatterns.Categories).ToList();

        var settings = session.Document.Settings;
        var h = session.HatchDefaults;
        string pattern = h.Pattern;
        if (editing is Guid id && session.Document.GetEntity(id) is HatchEntity hatch)
        {
            _editing = id;
            pattern = hatch.Pattern;
            _scaleText = Number(hatch.Scale);
            _angleText = Number(hatch.Angle);
            _islands = IslandNames[(int)hatch.Islands];
            _associative = hatch.Associative;
            _gapText = UnitFormatter.FormatLength(hatch.GapTolerance, settings);
            CanFollow = hatch.SourceIds.Count > 0;
            BoundaryLost = hatch.BoundaryLost;
            AreaText = UnitFormatter.FormatArea(hatch.Area, settings);
        }
        else
        {
            _scaleText = h.ScaleChosen ? Number(h.Scale) : AutoScale;
            _angleText = Number(h.Angle);
            _islands = IslandNames[(int)h.Islands];
            _associative = h.Associative;
            _originAtCorner = h.OriginAtCorner;
            _gapText = UnitFormatter.FormatLength(h.GapTolerance, settings);
            CanFollow = true;
            AreaText = "";
        }
        Refresh();
        _selected = _all.FirstOrDefault(i => string.Equals(i.Name, pattern, StringComparison.OrdinalIgnoreCase)) ?? _all.First(i => i.Name == HatchPatterns.Default);
        UpdatePreview();
    }

    public bool IsEditing => _editing is not null;
    public bool IsCreating => _editing is null;
    public string WindowTitle => IsEditing ? "Edit Hatch" : "Hatch";
    public bool CanFollow { get; }
    public bool BoundaryLost { get; }
    public string AreaText { get; }
    public IReadOnlyList<string> Categories { get; }
    public IReadOnlyList<string> IslandNames { get; }
    public ObservableCollection<HatchPatternItem> Items { get; } = new();

    public string Category { get => _category; set { if (SetProperty(ref _category, value ?? All)) Refresh(); } }
    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value ?? "")) Refresh(); } }

    public HatchPatternItem? Selected
    {
        get => _selected;
        set
        {
            if (value is null || !SetProperty(ref _selected, value)) return;      // a list that loses its selection keeps the pattern
            UpdatePreview();
            OnPropertyChanged(nameof(IsPattern));
            OnPropertyChanged(nameof(FavoriteLabel));
        }
    }

    /// <summary>False for the solid fill, which has no scale or angle.</summary>
    public bool IsPattern => _selected?.Name != HatchPatterns.Solid;

    public string ScaleText { get => _scaleText; set { if (SetProperty(ref _scaleText, value ?? "")) UpdatePreview(); } }
    public string AngleText { get => _angleText; set { if (SetProperty(ref _angleText, value ?? "")) UpdatePreview(); } }
    public string GapText { get => _gapText; set => SetProperty(ref _gapText, value ?? ""); }
    public string Islands { get => _islands; set => SetProperty(ref _islands, value ?? IslandNames[0]); }
    public bool Associative { get => _associative; set => SetProperty(ref _associative, value); }
    public bool OriginAtCorner { get => _originAtCorner; set => SetProperty(ref _originAtCorner, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>The chosen pattern at the chosen angle, for the large picture.</summary>
    public IReadOnlyList<Entity> Preview { get => _preview; private set => SetProperty(ref _preview, value); }

    /// <summary>How far apart the pattern's closest lines will be in the drawing at the scale typed.</summary>
    public string SpacingText
    {
        get
        {
            if (_selected is null || HatchPatterns.Find(_selected.Name) is not { IsSolid: false } pattern) return "";
            if (!TryScale(out double scale, out bool auto)) return "";
            if (auto) return "Scale Auto: chosen to suit the first area hatched (about 40 lines across it).";
            return $"Closest lines {UnitFormatter.FormatLength(HatchPatterns.FinestSpacing(pattern) * scale, _session.Document.Settings)} apart in the drawing.";
        }
    }

    public string FavoriteLabel => _selected is { IsFavorite: true } ? "Remove from favorites" : "Add to favorites";

    private bool IsFavoriteName(string name) => _favorites.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>A 3 x 2 sample of a pattern with its outline, for the pictures.</summary>
    public static IReadOnlyList<Entity> Sample(string pattern, double angle)
    {
        var corners = new[] { new Point2D(0, 0), new Point2D(3, 0), new Point2D(3, 2), new Point2D(0, 2) };
        return new Entity[]
        {
            new HatchEntity(new[] { corners }) { Pattern = pattern, Scale = 1, Angle = angle },
            new PolylineEntity(corners, true),
        };
    }

    private void Refresh()
    {
        string filter = _filter.Trim();
        IEnumerable<HatchPatternItem> items = _all;
        if (filter.Length > 0)
            items = items.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || i.Title.Contains(filter, StringComparison.OrdinalIgnoreCase));
        else if (_category == RecentCategory)
            items = _session.HatchDefaults.Recent.Select(n => _all.FirstOrDefault(i => string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase))).Where(i => i is not null).Select(i => i!);
        else if (_category == FavoritesCategory) items = items.Where(i => i.IsFavorite);
        else if (_category != All) items = items.Where(i => i.Category == _category);
        Items.Clear();
        foreach (var item in items) Items.Add(item);
    }

    private void UpdatePreview()
    {
        double angle = double.TryParse(_angleText.Trim().TrimEnd('°'), NumberStyles.Float, CultureInfo.InvariantCulture, out double a) && double.IsFinite(a) ? a : 0;
        Preview = _selected is null ? Array.Empty<Entity>() : Sample(_selected.Name, angle);
        OnPropertyChanged(nameof(SpacingText));
    }

    /// <summary>Marks or unmarks the chosen pattern as a favourite; kept on this computer.</summary>
    public void ToggleFavorite()
    {
        if (_selected is null) return;
        _selected.IsFavorite = !_selected.IsFavorite;
        for (int i = _favorites.Count - 1; i >= 0; i--)
            if (string.Equals(_favorites[i], _selected.Name, StringComparison.OrdinalIgnoreCase)) _favorites.RemoveAt(i);
        if (_selected.IsFavorite) _favorites.Add(_selected.Name);
        _favoritesChanged?.Invoke();
        OnPropertyChanged(nameof(FavoriteLabel));
        if (_category == FavoritesCategory) Refresh();
    }

    /// <summary>Twice as coarse, or twice as fine.</summary>
    public void StepScale(bool larger)
    {
        if (!TryScale(out double scale, out bool auto) || auto) scale = 1;
        ScaleText = Number(larger ? scale * 2 : scale / 2);
    }

    private bool TryScale(out double scale, out bool auto)
    {
        string text = _scaleText.Trim();
        auto = text.Length == 0 || string.Equals(text, AutoScale, StringComparison.OrdinalIgnoreCase);
        scale = 1;
        if (auto) return true;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) && scale > 1e-9 && scale < 1e9 && double.IsFinite(scale);
    }

    private bool Read(out double scale, out bool auto, out double angle, out double gap, out HatchIslands islands)
    {
        angle = gap = 0;
        islands = (HatchIslands)Math.Max(0, IslandNames.ToList().IndexOf(_islands));
        if (!TryScale(out scale, out auto)) { Message = "Scale: a number more than 0 (2 makes the pattern twice as coarse), or Auto."; return false; }
        if (auto && IsEditing) { Message = "Type a scale for this hatch."; return false; }
        if (!double.TryParse(_angleText.Trim().TrimEnd('°'), NumberStyles.Float, CultureInfo.InvariantCulture, out angle) || !double.IsFinite(angle) || Math.Abs(angle) > 36000)
        {
            Message = "Angle: degrees, counter-clockwise (45, -30 ...).";
            return false;
        }
        string gapText = _gapText.Trim();
        if (gapText.Length > 0 && (!UnitFormatter.TryParseLength(gapText, _session.Document.Settings, out gap) || gap < 0 || !double.IsFinite(gap)))
        {
            Message = "Gap tolerance: a length of 0 or more, in drawing units.";
            return false;
        }
        Message = "";
        return true;
    }

    /// <summary>
    /// Takes what is set. For a new hatch: it becomes what the next hatches are made with. For an existing
    /// hatch: that hatch is changed (one undo step). False with a message when something typed cannot be used.
    /// </summary>
    public bool Apply()
    {
        if (_selected is null) { Message = "Choose a pattern."; return false; }
        if (!Read(out double scale, out bool auto, out double angle, out double gap, out var islands)) return false;
        string pattern = _selected.Name;

        if (_editing is Guid id)
        {
            bool follow = _associative && CanFollow;
            return _session.UpdateHatch(id, h =>
            {
                h.Pattern = pattern;
                h.Scale = scale;
                h.Angle = angle;
                h.Islands = islands;
                h.GapTolerance = gap;
                h.Associative = follow;
            });
        }

        var d = _session.HatchDefaults;
        d.Pattern = pattern;
        d.ScaleChosen = !auto;
        if (!auto) d.Scale = scale;
        d.Angle = angle;
        d.Islands = islands;
        d.GapTolerance = gap;
        d.Associative = _associative;
        d.OriginAtCorner = _originAtCorner;
        return true;
    }

    /// <summary>New hatch: go on to clicking inside closed areas.</summary>
    public bool PickPoints() => IsCreating && Apply() && _session.StartCommand("HATCHPICK");

    /// <summary>New hatch: go on to selecting the objects that enclose the area.</summary>
    public bool SelectObjects() => IsCreating && Apply() && _session.StartCommand("HATCHSELECT");
}
