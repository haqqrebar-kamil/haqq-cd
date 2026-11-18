using System.Globalization;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>What the next hatch is made with. Set in the Hatch window; kept while the drawing is open.</summary>
public sealed class HatchSettings
{
    public string Pattern { get; set; } = HatchPatterns.Default;
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// False until a scale has been given (typed, or worked out for the first hatch). Until then the first hatch
    /// picks a scale that suits the size of its area, because 1 is only right for small details.
    /// </summary>
    public bool ScaleChosen { get; set; }

    public double Angle { get; set; }
    public HatchIslands Islands { get; set; } = HatchIslands.Normal;
    public bool Associative { get; set; } = true;

    /// <summary>The largest opening between boundary objects that is still treated as closed, in drawing units.</summary>
    public double GapTolerance { get; set; }

    /// <summary>The pattern starts at the lower-left corner of each area instead of the drawing's 0,0 (bricks and tiles line up with the corner).</summary>
    public bool OriginAtCorner { get; set; }

    /// <summary>Patterns used in this drawing session, the last one first.</summary>
    public List<string> Recent { get; } = new();
}

/// <summary>
/// HATCHPICK: click inside closed areas; each click finds the area around it and hatches it with the settings
/// of the Hatch window. Enter ends. Object snap is off for these clicks: the point is wanted inside, not on a line.
/// </summary>
public sealed class HatchPickCommand : CadCommandBase, IObjectPicker
{
    private List<Point2D> _openEnds = new();
    private int _made;
    private Point2D? _hoverAt;
    private HatchEntity? _hover;

    public override string Name => "HATCHPICK";
    public bool PickingObject => true;

    public override string Prompt
    {
        get
        {
            var h = Session.HatchDefaults;
            string what = string.Equals(h.Pattern, HatchPatterns.Solid, StringComparison.OrdinalIgnoreCase)
                ? "solid fill"
                : $"{h.Pattern}, scale {(h.ScaleChosen ? h.Scale.ToString("0.###", CultureInfo.InvariantCulture) : "to suit")}, angle {h.Angle.ToString("0.##", CultureInfo.InvariantCulture)}";
            return $"HATCH  Click inside a closed area ({what}){(_made > 0 ? $", {_made} made" : "")}, Enter to finish:";
        }
    }

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.PointInput p) return Invalid("Click inside a closed area, or press Enter to finish.");
        _hover = null;
        _hoverAt = null;
        var hatch = Session.HatchAt(p.Point, out _openEnds);
        if (hatch is not null) _made++;
        return CommandResult.Continue;
    }

    /// <summary>
    /// While the cursor rests inside an area, the hatch it would get is shown. After a click that found no closed
    /// area, the loose ends near it are circled so the gap can be seen.
    /// </summary>
    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        var preview = new List<Entity>();
        double mark = Session.PickTolerance * 1.5;
        foreach (var end in _openEnds)
            if (mark > 0) preview.Add(new CircleEntity(end, mark));

        if (Session.HatchPreviewAllowed)
        {
            if (_hover is null || !_hover.Contains(cursor))
            {
                if (_hoverAt is null || _hoverAt.Value.DistanceTo(cursor) > Session.PickTolerance * 2)
                {
                    _hoverAt = cursor;
                    _hover = Session.PreviewHatchAt(cursor);
                }
            }
            if (_hover is not null) preview.Add(_hover);
        }
        return preview;
    }
}

/// <summary>HATCHSELECT: hatches the area enclosed by the selected objects themselves (closed polylines, circles, lines that meet).</summary>
public sealed class HatchSelectCommand : SelectionCommandBase
{
    public override string Name => "HATCHSELECT";
    public override string Prompt => $"HATCH  Select the objects that close around the area ({Session.Selection.Count} selected), Enter to hatch:";

    protected override CommandResult OnSelectionReady()
    {
        Session.HatchSelection(Session.GetSelectedEntities().Select(e => e.Id).ToList());
        return CommandResult.Finished;
    }

    protected override CommandResult OnInputAfterSelection(CommandInput input) => CommandResult.Finished;
}
