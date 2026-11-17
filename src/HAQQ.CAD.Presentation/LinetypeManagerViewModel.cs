using System.Collections.ObjectModel;
using System.Globalization;
using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;

namespace HAQQ.CAD.Presentation;

/// <summary>One linetype in the Linetype Manager list.</summary>
public sealed class LinetypeRowViewModel
{
    public LinetypeRowViewModel(LinetypeDefinition definition)
    {
        Name = definition.Name;
        Description = definition.Description;
        Pattern = definition.Pattern.ToArray();
        IsBuiltIn = Linetypes.IsBuiltIn(definition.Name);
        Recipe = LinetypeManagerViewModel.Describe(Pattern);
    }

    public string Name { get; }
    public string Description { get; }
    public double[] Pattern { get; }
    public bool IsBuiltIn { get; }
    public string Kind => IsBuiltIn ? "Built-in" : "Custom";
    public string Tip => Description.Length > 0 ? Description : Recipe;

    /// <summary>How the linetype is made, in words: "dash 0.5, space 0.25".</summary>
    public string Recipe { get; }
}

/// <summary>One dash or one space of the linetype being edited: a slider and a number box share its value.</summary>
public sealed class LinetypeSegmentViewModel : ObservableObject
{
    private readonly LinetypeManagerViewModel _owner;
    private double _value;

    internal LinetypeSegmentViewModel(LinetypeManagerViewModel owner, int index, double value)
    {
        _owner = owner;
        Index = index;
        _value = value;
    }

    public int Index { get; }
    public bool IsGap => Index % 2 == 1;
    public string Label => (IsGap ? "Space " : "Dash ") + (Index / 2 + 1).ToString(CultureInfo.InvariantCulture);
    public string Hint => IsGap ? "Length of the space after the dash (more than 0)" : "Length of the dash (0 makes a dot)";

    /// <summary>Length in drawing units.</summary>
    public double Value => _value;

    public double Maximum => _owner.SliderMaximum;

    /// <summary>The slider: values snap to the editor's step.</summary>
    public double SliderValue
    {
        get => Math.Min(_value, Maximum);
        set
        {
            double step = _owner.Step;
            Set(Math.Round(value / step) * step);
        }
    }

    /// <summary>The number box: any length the drawing's units accept (0.375, 3/8, 1 1/2").</summary>
    public string Text
    {
        get => LinetypeManagerViewModel.Format(_value);
        set
        {
            if (!UnitFormatter.TryParseLength(value, _owner.Settings, out double v) || v < 0 || v > LinetypeManagerViewModel.MaxLength)
            {
                _owner.Message = $"\"{value}\" is not a length. Type a number such as 0.5 or 3/8.";
                OnPropertyChanged();
                return;
            }
            _owner.Message = "";
            Set(v);
        }
    }

    private void Set(double value)
    {
        value = Math.Clamp(Math.Round(value, 6), 0, LinetypeManagerViewModel.MaxLength);
        if (IsGap && value <= 0) value = _owner.Step;              // a space must have length
        if (value > _owner.SliderMaximum) _owner.GrowSlider(value);
        _value = value;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(SliderValue));
        OnPropertyChanged(nameof(Text));
        _owner.OnPatternEdited();
    }

    internal void RaiseMaximumChanged()
    {
        OnPropertyChanged(nameof(Maximum));
        OnPropertyChanged(nameof(SliderValue));
    }
}

/// <summary>
/// The Linetype Manager: the list of linetypes in the drawing and an editor that builds a linetype from up to
/// four dashes and four spaces. Every change goes through the editor session, so it can be undone.
/// </summary>
public sealed class LinetypeManagerViewModel : ObservableObject, IDisposable
{
    public const int MaxPairs = 4;
    public const double MaxLength = 100000;

    private readonly EditorSession _session;
    private LinetypeRowViewModel? _selected;
    private bool _isEditing;
    private string? _editingName;          // null while defining a new linetype
    private string _editName = "";
    private string _editDescription = "";
    private string _message = "";
    private double _sliderMaximum = 3;
    private double _previewUnits = 4;
    private string? _lastPost;

    public LinetypeManagerViewModel(EditorSession session)
    {
        _session = session;
        _session.StateChanged += OnSessionChanged;
        _session.MessagePosted += OnPosted;

        NewCommand = new RelayCommand(() => BeginNew());
        DuplicateCommand = new RelayCommand(() => BeginNew(Selected), () => Selected is { Pattern.Length: > 0 });
        EditCommand = new RelayCommand(BeginEdit, () => Selected is { IsBuiltIn: false });
        DeleteCommand = new RelayCommand(DeleteSelected, () => Selected is { IsBuiltIn: false });
        AddPairCommand = new RelayCommand(AddPair, () => IsEditing && Segments.Count / 2 < MaxPairs);
        RemovePairCommand = new RelayCommand(RemovePair, () => IsEditing && Segments.Count / 2 > 1);
        SaveCommand = new RelayCommand(() => Save(), () => IsEditing);
        CancelCommand = new RelayCommand(CancelEdit, () => IsEditing);
        Reload();
    }

    public void Dispose()
    {
        _session.StateChanged -= OnSessionChanged;
        _session.MessagePosted -= OnPosted;
    }

    internal DrawingSettings Settings => _session.Document.Settings;

    public ObservableCollection<LinetypeRowViewModel> Rows { get; } = new();
    public ObservableCollection<LinetypeSegmentViewModel> Segments { get; } = new();

    public RelayCommand NewCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand AddPairCommand { get; }
    public RelayCommand RemovePairCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    public LinetypeRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            RaiseCommands();
        }
    }

    /// <summary>True while the editor at the bottom is open (new linetype or changing a custom one).</summary>
    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (!SetProperty(ref _isEditing, value)) return;
            OnPropertyChanged(nameof(EditorTitle));
            OnPropertyChanged(nameof(NameLocked));
            RaiseCommands();
        }
    }

    /// <summary>The name cannot be changed while editing an existing linetype (layers and objects refer to it).</summary>
    public bool NameLocked => _editingName is not null;

    public string EditorTitle => !IsEditing ? "" : _editingName is null ? "New linetype" : $"Edit \"{_editingName}\"";

    public string EditName
    {
        get => _editName;
        set => SetProperty(ref _editName, value ?? "");
    }

    public string EditDescription
    {
        get => _editDescription;
        set => SetProperty(ref _editDescription, value ?? "");
    }

    /// <summary>The pattern in the editor: dash, space, dash, space...</summary>
    public double[] EditPattern => Segments.Select(s => s.Value).ToArray();

    /// <summary>The editor's pattern in words.</summary>
    public string EditRecipe => Describe(EditPattern);

    /// <summary>Length of one repeat of the editor's pattern.</summary>
    public string EditLength => Format(EditPattern.Sum());

    /// <summary>Top of every slider, in drawing units. Grows when a larger value is typed.</summary>
    public double SliderMaximum => _sliderMaximum;

    /// <summary>What one slider notch is worth.</summary>
    public double Step => _sliderMaximum <= 5 ? 0.05 : _sliderMaximum <= 25 ? 0.25 : _sliderMaximum <= 250 ? 1 : 10;

    /// <summary>
    /// How many drawing units every preview strip shows from end to end. It is the same for the list and for
    /// the editor, so the linetypes can be compared by eye.
    /// </summary>
    public double PreviewUnits
    {
        get => _previewUnits;
        private set => SetProperty(ref _previewUnits, value);
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value ?? "");
    }

    /// <summary>Linetype scale of the whole drawing (LTSCALE).</summary>
    public string LinetypeScaleText
    {
        get => Format(_session.Document.Settings.LinetypeScale);
        set
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 && double.IsFinite(v))
            {
                Message = "";
                if (Math.Abs(v - _session.Document.Settings.LinetypeScale) > 1e-12) _session.SetLinetypeScale(v);
            }
            else Message = "The linetype scale must be a number more than 0.";
            OnPropertyChanged();
        }
    }

    // ------------------------------------------------------------- actions

    /// <summary>Opens the editor for a new linetype, starting from <paramref name="basedOn"/> or from a plain dash.</summary>
    public void BeginNew(LinetypeRowViewModel? basedOn = null)
    {
        _editingName = null;
        EditName = UniqueName(basedOn is null ? "Custom" : basedOn.Name);
        EditDescription = "";
        LoadSegments(basedOn is { Pattern.Length: > 0 } ? basedOn.Pattern : new[] { 0.5, 0.25 });
        Message = "";
        OpenEditor();
    }

    /// <summary>Opens the editor on the selected custom linetype.</summary>
    public void BeginEdit()
    {
        if (Selected is not { IsBuiltIn: false } row) return;
        _editingName = row.Name;
        EditName = row.Name;
        EditDescription = row.Description;
        LoadSegments(row.Pattern);
        Message = "";
        OpenEditor();
    }

    private void OpenEditor()
    {
        _isEditing = false;                       // force the notifications, also when switching from one edit to another
        IsEditing = true;
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(NameLocked));
        OnPatternEdited();
    }

    public void CancelEdit()
    {
        _editingName = null;
        IsEditing = false;
        Segments.Clear();
        Message = "";
        UpdatePreviewUnits();
    }

    /// <summary>Adds one more dash and space (a copy of the last pair), up to four pairs.</summary>
    public void AddPair()
    {
        if (!IsEditing || Segments.Count / 2 >= MaxPairs) return;
        double dash = Segments.Count >= 2 ? Segments[^2].Value : 0.5;
        double gap = Segments.Count >= 2 ? Segments[^1].Value : 0.25;
        Segments.Add(new LinetypeSegmentViewModel(this, Segments.Count, dash));
        Segments.Add(new LinetypeSegmentViewModel(this, Segments.Count, gap));
        OnPatternEdited();
    }

    /// <summary>Removes the last dash and space; one pair always stays.</summary>
    public void RemovePair()
    {
        if (!IsEditing || Segments.Count / 2 <= 1) return;
        Segments.RemoveAt(Segments.Count - 1);
        Segments.RemoveAt(Segments.Count - 1);
        OnPatternEdited();
    }

    /// <summary>Creates the new linetype, or applies the changes to the one being edited. False (with <see cref="Message"/>) when it cannot.</summary>
    public bool Save()
    {
        if (!IsEditing) return false;
        double[] pattern = EditPattern;
        if (pattern.Sum() <= 0 || !LinetypeDefinition.IsValidPattern(pattern))
        {
            Message = "Give at least one dash or space a length.";
            return false;
        }

        string name = EditName.Trim();
        _lastPost = null;
        bool ok;
        if (_editingName is not null)
        {
            ok = _session.ModifyLinetype(_editingName, EditDescription.Trim(), pattern);
        }
        else
        {
            if (name.Length == 0) { Message = "Give the linetype a name."; return false; }
            if (Linetypes.IsReserved(name)) { Message = $"\"{name}\" cannot be used as a linetype name."; return false; }
            if (_session.Document.FindLinetype(name) is not null) { Message = $"There is already a linetype named \"{name}\". Choose another name."; return false; }
            ok = _session.AddLinetype(name, EditDescription.Trim(), pattern);
        }

        if (!ok)
        {
            Message = _lastPost ?? "The linetype could not be saved.";
            return false;
        }

        string saved = _editingName ?? name;
        _editingName = null;
        IsEditing = false;
        Segments.Clear();
        Reload();
        Selected = Rows.FirstOrDefault(r => string.Equals(r.Name, saved, StringComparison.OrdinalIgnoreCase));
        Message = _lastPost ?? "";
        return true;
    }

    public void DeleteSelected()
    {
        if (Selected is not { } row) return;
        _lastPost = null;
        bool ok = _session.DeleteLinetype(row.Name);
        if (ok && string.Equals(_editingName, row.Name, StringComparison.OrdinalIgnoreCase)) CancelEdit();
        Reload();
        Message = _lastPost ?? "";
    }

    // ------------------------------------------------------------ plumbing

    internal void OnPatternEdited()
    {
        OnPropertyChanged(nameof(EditPattern));
        OnPropertyChanged(nameof(EditRecipe));
        OnPropertyChanged(nameof(EditLength));
        UpdatePreviewUnits();
        RaiseCommands();
    }

    internal void GrowSlider(double value)
    {
        double wanted = NiceCeiling(value * 2);
        if (wanted <= _sliderMaximum) return;
        _sliderMaximum = wanted;
        OnPropertyChanged(nameof(SliderMaximum));
        OnPropertyChanged(nameof(Step));
        foreach (var s in Segments) s.RaiseMaximumChanged();
    }

    private void LoadSegments(IReadOnlyList<double> pattern)
    {
        Segments.Clear();
        _sliderMaximum = Math.Max(3, NiceCeiling(pattern.Max() * 2));
        OnPropertyChanged(nameof(SliderMaximum));
        OnPropertyChanged(nameof(Step));
        int count = Math.Min(pattern.Count, MaxPairs * 2);
        for (int i = 0; i < count; i++) Segments.Add(new LinetypeSegmentViewModel(this, i, pattern[i]));
    }

    private static double NiceCeiling(double v) => v <= 5 ? Math.Ceiling(v) : v <= 25 ? Math.Ceiling(v / 5) * 5 : v <= 250 ? Math.Ceiling(v / 25) * 25 : Math.Ceiling(v / 250) * 250;

    private string UniqueName(string stem)
    {
        for (int i = 1; ; i++)
        {
            string name = $"{stem} {i}";
            if (_session.Document.FindLinetype(name) is null) return name;
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Reload();
    private void OnPosted(string message) => _lastPost = message;

    /// <summary>Rebuilds the list from the drawing (after a change here, or an undo elsewhere).</summary>
    public void Reload()
    {
        var table = _session.Document.LinetypeTable;
        bool same = table.Count == Rows.Count;
        for (int i = 0; same && i < table.Count; i++)
            same = Rows[i].Name == table[i].Name && Rows[i].Description == table[i].Description && Rows[i].Pattern.SequenceEqual(table[i].Pattern);
        if (!same)
        {
            string? selected = Selected?.Name;
            Rows.Clear();
            foreach (var linetype in table) Rows.Add(new LinetypeRowViewModel(linetype));
            Selected = Rows.FirstOrDefault(r => r.Name == selected) ?? Rows.FirstOrDefault();
        }
        UpdatePreviewUnits();
        OnPropertyChanged(nameof(LinetypeScaleText));
        RaiseCommands();
    }

    private void UpdatePreviewUnits()
    {
        double longest = Rows.Count == 0 ? 0 : Rows.Max(r => r.Pattern.Sum());
        if (IsEditing) longest = Math.Max(longest, Segments.Sum(s => s.Value));
        PreviewUnits = Math.Max(4, Math.Ceiling(longest * 2.5));
    }

    private void RaiseCommands()
    {
        DuplicateCommand.RaiseCanExecuteChanged();
        EditCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        AddPairCommand.RaiseCanExecuteChanged();
        RemovePairCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    internal static string Format(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>A pattern in words: "dash 0.5, space 0.25, dot, space 0.25".</summary>
    public static string Describe(IReadOnlyList<double> pattern)
    {
        if (pattern.Count == 0) return "Solid line";
        var parts = new List<string>();
        for (int i = 0; i < pattern.Count; i++)
            parts.Add(i % 2 == 1 ? "space " + Format(pattern[i]) : pattern[i] <= 0 ? "dot" : "dash " + Format(pattern[i]));
        return string.Join(", ", parts);
    }
}
