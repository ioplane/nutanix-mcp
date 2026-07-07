using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using NutanixMcp.Data;
using NutanixMcp.Data.Repositories;

namespace NutanixMcp.Server.Tools;

[McpServerToolType]
public sealed class GetStatusTool(NutanixDbContext db, IIngestRunRepository runRepo)
{
    [McpServerTool(Name = "get_status")]
    [Description("Health and store stats: document/chunk counts, last ingest run, extension versions.")]
    public async Task<object> GetAsync(CancellationToken ct = default)
    {
        // Derive health from whether the store is actually reachable. A failed
        // count (DB down, schema missing, connection refused) reports
        // healthy=false with a reason rather than a hardcoded true.
        int docCount;
        int chunkCount;
        try
        {
            docCount = await db.Documents.CountAsync(ct).ConfigureAwait(false);
            chunkCount = await db.Chunks.CountAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new
            {
                healthy = false,
                reason = ex.Message,
                documents = (int?)null,
                chunks = (int?)null,
                last_run = (object?)null,
            };
        }

        var lastRun = await runRepo.GetLastAsync(ct).ConfigureAwait(false);

        return new
        {
            healthy = true,
            reason = (string?)null,
            documents = docCount,
            chunks = chunkCount,
            last_run = lastRun is null ? null : new
            {
                id = lastRun.Id,
                status = lastRun.Status,
                started_at = lastRun.StartedAt,
                finished_at = lastRun.FinishedAt,
                docs_total = lastRun.DocsTotal,
                docs_skipped = lastRun.DocsSkipped,
                docs_upserted = lastRun.DocsUpserted,
                chunks_upserted = lastRun.ChunksUpserted,
                error = lastRun.ErrorMsg,
            },
        };
    }
}
