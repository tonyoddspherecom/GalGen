namespace UniverseGen.Service;

/// <summary>
/// A small thread-safe least-recently-used cache. One lock: every operation is a dictionary lookup
/// and a few pointer moves, so contention isn't worth anything cleverer.
/// </summary>
internal sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();
    private readonly object _lock = new();

    public int Capacity { get; }

    public LruCache(int capacity) => Capacity = Math.Max(capacity, 0);

    public int Count { get { lock (_lock) return _map.Count; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (Capacity == 0) return;
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
                _order.Remove(existing);
            var node = new LinkedListNode<(TKey, TValue)>((key, value));
            _order.AddFirst(node);
            _map[key] = node;
            while (_map.Count > Capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> create)
    {
        if (TryGet(key, out TValue value)) return value;
        value = create(key);
        Set(key, value);
        return value;
    }

    public void Remove(TKey key)
    {
        lock (_lock)
        {
            if (_map.Remove(key, out var node))
                _order.Remove(node);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _order.Clear();
        }
    }
}
