namespace NutanixMcp.Cli.Bench;

public interface IBenchProgress
{
    void Start(int total);
    void Query(string queryId, int firstHitRank, double latencyMs);
    void Finish();
}
