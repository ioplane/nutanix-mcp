using System.Text.Json.Serialization;

namespace NutanixMcp.Core.Bench;

public sealed record BenchCategoryStats(
    [property: JsonPropertyName("queries")] int Queries,
    [property: JsonPropertyName("top_1")] double Top1,
    [property: JsonPropertyName("top_5")] double Top5,
    [property: JsonPropertyName("top_10")] double Top10);
