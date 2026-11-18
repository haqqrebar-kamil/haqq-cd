using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>The ordered set of selected entity ids.</summary>
public sealed class SelectionSet
{
    private readonly List<Guid> _ordered = new();
    private readonly HashSet<Guid> _set = new();

    public int Count => _ordered.Count;
    public IReadOnlyList<Guid> Ids => _ordered;
    public event EventHandler? Changed;

    public bool Contains(Guid id) => _set.Contains(id);

    public bool Add(Guid id)
    {
        if (!_set.Add(id)) return false;
        _ordered.Add(id);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public int AddRange(IEnumerable<Guid> ids)
    {
        int added = 0;
        foreach (var id in ids)
            if (_set.Add(id)) { _ordered.Add(id); added++; }
        if (added > 0) Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    public bool Remove(Guid id)
    {
        if (!_set.Remove(id)) return false;
        _ordered.Remove(id);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        if (_ordered.Count == 0) return;
        _ordered.Clear();
        _set.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops ids for which <paramref name="keep"/> returns false.</summary>
    public void Prune(Func<Guid, bool> keep)
    {
        int removed = _ordered.RemoveAll(id => !keep(id));
        if (removed == 0) return;
        _set.Clear();
        foreach (var id in _ordered) _set.Add(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Hit-testing against model geometry. Only entities on editable layers can be selected.</summary>
public static class SelectionService
{
    public static bool IsSelectable(DrawingDocument document, Entity entity) =>
        entity.SpaceId == document.ActiveSpaceId && document.GetLayer(entity.LayerId) is { IsEditable: true };

    /// <summary>The entity whose curve is nearest to <paramref name="point"/> within <paramref name="tolerance"/> model units.</summary>
    public static Entity? PickAt(DrawingDocument document, Point2D point, double tolerance)
    {
        Entity? best = null;
        double bestDistance = double.PositiveInfinity;
        long bestOrder = long.MinValue;
        foreach (var entity in document.QueryEntities(BoundingBox.FromCenter(point, tolerance)))
        {
            if (!IsSelectable(document, entity)) continue;
            double d = entity.DistanceTo(point);
            if (d > tolerance) continue;
            long order = document.GetDrawOrder(entity.Id);
            // Nearest wins; when equally near, the object drawn last (on top) wins.
            if (d < bestDistance - GeoMath.Epsilon || (Math.Abs(d - bestDistance) <= GeoMath.Epsilon && order > bestOrder))
            {
                best = entity;
                bestDistance = d;
                bestOrder = order;
            }
        }
        return best;
    }

    /// <summary>
    /// Window selection (<paramref name="crossing"/> false) returns entities entirely inside the box;
    /// crossing selection also returns entities the box touches.
    /// </summary>
    public static List<Entity> SelectInBox(DrawingDocument document, BoundingBox box, bool crossing)
    {
        var result = new List<Entity>();
        foreach (var entity in document.QueryEntities(box))
        {
            if (!IsSelectable(document, entity)) continue;
            if (box.Contains(entity.Bounds) || (crossing && entity.IntersectsBox(box))) result.Add(entity);
        }
        result.Sort((a, b) => document.GetDrawOrder(a.Id).CompareTo(document.GetDrawOrder(b.Id)));
        return result;
    }
}
