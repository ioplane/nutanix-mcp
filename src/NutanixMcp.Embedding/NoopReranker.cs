using NutanixMcp.Core.Retrieval;

namespace NutanixMcp.Embedding;

// Identity reranker — assigns score 0 to every candidate so the surrounding
// pipeline can skip the rerank step uniformly when no reranker model is
// configured. Selected by `RerankerFamilyDetector` when the reranker model
// directory is absent or empty. Score of 0 lets callers preserve the
// upstream RRF order via stable sort: equal scores → insertion order kept.
public sealed class NoopReranker : IReranker
{
    public Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<RerankCandidate> candidates,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var results = new RerankResult[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            results[i] = new RerankResult(candidates[i].ChunkId, 0f);
        }

        return Task.FromResult<IReadOnlyList<RerankResult>>(results);
    }

    public void Dispose()
    {
    }
}
