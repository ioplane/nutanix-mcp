using FluentAssertions;
using NutanixMcp.Server.Retrieval;
using Pgvector;
using Xunit;

namespace NutanixMcp.Server.Tests;

public class QueryEmbeddingCacheTest
{
    [Fact]
    public void HitReturnsStoredVector()
    {
        var cache = new QueryEmbeddingCache(capacity: 4);
        var v = MakeVec(0.1f);
        cache.Add("ahv vlan", v);

        cache.TryGet("ahv vlan", out var hit).Should().BeTrue();
        hit.ToArray().Length.Should().Be(1024);
        ((float)hit.ToArray()[0]).Should().BeApproximately(0.1f, 1e-2f);
    }

    [Fact]
    public void MissReturnsFalse()
    {
        var cache = new QueryEmbeddingCache(capacity: 4);
        cache.TryGet("not stored", out var miss).Should().BeFalse();
        miss.Should().BeNull();
    }

    [Fact]
    public void EvictionDropsOldestEntriesWhenOverCapacity()
    {
        // The implementation drops down to capacity/2 when count exceeds
        // capacity. For capacity=4, the 5th add keeps the 2 most recently used
        // entries plus the new one.
        var cache = new QueryEmbeddingCache(capacity: 4);
        cache.Add("a", MakeVec(0.0f));
        cache.Add("b", MakeVec(0.1f));
        cache.Add("c", MakeVec(0.2f));
        cache.Add("d", MakeVec(0.3f));

        // Bump b and d to most-recently-used so they outlive the eviction.
        cache.TryGet("b", out _).Should().BeTrue();
        cache.TryGet("d", out _).Should().BeTrue();

        // 5th add triggers EvictOldestHalf — keeps the entries with the newest
        // LastAccess values plus the just-added entry.
        cache.Add("e", MakeVec(0.4f));

        cache.TryGet("e", out _).Should().BeTrue();
        cache.TryGet("d", out _).Should().BeTrue();
        // a and c were oldest after the bumps; they get evicted.
        cache.TryGet("a", out _).Should().BeFalse();
        cache.TryGet("c", out _).Should().BeFalse();
    }

    [Fact]
    public void CapacityBelowOneThrows()
    {
        var act = () => new QueryEmbeddingCache(capacity: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static HalfVector MakeVec(float seed)
    {
        var arr = new Half[1024];
        for (int i = 0; i < arr.Length; i++)
        {
            arr[i] = (Half)(seed + (i * 1e-4f));
        }

        return new HalfVector(arr);
    }
}
