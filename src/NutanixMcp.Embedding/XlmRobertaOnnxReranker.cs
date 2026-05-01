using Microsoft.ML.OnnxRuntime;
using NutanixMcp.Core.Retrieval;

namespace NutanixMcp.Embedding;

// Cross-encoder reranker against bge-reranker-v2-m3 (XLM-RoBERTa-base, 568M
// params) — exported via Optimum (we use hooman650/bge-reranker-v2-m3-onnx-o4
// because BAAI does not publish an official ONNX). Same shape contract as
// arista's BERT path: takes (query, candidate) pairs, returns one logit per
// pair, higher = more relevant, no sigmoid needed for ranking.
//
// XLM-R cross-encoder pair encoding:
//   <s> query </s></s> doc </s>   — 4 special tokens, double `</s>` between
//                                    segments per HF/XLM-R convention.
// XLM-R does NOT use `token_type_ids`; the ONNX export has only two inputs.
public sealed class XlmRobertaOnnxReranker : IReranker
{
    private readonly InferenceSession _session;
    private readonly XlmRobertaTokenizer _tok;
    private readonly RerankerOptions _opt;
    private bool _disposed;

    public XlmRobertaOnnxReranker(RerankerOptions opt)
    {
        ArgumentNullException.ThrowIfNull(opt);
        _opt = opt;
        _tok = new XlmRobertaTokenizer(opt.VocabPath);

        var so = new SessionOptions
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

    public async Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<RerankCandidate> candidates,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return [];
        }

        int[] queryIds = _tok.EncodeBare(query);
        var results = new RerankResult[candidates.Count];

        for (int start = 0; start < candidates.Count; start += _opt.BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + _opt.BatchSize, candidates.Count);
            var slice = candidates.Skip(start).Take(end - start).ToList();
            float[] scores = await Task.Run(() => ScoreBatch(queryIds, slice), ct).ConfigureAwait(false);
            for (int i = 0; i < scores.Length; i++)
            {
                results[start + i] = new RerankResult(slice[i].ChunkId, scores[i]);
            }
        }

        return results;
    }

    private float[] ScoreBatch(int[] queryIds, List<RerankCandidate> batch)
    {
        int maxSeq = _opt.MaxSequenceLength;
        // XLM-R pair: <s> q </s></s> d </s> — 4 special tokens total.
        const int specialTokens = 4;
        int docBudget = Math.Max(1, maxSeq - queryIds.Length - specialTokens);

        var encoded = new List<int[]>(batch.Count);
        int maxLen = 0;
        foreach (var cand in batch)
        {
            int[] docIds = _tok.EncodeBare(cand.Text);
            if (docIds.Length > docBudget)
            {
                docIds = docIds[..docBudget];
            }

            int[] seq = new int[1 + queryIds.Length + 2 + docIds.Length + 1];
            int idx = 0;
            seq[idx++] = XlmRobertaTokenizer.BosTokenId;      // <s>
            Array.Copy(queryIds, 0, seq, idx, queryIds.Length);
            idx += queryIds.Length;
            seq[idx++] = XlmRobertaTokenizer.EosTokenId;      // </s>
            seq[idx++] = XlmRobertaTokenizer.EosTokenId;      // </s> (segment separator)
            Array.Copy(docIds, 0, seq, idx, docIds.Length);
            idx += docIds.Length;
            seq[idx] = XlmRobertaTokenizer.EosTokenId;        // </s>

            encoded.Add(seq);
            maxLen = Math.Max(maxLen, seq.Length);
        }

        int seqLen = Math.Max(maxLen, 1);
        long[] input = new long[batch.Count * seqLen];
        long[] mask = new long[batch.Count * seqLen];

        for (int b = 0; b < batch.Count; b++)
        {
            int[] ids = encoded[b];
            int rowStart = b * seqLen;
            for (int i = 0; i < seqLen; i++)
            {
                if (i < ids.Length)
                {
                    input[rowStart + i] = ids[i];
                    mask[rowStart + i] = 1;
                }
                else
                {
                    input[rowStart + i] = XlmRobertaTokenizer.PadTokenId;
                    mask[rowStart + i] = 0;
                }
            }
        }

        long[] shape = [batch.Count, seqLen];
        using var idsVal = OrtValue.CreateTensorValueFromMemory(input, shape);
        using var maskVal = OrtValue.CreateTensorValueFromMemory(mask, shape);

        var feeds = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
        {
            ["input_ids"] = idsVal,
            ["attention_mask"] = maskVal,
        };

        using var runOpts = new RunOptions();
        using var results = _session.Run(runOpts, feeds, ["logits"]);
        var logits = results[0].GetTensorDataAsSpan<float>();

        // Output shape [B, 1] — one raw score per pair. Higher = more relevant.
        float[] scores = new float[batch.Count];
        for (int b = 0; b < batch.Count; b++)
        {
            scores[b] = logits[b];
        }

        return scores;
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
