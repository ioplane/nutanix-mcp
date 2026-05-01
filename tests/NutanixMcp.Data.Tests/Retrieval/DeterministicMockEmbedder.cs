using System.Numerics.Tensors;
using NutanixMcp.Embedding;

namespace NutanixMcp.Data.Tests.Retrieval;

// Deterministic, content-addressable mock embedder for retrieval integration
// tests. The vector is derived from the input text via FNV-1a hashing, mod
// across 1024 dimensions, then L2-normalised so dot products yield a
// well-behaved cosine similarity. Identical text always returns the same
// vector — so the dense path can recover a seeded chunk perfectly when the
// test issues the chunk content as the query.
//
// Why not run real bge-m3 in tests? Two reasons. First, the model is 2.27 GB
// of external data; we keep CI without HF downloads green via SkippableFact.
// Second, retrieval-pipeline tests should fail because the SQL/RRF/rerank
// glue broke, not because the embedder is non-deterministic across batch
// sizes (real ONNX runtimes occasionally drift float results across
// platforms). The mock keeps the test focused.
internal sealed class DeterministicMockEmbedder : IEmbedder
{
    public int Dimension => 1024;

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool isQuery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var results = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            results[i] = HashToUnitVector(texts[i]);
        }

        return Task.FromResult(results);
    }

    public void Dispose()
    {
    }

    private static float[] HashToUnitVector(string text)
    {
        var vec = new float[1024];

        // FNV-1a 64-bit, two-stream so adjacent dimensions differ enough to
        // give cosine similarity a meaningful spread between similar/different
        // strings.
        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;

        var bytes = System.Text.Encoding.UTF8.GetBytes(text);

        ulong h1 = FnvOffset;
        ulong h2 = FnvOffset ^ 0xDEADBEEFUL;
        foreach (var b in bytes)
        {
            h1 ^= b;
            h1 *= FnvPrime;
            h2 ^= (ulong)((b * 31) + 7);
            h2 *= FnvPrime;
        }

        for (var d = 0; d < 1024; d++)
        {
            // Re-mix the hash by dimension so dim N ≠ dim 0.
            h1 ^= (ulong)d * FnvPrime;
            h2 ^= (ulong)(d + 17) * FnvPrime;
            h1 *= FnvPrime;
            h2 *= FnvPrime;

            // Map to [-1, 1] via low 24 bits of XOR.
            var raw = (int)((h1 ^ h2) & 0xFFFFFF) - 0x800000;
            vec[d] = raw / (float)0x800000;
        }

        var norm = TensorPrimitives.Norm<float>(vec);
        if (norm > 1e-9f)
        {
            TensorPrimitives.Divide(vec, norm, vec);
        }

        return vec;
    }
}
