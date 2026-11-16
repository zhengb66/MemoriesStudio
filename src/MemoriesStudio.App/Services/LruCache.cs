using System.Collections.Generic;

namespace MemoriesStudio.Services;

/// <summary>
/// 容量上限的 LRU 缓存。素材上千时位图缓存必须设上限，否则内存被
/// 缩略图 / 预览大图吃光（拖动、播放都会卡）。线程安全。
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Dictionary<TKey, (TValue Value, LinkedListNode<TKey> Node)> _map = new();
    private readonly LinkedList<TKey> _order = new();

    public LruCache(int capacity)
    {
        if (capacity < 1) capacity = 1;
        _capacity = capacity;
    }

    public int Count { get { lock (_lock) return _map.Count; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var entry))
            {
                _order.Remove(entry.Node);
                _order.AddFirst(entry.Node);
                value = entry.Value;
                return true;
            }
            value = default!;
            return false;
        }
    }

    public TValue this[TKey key]
    {
        set
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var entry))
                {
                    entry.Value = value;
                    _order.Remove(entry.Node);
                    _order.AddFirst(entry.Node);
                    return;
                }
                var node = new LinkedListNode<TKey>(key);
                _map[key] = (value, node);
                _order.AddFirst(node);
                if (_map.Count > _capacity)
                {
                    var oldest = _order.Last!;
                    _order.RemoveLast();
                    _map.Remove(oldest.Value);
                }
            }
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
