using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using NutanixMcp.Data;

namespace NutanixMcp.Server.Tools;

[McpServerToolType]
public sealed class ListDocumentsTool(NutanixDbContext db)
{
    [McpServerTool(Name = "list_documents")]
    [Description("List Nutanix documents in the store, optionally filtered by category, product, or version.")]
    public async Task<object> ListAsync(
        [Description("Optional category filter (16 categories: ahv, aos, kb, files, ndb, kubernetes, flow, prism-central, prism-element, move, disaster-recovery, cloud-clusters, nki, pulse, lcm, hardware-replacement).")] string? category = null,
        [Description("Optional product filter.")] string? product = null,
        [Description("Optional version filter (e.g. '11.0', '7.5'). Useful with multi-version corpora to focus on one release line.")] string? version = null,
        [Description("Maximum rows returned. Defaults to 100, capped at 500.")] int limit = 100,
        CancellationToken ct = default)
    {
        int capped = Math.Clamp(limit, 1, 500);

        var q = db.Documents.AsNoTracking();
        if (category is not null)
        {
            q = q.Where(d => d.Category == category);
        }

        if (product is not null)
        {
            q = q.Where(d => d.Product == product);
        }

        if (version is not null)
        {
            q = q.Where(d => d.Version == version);
        }

        var rows = await q
            .OrderBy(d => d.Category)
            .ThenBy(d => d.Title)
            .Take(capped)
            .Select(d => new
            {
                id = d.Id,
                title = d.Title,
                category = d.Category,
                product = d.Product,
                version = d.Version,
                slug = d.Slug,
                pages = d.Pages,
                section_count = d.SectionCount,
                kb_number = d.KbNumber,
                ingested_at = d.IngestedAt,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new { count = rows.Count, documents = rows };
    }
}
