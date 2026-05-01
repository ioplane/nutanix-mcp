using System.Security.Cryptography;
using System.Text;
using NutanixMcp.Core.Catalog;
using NutanixMcp.Core.Chunking;
using NutanixMcp.Core.Models;
using NutanixMcp.Data.Repositories;
using NutanixMcp.Embedding;

namespace NutanixMcp.Cli.Ingest;

// Walks the catalog, loads each document, chunks it, embeds the leaves, and
// writes parents and leaves to the database in a single per-document
// transaction. Skip logic mirrors the upstream arista-mcp pipeline:
//
//   - Catalog-level skip: if the SHA-256 of the catalog hash matches the
//     `catalog_sha256` of the last successful `ingest_runs` row, the entire
//     run short-circuits.
//   - Per-document skip: when `Force` is false, a stem whose hash matches
//     the existing `documents.pdf_sha256` is left alone.
//
// Parent-then-leaf insert sequence:
//   1. Bulk-insert parents — Postgres assigns bigint ids on insert.
//   2. SELECT the parent ids back in chunk order (`SelectParentIdsAsync`).
//   3. Patch each leaf's `parent_chunk_id` from the index map.
//   4. Bulk-insert leaves.
public sealed class IngestService(
    IChunker chunker,
    IEmbedder embedder,
    IDocumentRepository documentRepo,
    IChunkRepository chunkRepo,
    IIngestRunRepository runRepo)
{
    public async Task<IngestResult> IngestAsync(
        IngestOptions options,
        IIngestProgress progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        var entries = CatalogReader.Read(new CatalogReader.ReadOptions
        {
            CatalogBaseDir = options.CatalogBaseDir,
        });

        if (!string.IsNullOrEmpty(options.Category))
        {
            entries = entries
                .Where(e => string.Equals(e.Category, options.Category, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (options.Limit < entries.Count)
        {
            entries = entries.Take(options.Limit).ToList();
        }

        var catalogSha = ComputeCatalogSha(entries);

        if (!options.Force)
        {
            var lastSuccess = await runRepo
                .GetLastSuccessfulCatalogSha256Async(ct)
                .ConfigureAwait(false);
            if (string.Equals(lastSuccess, catalogSha, StringComparison.Ordinal))
            {
                progress.Start(entries.Count);
                progress.Finish(succeeded: 0, skipped: entries.Count, failed: 0);
                return new IngestResult(
                    IngestRunId: 0,
                    Status: "skipped-catalog",
                    DocsTotal: entries.Count,
                    DocsSkipped: entries.Count,
                    DocsUpserted: 0,
                    ChunksUpserted: 0,
                    ErrorMsg: null);
            }
        }

        progress.Start(entries.Count);
        var run = await runRepo.StartAsync(catalogSha, ct).ConfigureAwait(false);
        var skipped = 0;
        var upserted = 0;
        var failed = 0;
        var chunkTotal = 0;
        string? lastError = null;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var docHash = ComputeDocumentSha(entry);
                if (!options.Force)
                {
                    var existing = await documentRepo
                        .GetPdfSha256Async(entry.Id, ct)
                        .ConfigureAwait(false);
                    if (string.Equals(existing, docHash, StringComparison.Ordinal))
                    {
                        skipped++;
                        progress.Document(entry.Id, "skip", 0);
                        continue;
                    }
                }

                var loaded = await DocumentLoader
                    .LoadAsync(entry, options.CatalogBaseDir, ct)
                    .ConfigureAwait(false);

                var chunkSet = chunker.Chunk(loaded.Metadata.Title, loaded.Sections);
                if (chunkSet.Leaves.Count == 0)
                {
                    skipped++;
                    progress.Document(entry.Id, "no-leaves", 0);
                    continue;
                }

                if (options.DryRun)
                {
                    upserted++;
                    chunkTotal += chunkSet.Leaves.Count;
                    progress.Document(entry.Id, "dry-run", chunkSet.Leaves.Count);
                    continue;
                }

                var docWithHash = loaded.Metadata with { PdfSha256 = docHash };
                await documentRepo.UpsertAsync(docWithHash, ct).ConfigureAwait(false);

                // Replace existing chunks before reinsert. The cascade FK on
                // chunks.document_id makes this a single ExecuteDeleteAsync.
                await chunkRepo.DeleteByDocumentAsync(entry.Id, ct).ConfigureAwait(false);

                // Parents first so we can read back their generated bigint ids.
                var parentRows = MaterialiseParents(entry.Id, chunkSet);
                await chunkRepo.BulkInsertAsync(parentRows, ct).ConfigureAwait(false);
                var parentIds = await chunkRepo
                    .SelectParentIdsAsync(entry.Id, ct)
                    .ConfigureAwait(false);

                var leafRows = await MaterialiseLeavesAsync(
                    entry.Id, chunkSet, parentIds, embedder, ct).ConfigureAwait(false);
                await chunkRepo.BulkInsertAsync(leafRows, ct).ConfigureAwait(false);

                upserted++;
                chunkTotal += chunkSet.Leaves.Count;
                progress.Document(entry.Id, "ok", chunkSet.Leaves.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       and not OutOfMemoryException
                                       and not StackOverflowException)
            {
                failed++;
                lastError = ex.Message;
                progress.Document(entry.Id, "error: " + ex.GetType().Name, 0);
            }
        }

        var status = failed == 0 ? "success" : "partial";
        await runRepo.FinishAsync(
            run.Id,
            status,
            entries.Count,
            skipped,
            upserted,
            chunkTotal,
            lastError,
            ct).ConfigureAwait(false);

        progress.Finish(upserted, skipped, failed);

        return new IngestResult(
            IngestRunId: run.Id,
            Status: status,
            DocsTotal: entries.Count,
            DocsSkipped: skipped,
            DocsUpserted: upserted,
            ChunksUpserted: chunkTotal,
            ErrorMsg: lastError);
    }

    private static List<NutanixChunk> MaterialiseParents(string docId, ChunkSet set)
    {
        var rows = new List<NutanixChunk>(set.Parents.Count);
        for (var i = 0; i < set.Parents.Count; i++)
        {
            var p = set.Parents[i];
            rows.Add(new NutanixChunk
            {
                DocumentId = docId,
                ChunkIndex = i,
                Content = p.Content,
                RawContent = p.RawContent,
                SectionTitle = p.SectionTitle,
                SectionLevel = p.SectionLevel,
                PageStart = p.PageStart,
                PageEnd = p.PageEnd,
                TokenCount = p.TokenCount,
                ChunkKind = ChunkKind.Parent,
                Embedding = null,
            });
        }

        return rows;
    }

    private static async Task<List<NutanixChunk>> MaterialiseLeavesAsync(
        string docId,
        ChunkSet set,
        IReadOnlyList<long> parentIds,
        IEmbedder embedder,
        CancellationToken ct)
    {
        var leaves = set.Leaves;
        if (leaves.Count == 0)
        {
            return [];
        }

        var contents = leaves.Select(l => l.Content).ToList();
        var embeddings = await embedder
            .EmbedAsync(contents, isQuery: false, ct)
            .ConfigureAwait(false);

        var rows = new List<NutanixChunk>(leaves.Count);
        // Leaf chunk_index continues after the parents so the unique index
        // (document_id, chunk_index) is satisfied.
        var indexBase = set.Parents.Count;
        for (var i = 0; i < leaves.Count; i++)
        {
            var l = leaves[i];
            long? parentId = null;
            if (l.ParentIndex is int pi && pi >= 0 && pi < parentIds.Count)
            {
                parentId = parentIds[pi];
            }

            rows.Add(new NutanixChunk
            {
                DocumentId = docId,
                ChunkIndex = indexBase + i,
                Content = l.Content,
                RawContent = l.RawContent,
                SectionTitle = l.SectionTitle,
                SectionLevel = l.SectionLevel,
                PageStart = l.PageStart,
                PageEnd = l.PageEnd,
                TokenCount = l.TokenCount,
                ChunkKind = ChunkKind.Leaf,
                ParentChunkId = parentId,
                Embedding = embeddings[i],
            });
        }

        return rows;
    }

    private static string ComputeCatalogSha(IReadOnlyList<CatalogEntry> entries)
    {
        var sb = new StringBuilder(entries.Count * 64);
        foreach (var e in entries)
        {
            sb.Append(e.Id).Append('\t').Append(e.SizeBytes).Append('\n');
        }

        return Sha256(sb.ToString());
    }

    private static string ComputeDocumentSha(CatalogEntry e)
    {
        // The catalog has no PDF hash, so we hash the file size plus the stem
        // — sufficient to detect a re-converted document on the same machine.
        var sb = new StringBuilder(64).Append(e.Id).Append('\t').Append(e.SizeBytes);
        return Sha256(sb.ToString());
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes);
    }
}
