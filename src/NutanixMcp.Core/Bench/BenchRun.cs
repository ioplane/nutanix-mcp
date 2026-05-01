using System.Text.Json.Serialization;

namespace NutanixMcp.Core.Bench;

// Aggregate result of one bench invocation. Serialised one-row-per-line
// into bench-history.jsonl.
public sealed record BenchRun
{
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("git_sha")]
    public string? GitSha { get; init; }

    [JsonPropertyName("queries")]
    public required int Queries { get; init; }

    [JsonPropertyName("top_1")]
    public required double Top1 { get; init; }

    [JsonPropertyName("top_5")]
    public required double Top5 { get; init; }

    [JsonPropertyName("top_10")]
    public required double Top10 { get; init; }

    [JsonPropertyName("p50_ms")]
    public required double P50Ms { get; init; }

    [JsonPropertyName("p95_ms")]
    public required double P95Ms { get; init; }

    [JsonPropertyName("embedder")]
    public required string Embedder { get; init; }

    [JsonPropertyName("reranker")]
    public required string Reranker { get; init; }

    [JsonPropertyName("rrf_k")]
    public required int RrfK { get; init; }

    [JsonPropertyName("rerank_top_n")]
    public required int RerankTopN { get; init; }

    [JsonPropertyName("candidate_pool_size")]
    public required int CandidatePoolSize { get; init; }

    [JsonPropertyName("limit")]
    public required int Limit { get; init; }

    // Sprint 008 sweep params. Optional — nullable so older rows stay
    // schema-clean and so a default-config run does not lie about the
    // server-side ef_search.
    [JsonPropertyName("ef_search")]
    public int? EfSearch { get; init; }

    [JsonPropertyName("iterative_scan")]
    public string? IterativeScan { get; init; }

    [JsonPropertyName("reranker_dir")]
    public string? RerankerDir { get; init; }

    [JsonPropertyName("by_category")]
    public IReadOnlyDictionary<string, BenchCategoryStats>? ByCategory { get; init; }

    [JsonPropertyName("by_difficulty")]
    public IReadOnlyDictionary<string, BenchCategoryStats>? ByDifficulty { get; init; }
}
