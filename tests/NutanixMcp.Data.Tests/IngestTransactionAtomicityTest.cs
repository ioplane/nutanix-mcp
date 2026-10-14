using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NutanixMcp.Core.Models;
using NutanixMcp.Data.Repositories;
using NutanixMcp.Data.Tests.Fixtures;
using Xunit;

namespace NutanixMcp.Data.Tests;

// Proves the per-document ingest unit is transactional: the document upsert
// (EF) and the chunk COPY (raw Npgsql) run on the SAME connection inside one
// transaction, so a rollback undoes both. Before the fix the COPY ran on its
// own datasource connection outside the transaction, so a mid-document failure
// could leave the pdf_sha256 committed while the leaves never landed.
[Collection("Pgvector")]
public class IngestTransactionAtomicityTest(PgvectorFixture fx)
{
    private static NutanixDocument Doc(string id) => new()
    {
        Id = id,
        Url = "u",
        Category = "ahv",
        Title = "T",
        Slug = "s",
        MdPath = "m",
        JsonPath = "j",
        PdfSha256 = "deadbeef",
    };

    private static List<NutanixChunk> Chunks(string docId, int n) =>
        [.. Enumerable.Range(0, n).Select(i => new NutanixChunk
        {
            DocumentId = docId,
            ChunkIndex = i,
            Content = $"c{i}",
            RawContent = $"c{i}",
            TokenCount = 1,
            Embedding = [.. Enumerable.Repeat(0.1f, 1024)],
        })];

    [Fact]
    public async Task RolledBackTransaction_PersistsNeitherDocumentNorChunks()
    {
        await fx.ResetAsync();

        await using (var ctx = fx.CreateContext())
        {
            var docRepo = new DocumentRepository(ctx, TimeProvider.System);
            var chunkRepo = new ChunkRepository(ctx);
            var uow = new UnitOfWork(ctx);

            await using (var tx = await uow.BeginTransactionAsync(CancellationToken.None))
            {
                await docRepo.UpsertAsync(Doc("atomic-doc"), CancellationToken.None);
                await chunkRepo.BulkInsertAsync(Chunks("atomic-doc", 5), CancellationToken.None);
                // Dispose WITHOUT committing — IDbContextTransaction rolls back.
            }
        }

        // Verify with a fresh context so we read the committed database state,
        // not tracked entities from the rolled-back context.
        await using var verify = fx.CreateContext();
        (await verify.Documents.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().Be(0);
        (await verify.Chunks.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task CommittedTransaction_PersistsDocumentAndChunks()
    {
        await fx.ResetAsync();

        await using (var ctx = fx.CreateContext())
        {
            var docRepo = new DocumentRepository(ctx, TimeProvider.System);
            var chunkRepo = new ChunkRepository(ctx);
            var uow = new UnitOfWork(ctx);

            await using var tx = await uow.BeginTransactionAsync(CancellationToken.None);
            await docRepo.UpsertAsync(Doc("committed-doc"), CancellationToken.None);
            await chunkRepo.BulkInsertAsync(Chunks("committed-doc", 5), CancellationToken.None);
            await tx.CommitAsync(CancellationToken.None);
        }

        await using var verify = fx.CreateContext();
        (await verify.Documents.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().Be(1);
        (await verify.Chunks.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().Be(5);
    }
}
