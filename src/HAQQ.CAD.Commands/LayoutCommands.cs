using System.Globalization;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>What a new layout starts with on its sheet.</summary>
public enum LayoutTemplate
{
    /// <summary>Nothing but the paper.</summary>
    Blank,
    /// <summary>A border line half an inch in from the edge.</summary>
    Border,
    /// <summary>The border and a title block in the lower-right corner.</summary>
    TitleBlock,
}

/// <summary>A standard sheet: its name and its size in inches (landscape).</summary>
public sealed record PaperSize(string Key, string Name, double WidthInches, double HeightInches);

/// <summary>Standard sheets and viewport scales, and how scales are written and read.</summary>
public static class LayoutStandards
{
    /// <summary>The standard sheets, landscape (wide side first): architectural, ANSI and ISO.</summary>
    public static IReadOnlyList<PaperSize> Papers { get; } = new[]
    {
        new PaperSize("ARCHA", "ARCH A - 12 x 9 in", 12, 9),
        new PaperSize("ARCHB", "ARCH B - 18 x 12 in", 18, 12),
        new PaperSize("ARCHC", "ARCH C - 24 x 18 in", 24, 18),
        new PaperSize("ARCHD", "ARCH D - 36 x 24 in", 36, 24),
        new PaperSize("ARCHE1", "ARCH E1 - 42 x 30 in", 42, 30),
        new PaperSize("ARCHE", "ARCH E - 48 x 36 in", 48, 36),
        new PaperSize("ANSIA", "ANSI A (Letter) - 11 x 8.5 in", 11, 8.5),
        new PaperSize("ANSIB", "ANSI B (Tabloid) - 17 x 11 in", 17, 11),
        new PaperSize("ANSIC", "ANSI C - 22 x 17 in", 22, 17),
        new PaperSize("ANSID", "ANSI D - 34 x 22 in", 34, 22),
        new PaperSize("ANSIE", "ANSI E - 44 x 34 in", 44, 34),
        new PaperSize("A4", "ISO A4 - 297 x 210 mm", 297 / 25.4, 210 / 25.4),
        new PaperSize("A3", "ISO A3 - 420 x 297 mm", 420 / 25.4, 297 / 25.4),
        new PaperSize("A2", "ISO A2 - 594 x 420 mm", 594 / 25.4, 420 / 25.4),
        new PaperSize("A1", "ISO A1 - 841 x 594 mm", 841 / 25.4, 594 / 25.4),
        new PaperSize("A0", "ISO A0 - 1189 x 841 mm", 1189 / 25.4, 841 / 25.4),
    };

    /// <summary>Finds a sheet by its key; LETTER and TABLOID are the everyday names of ANSI A and B.</summary>
    public static PaperSize? FindPaper(string? key)
    {
        string k = (key ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (k == "LETTER") k = "ANSIA";
        if (k == "TABLOID") k = "ANSIB";
        return Papers.FirstOrDefault(p => p.Key == k);
    }

    /// <summary>The sheet a new layout starts with: Arch D for feet and inches, A1 for metric drawings.</summary>
    public static PaperSize DefaultPaper(DrawingSettings settings) =>
        Papers.First(p => p.Key == (UnitConverter.IsImperial(settings.Unit) ? "ARCHD" : "A1"));

    /// <summary>A sheet's size in drawing units.</summary>
    public static (double Width, double Height) SizeOf(PaperSize paper, DrawingSettings settings) =>
        (Math.Round(UnitConverter.Convert(paper.WidthInches, LinearUnit.Inch, settings.Unit), 6),
         Math.Round(UnitConverter.Convert(paper.HeightInches, LinearUnit.Inch, settings.Unit), 6));

    /// <summary>The usual scales, as the ratio of paper size to real size: 1/4" = 1'-0" is 1/48.</summary>
    public static IReadOnlyList<double> Scales(DrawingSettings settings) => UnitConverter.IsImperial(settings.Unit)
        ? new[] { 1.0, 1 / 4.0, 1 / 8.0, 1 / 12.0, 1 / 16.0, 1 / 24.0, 1 / 32.0, 1 / 48.0, 1 / 64.0, 1 / 96.0, 1 / 128.0, 1 / 192.0, 1 / 384.0 }
        : new[] { 1.0, 1 / 2.0, 1 / 5.0, 1 / 10.0, 1 / 20.0, 1 / 25.0, 1 / 50.0, 1 / 75.0, 1 / 100.0, 1 / 200.0, 1 / 500.0, 1 / 1000.0 };

    /// <summary>The scale a viewport starts with when there is no model yet to fit: 1/4" = 1'-0", or 1:50.</summary>
    public static double DefaultScale(DrawingSettings settings) => UnitConverter.IsImperial(settings.Unit) ? 1 / 48.0 : 1 / 50.0;

    /// <summary>
    /// A scale as people write it: 1/4" = 1'-0" for the usual building scales in feet and inches, otherwise 1:48.
    /// </summary>
    public static string ScaleName(double scale, DrawingSettings settings) => ScaleNames.Name(scale, settings);

    /// <summary>
    /// Reads a scale: 1:48, 1/48, 0.0208, or a paper length = a real length such as 1/4" = 1' or 1/4=1'.
    /// </summary>
    public static bool TryParseScale(string? text, DrawingSettings settings, out double scale)
    {
        scale = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();

        int equals = t.IndexOf('=');
        if (equals > 0)
        {
            if (!UnitFormatter.TryParseLength(t[..equals], settings, out double paper) || !UnitFormatter.TryParseLength(t[(equals + 1)..], settings, out double real)) return false;
            scale = paper / real;
        }
        else if (t.IndexOf(':') is > 0 and var colon)
        {
            if (!double.TryParse(t[..colon], NumberStyles.Float, CultureInfo.InvariantCulture, out double a) ||
                !double.TryParse(t[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double b)) return false;
            scale = a / b;
        }
        else if (t.IndexOf('/') is > 0 and var slash && double.TryParse(t[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out double n) &&
                 double.TryParse(t[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            scale = n / d;
        }
        else if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out scale)) return false;

        return double.IsFinite(scale) && scale > 1e-9 && scale < 1e9;
    }
}

/// <summary>VIEWPORT: a new window onto the model, by two corners on the sheet. It starts by showing the whole model.</summary>
public sealed class ViewportCommand : CadCommandBase
{
    private Point2D? _first;

    public override string Name => "VIEWPORT";
    public override string Prompt => _first is null ? "VIEWPORT  Click the first corner of the new viewport:" : "VIEWPORT  Click the opposite corner:";

    protected override CommandResult OnStart()
    {
        if (Document.ActiveLayout is not null) return CommandResult.Continue;
        Session.Post("Viewports are placed on a layout. Open a layout tab first (LAYOUT tab > New Layout).");
        return CommandResult.Finished;
    }

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.PointInput p) return Invalid("Click a point on the sheet.");
        if (_first is null)
        {
            _first = p.Point;
            return CommandResult.Continue;
        }
        var frame = BoundingBox.FromCorners(_first.Value, p.Point);
        if (!(frame.Width > 0) || !(frame.Height > 0)) return Invalid("Click a different point for the opposite corner.");
        return Session.AddViewport(frame) ? CommandResult.Finished : CommandResult.Continue;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        if (_first is not { } a || cursor.X == a.X || cursor.Y == a.Y) return Array.Empty<Entity>();
        var frame = BoundingBox.FromCorners(a, cursor);
        return new Entity[] { new ViewportEntity(frame.Center, frame.Width, frame.Height) };
    }
}

/// <summary>
/// VPSCALE: the scale of the selected viewports (or of one that is clicked). VPFIT: show the whole model in them.
/// </summary>
public sealed class ViewportScaleCommand : CadCommandBase, IObjectPicker, IRawTextCommand
{
    private readonly bool _fit;
    private List<Guid> _targets = new();
    private double? _pendingScale;          // chosen from the ribbon list before a viewport was picked

    public ViewportScaleCommand(bool fit) => _fit = fit;

    public override string Name => _fit ? "VPFIT" : "VPSCALE";
    public bool PickingObject => _targets.Count == 0;
    public bool WantsRawText => !_fit && _targets.Count > 0;

    public override string Prompt => _targets.Count == 0
        ? $"{Name}  Click the border of a viewport:"
        : "VPSCALE  Enter the scale, for example 1/4\"=1' or 1:50:";

    protected override CommandResult OnStart()
    {
        if (Document.ActiveLayout is null)
        {
            Session.Post("Viewports are on layouts. Open a layout tab first.");
            return CommandResult.Finished;
        }
        _targets = Session.GetSelectedEntities().OfType<ViewportEntity>().Select(v => v.Id).ToList();
        if (_targets.Count == 0 && Session.ActiveViewportId is Guid active) _targets.Add(active);
        if (_targets.Count == 0)
        {
            // Only one viewport on the sheet: that is the one meant.
            var all = Document.EntitiesUnordered.OfType<ViewportEntity>().Take(2).ToList();
            if (all.Count == 1) _targets.Add(all[0].Id);
        }
        return _fit && _targets.Count > 0 ? Apply(null) : CommandResult.Continue;
    }

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (_targets.Count == 0)
        {
            if (!_fit && input is CommandInput.KeywordInput early && LayoutStandards.TryParseScale(early.Keyword, Document.Settings, out double chosen))
            {
                _pendingScale = chosen;             // the scale first, then the viewport it is for
                return CommandResult.Continue;
            }
            if (input is not CommandInput.PointInput p) return Invalid("Click the border of a viewport.");
            if (SelectionService.PickAt(Document, p.Point, Session.PickTolerance) is not ViewportEntity viewport) return Invalid("That is not a viewport. Click on its border.");
            _targets.Add(viewport.Id);
            return _fit ? Apply(null) : _pendingScale is double pending ? Apply(pending) : CommandResult.Continue;
        }
        // A scale chosen from a menu arrives as a keyword or a number.
        string? typed = input switch
        {
            CommandInput.KeywordInput k => k.Keyword,
            CommandInput.NumberInput n => n.Value.ToString("R", CultureInfo.InvariantCulture),
            _ => null,
        };
        return typed is null ? Invalid("Type a scale such as 1/4\"=1' or 1:50.") : OnRawText(typed);
    }

    public CommandResult OnRawText(string text)
    {
        if (text.Trim().Length == 0) return CommandResult.Finished;
        if (!LayoutStandards.TryParseScale(text, Document.Settings, out double scale)) return Invalid($"\"{text}\" is not a scale. Type for example 1/4\"=1' or 1:50.");
        return Apply(scale);
    }

    private CommandResult Apply(double? scale)
    {
        bool ok = scale is double s ? Session.SetViewportScale(_targets, s) : Session.FitViewports(_targets);
        return ok ? CommandResult.Finished : CommandResult.Continue;
    }
}

/// <summary>VPLOCK: locks the selected viewports (or the one clicked, or the only one), or unlocks them when they are locked.</summary>
public sealed class ViewportLockCommand : CadCommandBase, IObjectPicker
{
    public override string Name => "VPLOCK";
    public bool PickingObject => true;
    public override string Prompt => "VPLOCK  Click the border of the viewport to lock or unlock:";

    private CommandResult Toggle(IReadOnlyList<ViewportEntity> viewports)
    {
        // Any one unlocked: lock them all. All locked: unlock.
        bool locked = viewports.Any(v => !v.Locked);
        Session.SetViewportLock(viewports.Select(v => v.Id).ToList(), locked);
        return CommandResult.Finished;
    }

    protected override CommandResult OnStart()
    {
        if (Document.ActiveLayout is null)
        {
            Session.Post("Viewports are on layouts. Open a layout tab first.");
            return CommandResult.Finished;
        }
        var targets = Session.GetSelectedEntities().OfType<ViewportEntity>().ToList();
        if (targets.Count == 0 && Session.ActiveViewportId is Guid active && Document.GetEntity(active) is ViewportEntity entered) targets.Add(entered);
        if (targets.Count == 0)
        {
            var all = Document.EntitiesUnordered.OfType<ViewportEntity>().Take(2).ToList();
            if (all.Count == 1) targets.Add(all[0]);
        }
        return targets.Count > 0 ? Toggle(targets) : CommandResult.Continue;
    }

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.PointInput p) return Invalid("Click the border of a viewport.");
        if (SelectionService.PickAt(Document, p.Point, Session.PickTolerance) is not ViewportEntity viewport) return Invalid("That is not a viewport. Click on its border.");
        return Toggle(new[] { viewport });
    }
}

/// <summary>LAYOUTRENAME: a new name for the layout being looked at.</summary>
public sealed class RenameLayoutCommand : CadCommandBase, IRawTextCommand
{
    public override string Name => "LAYOUTRENAME";
    public bool WantsRawText => true;
    public override string Prompt => $"LAYOUTRENAME  Enter a new name for \"{Document.ActiveLayout?.Name}\":";

    protected override CommandResult OnStart()
    {
        if (Document.ActiveLayout is not null) return CommandResult.Continue;
        Session.Post("Open the layout to rename first (the Model tab cannot be renamed).");
        return CommandResult.Finished;
    }

    public override CommandResult OnInput(CommandInput input) => CommandResult.Finished;

    public CommandResult OnRawText(string text)
    {
        if (text.Trim().Length == 0) return CommandResult.Finished;
        return Session.RenameLayout(Document.ActiveSpaceId, text) ? CommandResult.Finished : CommandResult.Continue;
    }
}

/// <summary>LAYOUTDELETE: removes the layout being looked at, with everything drawn on it. The model is not touched.</summary>
public sealed class DeleteLayoutCommand : CadCommandBase
{
    public override string Name => "LAYOUTDELETE";
    public override string Prompt => $"LAYOUTDELETE  Delete layout \"{Document.ActiveLayout?.Name}\" and everything drawn on it? [Yes/No] <No>:";
    public override IReadOnlyList<string> Options => new[] { "Yes", "No" };

    protected override CommandResult OnStart()
    {
        if (Document.ActiveLayout is not null) return CommandResult.Continue;
        Session.Post("Open the layout to delete first (the Model tab cannot be deleted).");
        return CommandResult.Finished;
    }

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.KeywordInput k && Matches(k.Keyword, "YES")) Session.DeleteLayout(Document.ActiveSpaceId);
        return CommandResult.Finished;
    }
}

/// <summary>PAGESETUP: the paper size of the layout being looked at: a standard sheet, or width,height in drawing units.</summary>
public sealed class PageSetupCommand : CadCommandBase
{
    public override string Name => "PAGESETUP";

    public override string Prompt
    {
        get
        {
            var layout = Document.ActiveLayout;
            string size = layout is null ? "" : $"{UnitFormatter.FormatLength(layout.PaperWidth, Document.Settings)} x {UnitFormatter.FormatLength(layout.PaperHeight, Document.Settings)}";
            return $"PAGESETUP  Enter the sheet [{string.Join("/", LayoutStandards.Papers.Select(p => p.Key))}] or width,height <{size}>:";
        }
    }

    public override IReadOnlyList<string> Options => LayoutStandards.Papers.Select(p => p.Key).ToList();

    protected override CommandResult OnStart()
    {
        if (Document.ActiveLayout is not null) return CommandResult.Continue;
        Session.Post("Open a layout first: the model has no paper size.");
        return CommandResult.Finished;
    }

    public override CommandResult OnInput(CommandInput input)
    {
        switch (input)
        {
            case CommandInput.EnterInput:
                return CommandResult.Finished;
            case CommandInput.KeywordInput k:
            {
                var paper = LayoutStandards.FindPaper(k.Keyword);
                if (paper is null) return Invalid("That is not one of the sheets. Type its name, or the size as width,height.");
                var (w, h) = LayoutStandards.SizeOf(paper, Document.Settings);
                return Session.SetPaperSize(Document.ActiveSpaceId, w, h) ? CommandResult.Finished : CommandResult.Continue;
            }
            case CommandInput.PointInput p:
                // "36,24" typed as a size arrives as a point.
                return Session.SetPaperSize(Document.ActiveSpaceId, p.Point.X, p.Point.Y) ? CommandResult.Finished : CommandResult.Continue;
            default:
                return Invalid("Type a sheet name, or the size as width,height.");
        }
    }
}
