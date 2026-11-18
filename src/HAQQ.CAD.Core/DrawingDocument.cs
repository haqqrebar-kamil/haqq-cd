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
    private readonly List<LinetypeDefinition> _linetypes = new(Linetypes.BuiltIn);
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

    // ------------------------------------------------------------ linetypes

    /// <summary>The built-in linetypes followed by the ones defined in this drawing.</summary>
    public IReadOnlyList<LinetypeDefinition> LinetypeTable => _linetypes;

    public LinetypeDefinition? FindLinetype(string? name)
    {
        foreach (var linetype in _linetypes)
            if (string.Equals(linetype.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) return linetype;
        return null;
    }

    /// <summary>
    /// A linetype name read from a file, brought up to date: a name this drawing defines is kept; otherwise a
    /// built-in name from an earlier build becomes the current one.
    /// </summary>
    public string CurrentLinetypeName(string name) =>
        Linetypes.IsReserved(name) || FindLinetype(name) is not null ? name : Linetypes.FromEarlierName(name);

    /// <summary>Adds a linetype defined by the user. The name must be new and not ByLayer / ByBlock.</summary>
    public void AddLinetype(LinetypeDefinition linetype, int? index = null)
    {
        if (Linetypes.IsReserved(linetype.Name)) throw new InvalidOperationException($"\"{linetype.Name}\" cannot be used as a linetype name.");
        if (FindLinetype(linetype.Name) is not null) throw new InvalidOperationException($"Linetype \"{linetype.Name}\" already exists.");
        if (index is int i && i >= Linetypes.BuiltIn.Count && i <= _linetypes.Count) _linetypes.Insert(i, linetype); else _linetypes.Add(linetype);
        OnChanged(DocumentChangeKind.Layers);
    }

    /// <summary>
    /// Replaces the dashes and description of a user-defined linetype, keeping its name, so that everything
    /// drawn with it changes. Returns the definition that was replaced.
    /// </summary>
    public LinetypeDefinition ReplaceLinetype(LinetypeDefinition replacement)
    {
        int index = _linetypes.FindIndex(l => string.Equals(l.Name, replacement.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException($"There is no linetype named \"{replacement.Name}\".");
        if (Linetypes.IsBuiltIn(replacement.Name)) throw new InvalidOperationException($"\"{replacement.Name}\" is a built-in linetype and cannot be changed.");
        var old = _linetypes[index];
        _linetypes[index] = replacement;
        OnChanged(DocumentChangeKind.Layers);
        return old;
    }

    /// <summary>Why a linetype cannot be removed, or null when removal is allowed.</summary>
    public string? GetLinetypeRemovalBlocker(string? name)
    {
        var linetype = FindLinetype(name);
        if (linetype is null) return $"There is no linetype named \"{name}\".";
        if (Linetypes.IsBuiltIn(linetype.Name)) return $"\"{linetype.Name}\" is a built-in linetype and cannot be deleted.";
        foreach (var layer in _layers)
            if (string.Equals(layer.Linetype, linetype.Name, StringComparison.OrdinalIgnoreCase)) return $"Linetype \"{linetype.Name}\" is used by layer \"{layer.Name}\".";
        foreach (var e in _entities.Values)
            if (string.Equals(e.Linetype, linetype.Name, StringComparison.OrdinalIgnoreCase)) return $"Linetype \"{linetype.Name}\" is used by objects in the drawing.";
        return null;
    }

    /// <summary>Removes a user-defined linetype and returns its position in the table.</summary>
    public int RemoveLinetype(string name)
    {
        string? blocker = GetLinetypeRemovalBlocker(name);
        if (blocker is not null) throw new InvalidOperationException(blocker);
        int index = _linetypes.FindIndex(l => string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        _linetypes.RemoveAt(index);
        OnChanged(DocumentChangeKind.Layers);
        return index;
    }

    /// <summary>
    /// The pattern an object is drawn with: its own linetype, or its layer's when set to ByLayer. ByBlock and
    /// names that are not in the table give a continuous line.
    /// </summary>
    public LinetypeDefinition ResolveLinetype(Entity entity)
    {
        string name = entity.Linetype;
        if (string.Equals(name, Linetypes.ByLayer, StringComparison.OrdinalIgnoreCase))
            name = GetLayer(entity.LayerId)?.Linetype ?? Linetypes.Solid;
        return FindLinetype(name) ?? _linetypes[0];
    }

    // ----------------------------------------------------- dimension styles

    private readonly List<DimensionStyle> _dimensionStyles = new() { new DimensionStyle() };
    private string _currentDimensionStyle = DimensionStyle.StandardName;

    /// <summary>The dimension styles of the drawing. "Standard" is always there.</summary>
    public IReadOnlyList<DimensionStyle> DimensionStyles => _dimensionStyles;

    public DimensionStyle? FindDimensionStyle(string? name) =>
        _dimensionStyles.FirstOrDefault(s => string.Equals(s.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The style new dimensions are made with.</summary>
    public DimensionStyle CurrentDimensionStyle => FindDimensionStyle(_currentDimensionStyle) ?? _dimensionStyles[0];

    public string CurrentDimensionStyleName
    {
        get => CurrentDimensionStyle.Name;
        set
        {
            var style = FindDimensionStyle(value) ?? throw new InvalidOperationException($"There is no dimension style named \"{value}\".");
            if (_currentDimensionStyle == style.Name) return;
            _currentDimensionStyle = style.Name;
            OnChanged(DocumentChangeKind.Settings);
        }
    }

    // ---------------------------------------------------------------- blocks

    private readonly List<BlockDefinition> _blocks = new();

    /// <summary>The blocks kept in the drawing, in the order they were made.</summary>
    public IReadOnlyList<BlockDefinition> Blocks => _blocks;

    public BlockDefinition? FindBlock(string? name) =>
        name is null ? null : _blocks.FirstOrDefault(b => string.Equals(b.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>How many times a block is placed, on the model and on every layout.</summary>
    public int CountBlockReferences(string name) =>
        _entities.Values.OfType<BlockReferenceEntity>().Count(r => string.Equals(r.BlockName, name, StringComparison.OrdinalIgnoreCase));

    public void AddBlock(BlockDefinition block, int? index = null)
    {
        if (!block.IsValid) throw new InvalidOperationException("The block has no usable name or no objects.");
        if (FindBlock(block.Name) is not null) throw new InvalidOperationException($"Block \"{block.Name}\" already exists.");
        if (index is int i && i >= 0 && i <= _blocks.Count) _blocks.Insert(i, block); else _blocks.Add(block);
        OnChanged(DocumentChangeKind.Settings);
    }

    /// <summary>Removes a block that is not placed anywhere, and returns its position in the table.</summary>
    public int RemoveBlock(string name)
    {
        int index = _blocks.FindIndex(b => string.Equals(b.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException($"There is no block named \"{name}\".");
        if (CountBlockReferences(_blocks[index].Name) > 0) throw new InvalidOperationException($"Block \"{_blocks[index].Name}\" is placed in the drawing.");
        _blocks.RemoveAt(index);
        OnChanged(DocumentChangeKind.Settings);
        return index;
    }

    /// <summary>
    /// Puts a new definition in place of the one with the same name and shows it in every reference.
    /// Returns the definition that was replaced.
    /// </summary>
    public BlockDefinition ReplaceBlock(BlockDefinition block)
    {
        if (!block.IsValid) throw new InvalidOperationException("The block has no usable name or no objects.");
        int index = _blocks.FindIndex(b => string.Equals(b.Name, block.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException($"There is no block named \"{block.Name}\".");
        var old = _blocks[index];
        _blocks[index] = block;
        foreach (var reference in _entities.Values.OfType<BlockReferenceEntity>().Where(r => ReferenceEquals(r.Definition, old)).ToList())
        {
            var updated = (BlockReferenceEntity)reference.Clone();
            updated.SetDefinition(block);
            ReplaceEntity(updated);
        }
        OnChanged(DocumentChangeKind.Settings);
        return old;
    }

    public void AddDimensionStyle(DimensionStyle style, int? index = null)
    {
        if (style.Problem is { } problem) throw new InvalidOperationException(problem);
        if (FindDimensionStyle(style.Name) is not null) throw new InvalidOperationException($"Dimension style \"{style.Name}\" already exists.");
        if (index is int i && i >= 1 && i <= _dimensionStyles.Count) _dimensionStyles.Insert(i, style); else _dimensionStyles.Add(style);
        OnChanged(DocumentChangeKind.Settings);
    }

    /// <summary>Why a dimension style cannot be removed, or null when removal is allowed.</summary>
    public string? GetDimensionStyleRemovalBlocker(string? name)
    {
        var style = FindDimensionStyle(name);
        if (style is null) return $"There is no dimension style named \"{name}\".";
        if (style.Name == DimensionStyle.StandardName) return "The Standard style cannot be deleted.";
        if (style.Name == CurrentDimensionStyle.Name) return $"\"{style.Name}\" is the current dimension style. Make another style current first.";
        if (_entities.Values.OfType<DimensionEntity>().Any(d => string.Equals(d.StyleName, style.Name, StringComparison.OrdinalIgnoreCase)))
            return $"Dimension style \"{style.Name}\" is used by dimensions in the drawing.";
        return null;
    }

    /// <summary>Removes a dimension style and returns its position in the table.</summary>
    public int RemoveDimensionStyle(string name)
    {
        if (GetDimensionStyleRemovalBlocker(name) is { } blocker) throw new InvalidOperationException(blocker);
        int index = _dimensionStyles.FindIndex(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        _dimensionStyles.RemoveAt(index);
        OnChanged(DocumentChangeKind.Settings);
        return index;
    }

    /// <summary>
    /// Replaces the values of the style with the same name. The dimensions that use it are not touched here;
    /// <see cref="ReplaceDimensionStyleAction"/> updates them as well. Returns the values that were replaced.
    /// </summary>
    public DimensionStyle ReplaceDimensionStyle(DimensionStyle style)
    {
        if (style.Problem is { } problem) throw new InvalidOperationException(problem);
        int index = _dimensionStyles.FindIndex(s => string.Equals(s.Name, style.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException($"There is no dimension style named \"{style.Name}\".");
        var old = _dimensionStyles[index];
        _dimensionStyles[index] = style with { Name = old.Name };
        OnChanged(DocumentChangeKind.Settings);
        return old;
    }

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

    /// <summary>Number of objects in the active space (the model, or the layout being looked at).</summary>
    public int EntityCount => ActiveSpaceId == Guid.Empty && _layouts.Count == 0 ? _entities.Count : _entities.Values.Count(InActiveSpace);

    private bool InActiveSpace(Entity e) => e.SpaceId == _activeSpace;

    /// <summary>The objects of the active space in draw order (oldest first).</summary>
    public IReadOnlyList<Entity> Entities
    {
        get
        {
            if (_orderedCache is null)
            {
                _orderedCache = _entities.Values.Where(InActiveSpace).ToList();
                _orderedCache.Sort((a, b) => _order[a.Id].CompareTo(_order[b.Id]));
            }
            return _orderedCache;
        }
    }

    /// <summary>The objects of the active space in no particular order (cheaper than <see cref="Entities"/>).</summary>
    public IReadOnlyCollection<Entity> EntitiesUnordered =>
        _layouts.Count == 0 ? _entities.Values : _entities.Values.Where(InActiveSpace).ToList();

    /// <summary>Every object of the drawing: the model and all layouts.</summary>
    public IReadOnlyCollection<Entity> AllEntities => _entities.Values;

    /// <summary>Every object of the drawing in draw order (for saving).</summary>
    public IReadOnlyList<Entity> AllEntitiesOrdered
    {
        get
        {
            var all = _entities.Values.ToList();
            all.Sort((a, b) => _order[a.Id].CompareTo(_order[b.Id]));
            return all;
        }
    }

    // ---------------------------------------------------------------- layouts

    private readonly List<Layout> _layouts = new();
    private Guid _activeSpace = Guid.Empty;

    /// <summary>The layouts (sheets) of the drawing, in tab order.</summary>
    public IReadOnlyList<Layout> Layouts => _layouts;

    public Layout? GetLayout(Guid id) => _layouts.FirstOrDefault(l => l.Id == id);

    /// <summary>Puts a layout at another place in the row of tabs. Returns where it was.</summary>
    public int MoveLayout(Guid id, int index)
    {
        int from = _layouts.FindIndex(l => l.Id == id);
        if (from < 0) throw new InvalidOperationException("That layout does not exist.");
        index = Math.Clamp(index, 0, _layouts.Count - 1);
        if (index == from) return from;
        var layout = _layouts[from];
        _layouts.RemoveAt(from);
        _layouts.Insert(index, layout);
        OnChanged(DocumentChangeKind.Settings);
        return from;
    }

    public Layout? FindLayout(string? name) =>
        _layouts.FirstOrDefault(l => string.Equals(l.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The space being looked at and worked in: <see cref="Guid.Empty"/> for the model, or a layout's id. It is
    /// a view setting, not part of the drawing's content, so changing it does not make the drawing "changed".
    /// </summary>
    public Guid ActiveSpaceId
    {
        get => _activeSpace;
        set
        {
            if (value != Guid.Empty && GetLayout(value) is null) throw new InvalidOperationException("That layout does not exist.");
            if (_activeSpace == value) return;
            _activeSpace = value;
            _orderedCache = null;
        }
    }

    /// <summary>The layout being looked at, or null in the model.</summary>
    public Layout? ActiveLayout => _activeSpace == Guid.Empty ? null : GetLayout(_activeSpace);

    public void AddLayout(Layout layout, int? index = null)
    {
        if (!layout.IsValid) throw new InvalidOperationException("That is not a usable layout (check its name and paper size).");
        if (GetLayout(layout.Id) is not null) throw new InvalidOperationException("A layout with this id already exists.");
        if (FindLayout(layout.Name) is not null) throw new InvalidOperationException($"There is already a layout named \"{layout.Name}\".");
        if (index is int i && i >= 0 && i <= _layouts.Count) _layouts.Insert(i, layout); else _layouts.Add(layout);
        OnChanged(DocumentChangeKind.Settings);
    }

    /// <summary>Removes an empty layout and returns its position among the tabs. The model becomes active if it was the active space.</summary>
    public int RemoveLayout(Guid id)
    {
        int index = _layouts.FindIndex(l => l.Id == id);
        if (index < 0) throw new InvalidOperationException("That layout does not exist.");
        if (_entities.Values.Any(e => e.SpaceId == id)) throw new InvalidOperationException("The layout still has objects on it.");
        _layouts.RemoveAt(index);
        if (_activeSpace == id)
        {
            _activeSpace = Guid.Empty;
            _orderedCache = null;
        }
        OnChanged(DocumentChangeKind.Settings);
        return index;
    }

    /// <summary>Replaces the name and paper size of the layout with the same id. Returns what was replaced.</summary>
    public Layout ReplaceLayout(Layout layout)
    {
        if (!layout.IsValid) throw new InvalidOperationException("That is not a usable layout (check its name and paper size).");
        int index = _layouts.FindIndex(l => l.Id == layout.Id);
        if (index < 0) throw new InvalidOperationException("That layout does not exist.");
        if (_layouts.Any(l => l.Id != layout.Id && string.Equals(l.Name, layout.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"There is already a layout named \"{layout.Name}\".");
        var old = _layouts[index];
        _layouts[index] = layout;
        OnChanged(DocumentChangeKind.Settings);
        return old;
    }

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

        // A new object goes into the space that is being looked at.
        if (!entity.HasSpace) entity.SpaceId = _activeSpace;
        if (entity.SpaceId != Guid.Empty && GetLayout(entity.SpaceId) is null) throw new InvalidOperationException("The entity's layout does not exist.");

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

    /// <summary>Objects of the active space whose bounding box intersects <paramref name="box"/> (unordered).</summary>
    public List<Entity> QueryEntities(BoundingBox box) => QueryEntities(box, _activeSpace);

    /// <summary>Objects of one space (<see cref="Guid.Empty"/> = the model) whose bounding box intersects <paramref name="box"/>.</summary>
    public List<Entity> QueryEntities(BoundingBox box, Guid spaceId)
    {
        var ids = _index.Query(box);
        var result = new List<Entity>(ids.Count);
        foreach (var id in ids)
        {
            var entity = _entities[id];
            if (entity.SpaceId == spaceId) result.Add(entity);
        }
        return result;
    }

    /// <summary>
    /// Bounding box of every object of the active space on a displayed layer; on a layout the sheet is included.
    /// Empty for an empty model.
    /// </summary>
    public BoundingBox GetExtents() => GetExtents(_activeSpace);

    public BoundingBox GetExtents(Guid spaceId)
    {
        var box = GetLayout(spaceId) is { } layout ? layout.Sheet : BoundingBox.Empty;
        foreach (var e in _entities.Values)
        {
            if (e.SpaceId != spaceId) continue;
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
