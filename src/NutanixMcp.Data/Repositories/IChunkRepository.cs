using NutanixMcp.Core.Models;

namespace NutanixMcp.Data.Repositories;

public interface IChunkRepository
{
    Task<int> BulkInsertAsync(IReadOnlyList<NutanixChunk> chunks, CancellationToken ct);
    Task<IReadOnlyList<long>> SelectParentIdsAsync(string documentId, CancellationToken ct);
    Task<int> DeleteByDocumentAsync(string documentId, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
}
