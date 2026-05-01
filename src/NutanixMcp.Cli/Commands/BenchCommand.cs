using System.CommandLine;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NutanixMcp.Cli.Bench;
using NutanixMcp.Cli.Configuration;
using NutanixMcp.Core.Bench;
using NutanixMcp.Core.Settings;
using NutanixMcp.Data;
using NutanixMcp.Embedding;
using NutanixMcp.Server.Retrieval;
using Spectre.Console;

namespace NutanixMcp.Cli.Commands;

public static class BenchCommand
{
    public static Command Build()
    {
        var queries = new Option<string>("--queries")
        {
            Description = "Path to the bench query JSON fixture.",
            Required = true,
        };

        var history = new Option<string>("--history")
        {
            Description = "Append-only history file (one BenchRun per line).",
            DefaultValueFactory = _ => "tests/fixtures/bench-history.jsonl",
        };

        var label = new Option<string?>("--label")
        {
            Description = "Run label written to the history row. Defaults to short git sha or 'local'.",
        };

        var limit = new Option<int>("--limit")
        {
            Description = "Cap on results returned per query.",
            DefaultValueFactory = _ => 10,
        };

        var poolSize = new Option<int>("--candidate-pool-size")
        {
            Description = "Candidates pulled from each retrieval side before fusion.",
            DefaultValueFactory = _ => 50,
        };

        var rerankN = new Option<int>("--rerank-top-n")
        {
            Description = "Cap on candidates fed to the cross-encoder.",
            DefaultValueFactory = _ => 30,
        };

        var rrfK = new Option<int>("--rrf-k")
        {
            Description = "RRF k constant.",
            DefaultValueFactory = _ => 60,
        };

        var smokeFloor = new Option<double>("--smoke-top10-floor")
        {
            Description = "Smoke-gate floor for top-10 hit rate. Below this, the verb exits 1.",
            DefaultValueFactory = _ => 0.80,
        };

        var noHistory = new Option<bool>("--no-history")
        {
            Description = "Skip the history append (debug aid).",
        };

        var cmd = new Command("bench", "Run the bench v2 fixture against the live retriever and append a row to bench-history.jsonl")
        {
            queries,
            history,
            label,
            limit,
            poolSize,
            rerankN,
            rrfK,
            smokeFloor,
            noHistory,
        };

        cmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            var settings = CliConfiguration.Load();
            var queriesPath = pr.GetValue(queries) ?? throw new InvalidOperationException("--queries is required");
            var historyPath = pr.GetValue(history) ?? "tests/fixtures/bench-history.jsonl";
            var runLabel = pr.GetValue(label) ?? ResolveDefaultLabel();
            var lim = pr.GetValue(limit);
            var pool = pr.GetValue(poolSize);
            var rerankTopN = pr.GetValue(rerankN);
            var k = pr.GetValue(rrfK);
            var floor = pr.GetValue(smokeFloor);
            var skipHistory = pr.GetValue(noHistory);

            await using var dataSource = DataSourceFactory.Build(settings.ConnectionString);
            var dbOpts = new DbContextOptionsBuilder<NutanixDbContext>()
                .UseNpgsql(dataSource, n => n.UseVector())
                .Options;
            await using var db = new NutanixDbContext(dbOpts);

            using var embedder = BuildEmbedder(settings);
            using var reranker = RerankerFamilyDetector.Build(
                ModelPaths.RerankerDir(settings.ModelsDir),
                settings.Gpu);

            var retriever = new HybridRetriever(embedder, reranker, dataSource);
            var harness = new BenchHarness(retriever, new BenchHarnessOptions
            {
                Limit = lim,
                CandidatePoolSize = pool,
                RerankTopN = rerankTopN,
                RrfK = k,
            });

            var queryRows = await BenchQueryParser.LoadAsync(queriesPath, ct).ConfigureAwait(false);
            var progress = new SpectreBenchProgress(AnsiConsole.Console);
            var outcome = await harness.RunAsync(queryRows, progress, ct).ConfigureAwait(false);

            var run = new BenchRun
            {
                Timestamp = DateTimeOffset.UtcNow,
                Label = runLabel,
                GitSha = ResolveGitSha(),
                Queries = outcome.PerQuery.Count,
                Top1 = outcome.Top1,
                Top5 = outcome.Top5,
                Top10 = outcome.Top10,
                P50Ms = outcome.P50Ms,
                P95Ms = outcome.P95Ms,
                Embedder = settings.EmbeddingModel,
                Reranker = settings.RerankerModel,
                RrfK = k,
                RerankTopN = rerankTopN,
                CandidatePoolSize = pool,
                Limit = lim,
                ByCategory = SummariseByCategory(outcome.PerQuery, q => q.Category),
                ByDifficulty = SummariseByCategory(outcome.PerQuery, q => q.Difficulty),
            };

            AnsiConsole.MarkupLineInterpolated(
                CultureInfo.InvariantCulture,
                $"[bold]results[/]: top_1=[green]{outcome.Top1:P1}[/] top_5=[green]{outcome.Top5:P1}[/] top_10=[green]{outcome.Top10:P1}[/] p50={outcome.P50Ms:F0}ms p95={outcome.P95Ms:F0}ms");

            if (!skipHistory)
            {
                await BenchHistoryWriter.AppendAsync(historyPath, run, ct).ConfigureAwait(false);
                AnsiConsole.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"[grey]history appended to {historyPath}[/]");
            }

            if (outcome.Top10 < floor)
            {
                AnsiConsole.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"[red]smoke-gate failed[/]: top_10 {outcome.Top10:P1} < floor {floor:P1}");
                return 1;
            }

            return 0;
        });

        return cmd;
    }

    private static OnnxEmbedder BuildEmbedder(NutanixMcpSettings settings) =>
        new(new EmbeddingOptions
        {
            ModelPath = ModelPaths.EmbedderModel(settings),
            VocabPath = ModelPaths.EmbedderVocab(settings),
            Gpu = settings.Gpu,
        });

    private static string ResolveDefaultLabel()
    {
        var sha = ResolveGitSha();
        return string.IsNullOrEmpty(sha) ? "local" : sha;
    }

    private static string? ResolveGitSha()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --short HEAD",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var sha = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return sha.Length == 0 ? null : sha;
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                   and not OutOfMemoryException
                                   and not StackOverflowException)
        {
            return null;
        }
    }

    private static Dictionary<string, BenchCategoryStats>? SummariseByCategory(
        IReadOnlyList<BenchPerQuery> rows,
        Func<BenchQuery, string?> selector)
    {
        var groups = rows
            .Where(p => !string.IsNullOrWhiteSpace(selector(p.Query)))
            .GroupBy(p => selector(p.Query)!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => new BenchCategoryStats(
                    Queries: g.Count(),
                    Top1: g.Count(p => BenchScoring.IsHit(p.FirstHitRank, 1)) / (double)g.Count(),
                    Top5: g.Count(p => BenchScoring.IsHit(p.FirstHitRank, 5)) / (double)g.Count(),
                    Top10: g.Count(p => BenchScoring.IsHit(p.FirstHitRank, 10)) / (double)g.Count()),
                StringComparer.Ordinal);
        return groups.Count == 0 ? null : groups;
    }
}
