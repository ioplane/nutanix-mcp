using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NutanixMcp.Cli.Configuration;
using NutanixMcp.Core.Bench;
using NutanixMcp.Data;
using NutanixMcp.Data.Entities;
using Spectre.Console;

namespace NutanixMcp.Cli.Commands;

// Sprint 006.5 — re-pin expect_chunk_ids in a bench fixture by querying the
// live database for (expect_doc, expect_section). Decouples fixture authoring
// from chunk-id stability: a full reingest reassigns chunk_ids, but the
// (doc, section) anchor is stable across reingests.
public static class ValidateBenchQueriesCommand
{
    private static readonly JsonSerializerOptions s_readOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions s_writeOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static Command Build()
    {
        var queries = new Option<string>("--queries")
        {
            Description = "Path to the bench query JSON fixture.",
            Required = true,
        };

        var write = new Option<bool>("--write")
        {
            Description = "Write the resolved chunk_ids back to the fixture. Without this flag the verb runs in dry-run mode and only reports what would change.",
        };

        var failOnUnresolved = new Option<bool>("--fail-on-unresolved")
        {
            Description = "Exit non-zero when one or more queries cannot be matched to any chunk. Use in CI to catch a fixture that has drifted past auto-repair.",
        };

        var cmd = new Command(
            "validate-bench-queries",
            "Re-pin expect_chunk_ids by matching (expect_doc, expect_section) against the live DB.")
        {
            queries,
            write,
            failOnUnresolved,
        };

        cmd.SetAction(async (pr, ct) =>
        {
            var settings = CliConfiguration.Load();
            string queriesPath = pr.GetValue(queries) ?? throw new InvalidOperationException("--queries is required");
            bool doWrite = pr.GetValue(write);
            bool failHard = pr.GetValue(failOnUnresolved);

            await using var dataSource = DataSourceFactory.Build(settings.ConnectionString);
            var dbOpts = new DbContextOptionsBuilder<NutanixDbContext>()
                .UseNpgsql(dataSource, n => n.UseVector())
                .Options;
            await using var db = new NutanixDbContext(dbOpts);

            var file = await LoadRawAsync(queriesPath, ct).ConfigureAwait(false);
            var console = AnsiConsole.Console;

            int updated = 0;
            int unchanged = 0;
            int unresolved = 0;
            var rewritten = new List<BenchQuery>(file.Queries.Count);

            foreach (var q in file.Queries)
            {
                if (string.IsNullOrWhiteSpace(q.ExpectDoc) || string.IsNullOrWhiteSpace(q.ExpectSection))
                {
                    console.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"[yellow]skip[/] {q.QueryId}: missing expect_doc or expect_section anchor");
                    rewritten.Add(q);
                    unchanged++;
                    continue;
                }

                var resolved = await ResolveChunkIdsAsync(db, q.ExpectDoc, q.ExpectSection, ct)
                    .ConfigureAwait(false);

                if (resolved.Count == 0)
                {
                    console.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"[red]miss[/] {q.QueryId}: no chunks found for ({q.ExpectDoc}, {q.ExpectSection})");
                    rewritten.Add(q);
                    unresolved++;
                    continue;
                }

                bool sameAsBefore = resolved.Count == q.ExpectChunkIds.Count
                    && resolved.Order().SequenceEqual(q.ExpectChunkIds.Order());

                if (sameAsBefore)
                {
                    rewritten.Add(q);
                    unchanged++;
                    continue;
                }

                console.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"[green]update[/] {q.QueryId}: {q.ExpectChunkIds.Count} -> {resolved.Count} chunk_ids");
                rewritten.Add(q with { ExpectChunkIds = resolved });
                updated++;
            }

            console.WriteLine();
            console.MarkupLineInterpolated(
                CultureInfo.InvariantCulture,
                $"[bold]summary[/]: {updated} updated, {unchanged} unchanged, {unresolved} unresolved (n={file.Queries.Count})");

            if (doWrite && updated > 0)
            {
                var newFile = file with { Queries = rewritten };
                await WriteAsync(queriesPath, newFile, ct).ConfigureAwait(false);
                console.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"[bold green]wrote[/] {queriesPath}");
            }
            else if (updated > 0)
            {
                console.MarkupLine("[yellow]dry-run[/]: pass --write to persist changes");
            }

            return failHard && unresolved > 0 ? 1 : 0;
        });

        return cmd;
    }

    // Tolerant load — does NOT call BenchQueryParser.Validate, because the
    // whole point of this verb is to operate on a fixture that has empty
    // expect_chunk_ids (the state right after fresh authoring or right after
    // a reingest invalidates pinned ids).
    private static async Task<BenchQueryFile> LoadRawAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<BenchQueryFile>(stream, s_readOpts, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException($"bench query file is empty: {path}");
    }

    private static async Task<IReadOnlyList<long>> ResolveChunkIdsAsync(
        NutanixDbContext db,
        string expectDoc,
        string expectSection,
        CancellationToken ct)
    {
        // Match the doc anchor against either Document.Slug or Document.Id —
        // bench fixtures occasionally name docs by the file stem rather than
        // the canonical slug. Both are unique so the union is safe.
        // Match the section anchor against Chunk.SectionTitle, leaves only
        // (parent rows are not what the bench scores against).
        var ids = await db.Chunks
            .AsNoTracking()
            .Where(c => c.ChunkKind == "leaf")
            .Where(c => c.SectionTitle == expectSection)
            .Where(c => c.Document.Slug == expectDoc || c.Document.Id == expectDoc)
            .OrderBy(c => c.ChunkIndex)
            .Select(c => c.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return ids;
    }

    private static async Task WriteAsync(string path, BenchQueryFile file, CancellationToken ct)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, file, s_writeOpts, ct).ConfigureAwait(false);
    }
}
