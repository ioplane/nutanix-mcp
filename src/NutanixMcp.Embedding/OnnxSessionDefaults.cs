using Microsoft.ML.OnnxRuntime;

namespace NutanixMcp.Embedding;

// Shared ONNX Runtime settings for the embedder and the reranker sessions.
internal static class OnnxSessionDefaults
{
    public static InferenceSession CreateSession(string modelPath, bool gpu)
    {
        // Before the first native ORT call, which initializes the runtime.
        OnnxTelemetry.DisableForProcess();

        // SessionOptions is disposed once the session is constructed — the native
        // handle it owns is not needed past InferenceSession creation.
        using var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Environment.ProcessorCount,
            // Sequence length differs per batch, so a memory pattern planned for one
            // shape is not reusable and only adds cached allocations.
            EnableMemoryPattern = false,
        };
        if (gpu)
        {
            so.AppendExecutionProvider_CUDA();
        }

        return new InferenceSession(modelPath, so);
    }

    // The CPU arena never returns memory on its own; shrinking it at the end of each
    // Run drops the activation buffers of a large batch instead of keeping the peak.
    // Requires the CPU arena to stay enabled (onnxruntime_run_options_config_keys.h).
    public static RunOptions CreateRunOptions()
    {
        var ro = new RunOptions();
        ro.AddRunConfigEntry("memory.enable_memory_arena_shrinkage", "cpu:0");
        return ro;
    }
}
