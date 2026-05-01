namespace NutanixMcp.Cli.Bench;

public sealed class BenchHarnessOptions
{
    public int Limit { get; init; } = 10;
    public int CandidatePoolSize { get; init; } = 50;
    public int RerankTopN { get; init; } = 30;
    public int RrfK { get; init; } = 60;
}
