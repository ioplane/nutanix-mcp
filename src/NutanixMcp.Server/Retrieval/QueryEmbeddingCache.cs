using System.Collections.Concurrent;
using Pgvector;

namespace NutanixMcp.Server.Retrieval;

// Bounded cache of query → HalfVector embeddings. Search flows often repeat
// near-identical queries; caching the first embedding saves a 200-300 ms
// ONNX inference (bge-m3 is ~5× heavier than Snowflake Arctic) on every
// subsequent hit.
//
// Eviction is "clear oldest half when full", not strict LRU — a perf cache
// shouldn't justify a linked-list + lock overhead. Under steady-state use
// this converges to a workable hot-set size. 256 entries × ~2 KB
// (1024 × 2 B HalfVector) ≈ 530 KB ceiling, negligible.
public sealed class QueryEmbeddingCache
{
    private readonly ConcurrentDictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private long _accessCounter;

    public QueryEmbeddingCache(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public bool TryGet(string key, out HalfVector value)
    {
        if (_map.TryGetValue(key, out var entry))
        {
            entry.LastAccess = Interlocked.Increment(ref _accessCounter);
            value = entry.Vector;
            return true;
        }
        value = default!;
        return false;
    }

    public void Add(string key, HalfVector value)
    {
        _map[key] = new Entry
        {
            Vector = value,
            LastAccess = Interlocked.Increment(ref _accessCounter),
        };

        if (_map.Count > _capacity)
        {
            EvictOldestHalf();
        }
    }

    private void EvictOldestHalf()
    {
        var snapshot = _map.ToArray();
        Array.Sort(snapshot, static (a, b) => a.Value.LastAccess.CompareTo(b.Value.LastAccess));
        int evictCount = snapshot.Length - (_capacity / 2);
        for (int i = 0; i < evictCount; i++)
        {
            _map.TryRemove(snapshot[i].Key, out _);
        }
    }

    private sealed class Entry
    {
        public required HalfVector Vector { get; init; }
        public long LastAccess { get; set; }
    }
}
