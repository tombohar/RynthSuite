using System.Collections.Generic;

namespace RynthCore.TerrainData;

/// <summary>
/// Small fixed-capacity least-recently-used map. Not thread-safe: callers hold
/// their own lock. A hit moves the entry to the front without allocating; only
/// an insert allocates (one list node), and the oldest entry is dropped when
/// the map is full.
/// </summary>
internal sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map;
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();

    public LruCache(int capacity)
    {
        _capacity = capacity < 1 ? 1 : capacity;
        _map = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(_capacity);
    }

    public int Count => _map.Count;

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            if (node != _order.First)
            {
                _order.Remove(node);
                _order.AddFirst(node);
            }
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            _order.Remove(existing);
            _map.Remove(key);
        }
        else if (_map.Count >= _capacity)
        {
            var oldest = _order.Last!;
            _order.RemoveLast();
            _map.Remove(oldest.Value.Key);
        }
        _map[key] = _order.AddFirst(new KeyValuePair<TKey, TValue>(key, value));
    }

    public void Clear()
    {
        _map.Clear();
        _order.Clear();
    }
}
