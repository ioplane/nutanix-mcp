using System.IO;

namespace NutanixMcp.Embedding.Tests;

// Resolves the absolute paths to local model assets. Walks up from the test
// binary directory until it finds a `models/embedder/model.onnx` sibling —
// matching the layout produced by `scripts/fetch-models.ps1`. Tests skip
// gracefully when the assets are not present (CI without HF download).
internal static class ModelPaths
{
    public static string? FindModelsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "models");
            if (Directory.Exists(candidate)
                && File.Exists(Path.Combine(candidate, "embedder", "model.onnx")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    public static (string ModelPath, string VocabPath)? Embedder()
    {
        var root = FindModelsRoot();
        if (root is null)
        {
            return null;
        }

        var model = Path.Combine(root, "embedder", "model.onnx");
        var vocab = Path.Combine(root, "embedder", "sentencepiece.bpe.model");
        if (!File.Exists(model) || !File.Exists(vocab))
        {
            return null;
        }

        return (model, vocab);
    }

    public static (string ModelPath, string VocabPath)? Reranker()
    {
        var root = FindModelsRoot();
        if (root is null)
        {
            return null;
        }

        var model = Path.Combine(root, "reranker", "model.onnx");
        var vocab = Path.Combine(root, "reranker", "sentencepiece.bpe.model");
        if (!File.Exists(model) || !File.Exists(vocab))
        {
            return null;
        }

        return (model, vocab);
    }
}
