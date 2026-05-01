namespace NutanixMcp.Cli.Ingest;

public sealed record IngestResult(
    long IngestRunId,
    string Status,
    int DocsTotal,
    int DocsSkipped,
    int DocsUpserted,
    int ChunksUpserted,
    string? ErrorMsg);
