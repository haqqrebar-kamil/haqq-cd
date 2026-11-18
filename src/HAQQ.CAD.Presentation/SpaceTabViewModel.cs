using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;

namespace HAQQ.CAD.Presentation;

/// <summary>One tab under the drawing: the model, or a layout.</summary>
public sealed class SpaceTabViewModel : ObservableObject
{
    private string _name;
    private bool _isActive;
    private string _detail = "";

    public SpaceTabViewModel(Guid id, string name)
    {
        Id = id;
        _name = name;
    }

    /// <summary><see cref="Guid.Empty"/> for the model, otherwise the layout's id.</summary>
    public Guid Id { get; }
    public bool IsModel => Id == Guid.Empty;

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }

    private bool _isRenaming;
    private string _editName = "";

    /// <summary>True while the name is being typed over in the tab itself.</summary>
    public bool IsRenaming { get => _isRenaming; set => SetProperty(ref _isRenaming, value); }

    /// <summary>The name as typed so far while renaming.</summary>
    public string EditName { get => _editName; set => SetProperty(ref _editName, value ?? ""); }

    /// <summary>The tooltip: what the tab is, and for a layout its sheet size.</summary>
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
}

/// <summary>An entry of the LAYOUT tab's lists: what is shown and the command that it runs.</summary>
public sealed record LayoutChoice(string Label, string Script);

/// <summary>The lists of the LAYOUT ribbon tab, for the drawing's units.</summary>
public static class LayoutChoices
{
    /// <summary>The standard sheets, each with its size in the drawing's units.</summary>
    public static IReadOnlyList<LayoutChoice> Papers(DrawingSettings settings) =>
        LayoutStandards.Papers.Select(p => new LayoutChoice(p.Name, "PAGESETUP " + p.Key)).ToList();

    /// <summary>The usual viewport scales, written the way the drawing's units are.</summary>
    public static IReadOnlyList<LayoutChoice> Scales(DrawingSettings settings) =>
        LayoutStandards.Scales(settings).Select(s => new LayoutChoice(
            LayoutStandards.ScaleName(s, settings),
            "VPSCALE 1:" + Math.Round(1.0 / s, 6).ToString("R", System.Globalization.CultureInfo.InvariantCulture))).ToList();
}
