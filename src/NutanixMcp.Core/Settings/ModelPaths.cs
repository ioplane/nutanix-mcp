namespace NutanixMcp.Core.Settings;

// Centralises filesystem layout for the ONNX models. bge-m3 ships an
// external-data ONNX (model.onnx + model.onnx_data); both files must
// co-locate. The reranker uses the same pattern (model.onnx +
// model.onnx.data). Runtime opens the small graph file; ONNX Runtime
// follows the external-data references automatically.
public static class ModelPaths
{
    public static string EmbedderModel(NutanixMcpSettings settings) =>
        Path.Combine(settings.ModelsDir, "embedder", "model.onnx");

    // bge-m3 is XLM-R; the tokenizer asset is the SentencePiece BPE model.
    // Property name kept as `EmbedderVocab` for symmetry with the BERT path.
    public static string EmbedderVocab(NutanixMcpSettings settings) =>
        Path.Combine(settings.ModelsDir, "embedder", "sentencepiece.bpe.model");

    public static string RerankerDir(string modelsDir) =>
        Path.Combine(modelsDir, "reranker");
}
