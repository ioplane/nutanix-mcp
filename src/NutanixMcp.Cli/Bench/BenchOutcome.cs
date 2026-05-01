namespace NutanixMcp.Cli.Bench;

public sealed record BenchOutcome
{
    public required IReadOnlyList<BenchPerQuery> PerQuery { get; init; }
    public required double Top1 { get; init; }
    public required double Top5 { get; init; }
    public required double Top10 { get; init; }
    public required double P50Ms { get; init; }
    public required double P95Ms { get; init; }
}
