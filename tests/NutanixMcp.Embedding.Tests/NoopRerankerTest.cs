using AwesomeAssertions;
using NutanixMcp.Core.Retrieval;
using Xunit;

namespace NutanixMcp.Embedding.Tests;

public class NoopRerankerTest
{
    [Fact]
    public async Task ReturnsZeroScorePreservingChunkIds()
    {
        using var rr = new NoopReranker();
        var candidates = new RerankCandidate[]
        {
            new(10L, "first"),
            new(20L, "second"),
            new(30L, "third"),
        };

        var scores = await rr.RerankAsync("query", candidates, CancellationToken.None);

        scores.Select(s => s.ChunkId).Should().BeEquivalentTo([10L, 20L, 30L], o => o.WithStrictOrdering());
        scores.Select(s => s.Score).Should().AllBeEquivalentTo(0f);
    }

    [Fact]
    public async Task EmptyCandidatesReturnsEmpty()
    {
        using var rr = new NoopReranker();
        var scores = await rr.RerankAsync("query", [], CancellationToken.None);
        scores.Should().BeEmpty();
    }
}
