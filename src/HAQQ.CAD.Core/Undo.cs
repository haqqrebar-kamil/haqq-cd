namespace HAQQ.CAD.Core;

/// <summary>A reversible change to a document. Every edit goes through one of these.</summary>
public interface IUndoableAction
{
    string Name { get; }
    void Do(DrawingDocument document);
    void Undo(DrawingDocument document);
}

public sealed class AddEntitiesAction : IUndoableAction
{
    private readonly List<Entity> _entities;
    private long[]? _orders;

    public AddEntitiesAction(string name, IEnumerable<Entity> entities)
    {
        Name = name;
        _entities = entities.Select(e => e.Clone()).ToList();
    }

    public string Name { get; }
    public IReadOnlyList<Entity> Entities => _entities;

    public void Do(DrawingDocument document)
    {
        for (int i = 0; i < _entities.Count; i++)
            document.AddEntity(_entities[i].Clone(), _orders?[i]);
        _orders ??= _entities.Select(e => document.GetDrawOrder(e.Id)).ToArray();
    }

    public void Undo(DrawingDocument document)
    {
        for (int i = _entities.Count - 1; i >= 0; i--) document.RemoveEntity(_entities[i].Id);
    }
}

public sealed class RemoveEntitiesAction : IUndoableAction
{
    private readonly List<Guid> _ids;
    private List<(Entity Entity, long Order)> _removed = new();

    public RemoveEntitiesAction(string name, IEnumerable<Guid> ids)
    {
        Name = name;
        _ids = ids.ToList();
    }

    public string Name { get; }

    public void Do(DrawingDocument document)
    {
        _removed = new List<(Entity, long)>(_ids.Count);
        foreach (var id in _ids)
        {
            var entity = document.GetEntity(id);
            if (entity is null) continue;
            long order = document.RemoveEntity(id);
            _removed.Add((entity.Clone(), order));
        }
    }

    public void Undo(DrawingDocument document)
    {
        foreach (var (entity, order) in _removed) document.AddEntity(entity.Clone(), order);
    }
}

/// <summary>Replaces entities with modified copies (same ids).</summary>
public sealed class ModifyEntitiesAction : IUndoableAction
{
    private readonly List<Entity> _before;
    private readonly List<Entity> _after;

    public ModifyEntitiesAction(string name, IEnumerable<Entity> before, IEnumerable<Entity> after)
    {
        Name = name;
        _before = before.Select(e => e.Clone()).ToList();
        _after = after.Select(e => e.Clone()).ToList();
        if (_before.Count != _after.Count) throw new ArgumentException("Before/after counts differ.");
    }

    public string Name { get; }

    public void Do(DrawingDocument document)
    {
        foreach (var e in _after) document.ReplaceEntity(e.Clone());
    }

    public void Undo(DrawingDocument document)
    {
        foreach (var e in _before) document.ReplaceEntity(e.Clone());
    }
}

public sealed class AddLayerAction : IUndoableAction
{
    private readonly Layer _layer;

    public AddLayerAction(Layer layer) => _layer = layer.Clone();

    public string Name => "New layer";
    public void Do(DrawingDocument document) => document.AddLayer(_layer.Clone());
    public void Undo(DrawingDocument document) => document.RemoveLayer(_layer.Id);
}

public sealed class RemoveLayerAction : IUndoableAction
{
    private readonly Guid _layerId;
    private Layer? _removed;
    private int _index;

    public RemoveLayerAction(Guid layerId) => _layerId = layerId;

    public string Name => "Delete layer";

    public void Do(DrawingDocument document)
    {
        _removed = document.GetLayer(_layerId)?.Clone();
        _index = document.RemoveLayer(_layerId);
    }

    public void Undo(DrawingDocument document)
    {
        if (_removed is not null) document.AddLayer(_removed.Clone(), _index);
    }
}

public sealed class ModifyLayerAction : IUndoableAction
{
    private readonly Layer _before;
    private readonly Layer _after;

    public ModifyLayerAction(Layer before, Layer after)
    {
        _before = before.Clone();
        _after = after.Clone();
    }

    public string Name => "Layer properties";
    public void Do(DrawingDocument document) => document.ReplaceLayer(_after);
    public void Undo(DrawingDocument document) => document.ReplaceLayer(_before);
}

public sealed class SetCurrentLayerAction : IUndoableAction
{
    private readonly Guid _before;
    private readonly Guid _after;

    public SetCurrentLayerAction(Guid before, Guid after)
    {
        _before = before;
        _after = after;
    }

    public string Name => "Set current layer";
    public void Do(DrawingDocument document) => document.CurrentLayerId = _after;
    public void Undo(DrawingDocument document) => document.CurrentLayerId = _before;
}

/// <summary>Linear undo/redo history for one document.</summary>
public sealed class UndoStack
{
    private readonly List<IUndoableAction> _undo = new();
    private readonly List<IUndoableAction> _redo = new();

    public int Capacity { get; set; } = 500;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? NextUndoName => CanUndo ? _undo[^1].Name : null;
    public string? NextRedoName => CanRedo ? _redo[^1].Name : null;

    public event EventHandler? Changed;

    /// <summary>Performs the action and records it.</summary>
    public void Execute(DrawingDocument document, IUndoableAction action)
    {
        using (document.BeginBatch()) action.Do(document);
        Record(action);
    }

    /// <summary>Records an action whose effect has already been applied to the document.</summary>
    public void Record(IUndoableAction action)
    {
        _undo.Add(action);
        if (_undo.Count > Capacity) _undo.RemoveAt(0);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IUndoableAction? Undo(DrawingDocument document)
    {
        if (!CanUndo) return null;
        var action = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        using (document.BeginBatch()) action.Undo(document);
        _redo.Add(action);
        Changed?.Invoke(this, EventArgs.Empty);
        return action;
    }

    public IUndoableAction? Redo(DrawingDocument document)
    {
        if (!CanRedo) return null;
        var action = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        using (document.BeginBatch()) action.Do(document);
        _undo.Add(action);
        Changed?.Invoke(this, EventArgs.Empty);
        return action;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Replaces the drawing's unit and display settings.</summary>
public sealed class ModifySettingsAction : IUndoableAction
{
    private readonly DrawingSettings _before;
    private readonly DrawingSettings _after;

    public ModifySettingsAction(string name, DrawingSettings before, DrawingSettings after)
    {
        Name = name;
        _before = before.Clone();
        _after = after.Clone();
    }

    public string Name { get; }

    public void Do(DrawingDocument document)
    {
        document.Settings = _after.Clone();
        document.NotifySettingsChanged();
    }

    public void Undo(DrawingDocument document)
    {
        document.Settings = _before.Clone();
        document.NotifySettingsChanged();
    }
}

/// <summary>Several actions carried out, and undone, as one step (for example TRIM: change one object and add another).</summary>
public sealed class CompositeAction : IUndoableAction
{
    private readonly IReadOnlyList<IUndoableAction> _actions;

    public CompositeAction(string name, params IUndoableAction[] actions)
    {
        Name = name;
        _actions = actions;
    }

    public string Name { get; }

    public void Do(DrawingDocument document)
    {
        int done = 0;
        try
        {
            for (; done < _actions.Count; done++) _actions[done].Do(document);
        }
        catch
        {
            // Leave the document as it was if a later part is rejected.
            for (int i = done - 1; i >= 0; i--) _actions[i].Undo(document);
            throw;
        }
    }

    public void Undo(DrawingDocument document)
    {
        for (int i = _actions.Count - 1; i >= 0; i--) _actions[i].Undo(document);
    }
}

public sealed class AddLinetypeAction : IUndoableAction
{
    private readonly LinetypeDefinition _linetype;

    public AddLinetypeAction(LinetypeDefinition linetype) => _linetype = linetype;

    public string Name => "New linetype";
    public void Do(DrawingDocument document) => document.AddLinetype(_linetype);
    public void Undo(DrawingDocument document) => document.RemoveLinetype(_linetype.Name);
}

public sealed class RemoveLinetypeAction : IUndoableAction
{
    private readonly string _name;
    private LinetypeDefinition? _removed;
    private int _index;

    public RemoveLinetypeAction(string name) => _name = name;

    public string Name => "Delete linetype";

    public void Do(DrawingDocument document)
    {
        _removed = document.FindLinetype(_name);
        _index = document.RemoveLinetype(_name);
    }

    public void Undo(DrawingDocument document)
    {
        if (_removed is not null) document.AddLinetype(_removed, _index);
    }
}

public sealed class ReplaceLinetypeAction : IUndoableAction
{
    private readonly LinetypeDefinition _replacement;
    private LinetypeDefinition? _old;

    public ReplaceLinetypeAction(LinetypeDefinition replacement) => _replacement = replacement;

    public string Name => "Edit linetype";
    public void Do(DrawingDocument document) => _old = document.ReplaceLinetype(_replacement);

    public void Undo(DrawingDocument document)
    {
        if (_old is not null) document.ReplaceLinetype(_old);
    }
}

public sealed class AddDimensionStyleAction : IUndoableAction
{
    private readonly DimensionStyle _style;
    public AddDimensionStyleAction(DimensionStyle style) => _style = style;
    public string Name => "New dimension style";
    public void Do(DrawingDocument document) => document.AddDimensionStyle(_style);
    public void Undo(DrawingDocument document) => document.RemoveDimensionStyle(_style.Name);
}

public sealed class RemoveDimensionStyleAction : IUndoableAction
{
    private readonly string _name;
    private DimensionStyle? _removed;
    private int _index;

    public RemoveDimensionStyleAction(string name) => _name = name;
    public string Name => "Delete dimension style";

    public void Do(DrawingDocument document)
    {
        _removed = document.FindDimensionStyle(_name);
        _index = document.RemoveDimensionStyle(_name);
    }

    public void Undo(DrawingDocument document)
    {
        if (_removed is not null) document.AddDimensionStyle(_removed, _index);
    }
}

/// <summary>Changes the values of a dimension style and redraws every dimension that uses it with them.</summary>
public sealed class ReplaceDimensionStyleAction : IUndoableAction
{
    private readonly DimensionStyle _style;
    private DimensionStyle? _old;
    private List<Entity> _before = new();

    public ReplaceDimensionStyleAction(DimensionStyle style) => _style = style;
    public string Name => "Edit dimension style";

    public void Do(DrawingDocument document)
    {
        _old = document.ReplaceDimensionStyle(_style);
        var current = document.FindDimensionStyle(_style.Name)!;
        _before = document.EntitiesUnordered.OfType<DimensionEntity>()
            .Where(d => string.Equals(d.StyleName, current.Name, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Clone())
            .ToList();
        foreach (var entity in _before)
        {
            var updated = (DimensionEntity)entity.Clone();
            updated.Style = current;
            document.ReplaceEntity(updated);
        }
    }

    public void Undo(DrawingDocument document)
    {
        if (_old is null) return;
        document.ReplaceDimensionStyle(_old);
        foreach (var entity in _before) document.ReplaceEntity(entity.Clone());
    }
}

public sealed class SetCurrentDimensionStyleAction : IUndoableAction
{
    private readonly string _before;
    private readonly string _after;

    public SetCurrentDimensionStyleAction(string before, string after)
    {
        _before = before;
        _after = after;
    }

    public string Name => "Set current dimension style";
    public void Do(DrawingDocument document) => document.CurrentDimensionStyleName = _after;
    public void Undo(DrawingDocument document) => document.CurrentDimensionStyleName = _before;
}

