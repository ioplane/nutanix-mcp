using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NutanixMcp.Data.Entities;
using NutanixMcp.Data.Tests.Fixtures;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Xunit;

namespace NutanixMcp.Data.Tests;

[Collection("Pgvector")]
public class HnswIndexSearchTest(PgvectorFixture fx)
{
    [Fact]
    public async Task NearestNeighboursReturnedInOrder()
    {
        await fx.ResetAsync();

        await using var ctx = fx.CreateContext();
        var rng = new Random(42);

        ctx.Documents.Add(new DocumentEntity
        {
            Id = "hnsw-doc",
            Url = "u",
            Category = "ahv",
            Title = "T",
            Slug = "s",
            MdPath = "m",
            JsonPath = "j",
        });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        const int chunkCount = 500;
        for (int i = 0; i < chunkCount; i++)
        {
            var v = new Half[1024];
            for (int j = 0; j < 1024; j++)
            {
                v[j] = (Half)((rng.NextSingle() * 2f) - 1f);
            }

            ctx.Chunks.Add(new ChunkEntity
            {
                DocumentId = "hnsw-doc",
                ChunkIndex = i,
                Content = $"chunk {i}",
                RawContent = $"chunk {i}",
                TokenCount = 1,
                Embedding = new HalfVector(v),
            });
        }

        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        var query = (await ctx.Chunks.AsNoTracking().FirstAsync(c => c.ChunkIndex == 0, cancellationToken: TestContext.Current.CancellationToken)).Embedding!;

        var nearest = await ctx.Chunks
            .Where(c => c.Embedding != null)
            .OrderBy(c => c.Embedding!.CosineDistance(query))
            .Take(5)
            .Select(c => c.ChunkIndex)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        nearest.Should().HaveCount(5);
        nearest[0].Should().Be(0);
    }
}
