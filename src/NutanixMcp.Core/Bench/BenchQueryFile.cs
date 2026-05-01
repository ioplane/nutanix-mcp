using System.Text.Json.Serialization;

namespace NutanixMcp.Core.Bench;

// Wrapper around a list of BenchQuery rows. The JSON fixture file uses a
// top-level object so the format can grow extra metadata (corpus_sha,
// schema_version) without breaking parsers that pin on the rows array.
public sealed record BenchQueryFile
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 2;

    [JsonPropertyName("corpus_sha")]
    public string? CorpusSha { get; init; }

    [JsonPropertyName("queries")]
    public required IReadOnlyList<BenchQuery> Queries { get; init; }
}
