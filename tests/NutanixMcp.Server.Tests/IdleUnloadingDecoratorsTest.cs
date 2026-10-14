using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NutanixMcp.Core.Retrieval;
using NutanixMcp.Embedding;
using NutanixMcp.Server.Retrieval;
using Xunit;

namespace NutanixMcp.Server.Tests;

public class IdleUnloadingDecoratorsTest
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(10);
    private static readonly string[] s_query = ["q"];
    private static readonly float[] s_vector = [1f];

    [Fact]
    public void EmbedderDimensionDoesNotLoadModel()
    {
        int created = 0;
        using var embedder = new IdleUnloadingEmbedder(
            () => { created++; return new FakeEmbedder(); }, dimension: 1024, s_timeout, new FakeTimeProvider());

        embedder.Dimension.Should().Be(1024);
        created.Should().Be(0);
    }

    [Fact]
    public async Task EmbedderDelegatesAndUnloadsWhenIdle()
    {
        var inner = new FakeEmbedder();
        var time = new FakeTimeProvider();
        using var embedder = new IdleUnloadingEmbedder(() => inner, dimension: 1024, s_timeout, time);

        var vectors = await embedder.EmbedAsync(s_query, isQuery: true, CancellationToken.None);
        time.Advance(s_timeout);

        vectors.Should().ContainSingle().Which.Should().Equal(1f);
        inner.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task RerankerDelegatesAndUnloadsWhenIdle()
    {
        var inner = new FakeReranker();
        var time = new FakeTimeProvider();
        using var reranker = new IdleUnloadingReranker(() => inner, s_timeout, time);

        var results = await reranker.RerankAsync("q", [new RerankCandidate(7, "doc")], CancellationToken.None);
        time.Advance(s_timeout);

        results.Should().ContainSingle().Which.ChunkId.Should().Be(7);
        inner.Disposed.Should().BeTrue();
    }

    private sealed class FakeEmbedder : IEmbedder
    {
        public bool Disposed { get; private set; }
        public int Dimension => 1;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool isQuery, CancellationToken ct) =>
            Task.FromResult(texts.Select(_ => s_vector).ToArray());

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeReranker : IReranker
    {
        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<RerankResult>> RerankAsync(
            string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RerankResult>>(
                candidates.Select(c => new RerankResult(c.ChunkId, 1f)).ToList());

        public void Dispose() => Disposed = true;
    }
}
