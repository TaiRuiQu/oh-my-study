namespace OhMyStudy.Documents;

/// <summary>A synchronized LRU whose retained payload never exceeds the byte or entry budget.</summary>
public sealed class BoundedPageCache<TKey, TValue> where TKey : notnull
{
    private sealed record Entry(TKey Key, TValue Value, long Bytes);
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;
    public long MaximumBytes { get; }
    public int MaximumEntries { get; }
    public long RetainedBytes { get { lock (_gate) return _bytes; } }
    public int Count { get { lock (_gate) return _entries.Count; } }

    public BoundedPageCache(long maximumBytes, int maximumEntries = 128)
    {
        if (maximumBytes <= 0 || maximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        MaximumBytes = maximumBytes; MaximumEntries = maximumEntries;
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node)) { value = default; return false; }
            _lru.Remove(node); _lru.AddFirst(node); value = node.Value.Value; return true;
        }
    }

    public bool Set(TKey key, TValue value, long byteSize)
    {
        if (byteSize < 0) throw new ArgumentOutOfRangeException(nameof(byteSize));
        lock (_gate)
        {
            RemoveCore(key);
            if (byteSize > MaximumBytes) return false;
            while (_lru.Last is { } last && (_bytes > MaximumBytes - byteSize || _entries.Count >= MaximumEntries)) RemoveCore(last.Value.Key);
            var node = _lru.AddFirst(new Entry(key, value, byteSize));
            _entries.Add(key, node); _bytes += byteSize; return true;
        }
    }

    public bool Remove(TKey key) { lock (_gate) return RemoveCore(key); }
    public void Clear() { lock (_gate) { _entries.Clear(); _lru.Clear(); _bytes = 0; } }
    public IReadOnlyList<TKey> Keys { get { lock (_gate) return _lru.Select(e => e.Key).ToArray(); } }
    private bool RemoveCore(TKey key)
    {
        if (!_entries.Remove(key, out var node)) return false;
        _lru.Remove(node); _bytes -= node.Value.Bytes; return true;
    }
}

public readonly record struct PageCacheKey(string PageId, int MaximumDimension, ushort Rotation);

/// <summary>Independent resolution levels share one memory budget; a larger cached level can satisfy a smaller request.</summary>
public sealed class BoundedRasterPageCache(long maximumBytes = 96L * 1024 * 1024, int maximumEntries = 64)
{
    private readonly BoundedPageCache<PageCacheKey, RasterizedPage> _cache = new(maximumBytes, maximumEntries);
    public long RetainedBytes => _cache.RetainedBytes;
    public int Count => _cache.Count;
    public bool Set(PageCacheKey key, RasterizedPage raster) => _cache.Set(key, raster, raster.Bgra32.LongLength);
    public bool TryGet(PageCacheKey requested, out RasterizedPage? raster)
    {
        if (_cache.TryGet(requested, out raster)) return true;
        foreach (var key in _cache.Keys.Where(k => k.PageId == requested.PageId && k.Rotation == requested.Rotation && k.MaximumDimension >= requested.MaximumDimension).OrderBy(k => k.MaximumDimension))
            if (_cache.TryGet(key, out raster)) return true;
        raster = null; return false;
    }
    public void Invalidate(string pageId) { foreach (var key in _cache.Keys.Where(k => k.PageId == pageId)) _cache.Remove(key); }
    public void Clear() => _cache.Clear();
}
