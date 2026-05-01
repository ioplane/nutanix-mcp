namespace NutanixMcp.Cli.Bench;

public sealed class NullBenchProgress : IBenchProgress
{
    public static NullBenchProgress Instance { get; } = new();

    public void Start(int total)
    {
    }

    public void Query(string queryId, int firstHitRank, double latencyMs)
    {
    }

    public void Finish()
    {
    }
}
