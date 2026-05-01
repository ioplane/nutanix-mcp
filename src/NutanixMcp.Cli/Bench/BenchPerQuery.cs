using NutanixMcp.Core.Bench;

namespace NutanixMcp.Cli.Bench;

public sealed record BenchPerQuery
{
    public required BenchQuery Query { get; init; }
    public required int FirstHitRank { get; init; }
    public required double LatencyMs { get; init; }
    public required IReadOnlyList<long> ReturnedChunkIds { get; init; }
}
