using System.Text.Json.Serialization;

namespace NutanixMcp.Core.Bench;

// One curated bench row. Bound to the JSON fixture at
// tests/fixtures/bench-queries.json.
public sealed record BenchQuery
{
    [JsonPropertyName("query_id")]
    public required string QueryId { get; init; }

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    // Union of acceptable chunk ids. A query is a top-N hit when any of
    // these appears in the first N retrieval results.
    [JsonPropertyName("expect_chunk_ids")]
    public required IReadOnlyList<long> ExpectChunkIds { get; init; }

    // Triage hints — they do not enter the score.
    [JsonPropertyName("expect_doc")]
    public string? ExpectDoc { get; init; }

    [JsonPropertyName("expect_section")]
    public string? ExpectSection { get; init; }

    [JsonPropertyName("expect_product")]
    public string? ExpectProduct { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("difficulty")]
    public string? Difficulty { get; init; }
}
