using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using NutanixMcp.Data;

namespace NutanixMcp.Server.Tools;

[McpServerToolType]
public sealed class GetKbArticleTool(NutanixDbContext db)
{
    [McpServerTool(Name = "get_kb_article")]
    [Description("Direct O(1) lookup of a Nutanix KB article by its numeric KB-NNNNN identifier. Returns the document metadata and the full body (concatenated chunks in order).")]
    public async Task<object> GetAsync(
        [Description("KB number — the integer N from a KB-NNNNN slug. Accepts plain integers, prefix 'KB-' is stripped if present.")] string kbNumber,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kbNumber);

        var trimmed = kbNumber.Trim();
        if (trimmed.StartsWith("KB-", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[3..];
        }
        else if (trimmed.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        if (!int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var kb))
        {
            return new { found = false, message = $"could not parse KB number from '{kbNumber}'" };
        }

        var doc = await db.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.KbNumber == kb, ct)
            .ConfigureAwait(false);

        if (doc is null)
        {
            return new { found = false, kb_number = kb };
        }

        var chunks = await db.Chunks
            .AsNoTracking()
            .Where(c => c.DocumentId == doc.Id && c.ChunkKind == "leaf")
            .OrderBy(c => c.ChunkIndex)
            .Select(c => c.RawContent)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var tags = JsonSerializer.Deserialize<string[]>(doc.TagsJson) ?? [];

        return new
        {
            found = true,
            kb_number = doc.KbNumber,
            id = doc.Id,
            title = doc.Title,
            url = doc.Url,
            category = doc.Category,
            product = doc.Product,
            version = doc.Version,
            tags,
            chunk_count = chunks.Count,
            body = string.Join("\n\n", chunks),
        };
    }
}
