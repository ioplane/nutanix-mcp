namespace NutanixMcp.Core.Settings;

public sealed class NutanixMcpSettings
{
    // The default points at the local podman postgres declared in
    // docker/compose.yaml. Production deployments must override this via
    // the NUTANIX_MCP__ConnectionString environment variable.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "S2068:Credentials should not be hard-coded",
        Justification = "Local dev default only; overridden in all real deployments.")]
    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5435;Database=nutanix;Username=nutanix;Password=nutanix";

    public string ModelsDir { get; set; } = "models";

    // Override for the reranker model directory. Falls back to {ModelsDir}/reranker
    // when null. Production override for the INT8 fine-tuned variant:
    //   NUTANIX_MCP__RerankerDir=/var/lib/nutanix-mcp/models/reranker-finetuned-int8
    // See deploy/systemd/nutanix-mcp.env.example.
    public string? RerankerDir { get; set; }

    // Default embedder is bge-m3 — multilingual XLM-R, 1024-dim, no query prefix.
    public string EmbeddingModel { get; set; } = "bge-m3";
    public int EmbeddingDim { get; set; } = 1024;

    // Reranker is bge-reranker-v2-m3 (XLM-R, 568M).
    public string RerankerModel { get; set; } = "bge-reranker-v2-m3";

    public bool Gpu { get; set; }
    public McpTransport Transport { get; set; } = McpTransport.Stdio;
    public int HttpPort { get; set; } = 7456;

    public int IngestBatchSize { get; set; } = 32;

    // Chunking thresholds — see docs/plans/chunking-strategy.md § 4.2.
    public int ChunkMaxTokens { get; set; } = 1200;
    public int ChunkTargetTokens { get; set; } = 512;
    public int ChunkOverlapTokens { get; set; } = 64;
    public int ChunkMinTokens { get; set; } = 40;
}
