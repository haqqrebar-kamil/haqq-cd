using System.Collections.ObjectModel;
using System.Globalization;
using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;

namespace HAQQ.CAD.Presentation;

/// <summary>Names shown for the choices of a dimension style, shared by the settings window and the ribbon.</summary>
public static class DimensionStyleChoices
{
    public static IReadOnlyList<string> Arrows { get; } = new[] { "Arrow", "Tick", "Dot", "None" };

    public static IReadOnlyList<string> Formats { get; } = new[] { "Same as the drawing", "Decimal", "Feet and inches", "Inches with fractions" };

    public static IReadOnlyList<string> Decimals { get; } = new[] { "0", "0.0", "0.00", "0.000", "0.0000", "0.00000", "0.000000" };

    public static IReadOnlyList<string> Fractions { get; } = new[] { "1\"", "1/2\"", "1/4\"", "1/8\"", "1/16\"", "1/32\"", "1/64\"" };

    public static string ArrowName(DimensionArrow arrow) => Arrows[(int)arrow];
    public static DimensionArrow? ArrowOf(string? name) => Arrows.ToList().IndexOf(name ?? "") is >= 0 and var i ? (DimensionArrow)i : null;
    public static string DecimalsName(int places) => Decimals[Math.Clamp(places, 0, Decimals.Count - 1)];
    public static string FractionName(int denominator) => denominator <= 1 ? Fractions[0] : $"1/{denominator}\"";
    public static int? FractionOf(string? name) => Fractions.ToList().IndexOf(name ?? "") is >= 0 and var i ? 1 << i : null;
}

/// <summary>
/// The Dimension Settings window: the dimension styles of the drawing, and every value of the selected one.
/// A change is applied at once (to the style and to every dimension drawn with it) and can be undone.
/// </summary>
public sealed class DimensionStylesViewModel : ObservableObject, IDisposable
{
    private readonly EditorSession _session;
    private string? _selected;
    private string _newName = "";
    private string _message = "";
    private string? _lastPost;

    public DimensionStylesViewModel(EditorSession session)
    {
        _session = session;
        _session.StateChanged += OnSessionChanged;
        _session.MessagePosted += OnPosted;
        NewCommand = new RelayCommand(() => AddNew());
        DeleteCommand = new RelayCommand(DeleteSelected, () => Selected is not null && Selected != DimensionStyle.StandardName);
        SetCurrentCommand = new RelayCommand(() => { if (Selected is { } name) Run(() => _session.SetCurrentDimensionStyle(name)); },
            () => Selected is not null && Selected != CurrentName);
        Reload();
        Selected = CurrentName;
    }

    public void Dispose()
    {
        _session.StateChanged -= OnSessionChanged;
        _session.MessagePosted -= OnPosted;
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Reload();
    private void OnPosted(string message) => _lastPost = message;

    public ObservableCollection<string> Styles { get; } = new();
    public RelayCommand NewCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand SetCurrentCommand { get; }

    public IReadOnlyList<string> ArrowNames => DimensionStyleChoices.Arrows;
    public IReadOnlyList<string> FormatNames => DimensionStyleChoices.Formats;
    public IReadOnlyList<string> DecimalNames => DimensionStyleChoices.Decimals;
    public IReadOnlyList<string> FractionNames => DimensionStyleChoices.Fractions;
    public IReadOnlyList<string> AngleNames { get; } = new[] { "0", "0.0", "0.00", "0.000", "0.0000" };

    /// <summary>The style new dimensions are drawn with.</summary>
    public string CurrentName => _session.Document.CurrentDimensionStyleName;

    /// <summary>The style whose values are shown.</summary>
    public string? Selected
    {
        get => _selected;
        set
        {
            if (value is null || _session.Document.FindDimensionStyle(value) is null) { OnPropertyChanged(); return; }
            if (!SetProperty(ref _selected, value)) return;
            RaiseAll();
        }
    }

    /// <summary>Name for the next new style; empty gives "Style 1", "Style 2"...</summary>
    public string NewName
    {
        get => _newName;
        set => SetProperty(ref _newName, value ?? "");
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    private DimensionStyle Style => _session.Document.FindDimensionStyle(_selected) ?? _session.Document.CurrentDimensionStyle;

    /// <summary>The selected style with its sizes brought to a fixed text height, for the sample picture.</summary>
    public DimensionStyle PreviewStyle => Style.Scaled(3.2 / Style.TextHeight);

    public DrawingSettings DrawingSettings => _session.Document.Settings;

    /// <summary>What the selected style is: its name, and whether it is current and how many dimensions use it.</summary>
    public string Summary
    {
        get
        {
            var style = Style;
            int used = _session.Document.AllEntities.OfType<DimensionEntity>().Count(d => string.Equals(d.StyleName, style.Name, StringComparison.OrdinalIgnoreCase));
            string current = style.Name == CurrentName ? "current style, " : "";
            return $"{style.Name}  ({current}used by {used} dimension{(used == 1 ? "" : "s")})";
        }
    }

    // ------------------------------------------------------------- actions

    /// <summary>Adds a style that starts as a copy of the selected one, and selects it.</summary>
    public bool AddNew()
    {
        string name = NewName.Trim();
        if (name.Length == 0)
        {
            for (int i = 1; ; i++)
            {
                name = $"Style {i}";
                if (_session.Document.FindDimensionStyle(name) is null) break;
            }
        }
        if (!Run(() => _session.AddDimensionStyle(Style with { Name = name }))) return false;
        NewName = "";
        Reload();
        Selected = _session.Document.FindDimensionStyle(name)?.Name;
        return true;
    }

    private void DeleteSelected()
    {
        if (Selected is not { } name) return;
        if (!Run(() => _session.DeleteDimensionStyle(name))) return;
        _selected = null;
        Reload();
        Selected = CurrentName;
    }

    private bool Run(Func<bool> action)
    {
        _lastPost = null;
        bool ok = action();
        Message = ok ? "" : _lastPost ?? "That could not be done.";
        return ok;
    }

    private void Change(Func<DimensionStyle, DimensionStyle> change)
    {
        var style = Style;
        Run(() => _session.ModifyDimensionStyle(change(style)));
        RaiseAll();
    }

    private void ChangeLength(string text, Func<DimensionStyle, double, DimensionStyle> change)
    {
        if (!UnitFormatter.TryParseLength(text, _session.Document.Settings, out double v))
        {
            Message = $"\"{text}\" is not a length.";
            RaiseAll();
            return;
        }
        Change(s => change(s, v));
    }

    private string Length(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    // ------------------------------------------------------- style values

    public string TextHeightText { get => Length(Style.TextHeight); set => ChangeLength(value, (s, v) => s with { TextHeight = v }); }
    public string TextGapText { get => Length(Style.TextGap); set => ChangeLength(value, (s, v) => s with { TextGap = v }); }
    public string ArrowSizeText { get => Length(Style.ArrowSize); set => ChangeLength(value, (s, v) => s with { ArrowSize = v }); }
    public string ExtensionOffsetText { get => Length(Style.ExtensionOffset); set => ChangeLength(value, (s, v) => s with { ExtensionOffset = v }); }
    public string ExtensionBeyondText { get => Length(Style.ExtensionBeyond); set => ChangeLength(value, (s, v) => s with { ExtensionBeyond = v }); }
    public string RoundOffText { get => Length(Style.RoundOff); set => ChangeLength(value, (s, v) => s with { RoundOff = v }); }

    public string ScaleFactorText
    {
        get => Style.ScaleFactor.ToString("0.######", CultureInfo.InvariantCulture);
        set
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) { Message = $"\"{value}\" is not a number."; RaiseAll(); return; }
            Change(s => s with { ScaleFactor = v });
        }
    }

    public string Prefix { get => Style.Prefix; set => Change(s => s with { Prefix = value ?? "" }); }
    public string Suffix { get => Style.Suffix; set => Change(s => s with { Suffix = value ?? "" }); }

    public string SelectedArrow
    {
        get => DimensionStyleChoices.ArrowName(Style.Arrow);
        set { if (DimensionStyleChoices.ArrowOf(value) is { } arrow && arrow != Style.Arrow) Change(s => s with { Arrow = arrow }); }
    }

    public string SelectedFormat
    {
        get => FormatNames[(int)Style.Format];
        set
        {
            int i = FormatNames.ToList().IndexOf(value ?? "");
            if (i >= 0 && (DimensionUnitFormat)i != Style.Format) Change(s => s with { Format = (DimensionUnitFormat)i });
        }
    }

    public string SelectedDecimals
    {
        get => DimensionStyleChoices.DecimalsName(Style.Precision);
        set
        {
            int i = DecimalNames.ToList().IndexOf(value ?? "");
            if (i >= 0 && i != Style.Precision) Change(s => s with { Precision = i });
        }
    }

    public string SelectedFraction
    {
        get => DimensionStyleChoices.FractionName(Style.FractionDenominator);
        set { if (DimensionStyleChoices.FractionOf(value) is { } d && d != Style.FractionDenominator) Change(s => s with { FractionDenominator = d }); }
    }

    public string SelectedAngleDecimals
    {
        get => AngleNames[Math.Clamp(Style.AnglePrecision, 0, AngleNames.Count - 1)];
        set
        {
            int i = AngleNames.ToList().IndexOf(value ?? "");
            if (i >= 0 && i != Style.AnglePrecision) Change(s => s with { AnglePrecision = i });
        }
    }

    public bool SuppressZeroFeet { get => Style.SuppressZeroFeet; set { if (value != Style.SuppressZeroFeet) Change(s => s with { SuppressZeroFeet = value }); } }
    public bool SuppressZeroInches { get => Style.SuppressZeroInches; set { if (value != Style.SuppressZeroInches) Change(s => s with { SuppressZeroInches = value }); } }

    /// <summary>False when the style writes lengths the way the drawing does, so its own format choices do not apply.</summary>
    public bool HasOwnFormat => Style.Format != DimensionUnitFormat.Drawing;

    /// <summary>A length written the way the selected style writes it, as an example.</summary>
    public string Example => Style.FormatLength(66.53125, _session.Document.Settings) + "   and   " + Style.FormatLength(6.0, _session.Document.Settings);

    // ------------------------------------------------------------ plumbing

    public void Reload()
    {
        var names = _session.Document.DimensionStyles.Select(s => s.Name).ToList();
        bool listChanged = !names.SequenceEqual(Styles);
        MainViewModel.SyncNames(Styles, names);
        if (_selected is null || _session.Document.FindDimensionStyle(_selected) is null) _selected = CurrentName;
        if (listChanged) OnPropertyChanged(nameof(Selected));
        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (string name in new[]
        {
            nameof(Selected), nameof(CurrentName), nameof(Summary), nameof(PreviewStyle), nameof(DrawingSettings), nameof(Example), nameof(HasOwnFormat),
            nameof(TextHeightText), nameof(TextGapText), nameof(ArrowSizeText), nameof(ExtensionOffsetText), nameof(ExtensionBeyondText),
            nameof(RoundOffText), nameof(ScaleFactorText), nameof(Prefix), nameof(Suffix), nameof(SelectedArrow), nameof(SelectedFormat),
            nameof(SelectedDecimals), nameof(SelectedFraction), nameof(SelectedAngleDecimals), nameof(SuppressZeroFeet), nameof(SuppressZeroInches),
        }) OnPropertyChanged(name);
        DeleteCommand.RaiseCanExecuteChanged();
        SetCurrentCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>
/// The dimension style strip on the ribbon: the style of new dimensions (or of the selected dimensions), its
/// text height and what its ends look like.
/// </summary>
public sealed class DimensionBarViewModel : ObservableObject
{
    private readonly EditorSession _session;
    private string? _selectedStyle;
    private string _textHeightText = "";
    private string? _selectedArrow;
    private bool _isActive;

    internal DimensionBarViewModel(EditorSession session) => _session = session;

    public ObservableCollection<string> StyleNames { get; } = new();
    public IReadOnlyList<string> ArrowNames => DimensionStyleChoices.Arrows;

    /// <summary>True while a dimension command runs or a dimension is selected: the Dimension Style group is shown on the HOME tab then.</summary>
    public bool IsActive
    {
        get => _isActive;
        private set => SetProperty(ref _isActive, value);
    }

    private List<DimensionEntity> SelectedDimensions => _session.GetSelectedEntities().OfType<DimensionEntity>().ToList();

    /// <summary>The style shown: that of the selected dimensions (null when they differ), else the current style.</summary>
    private DimensionStyle? Shown
    {
        get
        {
            var dims = SelectedDimensions;
            if (dims.Count == 0) return _session.Document.CurrentDimensionStyle;
            var names = dims.Select(d => d.StyleName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return names.Count == 1 ? _session.Document.FindDimensionStyle(names[0]) : null;
        }
    }

    /// <summary>With dimensions selected: their style, and setting it gives them another. Otherwise the current style.</summary>
    public string? SelectedStyle
    {
        get => _selectedStyle;
        set
        {
            if (string.IsNullOrEmpty(value) || value == _selectedStyle) return;
            bool ok = SelectedDimensions.Count > 0 ? _session.SetDimensionStyleOfSelection(value) : _session.SetCurrentDimensionStyle(value);
            if (!ok) OnPropertyChanged();
        }
    }

    /// <summary>Text height of the style shown. Changing it changes arrows and gaps in proportion, for every dimension of that style.</summary>
    public string TextHeightText
    {
        get => _textHeightText;
        set
        {
            if (value == _textHeightText) return;
            if (Shown is { } style && UnitFormatter.TryParseLength(value, _session.Document.Settings, out double height) && height > 0)
                _session.ModifyDimensionStyle(style.Scaled(height / style.TextHeight));
            else _session.Post("The text height must be a length more than 0.");
            OnPropertyChanged();
        }
    }

    public string? SelectedArrow
    {
        get => _selectedArrow;
        set
        {
            if (string.IsNullOrEmpty(value) || value == _selectedArrow) return;
            if (Shown is not { } style || DimensionStyleChoices.ArrowOf(value) is not { } arrow || !_session.ModifyDimensionStyle(style with { Arrow = arrow }))
                OnPropertyChanged();
        }
    }

    internal void Refresh()
    {
        var names = _session.Document.DimensionStyles.Select(s => s.Name).ToList();
        bool listChanged = !names.SequenceEqual(StyleNames);
        MainViewModel.SyncNames(StyleNames, names);

        var shown = Shown;
        string? style = shown?.Name;
        if (style != _selectedStyle || listChanged) { _selectedStyle = style; OnPropertyChanged(nameof(SelectedStyle)); }
        string height = shown is null ? "" : UnitFormatter.FormatLength(shown.TextHeight, _session.Document.Settings);
        if (height != _textHeightText) { _textHeightText = height; OnPropertyChanged(nameof(TextHeightText)); }
        string? arrow = shown is null ? null : DimensionStyleChoices.ArrowName(shown.Arrow);
        if (arrow != _selectedArrow) { _selectedArrow = arrow; OnPropertyChanged(nameof(SelectedArrow)); }
        IsActive = _session.ActiveCommand is DimensionCommandBase || SelectedDimensions.Count > 0;
    }
}
