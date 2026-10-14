using NutanixMcp.Core.Retrieval;

namespace NutanixMcp.Server.Retrieval;

// IReranker facade over IdleUnloader; same lifecycle as IdleUnloadingEmbedder.
public sealed class IdleUnloadingReranker(Func<IReranker> factory, TimeSpan idleTimeout, TimeProvider time)
    : IReranker
{
    private readonly IdleUnloader<IReranker> _unloader = new(factory, idleTimeout, time);

    public Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<RerankCandidate> candidates,
        CancellationToken ct) =>
        _unloader.UseAsync(r => r.RerankAsync(query, candidates, ct));

    public void Dispose() => _unloader.Dispose();
}
