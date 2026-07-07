using System.Numerics.Tensors;
using Microsoft.ML.OnnxRuntime;

namespace NutanixMcp.Embedding;

// Runs bge-m3 (XLM-RoBERTa-base, 568M params, 1024-dim) via ONNX Runtime.
// Inputs:  input_ids, attention_mask (int64 [B, L]). XLM-R has type_vocab_size=1
//          and the export does not declare a token_type_ids input.
// Output:  sentence_embedding [B, 1024] — the BAAI ONNX export ships a pre-pooled,
//          L2-normalised sentence embedding (mean-pool + normalize baked into the
//          graph). Verified via the InspectModel diagnostic on 2026-05-01:
//              outputs:
//                token_embeddings   [B, L, 1024]
//                sentence_embedding [B, 1024]
//          We still defensively re-normalise on the .NET side to protect
//          retrieval quality if a future export drops the normalize op silently.
public sealed class OnnxEmbedder : IEmbedder
{
    private const int HiddenSize = 1024;

    private readonly InferenceSession _session;
    private readonly XlmRobertaTokenizer _tok;
    private readonly EmbeddingOptions _opt;
    private bool _disposed;

    public int Dimension => HiddenSize;

    public OnnxEmbedder(EmbeddingOptions opt)
    {
        ArgumentNullException.ThrowIfNull(opt);

        _opt = opt;
        _tok = new XlmRobertaTokenizer(opt.VocabPath);

        // Dispose the SessionOptions once the session is constructed — the
        // native handle it owns is not needed past InferenceSession creation
        // (the session retains whatever it needs internally).
        using var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Environment.ProcessorCount,
        };
        if (opt.Gpu)
        {
            so.AppendExecutionProvider_CUDA();
        }

        _session = new InferenceSession(opt.ModelPath, so);
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool isQuery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
        {
            return [];
        }

        // bge-m3 does NOT require a query prefix (unlike Snowflake Arctic).
        // Settings.QueryPrefix defaults to "" and is concatenated only if a
        // future swap-in model needs it.
        var prepped = isQuery && _opt.QueryPrefix.Length > 0
            ? texts.Select(t => _opt.QueryPrefix + t).ToList()
            : [.. texts];

        float[][] results = new float[texts.Count][];

        for (int start = 0; start < prepped.Count; start += _opt.BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + _opt.BatchSize, prepped.Count);
            var batch = prepped.GetRange(start, end - start);

            float[][] embedded = await Task.Run(() => EmbedBatch(batch), ct).ConfigureAwait(false);
            for (int i = 0; i < embedded.Length; i++)
            {
                results[start + i] = embedded[i];
            }
        }

        return results;
    }

    private float[][] EmbedBatch(List<string> batch)
    {
        var (input, mask, seqLen) = _tok.EncodeBatchForEmbedding(batch, _opt.MaxSequenceLength);
        long[] shape = [batch.Count, seqLen];

        using var idsVal = OrtValue.CreateTensorValueFromMemory(input, shape);
        using var maskVal = OrtValue.CreateTensorValueFromMemory(mask, shape);

        var feeds = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
        {
            ["input_ids"] = idsVal,
            ["attention_mask"] = maskVal,
        };

        using var runOpts = new RunOptions();
        using var results = _session.Run(runOpts, feeds, ["sentence_embedding"]);
        var sentence = results[0].GetTensorDataAsSpan<float>();

        float[][] outputs = new float[batch.Count][];
        for (int b = 0; b < batch.Count; b++)
        {
            outputs[b] = ExtractAndNormalize(sentence, b);
        }

        return outputs;
    }

    // Defensive re-normalize. The export already emits unit vectors; this guards
    // retrieval quality if a future export drops the normalize op silently.
    // TensorPrimitives.Norm + Divide are SIMD-vectorized on .NET 10.
    private static float[] ExtractAndNormalize(ReadOnlySpan<float> sentence, int batchIdx)
    {
        float[] vec = new float[HiddenSize];
        sentence.Slice(batchIdx * HiddenSize, HiddenSize).CopyTo(vec);

        float norm = TensorPrimitives.Norm<float>(vec);
        if (norm > 1e-9f)
        {
            TensorPrimitives.Divide(vec, norm, vec);
        }

        return vec;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _session.Dispose();
        _disposed = true;
    }
}
