using System.Diagnostics;
using FluentAssertions;
using NutanixMcp.Core.Models;
using NutanixMcp.Data.Entities;
using NutanixMcp.Data.Repositories;
using NutanixMcp.Data.Tests.Fixtures;
using Xunit;

namespace NutanixMcp.Data.Tests;

[Collection("Pgvector")]
public class ChunkRepositoryBulkInsertTest(PgvectorFixture fx)
{
    [Fact]
    public async Task BulkInsert1000Chunks_CompletesUnderFiveSeconds()
    {
        await fx.ResetAsync();

        await using var ctx = fx.CreateContext();
        ctx.Documents.Add(new DocumentEntity
        {
            Id = "bulk-doc",
            Url = "u",
            Category = "ahv",
            Title = "T",
            Slug = "s",
            MdPath = "m",
            JsonPath = "j",
        });
        await ctx.SaveChangesAsync();

        var rng = new Random(7);
        var chunks = Enumerable.Range(0, 1000).Select(i =>
        {
            float[] vec = new float[1024];
            for (int j = 0; j < 1024; j++)
            {
                vec[j] = rng.NextSingle();
            }

            return new NutanixChunk
            {
                DocumentId = "bulk-doc",
                ChunkIndex = i,
                Content = $"title > s\n\nbody {i}",
                RawContent = $"body {i}",
                TokenCount = 4,
                Embedding = vec,
            };
        }).ToList();

        var repo = new ChunkRepository(fx.DataSource, ctx);
        var sw = Stopwatch.StartNew();
        int inserted = await repo.BulkInsertAsync(chunks, CancellationToken.None);
        sw.Stop();

        inserted.Should().Be(1000);
        sw.ElapsedMilliseconds.Should().BeLessThan(5000);

        int count = await repo.CountAsync(CancellationToken.None);
        count.Should().BeGreaterThanOrEqualTo(1000);
    }

    [Fact]
    public async Task DeleteByDocument_RemovesAllChunks()
    {
        await fx.ResetAsync();

        await using var ctx = fx.CreateContext();
        ctx.Documents.Add(new DocumentEntity
        {
            Id = "del-doc",
            Url = "u",
            Category = "ahv",
            Title = "T",
            Slug = "s",
            MdPath = "m",
            JsonPath = "j",
        });
        await ctx.SaveChangesAsync();

        var chunks = Enumerable.Range(0, 10).Select(i => new NutanixChunk
        {
            DocumentId = "del-doc",
            ChunkIndex = i,
            Content = $"c{i}",
            RawContent = $"c{i}",
            TokenCount = 1,
            Embedding = [.. Enumerable.Repeat(0.1f, 1024)],
        }).ToList();

        var repo = new ChunkRepository(fx.DataSource, ctx);
        await repo.BulkInsertAsync(chunks, CancellationToken.None);

        int deleted = await repo.DeleteByDocumentAsync("del-doc", CancellationToken.None);
        deleted.Should().Be(10);
    }
}
