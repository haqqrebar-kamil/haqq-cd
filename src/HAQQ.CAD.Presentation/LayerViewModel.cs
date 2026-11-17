using System.Globalization;
using HAQQ.CAD.Core;

namespace HAQQ.CAD.Presentation;

/// <summary>One row of the layer manager. Every edit is routed through the session so it is undoable.</summary>
public sealed class LayerViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private string _name = "";
    private string _colorHex = "#FFFFFF";
    private string _lineweightText = "";
    private bool _isVisible, _isLocked, _isFrozen, _isCurrent;
    private string _linetype = "";

    private static readonly Dictionary<string, string> ColorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#FFFFFF"] = "White", ["#FF0000"] = "Red", ["#FFFF00"] = "Yellow", ["#00FF00"] = "Green",
        ["#00FFFF"] = "Cyan", ["#0000FF"] = "Blue", ["#FF00FF"] = "Magenta", ["#FF7F00"] = "Orange",
        ["#808080"] = "Gray", ["#C0C0C0"] = "Light gray", ["#7FDF7F"] = "Light green", ["#7FBFFF"] = "Light blue",
        ["#FF7F7F"] = "Light red", ["#BF7FFF"] = "Violet",
    };

    /// <summary>The standard colours (#RRGGBB) that have a plain name.</summary>
    public static IReadOnlyList<string> StandardColors { get; } = new[]
    {
        "#FFFFFF", "#FF0000", "#FFFF00", "#00FF00", "#00FFFF", "#0000FF", "#FF00FF",
        "#FF7F00", "#808080", "#C0C0C0", "#7FDF7F", "#7FBFFF", "#FF7F7F", "#BF7FFF",
    };

    /// <summary>The #RRGGBB value of a colour name ("Red"), or null.</summary>
    public static string? ColorFromName(string? name)
    {
        foreach (var pair in ColorNames)
            if (string.Equals(pair.Value, name?.Trim(), StringComparison.OrdinalIgnoreCase)) return pair.Key;
        return null;
    }

    /// <summary>A plain name for a standard colour ("Red"); other colours are shown as #RRGGBB.</summary>
    public static string ColorNameOf(string hex) => ColorNames.TryGetValue(hex, out string? name) ? name : hex.ToUpperInvariant();

    internal LayerViewModel(MainViewModel owner, Layer layer, bool isCurrent)
    {
        _owner = owner;
        Id = layer.Id;
        Sync(layer, isCurrent);
    }

    public Guid Id { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (value == _name) return;
            if (!_owner.Session.UpdateLayer(Id, l => l.Name = value ?? "")) OnPropertyChanged();
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (value == _isVisible) return;
            if (!_owner.Session.UpdateLayer(Id, l => l.IsVisible = value)) OnPropertyChanged();
        }
    }

    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (value == _isLocked) return;
            if (!_owner.Session.UpdateLayer(Id, l => l.IsLocked = value)) OnPropertyChanged();
        }
    }

    public bool IsFrozen
    {
        get => _isFrozen;
        set
        {
            if (value == _isFrozen) return;
            if (!_owner.Session.UpdateLayer(Id, l => l.IsFrozen = value)) OnPropertyChanged();
        }
    }

    /// <summary>Layer colour as #RRGGBB.</summary>
    public string ColorHex
    {
        get => _colorHex;
        set
        {
            if (string.Equals(value, _colorHex, StringComparison.OrdinalIgnoreCase)) return;
            if (!CadColor.TryParse(value, out var color) || !color.IsExplicit ||
                !_owner.Session.UpdateLayer(Id, l => l.Color = color))
                OnPropertyChanged();
        }
    }

    /// <summary>Lineweight in millimetres, as text.</summary>
    public string LineweightText
    {
        get => _lineweightText;
        set
        {
            if (value == _lineweightText) return;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double mm) ||
                !_owner.Session.UpdateLayer(Id, l => l.LineweightMm = mm))
                OnPropertyChanged();
        }
    }

    /// <summary>Colour name for display beside the swatch.</summary>
    public string ColorName => ColorNameOf(_colorHex);

    /// <summary>Linetype of the layer: objects set to ByLayer are drawn with it.</summary>
    public string Linetype
    {
        get => _linetype;
        set
        {
            if (string.IsNullOrEmpty(value) || value == _linetype) return;
            var definition = _owner.Session.Document.FindLinetype(value);
            if (definition is null || !_owner.Session.UpdateLayer(Id, l => l.Linetype = definition.Name)) OnPropertyChanged();
        }
    }

    /// <summary>Makes this the layer new objects are drawn on.</summary>
    public void MakeCurrent() => _owner.Session.SetCurrentLayer(Id);

    public bool IsCurrent
    {
        get => _isCurrent;
        private set => SetProperty(ref _isCurrent, value);
    }

    internal void Sync(Layer layer, bool isCurrent)
    {
        if (SetField(ref _name, layer.Name)) OnPropertyChanged(nameof(Name));
        if (SetField(ref _colorHex, layer.Color.ToString()))
        {
            OnPropertyChanged(nameof(ColorHex));
            OnPropertyChanged(nameof(ColorName));
        }
        if (SetField(ref _linetype, layer.Linetype)) OnPropertyChanged(nameof(Linetype));
        if (SetField(ref _lineweightText, layer.LineweightMm.ToString("0.##", CultureInfo.InvariantCulture))) OnPropertyChanged(nameof(LineweightText));
        if (SetField(ref _isVisible, layer.IsVisible)) OnPropertyChanged(nameof(IsVisible));
        if (SetField(ref _isLocked, layer.IsLocked)) OnPropertyChanged(nameof(IsLocked));
        if (SetField(ref _isFrozen, layer.IsFrozen)) OnPropertyChanged(nameof(IsFrozen));
        IsCurrent = isCurrent;
    }

    private static bool SetField<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        return true;
    }
}
