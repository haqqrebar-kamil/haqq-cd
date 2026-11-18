using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>Objects copied from another drawing (a PDF page) that go on one layer, with the look that layer gets when it is new.</summary>
public sealed record CopyLayer(string Name, CadColor Color, string Linetype, IReadOnlyList<Entity> Entities);

/// <summary>
/// Places a copied drawing: its outline and a sample of its lines follow the cursor, held by the lower-left
/// corner; a click (or typed coordinates) puts all of it down as one undo step, on its layers, and leaves
/// it selected so it can be moved, calibrated or cleaned up straight away.
/// </summary>
public sealed class PlaceCopyCommand : CadCommandBase
{
    private const int PreviewLimit = 400;

    private readonly IReadOnlyList<CopyLayer> _layers;
    private readonly string _sourceKey;
    private readonly BoundingBox _bounds;
    private readonly List<Entity> _sample;

    public PlaceCopyCommand(IReadOnlyList<CopyLayer> layers, string sourceKey)
    {
        _layers = layers;
        _sourceKey = sourceKey;
        var box = BoundingBox.Empty;
        int total = 0;
        foreach (var layer in layers)
            foreach (var entity in layer.Entities)
            {
                box = box.Union(entity.Bounds);
                total++;
            }
        _bounds = box;
        // Every n-th object, so a big drawing still follows the cursor smoothly.
        int step = Math.Max(1, total / PreviewLimit), i = 0;
        _sample = new List<Entity>();
        foreach (var layer in layers)
            foreach (var entity in layer.Entities)
                if (i++ % step == 0 && _sample.Count < PreviewLimit) _sample.Add(entity);
    }

    public override string Name => "COPYPDF";
    public override string Prompt => "COPYPDF  Click where the lower-left corner of the copied drawing goes (or type coordinates, 0,0 keeps it at the origin):";

    private Vector2D Offset(Point2D at) => new(at.X - _bounds.MinX, at.Y - _bounds.MinY);

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.PointInput p) return Invalid("Click a point, or type coordinates such as 0,0.");
        return Session.PlaceCopy(_layers, Offset(p.Point), _sourceKey) ? CommandResult.Finished : CommandResult.Continue;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        if (_bounds.IsEmpty) return Array.Empty<Entity>();
        var move = Transform2D.Translation(Offset(cursor));
        var preview = new List<Entity>(_sample.Count + 1);
        var frame = new PolylineEntity(new[]
        {
            new Point2D(_bounds.MinX, _bounds.MinY), new Point2D(_bounds.MaxX, _bounds.MinY),
            new Point2D(_bounds.MaxX, _bounds.MaxY), new Point2D(_bounds.MinX, _bounds.MaxY),
        }, true);
        frame.Transform(move);
        preview.Add(frame);
        foreach (var entity in _sample)
        {
            var copy = entity.Clone();
            copy.Transform(move);
            preview.Add(copy);
        }
        return preview;
    }
}

/// <summary>
/// CALIBRATE: gives objects their true size from one known length. Click the two ends of something whose
/// real length is known (a dimensioned wall, a scale bar), type that length, and the selected objects - or
/// the drawing that was copied last - are scaled so it measures exactly that.
/// </summary>
public sealed class CalibrateCommand : CadCommandBase, IRawTextCommand
{
    private List<Guid> _targets = new();
    private Point2D? _first, _second;
    private bool _lastCopy;

    public override string Name => "CALIBRATE";
    public bool WantsRawText => _second is not null;

    public override string Prompt =>
        _first is null ? $"CALIBRATE  {_targets.Count} object(s). Click one end of a length you know:"
        : _second is null ? "CALIBRATE  Click the other end:"
        : $"CALIBRATE  It measures {UnitFormatter.FormatLength(_first.Value.DistanceTo(_second.Value), Document.Settings)} now. Type its real length:";

    protected override CommandResult OnStart()
    {
        _targets = Session.GetSelectedEntities().Where(e => e is not ViewportEntity).Select(e => e.Id).ToList();
        if (_targets.Count == 0)
        {
            _targets = Session.LastCopyIds.Where(id => Document.GetEntity(id) is { } e && e.SpaceId == Document.ActiveSpaceId).ToList();
            _lastCopy = _targets.Count > 0;
        }
        else _lastCopy = Session.LastCopyIds.Count > 0 && _targets.Count == Session.LastCopyIds.Count && _targets.All(Session.LastCopyIds.Contains);
        if (_targets.Count > 0) return CommandResult.Continue;
        Session.Post("Select the objects to give their true size first (a drawing just copied from a PDF is taken by itself).");
        return CommandResult.Finished;
    }

    public override CommandResult OnInput(CommandInput input)
    {
        if (_second is not null) return CommandResult.Finished;                   // Enter with no length: leave it
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.PointInput p) return Invalid("Click a point.");
        if (_first is null)
        {
            _first = p.Point;
            return CommandResult.Continue;
        }
        if (p.Point.DistanceTo(_first.Value) <= GeoMath.Epsilon) return Invalid("Click a different point for the other end.");
        _second = p.Point;
        return CommandResult.Continue;
    }

    public CommandResult OnRawText(string text)
    {
        if (text.Trim().Length == 0) return CommandResult.Finished;
        if (!UnitFormatter.TryParseLength(text, Document.Settings, out double real) || !(real > GeoMath.Epsilon))
            return Invalid($"\"{text}\" is not a length. Type for example 12'-6\", 150 or 3000mm.");
        double measured = _first!.Value.DistanceTo(_second!.Value), factor = real / measured;
        if (!double.IsFinite(factor) || factor < 1e-9 || factor > 1e9) return Invalid("That length cannot be used.");
        var scale = Transform2D.Scaling(factor, _first.Value);
        if (!Session.ModifyEntities(_targets, e => e.Transform(scale), "Calibrate")) return CommandResult.Continue;
        Session.Post($"Scaled {_targets.Count} object(s) by {factor:0.######}: that length is now {UnitFormatter.FormatLength(real, Document.Settings)}.");
        if (_lastCopy) Session.NoteCopyCalibrated(factor);
        return CommandResult.Finished;
    }

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor) =>
        _first is { } a && _second is null && cursor.DistanceTo(a) > GeoMath.Epsilon ? new Entity[] { new LineEntity(a, cursor) } : Array.Empty<Entity>();
}

/// <summary>
/// CLEANUP: tidies the selected lines, arcs and circles (see <see cref="GeometryCleanup"/>): near-horizontal and
/// near-vertical lines made exact, doubles removed, pieces on one line merged, lines meeting end to end
/// joined into polylines. Nothing moves further than the tolerance typed.
/// </summary>
public sealed class CleanupCommand : SelectionCommandBase
{
    public override string Name => "CLEANUP";

    public override string Prompt => Selecting
        ? SelectPrompt
        : $"CLEANUP  Tolerance: how far a point may move, and how close ends must be <{UnitFormatter.FormatLength(Session.CleanupTolerance, Document.Settings)}>:";

    protected override CommandResult OnSelectionReady() => CommandResult.Continue;

    protected override CommandResult OnInputAfterSelection(CommandInput input)
    {
        double tolerance = Session.CleanupTolerance;
        if (input is CommandInput.NumberInput n)
        {
            if (!(n.Value >= 0) || !double.IsFinite(n.Value)) return Invalid("The tolerance cannot be less than 0.");
            tolerance = n.Value;
        }
        else if (input is not CommandInput.EnterInput) return Invalid("Type a length, or press Enter to keep the one shown.");

        var sources = Session.GetSelectedEntities()
            .Where(e => e is LineEntity or ArcEntity or CircleEntity && Document.GetLayer(e.LayerId) is { IsEditable: true })
            .ToList();
        if (sources.Count == 0)
        {
            Session.Post("There are no lines, arcs or circles on unlocked layers in the selection.");
            return CommandResult.Finished;
        }

        var result = new List<Entity>();
        int straightened = 0, duplicates = 0, merged = 0, tiny = 0, joined = 0;
        foreach (var group in sources.GroupBy(e => e.LayerId))
        {
            result.AddRange(GeometryCleanup.Clean(group.ToList(), tolerance, 0.5, join: true, out var report));
            straightened += report.Straightened;
            duplicates += report.DuplicatesRemoved;
            merged += report.Merged;
            tiny += report.TinyRemoved;
            joined += report.Joined;
        }
        var total = new CleanupReport(straightened, duplicates, merged, tiny, joined);
        Session.CleanupTolerance = tolerance;
        if (!total.ChangedAnything)
        {
            Session.Post("Nothing needed tidying at that tolerance.");
            return CommandResult.Finished;
        }

        // Objects that came through unchanged stay as they are; the rest are replaced.
        var sourceIds = sources.Select(e => e.Id).ToHashSet();
        var keptIds = result.Where(e => sourceIds.Contains(e.Id) && ReferenceEquals(Document.GetEntity(e.Id), e)).Select(e => e.Id).ToHashSet();
        var added = result.Where(e => !keptIds.Contains(e.Id)).Select(e => e.CopyWithNewId()).ToList();
        foreach (var entity in added) entity.SpaceId = Document.ActiveSpaceId;
        var removed = sourceIds.Where(id => !keptIds.Contains(id)).ToList();
        if (Session.Execute(new CompositeAction("CLEANUP", new RemoveEntitiesAction("CLEANUP", removed), new AddEntitiesAction("CLEANUP", added))))
        {
            Session.ClearSelection();
            Session.Post($"Clean-up: {total}. {sources.Count} object(s) became {result.Count}.");
        }
        return CommandResult.Finished;
    }
}
