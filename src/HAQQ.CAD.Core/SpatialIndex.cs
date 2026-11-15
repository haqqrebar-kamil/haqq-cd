using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Core;

/// <summary>
/// Uniform-grid spatial hash over entity bounding boxes. Accelerates viewport culling,
/// hit-testing and snapping on large drawings. Items that would span too many cells are kept
/// in a separate "oversize" list that is always checked.
/// </summary>
public sealed class SpatialIndex
{
    private const int MaxCellsPerItem = 256;

    private readonly Dictionary<long, List<Guid>> _cells = new(CellKeyComparer.Instance);
    private readonly Dictionary<Guid, BoundingBox> _bounds = new();
    private readonly HashSet<Guid> _oversize = new();
    private double _cellSize;

    public SpatialIndex(double cellSize = 64.0)
    {
        _cellSize = cellSize > 0 && double.IsFinite(cellSize) ? cellSize : 64.0;
    }

    public int Count => _bounds.Count;
    public double CellSize => _cellSize;

    public void Clear()
    {
        _cells.Clear();
        _bounds.Clear();
        _oversize.Clear();
    }

    /// <summary>Replaces the whole content, choosing a cell size suited to the items.</summary>
    public void Rebuild(IEnumerable<KeyValuePair<Guid, BoundingBox>> items)
    {
        var list = items.Where(i => !i.Value.IsEmpty).ToList();
        Clear();
        if (list.Count > 0)
        {
            double sum = 0;
            foreach (var item in list) sum += Math.Max(item.Value.Width, item.Value.Height);
            double average = sum / list.Count;
            _cellSize = average > 1e-9 && double.IsFinite(average) ? average * 2.0 : 1.0;
        }
        foreach (var item in list) Insert(item.Key, item.Value);
    }

    public void Insert(Guid id, BoundingBox box)
    {
        if (box.IsEmpty) return;
        if (_bounds.ContainsKey(id)) Remove(id);
        _bounds[id] = box;

        if (!TryGetCellRange(box, out int x0, out int y0, out int x1, out int y1) ||
            (long)(x1 - x0 + 1) * (y1 - y0 + 1) > MaxCellsPerItem)
        {
            _oversize.Add(id);
            return;
        }

        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        {
            long key = Key(x, y);
            if (!_cells.TryGetValue(key, out var bucket)) _cells[key] = bucket = new List<Guid>(4);
            bucket.Add(id);
        }
    }

    public bool Remove(Guid id)
    {
        if (!_bounds.Remove(id, out var box)) return false;
        if (_oversize.Remove(id)) return true;
        if (!TryGetCellRange(box, out int x0, out int y0, out int x1, out int y1)) return true;

        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        {
            long key = Key(x, y);
            if (_cells.TryGetValue(key, out var bucket))
            {
                bucket.Remove(id);
                if (bucket.Count == 0) _cells.Remove(key);
            }
        }
        return true;
    }

    /// <summary>Returns the ids of all items whose bounding box intersects <paramref name="box"/>.</summary>
    public HashSet<Guid> Query(BoundingBox box)
    {
        var result = new HashSet<Guid>();
        if (box.IsEmpty || _bounds.Count == 0) return result;

        bool haveRange = TryGetCellRange(box, out int x0, out int y0, out int x1, out int y1);
        long cellCount = haveRange ? (long)(x1 - x0 + 1) * (y1 - y0 + 1) : long.MaxValue;

        if (!haveRange || cellCount > Math.Max(64, _cells.Count))
        {
            // The query covers more cells than exist: scanning the items directly is cheaper.
            foreach (var pair in _bounds)
                if (pair.Value.Intersects(box)) result.Add(pair.Key);
            return result;
        }

        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        {
            if (!_cells.TryGetValue(Key(x, y), out var bucket)) continue;
            foreach (var id in bucket)
                if (_bounds[id].Intersects(box)) result.Add(id);
        }

        foreach (var id in _oversize)
            if (_bounds[id].Intersects(box)) result.Add(id);

        return result;
    }

    private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

    /// <summary>
    /// Mixes both cell coordinates into the hash. The default hash of a long XORs its halves,
    /// which makes neighbouring cells collide heavily.
    /// </summary>
    private sealed class CellKeyComparer : IEqualityComparer<long>
    {
        public static readonly CellKeyComparer Instance = new();
        public bool Equals(long a, long b) => a == b;
        public int GetHashCode(long key) => (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> 32);
    }

    private bool TryGetCellRange(BoundingBox box, out int x0, out int y0, out int x1, out int y1)
    {
        double fx0 = Math.Floor(box.MinX / _cellSize), fy0 = Math.Floor(box.MinY / _cellSize);
        double fx1 = Math.Floor(box.MaxX / _cellSize), fy1 = Math.Floor(box.MaxY / _cellSize);
        const double limit = 1_000_000_000.0;
        if (!(Math.Abs(fx0) < limit && Math.Abs(fy0) < limit && Math.Abs(fx1) < limit && Math.Abs(fy1) < limit))
        {
            x0 = y0 = x1 = y1 = 0;
            return false;
        }
        x0 = (int)fx0; y0 = (int)fy0; x1 = (int)fx1; y1 = (int)fy1;
        return true;
    }
}
