using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Core;

/// <summary>
/// A block: a named set of objects drawn about a base point at 0,0, kept once in the drawing and shown
/// wherever a <see cref="BlockReferenceEntity"/> places it. A definition never changes after it is made;
/// redefining a block puts a new definition in its place.
/// </summary>
public sealed class BlockDefinition
{
    public const int MaxNameLength = 64;
    public const int MaxEntities = 5000;

    private readonly List<Entity> _entities;

    /// <param name="entities">The objects, in the block's own coordinates. They are copied; blocks inside are left out.</param>
    public BlockDefinition(string name, IEnumerable<Entity> entities, string description = "", string category = "")
    {
        Name = (name ?? "").Trim();
        Description = (description ?? "").Trim();
        Category = (category ?? "").Trim();
        _entities = new List<Entity>();
        foreach (var entity in entities)
        {
            if (entity is BlockReferenceEntity or ViewportEntity) continue;
            var copy = entity.CopyWithNewId();
            copy.LayerId = Guid.Empty;          // the contents take the layer of the place they are inserted
            copy.GroupId = null;
            copy.SpaceId = Guid.Empty;
            _entities.Add(copy);
        }
        Bounds = BoundingBox.Empty;
        foreach (var entity in _entities) Bounds = Bounds.Union(entity.Bounds);
    }

    public string Name { get; }

    /// <summary>What the block is, for the library ("Three-seat sofa 84 x 36").</summary>
    public string Description { get; }

    /// <summary>The library group it came from ("Furniture"); empty for a block made in the drawing.</summary>
    public string Category { get; }

    public IReadOnlyList<Entity> Entities => _entities;

    /// <summary>The extent of the contents about the base point.</summary>
    public BoundingBox Bounds { get; }

    /// <summary>True for a name that can be used: 1 to 64 characters, nothing unprintable.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxNameLength && !name.Any(char.IsControl);

    public bool IsValid =>
        IsValidName(Name) && _entities.Count > 0 && _entities.Count <= MaxEntities && _entities.All(e => e.IsValid) &&
        Description.Length <= 256 && !Description.Any(char.IsControl) && Category.Length <= MaxNameLength && !Category.Any(char.IsControl);

    /// <summary>The same contents under another name (for a copy into a library or a drawing where the name is taken).</summary>
    public BlockDefinition Renamed(string name) => new(name, _entities, Description, Category);

    /// <summary>The same contents made larger or smaller about the base point (for a drawing in other units).</summary>
    public BlockDefinition Scaled(double factor)
    {
        if (factor == 1.0) return this;
        var scaled = _entities.Select(e => e.Clone()).ToList();
        var transform = Transform2D.Scaling(factor, Point2D.Origin);
        foreach (var entity in scaled) entity.Transform(transform);
        return new BlockDefinition(Name, scaled, Description, Category);
    }
}

/// <summary>
/// One placing of a block: where its base point is, how large and at what angle. It is one object: a click
/// anywhere on it selects all of it, and EXPLODE turns it back into separate objects.
/// </summary>
public sealed class BlockReferenceEntity : Entity
{
    public const string Type = "insert";

    private Point2D _position;
    private double _scale = 1.0, _rotation;
    private List<Entity>? _resolved;

    public BlockReferenceEntity(Guid id, BlockDefinition definition, Point2D position) : base(id)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _position = position;
    }

    public BlockReferenceEntity(BlockDefinition definition, Point2D position) : this(Guid.NewGuid(), definition, position) { }

    /// <summary>The block that is shown. Set when a block is redefined.</summary>
    public BlockDefinition Definition { get; private set; }

    public string BlockName => Definition.Name;

    /// <summary>Where the block's base point is.</summary>
    public Point2D Position { get => _position; set { _position = value; _resolved = null; } }

    /// <summary>Size against the block as it was made (1 = the same).</summary>
    public double Scale { get => _scale; set { _scale = value; _resolved = null; } }

    /// <summary>Angle in radians, counter-clockwise, about the base point.</summary>
    public double Rotation { get => _rotation; set { _rotation = value; _resolved = null; } }

    public void SetDefinition(BlockDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _resolved = null;
    }

    /// <summary>
    /// The block's objects where this reference puts them, on this reference's layer. They are worked out once
    /// and kept until the reference is moved, scaled or turned.
    /// </summary>
    public IReadOnlyList<Entity> Resolved
    {
        get
        {
            if (_resolved is not null) return _resolved;
            var list = new List<Entity>(Definition.Entities.Count);
            var scale = Transform2D.Scaling(_scale, Point2D.Origin);
            var turn = Transform2D.Rotation(_rotation, Point2D.Origin);
            var move = Transform2D.Translation(new Vector2D(_position.X, _position.Y));
            foreach (var source in Definition.Entities)
            {
                var copy = source.Clone();
                if (_scale != 1.0) copy.Transform(scale);
                if (_rotation != 0.0) copy.Transform(turn);
                copy.Transform(move);
                copy.LayerId = LayerId;
                list.Add(copy);
            }
            return _resolved = list;
        }
    }

    public override string TypeName => Type;

    public override bool IsValid =>
        _position.IsFinite && GeoMath.IsFinite(_scale) && _scale > GeoMath.Epsilon && GeoMath.IsFinite(_rotation) && Definition.IsValid;

    public override BoundingBox Bounds
    {
        get
        {
            var box = BoundingBox.Empty;
            foreach (var entity in Resolved) box = box.Union(entity.Bounds);
            return box.IsEmpty ? new BoundingBox(_position.X, _position.Y, _position.X, _position.Y) : box;
        }
    }

    public override double DistanceTo(Point2D point)
    {
        double best = double.MaxValue;
        foreach (var entity in Resolved) best = Math.Min(best, entity.DistanceTo(point));
        return best;
    }

    public override bool IntersectsBox(BoundingBox box)
    {
        foreach (var entity in Resolved) if (entity.IntersectsBox(box)) return true;
        return false;
    }

    /// <summary>Moves, turns and scales. A mirror moves and turns the block but does not flip its contents.</summary>
    public override void Transform(in Transform2D transform)
    {
        var direction = new Vector2D(Math.Cos(_rotation), Math.Sin(_rotation));
        var moved = transform.Apply(_position + direction) - transform.Apply(_position);
        if (moved.Length > GeoMath.Epsilon) _rotation = moved.Angle;
        _position = transform.Apply(_position);
        _scale *= transform.ScaleFactor;
        _resolved = null;
    }

    protected override Entity CloneGeometry(Guid id) => new BlockReferenceEntity(id, Definition, _position) { Scale = _scale, Rotation = _rotation };
}
