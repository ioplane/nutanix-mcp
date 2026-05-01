namespace NutanixMcp.Cli.Ingest;

public interface IIngestProgress
{
    void Start(int total);
    void Document(string docId, string status, int chunkCount);
    void Finish(int succeeded, int skipped, int failed);
}
