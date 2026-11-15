using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Core;

public enum DocumentChangeKind
{
    Entities,
    Layers,
    Settings,
}

public sealed class DocumentChangedEventArgs : EventArgs
{
    public DocumentChangedEventArgs(DocumentChangeKind kind) => Kind = kind;
    public DocumentChangeKind Kind { get; }
}

/// <summary>Project information stored with the drawing.</summary>
public sealed class DocumentMetadata
{
    public string ProjectName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string Revision { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The in-memory drawing: layers, entities (model space), settings and metadata.
/// Not thread-safe: all access happens on the thread that owns the document (the UI thread).
/// Background work such as autosave operates on serialized snapshots, never on the live document.
/// </summary>
public sealed class DrawingDocument
{
    private readonly List<Layer> _layers = new();
    private readonly Dictionary<Guid, Entity> _entities = new();
    private readonly Dictionary<Guid, long> _order = new();
    private readonly SpatialIndex _index = new();
    private List<Entity>? _orderedCache;
    private long _nextOrder = 1;
    private int _countAtLastRebuild;
    private Guid _currentLayerId;

    public DrawingDocument() : this(Guid.NewGuid(), createDefaultLayer: true) { }

    public DrawingDocument(Guid id, bool createDefaultLayer)
    {
        Id = id;
        if (createDefaultLayer)
        {
            var layer0 = new Layer(Guid.NewGuid(), Layer.DefaultLayerName);
            _layers.Add(layer0);
            _currentLayerId = layer0.Id;
        }
    }

    /// <summary>Persistent identifier of the drawing itself.</summary>
    public Guid Id { get; }
    public DrawingSettings Settings { get; set; } = new();
    public DocumentMetadata Metadata { get; set; } = new();
    public Dictionary<string, string> CustomProperties { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Raw JSON of entities written by a newer HAQQ CAD version that this version does not
    /// understand. They are kept verbatim and written back on save so nothing is silently lost.
    /// </summary>
    public List<string> UnknownEntities { get; } = new();

    /// <summary>True when the document has changes that are not saved to disk.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>Incremented on every change; lets observers detect edits cheaply.</summary>
    public long ChangeStamp { get; private set; }

    public event EventHandler<DocumentChangedEventArgs>? Changed;

    public void MarkSaved() => IsDirty = false;

    /// <summary>Flags the document as having unsaved changes (used after crash recovery).</summary>
    public void MarkDirty() => IsDirty = true;

    private int _batchDepth;
    private readonly HashSet<DocumentChangeKind> _pendingKinds = new();

    /// <summary>
    /// Groups many edits into one change notification. Dispose the returned scope to end the batch;
    /// one <see cref="Changed"/> event per kind of change is then raised. Batches may nest.
    /// </summary>
    public IDisposable BeginBatch()
    {
        _batchDepth++;
        return new BatchScope(this);
    }

    private sealed class BatchScope : IDisposable
    {
        private DrawingDocument? _owner;
        public BatchScope(DrawingDocument owner) => _owner = owner;

        public void Dispose()
        {
            var owner = _owner;
            if (owner is null) return;
            _owner = null;
            if (--owner._batchDepth > 0) return;
            var kinds = owner._pendingKinds.ToArray();
            owner._pendingKinds.Clear();
            foreach (var kind in kinds) owner.Changed?.Invoke(owner, new DocumentChangedEventArgs(kind));
        }
    }

    private void OnChanged(DocumentChangeKind kind)
    {
        IsDirty = true;
        ChangeStamp++;
        Metadata.ModifiedUtc = DateTime.UtcNow;
        if (_batchDepth > 0)
        {
            _pendingKinds.Add(kind);
            return;
        }
        Changed?.Invoke(this, new DocumentChangedEventArgs(kind));
    }

    public void NotifySettingsChanged() => OnChanged(DocumentChangeKind.Settings);

    // ------------------------------------------------------------------ layers

    public IReadOnlyList<Layer> Layers => _layers;

    public Guid CurrentLayerId
    {
        get => _currentLayerId;
        set
        {
            if (GetLayer(value) is null) throw new InvalidOperationException("Layer does not exist.");
            if (_currentLayerId == value) return;
            _currentLayerId = value;
            OnChanged(DocumentChangeKind.Layers);
        }
    }

    public Layer CurrentLayer => GetLayer(_currentLayerId) ?? _layers[0];

    public Layer? GetLayer(Guid id)
    {
        foreach (var layer in _layers) if (layer.Id == id) return layer;
        return null;
    }

    public Layer? FindLayer(string name)
    {
        foreach (var layer in _layers)
            if (string.Equals(layer.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) return layer;
        return null;
    }

    public void AddLayer(Layer layer, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (GetLayer(layer.Id) is not null) throw new InvalidOperationException("A layer with this id already exists.");
        if (FindLayer(layer.Name) is not null) throw new InvalidOperationException($"Layer \"{layer.Name}\" already exists.");
        if (index is int i && i >= 0 && i <= _layers.Count) _layers.Insert(i, layer); else _layers.Add(layer);
        if (_layers.Count == 1) _currentLayerId = layer.Id;
        OnChanged(DocumentChangeKind.Layers);
    }

    /// <summary>Why a layer cannot be removed, or null when removal is allowed.</summary>
    public string? GetLayerRemovalBlocker(Guid id)
    {
        var layer = GetLayer(id);
        if (layer is null) return "Layer does not exist.";
        if (layer.Name == Layer.DefaultLayerName) return "Layer 0 cannot be deleted.";
        if (id == _currentLayerId) return "The current layer cannot be deleted.";
        foreach (var e in _entities.Values) if (e.LayerId == id) return $"Layer \"{layer.Name}\" still contains objects.";
        return null;
    }

    /// <summary>Removes a layer and returns the index it had.</summary>
    public int RemoveLayer(Guid id)
    {
        string? blocker = GetLayerRemovalBlocker(id);
        if (blocker is not null) throw new InvalidOperationException(blocker);
        int index = _layers.FindIndex(l => l.Id == id);
        _layers.RemoveAt(index);
        OnChanged(DocumentChangeKind.Layers);
        return index;
    }

    /// <summary>Replaces the stored state of the layer with the same id.</summary>
    public void ReplaceLayer(Layer snapshot)
    {
        int index = _layers.FindIndex(l => l.Id == snapshot.Id);
        if (index < 0) throw new InvalidOperationException("Layer does not exist.");
        var clash = FindLayer(snapshot.Name);
        if (clash is not null && clash.Id != snapshot.Id)
            throw new InvalidOperationException($"Layer \"{snapshot.Name}\" already exists.");
        if (_layers[index].Name == Layer.DefaultLayerName && snapshot.Name != Layer.DefaultLayerName)
            throw new InvalidOperationException("Layer 0 cannot be renamed.");
        _layers[index] = snapshot.Clone();
        OnChanged(DocumentChangeKind.Layers);
    }

    // ---------------------------------------------------------------- entities

    public int EntityCount => _entities.Count;

    /// <summary>All entities in draw order (oldest first).</summary>
    public IReadOnlyList<Entity> Entities
    {
        get
        {
            if (_orderedCache is null)
            {
                _orderedCache = _entities.Values.ToList();
                _orderedCache.Sort((a, b) => _order[a.Id].CompareTo(_order[b.Id]));
            }
            return _orderedCache;
        }
    }

    /// <summary>All entities in no particular order (cheaper than <see cref="Entities"/>).</summary>
    public IReadOnlyCollection<Entity> EntitiesUnordered => _entities.Values;

    public Entity? GetEntity(Guid id) => _entities.GetValueOrDefault(id);
    public bool ContainsEntity(Guid id) => _entities.ContainsKey(id);
    public long GetDrawOrder(Guid id) => _order[id];

    /// <summary>Adds an entity. <paramref name="drawOrder"/> restores a previous position (used by undo).</summary>
    public void AddEntity(Entity entity, long? drawOrder = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (_entities.ContainsKey(entity.Id)) throw new InvalidOperationException("An entity with this id already exists.");
        if (GetLayer(entity.LayerId) is null) throw new InvalidOperationException("The entity's layer does not exist.");
        if (!entity.IsValid) throw new InvalidOperationException($"Invalid {entity.TypeName} geometry.");

        long order = drawOrder ?? _nextOrder;
        if (order >= _nextOrder) _nextOrder = order + 1;
        _entities.Add(entity.Id, entity);
        _order[entity.Id] = order;
        _index.Insert(entity.Id, entity.Bounds);
        _orderedCache = null;
        MaybeRebuildIndex();
        OnChanged(DocumentChangeKind.Entities);
    }

    /// <summary>Removes an entity and returns its draw order so an undo can restore it.</summary>
    public long RemoveEntity(Guid id)
    {
        if (!_entities.Remove(id)) throw new InvalidOperationException("Entity does not exist.");
        long order = _order[id];
        _order.Remove(id);
        _index.Remove(id);
        _orderedCache = null;
        OnChanged(DocumentChangeKind.Entities);
        return order;
    }

    /// <summary>Swaps in a modified copy of an existing entity (same id), keeping its draw order.</summary>
    public void ReplaceEntity(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!_entities.ContainsKey(entity.Id)) throw new InvalidOperationException("Entity does not exist.");
        if (GetLayer(entity.LayerId) is null) throw new InvalidOperationException("The entity's layer does not exist.");
        if (!entity.IsValid) throw new InvalidOperationException($"Invalid {entity.TypeName} geometry.");
        _entities[entity.Id] = entity;
        _index.Insert(entity.Id, entity.Bounds);
        _orderedCache = null;
        OnChanged(DocumentChangeKind.Entities);
    }

    /// <summary>Entities whose bounding box intersects <paramref name="box"/> (unordered).</summary>
    public List<Entity> QueryEntities(BoundingBox box)
    {
        var ids = _index.Query(box);
        var result = new List<Entity>(ids.Count);
        foreach (var id in ids) result.Add(_entities[id]);
        return result;
    }

    /// <summary>Bounding box of every entity on a displayed layer; empty for an empty drawing.</summary>
    public BoundingBox GetExtents()
    {
        var box = BoundingBox.Empty;
        foreach (var e in _entities.Values)
        {
            var layer = GetLayer(e.LayerId);
            if (layer is { IsDisplayed: true }) box = box.Union(e.Bounds);
        }
        return box;
    }

    private void MaybeRebuildIndex()
    {
        // Re-tune the grid cell size as the drawing grows.
        if (_entities.Count < 64 || _entities.Count < _countAtLastRebuild * 2) return;
        _countAtLastRebuild = _entities.Count;
        _index.Rebuild(_entities.Select(p => new KeyValuePair<Guid, BoundingBox>(p.Key, p.Value.Bounds)));
    }
}
