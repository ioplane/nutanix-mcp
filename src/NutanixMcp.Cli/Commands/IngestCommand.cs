using System.CommandLine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NutanixMcp.Cli.Configuration;
using NutanixMcp.Cli.Ingest;
using NutanixMcp.Core.Chunking;
using NutanixMcp.Data;
using NutanixMcp.Data.Repositories;
using NutanixMcp.Embedding;
using Spectre.Console;

namespace NutanixMcp.Cli.Commands;

public static class IngestCommand
{
    public static Command Build()
    {
        var catalog = new Option<string>("--catalog")
        {
            Description = "Catalog base directory containing md/ and json/ subdirectories.",
            Required = true,
        };

        var category = new Option<string?>("--category")
        {
            Description = "Optional category filter (e.g. 'ahv', 'kb', 'hardware-replacement').",
        };

        var force = new Option<bool>("--force")
        {
            Description = "Re-ingest every document even if its sha matches the existing row.",
        };

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Walk the catalog and chunk every doc but do not write to the database.",
        };

        var limit = new Option<int>("--limit")
        {
            Description = "Stop after this many catalog entries (debug aid).",
            DefaultValueFactory = _ => int.MaxValue,
        };

        var verbose = new Option<bool>("--verbose")
        {
            Description = "Per-document trace output.",
        };

        var cmd = new Command("ingest", "Walk the catalog and populate the documents and chunks tables")
        {
            catalog,
            category,
            force,
            dryRun,
            limit,
            verbose,
        };

        cmd.SetAction(async (pr, ct) =>
        {
            var settings = CliConfiguration.Load();
            var opts = new IngestOptions
            {
                CatalogBaseDir = pr.GetValue(catalog) ?? throw new InvalidOperationException("--catalog is required"),
                Category = pr.GetValue(category),
                Force = pr.GetValue(force),
                DryRun = pr.GetValue(dryRun),
                Limit = pr.GetValue(limit),
                Verbose = pr.GetValue(verbose),
            };

            await using var dataSource = DataSourceFactory.Build(settings.ConnectionString);
            var dbOptions = new DbContextOptionsBuilder<NutanixDbContext>()
                .UseNpgsql(dataSource, n => n.UseVector())
                .Options;
            await using var db = new NutanixDbContext(dbOptions);

            using var embedder = BuildEmbedder(settings);

            var documentRepo = new DocumentRepository(db, TimeProvider.System);
            var chunkRepo = new ChunkRepository(db);
            var runRepo = new IngestRunRepository(db, TimeProvider.System);
            var unitOfWork = new UnitOfWork(db);

            var chunker = new SectionAwareChunker(new ChunkingOptions
            {
                TargetTokens = settings.ChunkTargetTokens,
                MaxTokens = settings.ChunkMaxTokens,
                OverlapTokens = settings.ChunkOverlapTokens,
                MinTokens = settings.ChunkMinTokens,
            });

            var service = new IngestService(chunker, embedder, documentRepo, chunkRepo, runRepo, unitOfWork);
            var progress = new SpectreIngestProgress(AnsiConsole.Console);

            try
            {
                var result = await service.IngestAsync(opts, progress, ct).ConfigureAwait(false);
                AnsiConsole.MarkupLineInterpolated(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"[bold]run #{result.IngestRunId}[/] status=[green]{result.Status}[/] docs={result.DocsTotal} skip={result.DocsSkipped} ok={result.DocsUpserted} chunks={result.ChunksUpserted}");
                return result.Status switch
                {
                    "success" or "skipped-catalog" => 0,
                    "partial" => 2,
                    _ => 3,
                };
            }
            catch (OperationCanceledException)
            {
                return 130;
            }
        });

        return cmd;
    }

    private static OnnxEmbedder BuildEmbedder(Core.Settings.NutanixMcpSettings settings)
    {
        return new OnnxEmbedder(new EmbeddingOptions
        {
            ModelPath = Core.Settings.ModelPaths.EmbedderModel(settings),
            VocabPath = Core.Settings.ModelPaths.EmbedderVocab(settings),
            Gpu = settings.Gpu,
        });
    }
}
