namespace NutanixMcp.Cli.Ingest;

public sealed class IngestOptions
{
    public required string CatalogBaseDir { get; init; }
    public string? Category { get; init; }
    public bool Force { get; init; }
    public bool DryRun { get; init; }
    public int Limit { get; init; } = int.MaxValue;
    public bool Verbose { get; init; }
}
