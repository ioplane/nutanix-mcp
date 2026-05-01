namespace NutanixMcp.Cli.Ingest;

public sealed class NullIngestProgress : IIngestProgress
{
    public static NullIngestProgress Instance { get; } = new();

    public void Start(int total)
    {
    }

    public void Document(string docId, string status, int chunkCount)
    {
    }

    public void Finish(int succeeded, int skipped, int failed)
    {
    }
}
