using Microsoft.EntityFrameworkCore.Storage;

namespace NutanixMcp.Data.Repositories;

// Transaction + change-tracker seam over the shared NutanixDbContext. The ingest
// pipeline uses this to run a whole per-document unit (document upsert + chunk
// delete + parent/leaf COPYs) inside ONE transaction on the context's own
// connection, so a mid-document failure rolls the pdf_sha256 back instead of
// committing a half-indexed document that re-ingest would then skip forever.
public interface IUnitOfWork
{
    // Begins a transaction on the DbContext's connection. Chunk COPYs run on that
    // same connection (see ChunkRepository.BulkInsertAsync) so they participate in
    // this transaction. Rolls back automatically if disposed before CommitAsync.
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct);

    // Drops all tracked entities. Called after each document so the change tracker
    // does not accumulate one DocumentEntity per document (O(N^2) DetectChanges).
    void ClearChangeTracker();
}
