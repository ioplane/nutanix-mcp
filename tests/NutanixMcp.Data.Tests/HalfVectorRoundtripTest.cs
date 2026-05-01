using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NutanixMcp.Data.Entities;
using NutanixMcp.Data.Tests.Fixtures;
using Pgvector;
using Xunit;

namespace NutanixMcp.Data.Tests;

[Collection("Pgvector")]
public class HalfVectorRoundtripTest(PgvectorFixture fx)
{
    [Fact]
    public async Task InsertAndReadHalfVector()
    {
        await using var ctx = fx.CreateContext();
        var doc = new DocumentEntity
        {
            Id = "doc1",
            Url = "u",
            Category = "ahv",
            Title = "T",
            Slug = "s",
            MdPath = "m",
            JsonPath = "j",
        };
        ctx.Documents.Add(doc);
        await ctx.SaveChangesAsync();

        var vec = new Half[1024];
        for (int i = 0; i < 1024; i++)
        {
            vec[i] = (Half)(i / 1024f);
        }

        var chunk = new ChunkEntity
        {
            DocumentId = "doc1",
            ChunkIndex = 0,
            Content = "title > section\n\nhello world",
            RawContent = "hello world",
            TokenCount = 3,
            Embedding = new HalfVector(vec),
        };
        ctx.Chunks.Add(chunk);
        await ctx.SaveChangesAsync();

        var loaded = await ctx.Chunks.AsNoTracking().FirstAsync(c => c.DocumentId == "doc1");
        loaded.Embedding.Should().NotBeNull();
        var arr = loaded.Embedding.ToArray();
        arr.Length.Should().Be(1024);
        ((float)arr[100]).Should().BeApproximately(100f / 1024f, 1e-2f);
    }
}
