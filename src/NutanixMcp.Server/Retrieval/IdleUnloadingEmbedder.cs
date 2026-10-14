using NutanixMcp.Embedding;

namespace NutanixMcp.Server.Retrieval;

// IEmbedder facade over IdleUnloader: the ONNX session is created on the first query and
// released after the idle timeout (see NutanixMcpSettings.ModelIdleUnloadMinutes).
public sealed class IdleUnloadingEmbedder(Func<IEmbedder> factory, int dimension, TimeSpan idleTimeout, TimeProvider time)
    : IEmbedder
{
    private readonly IdleUnloader<IEmbedder> _unloader = new(factory, idleTimeout, time);

    // Known from settings, so reading it never forces the 2.27 GB model to load.
    public int Dimension { get; } = dimension;

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool isQuery, CancellationToken ct) =>
        _unloader.UseAsync(e => e.EmbedAsync(texts, isQuery, ct));

    public void Dispose() => _unloader.Dispose();
}
