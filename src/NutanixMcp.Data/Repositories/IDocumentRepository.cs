using NutanixMcp.Core.Models;

namespace NutanixMcp.Data.Repositories;

public interface IDocumentRepository
{
    Task UpsertAsync(NutanixDocument doc, CancellationToken ct);
    Task<NutanixDocument?> GetByIdAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<string>> GetAllIdsAsync(CancellationToken ct);
    Task<string?> GetPdfSha256Async(string id, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task<NutanixDocument?> GetByKbNumberAsync(int kbNumber, CancellationToken ct);
}
