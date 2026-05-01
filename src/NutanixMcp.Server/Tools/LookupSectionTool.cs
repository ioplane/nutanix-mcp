using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using NutanixMcp.Data;

namespace NutanixMcp.Server.Tools;

[McpServerToolType]
public sealed class LookupSectionTool(NutanixDbContext db)
{
    [McpServerTool(Name = "lookup_section")]
    [Description("Return the full concatenated raw text of a named section within a Nutanix document, in chunk order.")]
    public async Task<object> LookupAsync(
        [Description("Document ID (from search_docs or list_documents).")] string documentId,
        [Description("Exact section title.")] string sectionTitle,
        CancellationToken ct = default)
    {
        var chunks = await db.Chunks
            .AsNoTracking()
            .Where(c => c.DocumentId == documentId && c.SectionTitle == sectionTitle)
            .OrderBy(c => c.ChunkIndex)
            .Select(c => new { c.ChunkIndex, c.RawContent, c.PageStart, c.PageEnd })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (chunks.Count == 0)
        {
            return new
            {
                found = false,
                message = $"no chunks found for document {documentId} section '{sectionTitle}'",
            };
        }

        string body = string.Join("\n\n", chunks.Select(c => c.RawContent));
        return new
        {
            found = true,
            document_id = documentId,
            section_title = sectionTitle,
            chunk_count = chunks.Count,
            page_start = chunks[0].PageStart,
            page_end = chunks[^1].PageEnd,
            body,
        };
    }
}
