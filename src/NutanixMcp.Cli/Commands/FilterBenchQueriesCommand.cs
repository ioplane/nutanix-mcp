using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NutanixMcp.Cli.Configuration;
using NutanixMcp.Core.Settings;
using NutanixMcp.Data;
using NutanixMcp.Embedding;
using NutanixMcp.Server.Retrieval;
using Spectre.Console;

namespace NutanixMcp.Cli.Commands;

// Sprint 006-extension — fairness filter for LLM-generated bench queries.
// Mirrors arista-mcp's validate-bench-queries verb. Reads a JSONL of LLM
// candidates with `source_chunk_id`, drops queries whose source is not in
// the current retriever's top-K, and enriches survivors with the retriever's
// top-10 chunk_ids so a downstream multi-positive annotation pass can label
// which top-10 chunks would be reasonable answers.
//
// Output schema per JSONL row: query, source_chunk_id, source_doc_id,
// source_doc_title, source_section_title, source_category,
// generation_model, retriever_rank, retriever_top_10_chunk_ids.
public static class FilterBenchQueriesCommand
{
    private static readonly JsonSerializerOptions s_readOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions s_writeOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static Command Build()
    {
        var input = new Option<string>("--input")
        {
            Description = "JSONL of LLM-generated candidates with source_chunk_id",
            Required = true,
        };
        var output = new Option<string>("--output")
        {
            Description = "JSONL output: surviving queries + retriever top-10",
            Required = true,
        };
        var topK = new Option<int>("--top-k")
        {
            Description = "Keep queries where source_chunk_id is in top-K retrieval",
            DefaultValueFactory = _ => 20,
        };
        var poolSize = new Option<int>("--candidate-pool-size")
        {
            Description = "Candidates pulled from each retrieval side before fusion",
            DefaultValueFactory = _ => 50,
        };
        var rerankN = new Option<int>("--rerank-top-n")
        {
            Description = "Cap on candidates fed to the cross-encoder",
            DefaultValueFactory = _ => 30,
        };

        var cmd = new Command(
            "filter-bench-queries",
            "Drop LLM-generated bench queries whose source_chunk_id is not in the current retriever's top-K. Enrich survivors with the top-10 chunk_ids for multi-positive annotation.")
        {
            input, output, topK, poolSize, rerankN,
        };

        cmd.SetAction(async (pr, ct) =>
        {
            var settings = CliConfiguration.Load();
            string inPath = pr.GetValue(input)!;
            string outPath = pr.GetValue(output)!;
            int k = pr.GetValue(topK);
            int pool = pr.GetValue(poolSize);
            int rerankTopN = pr.GetValue(rerankN);

            await using var ds = DataSourceFactory.Build(settings.ConnectionString);
            var dbOpts = new DbContextOptionsBuilder<NutanixDbContext>()
                .UseNpgsql(ds, n => n.UseVector())
                .Options;
            await using var db = new NutanixDbContext(dbOpts);

            using var embedder = BuildEmbedder(settings);
            using var reranker = RerankerFamilyDetector.Build(
                ModelPaths.RerankerDir(settings.ModelsDir),
                settings.Gpu);

            var retriever = new HybridRetriever(embedder, reranker, ds);
            var console = AnsiConsole.Console;

            string? outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            int kept = 0;
            int dropped = 0;
            int idx = 0;

            await using var writer = new StreamWriter(outPath, append: false);
            foreach (string line in File.ReadLines(inPath))
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                idx++;
                LlmCandidate? rec;
                try
                {
                    rec = JsonSerializer.Deserialize<LlmCandidate>(line, s_readOpts);
                }
                catch (JsonException)
                {
                    rec = null;
                }
                if (rec is null || rec.SourceChunkId <= 0 || string.IsNullOrWhiteSpace(rec.Query))
                {
                    dropped++;
                    continue;
                }

                var resp = await retriever.SearchAsync(
                    rec.Query,
                    new RetrievalOptions
                    {
                        Limit = k,
                        CandidatePoolSize = pool,
                        RerankTopN = rerankTopN,
                    },
                    ct).ConfigureAwait(false);

                var top = resp.Results.Select(r => r.ChunkId).ToList();
                int rank = top.IndexOf(rec.SourceChunkId);
                if (rank < 0)
                {
                    dropped++;
                    continue;
                }

                var enriched = rec with
                {
                    RetrieverRank = rank,
                    RetrieverTop10ChunkIds = [.. top.Take(10)],
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(enriched, s_writeOpts)).ConfigureAwait(false);
                kept++;

                if (idx % 25 == 0)
                {
                    console.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"[dim]{idx}[/] kept={kept} dropped={dropped}");
                }
            }

            console.WriteLine();
            console.MarkupLineInterpolated(
                CultureInfo.InvariantCulture,
                $"[bold]summary[/]: candidates={idx} kept={kept} dropped={dropped} survival={kept * 100.0 / Math.Max(1, idx):F1}%");
            return kept > 0 ? 0 : 1;
        });

        return cmd;
    }

    private static OnnxEmbedder BuildEmbedder(NutanixMcpSettings settings)
    {
        return new OnnxEmbedder(new EmbeddingOptions
        {
            ModelPath = ModelPaths.EmbedderModel(settings),
            VocabPath = ModelPaths.EmbedderVocab(settings),
            Gpu = settings.Gpu,
        });
    }

    public sealed record LlmCandidate
    {
        [System.Text.Json.Serialization.JsonPropertyName("query")]
        public required string Query { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("source_chunk_id")]
        public required long SourceChunkId { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("source_doc_id")]
        public string? SourceDocId { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("source_doc_title")]
        public string? SourceDocTitle { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("source_section_title")]
        public string? SourceSectionTitle { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("source_category")]
        public string? SourceCategory { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("generation_model")]
        public string? GenerationModel { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("retriever_rank")]
        public int? RetrieverRank { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("retriever_top_10_chunk_ids")]
        public IReadOnlyList<long>? RetrieverTop10ChunkIds { get; init; }
    }
}
