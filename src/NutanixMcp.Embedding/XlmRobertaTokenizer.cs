using System.IO;
using Microsoft.ML.Tokenizers;

namespace NutanixMcp.Embedding;

// XLM-RoBERTa tokenizer for the bge-m3 family (embedder + reranker share the
// same SentencePiece model). Loads `sentencepiece.bpe.model` via
// `Microsoft.ML.Tokenizers.SentencePieceTokenizer` and remaps native SP ids onto
// HuggingFace's XLM-R id space (fairseq alignment):
//
//   SP id  | HF id | token
//   -------|-------|--------
//     0    |  3    | <unk>      (`<unk>` is sp 0; HF puts unk at 3)
//     1    |  -    | <s>        (sp has <s> at 1, HF uses 0 via specials table)
//     2    |  -    | </s>       (sp has </s> at 2, HF uses 2 via specials table)
//    >=3   | sp+1  | real piece (fairseq_offset = 1)
//
// For ordinary text encoding, `sp_model.PieceToId` returns 0 for unknown and
// >=3 for real pieces — sp ids 1/2 only appear for the literal `<s>` / `</s>`
// strings, which HF short-circuits through a specials dict. So the uniform
// remap `sp == 0 ? UnkTokenId : sp + 1` matches HF `_convert_token_to_id`
// output byte-for-byte on real user input.
public sealed class XlmRobertaTokenizer
{
    // HF XLM-R special-token ids (fairseq_tokens_to_ids). Fixed by the model
    // family, not by the SPM file — don't try to derive them from
    // SpmTokenizer.BeginningOfSentenceId etc. (which would return SP ids 1/2,
    // not the HF ids 0/2).
    public const int BosTokenId = 0;   // <s>
    public const int PadTokenId = 1;   // <pad>
    public const int EosTokenId = 2;   // </s>
    public const int UnkTokenId = 3;   // <unk>

    private const int FairseqOffset = 1;

    private readonly SentencePieceTokenizer _tok;

    public XlmRobertaTokenizer(string spmPath)
    {
        using var fs = File.OpenRead(spmPath);
        _tok = SentencePieceTokenizer.Create(
            fs,
            addBeginningOfSentence: false,
            addEndOfSentence: false,
            specialTokens: new Dictionary<string, int>(0, StringComparer.Ordinal));
    }

    // Encodes text raw (no BOS/EOS). Caller is responsible for the canonical
    // pair sequence: embed = `<s> text </s>`, reranker = `<s> q </s></s> d </s>`.
    public int[] EncodeBare(string text)
    {
        var spIds = _tok.EncodeToIds(
            text,
            addBeginningOfSentence: false,
            addEndOfSentence: false,
            considerPreTokenization: true,
            considerNormalization: true);

        int[] result = new int[spIds.Count];
        for (int i = 0; i < spIds.Count; i++)
        {
            int sp = spIds[i];
            result[i] = sp == 0 ? UnkTokenId : sp + FairseqOffset;
        }

        return result;
    }

    // Batch encode for the bi-encoder embedder. Wraps each text in `<s> ... </s>`,
    // truncates to `maxSeqLen`, pads to the longest sequence in the batch with
    // `<pad>`, and returns flattened `[batch * seqLen]` arrays for ONNX.
    public (long[] InputIds, long[] AttentionMask, int SeqLen) EncodeBatchForEmbedding(
        IReadOnlyList<string> texts,
        int maxSeqLen)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var encoded = new List<int[]>(texts.Count);
        int maxLen = 0;

        foreach (string t in texts)
        {
            int[] bare = EncodeBare(t);
            // Reserve 2 tokens for <s> and </s> when truncating.
            int contentMax = Math.Max(0, maxSeqLen - 2);
            if (bare.Length > contentMax)
            {
                bare = bare[..contentMax];
            }

            int[] withSpecials = new int[bare.Length + 2];
            withSpecials[0] = BosTokenId;
            Array.Copy(bare, 0, withSpecials, 1, bare.Length);
            withSpecials[^1] = EosTokenId;

            encoded.Add(withSpecials);
            maxLen = Math.Max(maxLen, withSpecials.Length);
        }

        int seqLen = Math.Max(maxLen, 1);
        long[] input = new long[texts.Count * seqLen];
        long[] mask = new long[texts.Count * seqLen];

        for (int b = 0; b < texts.Count; b++)
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
                    input[rowStart + i] = PadTokenId;
                    mask[rowStart + i] = 0;
                }
            }
        }

        return (input, mask, seqLen);
    }
}
