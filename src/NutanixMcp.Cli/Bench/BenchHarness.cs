using System.Diagnostics;
using NutanixMcp.Core.Bench;
using NutanixMcp.Server.Retrieval;

namespace NutanixMcp.Cli.Bench;

// Drives the bench: per query, calls IHybridRetriever, scores against
// expect_chunk_ids, records latency, then returns aggregates.
public sealed class BenchHarness(
    IHybridRetriever retriever,
    BenchHarnessOptions options)
{
    private readonly IHybridRetriever _retriever = retriever
        ?? throw new ArgumentNullException(nameof(retriever));

    private readonly BenchHarnessOptions _options = options
        ?? throw new ArgumentNullException(nameof(options));

    public async Task<BenchOutcome> RunAsync(
        IReadOnlyList<BenchQuery> queries,
        IBenchProgress progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(progress);

        var perQuery = new List<BenchPerQuery>(queries.Count);
        var latenciesMs = new List<double>(queries.Count);
        progress.Start(queries.Count);

        for (var i = 0; i < queries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var q = queries[i];
            var sw = Stopwatch.StartNew();
            var response = await _retriever
                .SearchAsync(q.Query, BuildOptions(), ct)
                .ConfigureAwait(false);
            sw.Stop();

            var ranked = response.Results.Select(r => r.ChunkId).ToList();
            var rank = BenchScoring.FirstHitRank(ranked, q.ExpectChunkIds);
            perQuery.Add(new BenchPerQuery
            {
                Query = q,
                FirstHitRank = rank,
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                ReturnedChunkIds = ranked,
            });
            latenciesMs.Add(sw.Elapsed.TotalMilliseconds);
            progress.Query(q.QueryId, rank, sw.Elapsed.TotalMilliseconds);
        }

        progress.Finish();

        var top1 = perQuery.Count == 0 ? 0 : perQuery.Count(p => BenchScoring.IsHit(p.FirstHitRank, 1)) / (double)perQuery.Count;
        var top5 = perQuery.Count == 0 ? 0 : perQuery.Count(p => BenchScoring.IsHit(p.FirstHitRank, 5)) / (double)perQuery.Count;
        var top10 = perQuery.Count == 0 ? 0 : perQuery.Count(p => BenchScoring.IsHit(p.FirstHitRank, 10)) / (double)perQuery.Count;
        var (p50, p95) = ComputePercentiles(latenciesMs);

        return new BenchOutcome
        {
            PerQuery = perQuery,
            Top1 = top1,
            Top5 = top5,
            Top10 = top10,
            P50Ms = p50,
            P95Ms = p95,
        };
    }

    private RetrievalOptions BuildOptions() => new()
    {
        Limit = _options.Limit,
        CandidatePoolSize = _options.CandidatePoolSize,
        RerankTopN = _options.RerankTopN,
        RrfK = _options.RrfK,
    };

    private static (double P50, double P95) ComputePercentiles(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return (0, 0);
        }

        var sorted = values.OrderBy(v => v).ToArray();
        return (Percentile(sorted, 0.50), Percentile(sorted, 0.95));
    }

    // Linear interpolation between adjacent samples — same convention as
    // `numpy.percentile(method='linear')`.
    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var pos = q * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        if (lo == hi)
        {
            return sorted[lo];
        }

        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (pos - lo));
    }
}

public sealed class BenchHarnessOptions
{
    public int Limit { get; init; } = 10;
    public int CandidatePoolSize { get; init; } = 50;
    public int RerankTopN { get; init; } = 30;
    public int RrfK { get; init; } = 60;
}

public sealed record BenchPerQuery
{
    public required BenchQuery Query { get; init; }
    public required int FirstHitRank { get; init; }
    public required double LatencyMs { get; init; }
    public required IReadOnlyList<long> ReturnedChunkIds { get; init; }
}

public sealed record BenchOutcome
{
    public required IReadOnlyList<BenchPerQuery> PerQuery { get; init; }
    public required double Top1 { get; init; }
    public required double Top5 { get; init; }
    public required double Top10 { get; init; }
    public required double P50Ms { get; init; }
    public required double P95Ms { get; init; }
}
