using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using NutanixMcp.Data;

namespace NutanixMcp.Server.Tools;

[McpServerToolType]
public sealed class ListCategoriesTool(NutanixDbContext db)
{
    [McpServerTool(Name = "list_categories")]
    [Description("List all Nutanix document categories in the store with document and chunk counts. Useful as a discovery step before search_docs / list_documents to know which category filters are populated.")]
    public async Task<object> ListAsync(CancellationToken ct = default)
    {
        // Two cheap aggregates joined client-side rather than a single grouped
        // join — keeps the query plan trivial and the result ordering stable.
        var docCounts = await db.Documents
            .AsNoTracking()
            .GroupBy(d => d.Category)
            .Select(g => new { category = g.Key, documents = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var chunkCounts = await db.Chunks
            .AsNoTracking()
            .Where(c => c.ChunkKind == "leaf")
            .GroupBy(c => c.Document.Category)
            .Select(g => new { category = g.Key, chunks = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byCat = chunkCounts.ToDictionary(x => x.category, x => x.chunks, StringComparer.Ordinal);

        var rows = docCounts
            .OrderBy(x => x.category, StringComparer.Ordinal)
            .Select(x => new
            {
                x.category,
                x.documents,
                chunks = byCat.TryGetValue(x.category, out int c) ? c : 0,
            })
            .ToList();

        return new { count = rows.Count, categories = rows };
    }
}
