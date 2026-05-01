using System.IO;
using NutanixMcp.Core.Retrieval;

namespace NutanixMcp.Embedding;

// Picks the right IReranker implementation by sniffing the contents of the
// reranker model directory:
//
//   - sentencepiece.bpe.model  + model.onnx (or model.onnx.data alongside)
//                              → XlmRobertaOnnxReranker (bge-reranker-v2-m3)
//   - vocab.txt                + model.onnx
//                              → BERT-WordPiece reranker (deferred)
//   - neither                  → NoopReranker (degraded mode)
//
// Centralising the detection here keeps DI wiring (`ServerHosting`) free of
// model-format conditionals and makes a future swap-in of a BERT-family
// reranker a one-file change here. The BERT branch is deliberately stubbed —
// arista shipped one (`OnnxReranker.cs`) and we will only port it if a
// concrete need arises.
public static class RerankerFamilyDetector
{
    public enum RerankerFamily
    {
        None,
        XlmRoberta,
        BertWordPiece,
    }

    public static RerankerFamily Detect(string rerankerDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(rerankerDir);

        if (!Directory.Exists(rerankerDir))
        {
            return RerankerFamily.None;
        }

        var hasOnnx = File.Exists(Path.Combine(rerankerDir, "model.onnx"));
        if (!hasOnnx)
        {
            return RerankerFamily.None;
        }

        var hasSpm = File.Exists(Path.Combine(rerankerDir, "sentencepiece.bpe.model"));
        if (hasSpm)
        {
            return RerankerFamily.XlmRoberta;
        }

        var hasVocab = File.Exists(Path.Combine(rerankerDir, "vocab.txt"));
        if (hasVocab)
        {
            return RerankerFamily.BertWordPiece;
        }

        return RerankerFamily.None;
    }

    // Convenience constructor for DI: detect family, build the reranker with
    // the matching tokenizer asset path, fall back to NoopReranker.
    public static IReranker Build(string rerankerDir, bool gpu)
    {
        var family = Detect(rerankerDir);
        return family switch
        {
            RerankerFamily.XlmRoberta => new XlmRobertaOnnxReranker(new RerankerOptions
            {
                ModelPath = Path.Combine(rerankerDir, "model.onnx"),
                VocabPath = Path.Combine(rerankerDir, "sentencepiece.bpe.model"),
                Gpu = gpu,
            }),
            RerankerFamily.BertWordPiece => throw new NotSupportedException(
                "BERT-WordPiece reranker family is not implemented yet — port arista-mcp's "
                + "OnnxReranker.cs when a need arises."),
            _ => new NoopReranker(),
        };
    }
}
