using Microsoft.ML.OnnxRuntime;
using Xunit;
using Xunit.Abstractions;

namespace NutanixMcp.Embedding.Tests;

// Diagnostic: prints ONNX input/output metadata so we can verify the
// expected tensor names. Run with `dotnet test --filter InspectModel`.
public class InspectModel(ITestOutputHelper output)
{
#pragma warning disable S2699 // diagnostic-only test, no assertion needed
    [SkippableFact]
    public void PrintEmbedderIO()
    {
        var paths = ModelPaths.Embedder();
        Skip.If(paths is null, "embedder model assets not present");

        OnnxTelemetry.DisableForProcess();
        using var sess = new InferenceSession(paths.Value.ModelPath);
        output.WriteLine("=== embedder inputs ===");
        foreach (var (name, meta) in sess.InputMetadata)
        {
            output.WriteLine($"  {name}: {meta.ElementType} dims=[{string.Join(',', meta.Dimensions)}]");
        }

        output.WriteLine("=== embedder outputs ===");
        foreach (var (name, meta) in sess.OutputMetadata)
        {
            output.WriteLine($"  {name}: {meta.ElementType} dims=[{string.Join(',', meta.Dimensions)}]");
        }
    }

    [SkippableFact]
    public void PrintRerankerIO_Diagnostic()
    {
        var paths = ModelPaths.Reranker();
        Skip.If(paths is null, "reranker model assets not present");

        OnnxTelemetry.DisableForProcess();
        using var sess = new InferenceSession(paths.Value.ModelPath);
        output.WriteLine("=== reranker inputs ===");
        foreach (var (name, meta) in sess.InputMetadata)
        {
            output.WriteLine($"  {name}: {meta.ElementType} dims=[{string.Join(',', meta.Dimensions)}]");
        }

        output.WriteLine("=== reranker outputs ===");
        foreach (var (name, meta) in sess.OutputMetadata)
        {
            output.WriteLine($"  {name}: {meta.ElementType} dims=[{string.Join(',', meta.Dimensions)}]");
        }
    }
#pragma warning restore S2699
}
