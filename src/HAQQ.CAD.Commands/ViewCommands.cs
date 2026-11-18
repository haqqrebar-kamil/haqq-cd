using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

public enum ViewChangeKind
{
    /// <summary>Show the whole drawing.</summary>
    Extents,
    /// <summary>Show <see cref="ViewChange.Box"/> as large as it fits.</summary>
    Window,
    /// <summary>Put <see cref="ViewChange.Center"/> in the middle of the view and magnify by <see cref="ViewChange.Factor"/>.</summary>
    Center,
    /// <summary>Magnify about the middle of the view by <see cref="ViewChange.Factor"/> (2 = twice as close, 0.5 = twice as far).</summary>
    Factor,
}

/// <summary>A change of view asked for by a command. The shell that owns the view carries it out.</summary>
public sealed record ViewChange(ViewChangeKind Kind, BoundingBox Box = default, Point2D Center = default, double Factor = 1.0);

/// <summary>
/// ZOOM: All (the whole drawing), Window (two corners), Center (a point and a magnification), In and Out.
/// Clicking a point at the first prompt starts a window; a typed number is a magnification.
/// </summary>
public sealed class ZoomCommand : CadCommandBase
{
    /// <summary>How much closer one step of In is (Out is the same step back).</summary>
    public const double Step = 2.0;

    private enum Stage { Option, FirstCorner, SecondCorner, CenterPoint, Magnification }

    private readonly string? _preset;
    private Stage _stage = Stage.Option;
    private Point2D _point;

    /// <param name="preset">An option to take at once ("W", "I", "O", "C"), or null to ask.</param>
    public ZoomCommand(string? preset = null) => _preset = preset;

    public override string Name => "ZOOM";

    public override string Prompt => _stage switch
    {
        Stage.Option => "ZOOM  Click the first corner of a window, or [All/Window/Center/In/Out] <All>:",
        Stage.FirstCorner => "ZOOM  Click the first corner of the window:",
        Stage.SecondCorner => "ZOOM  Click the opposite corner:",
        Stage.CenterPoint => "ZOOM  Click the point to put in the middle of the view:",
        _ => "ZOOM  Enter magnification (2 = twice as close, 0.5 = twice as far) <1>:",
    };

    public override IReadOnlyList<string> Options =>
        _stage == Stage.Option ? new[] { "All", "Window", "Center", "In", "Out" } : Array.Empty<string>();

    protected override CommandResult OnStart() =>
        _preset is null ? CommandResult.Continue : OnInput(new CommandInput.KeywordInput(_preset));

    public override CommandResult OnInput(CommandInput input)
    {
        switch (_stage)
        {
            case Stage.Option:
                switch (input)
                {
                    case CommandInput.EnterInput:
                        return Done(new ViewChange(ViewChangeKind.Extents));
                    case CommandInput.KeywordInput k:
                        if (Matches(k.Keyword, "ALL") || Matches(k.Keyword, "EXTENTS")) return Done(new ViewChange(ViewChangeKind.Extents));
                        if (Matches(k.Keyword, "WINDOW")) { _stage = Stage.FirstCorner; return CommandResult.Continue; }
                        if (Matches(k.Keyword, "CENTER")) { _stage = Stage.CenterPoint; return CommandResult.Continue; }
                        if (Matches(k.Keyword, "IN")) return Done(new ViewChange(ViewChangeKind.Factor, Factor: Step));
                        if (Matches(k.Keyword, "OUT")) return Done(new ViewChange(ViewChangeKind.Factor, Factor: 1.0 / Step));
                        return Invalid("Type A, W, C, I or O.");
                    case CommandInput.NumberInput n when n.Value > 0:
                        return Done(new ViewChange(ViewChangeKind.Factor, Factor: n.Value));
                    case CommandInput.PointInput p:
                        _point = p.Point;
                        _stage = Stage.SecondCorner;
                        return CommandResult.Continue;
                }
                return Invalid("Click a corner, or type A, W, C, I or O.");

            case Stage.FirstCorner:
                if (input is CommandInput.EnterInput) return CommandResult.Finished;
                if (input is not CommandInput.PointInput first) return Invalid("Click a corner of the window.");
                _point = first.Point;
                _stage = Stage.SecondCorner;
                return CommandResult.Continue;

            case Stage.SecondCorner:
                if (input is CommandInput.EnterInput) return CommandResult.Finished;
                if (input is not CommandInput.PointInput second) return Invalid("Click the opposite corner.");
                var box = BoundingBox.FromCorners(_point, second.Point);
                if (!(box.Width > 0) && !(box.Height > 0)) return Invalid("Click a different point for the opposite corner.");
                return Done(new ViewChange(ViewChangeKind.Window, Box: box));

            case Stage.CenterPoint:
                if (input is CommandInput.EnterInput) return CommandResult.Finished;
                if (input is not CommandInput.PointInput center) return Invalid("Click a point.");
                _point = center.Point;
                _stage = Stage.Magnification;
                return CommandResult.Continue;

            default:
                if (input is CommandInput.EnterInput) return Done(new ViewChange(ViewChangeKind.Center, Center: _point));
                if (input is CommandInput.NumberInput { Value: > 0 } m) return Done(new ViewChange(ViewChangeKind.Center, Center: _point, Factor: m.Value));
                return Invalid("Type a number more than 0, or press Enter to keep the size.");
        }
    }

    private CommandResult Done(ViewChange change)
    {
        Session.RequestView(change);
        return CommandResult.Finished;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        if (_stage != Stage.SecondCorner || cursor.X == _point.X || cursor.Y == _point.Y) return Array.Empty<Entity>();
        var a = _point;
        var c = cursor;
        var b = new Point2D(c.X, a.Y);
        var d = new Point2D(a.X, c.Y);
        return new Entity[] { new LineEntity(a, b), new LineEntity(b, c), new LineEntity(c, d), new LineEntity(d, a) };
    }
}

/// <summary>
/// PAN: while it runs, holding the left button and dragging moves the view (the shell does the moving).
/// Enter, Space or Esc ends it. The middle button pans at any time, with or without this command.
/// </summary>
public sealed class PanCommand : CadCommandBase
{
    public override string Name => "PAN";
    public override string Prompt => "PAN  Hold the left button and drag to move the view. Enter or Esc to finish:";

    public override CommandResult OnInput(CommandInput input) =>
        input is CommandInput.EnterInput ? CommandResult.Finished : CommandResult.Continue;
}
