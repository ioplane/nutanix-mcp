using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using NutanixMcp.Core.Chunking;
using NutanixMcp.Core.Models;
using Pgvector;

namespace NutanixMcp.Data.Repositories;

public sealed class ChunkRepository(NutanixDbContext db) : IChunkRepository
{
    public async Task<int> BulkInsertAsync(IReadOnlyList<NutanixChunk> chunks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            return 0;
        }

        // Run the COPY on the DbContext's own connection so it participates in
        // whatever transaction the context has open. Opening a separate
        // datasource connection here — as this used to — put the COPY outside
        // the per-document ingest transaction, so a mid-document failure could
        // leave the leaves un-inserted while the document's pdf_sha256 was
        // already committed. When there is no ambient transaction (e.g. the
        // bulk-insert perf test) the connection is opened and closed here.
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        bool openedHere = conn.State != ConnectionState.Open;
        if (openedHere)
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
        }

        try
        {
            return await CopyChunksAsync(conn, chunks, ct).ConfigureAwait(false);
        }
        finally
        {
            if (openedHere)
            {
                await conn.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> CopyChunksAsync(
        NpgsqlConnection conn, IReadOnlyList<NutanixChunk> chunks, CancellationToken ct)
    {
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY chunks (document_id, chunk_index, content, raw_content, "
            + "section_title, section_level, page_start, page_end, token_count, "
            + "embedding, embedding_model, chunk_kind, parent_chunk_id) FROM STDIN BINARY",
            ct).ConfigureAwait(false);

        foreach (var c in chunks)
        {
            await writer.StartRowAsync(ct).ConfigureAwait(false);
            await writer.WriteAsync(c.DocumentId, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await writer.WriteAsync(c.ChunkIndex, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
            await writer.WriteAsync(c.Content, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await writer.WriteAsync(c.RawContent, NpgsqlDbType.Text, ct).ConfigureAwait(false);

            if (c.SectionTitle is null)
            {
                await writer.WriteNullAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteAsync(c.SectionTitle, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            }

            if (c.SectionLevel is null)
            {
                await writer.WriteNullAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteAsync(c.SectionLevel.Value, NpgsqlDbType.Smallint, ct).ConfigureAwait(false);
            }

            if (c.PageStart is null)
            {
                await writer.WriteNullAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteAsync(c.PageStart.Value, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
            }

            if (c.PageEnd is null)
            {
                await writer.WriteNullAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteAsync(c.PageEnd.Value, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
            }

            await writer.WriteAsync(c.TokenCount, NpgsqlDbType.Integer, ct).ConfigureAwait(false);

            if (c.Embedding is null)
            {
                await writer.WriteNullAsync(ct).ConfigureAwait(false);
            }
            else
            {
                Half[] halfArr = [.. c.Embedding.Select(static f => (Half)f)];
                await writer.WriteAsync(new HalfVector(halfArr), ct).ConfigureAwait(false);
            }

            if (c.EmbeddingModel is null)
            {
                await writer.WriteNullAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteAsync(c.EmbeddingModel, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            }

            await writer.WriteAsync(
                c.ChunkKind == ChunkKind.Parent ? "parent" : "leaf",
                NpgsqlDbType.Text, ct).ConfigureAwait(false);

            if (c.ParentChunkId is null)
            {
                await writer.WriteNullAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteAsync(c.ParentChunkId.Value, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
            }
        }

        await writer.CompleteAsync(ct).ConfigureAwait(false);
        return chunks.Count;
    }

    public async Task<IReadOnlyList<long>> SelectParentIdsAsync(
        string documentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        var ids = await db.Chunks
            .Where(c => c.DocumentId == documentId && c.ChunkKind == "parent")
            .OrderBy(c => c.ChunkIndex)
            .Select(c => c.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        return ids;
    }

    public Task<int> DeleteByDocumentAsync(string documentId, CancellationToken ct) =>
        db.Chunks.Where(c => c.DocumentId == documentId).ExecuteDeleteAsync(ct);

    public Task<int> CountAsync(CancellationToken ct) => db.Chunks.CountAsync(ct);
}
