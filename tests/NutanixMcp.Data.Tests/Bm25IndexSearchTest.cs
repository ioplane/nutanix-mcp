using System.Globalization;
using FluentAssertions;
using Npgsql;
using NutanixMcp.Data.Entities;
using NutanixMcp.Data.Tests.Fixtures;
using Pgvector;
using Xunit;

namespace NutanixMcp.Data.Tests;

[Collection("Pgvector")]
public class Bm25IndexSearchTest(PgvectorFixture fx)
{
    [Fact]
    public async Task Bm25QueryReturnsOrderedByRelevance()
    {
        await fx.ResetAsync();

        await using var ctx = fx.CreateContext();

        ctx.Documents.Add(new DocumentEntity
        {
            Id = "bm25-doc",
            Url = "u",
            Category = "ahv",
            Title = "T",
            Slug = "s",
            MdPath = "m",
            JsonPath = "j",
        });
        await ctx.SaveChangesAsync();

        var texts = new[]
        {
            "AHV cluster networking with VLAN tagging on Nutanix",
            "Prism Element user authentication with Active Directory",
            "Prism Central LCM upgrade workflow for Nutanix Files",
            "AHV virtual switch VLAN configuration for guest VMs",
            "Disaster recovery setup with async replication on Move",
        };

        var flat = new Half[1024];
        for (var j = 0; j < 1024; j++)
        {
            flat[j] = (Half)0.1f;
        }

        for (var i = 0; i < texts.Length; i++)
        {
            ctx.Chunks.Add(new ChunkEntity
            {
                DocumentId = "bm25-doc",
                ChunkIndex = i,
                Content = texts[i],
                RawContent = texts[i],
                TokenCount = texts[i].Split(' ').Length,
                Embedding = new HalfVector(flat),
            });
        }

        await ctx.SaveChangesAsync();

        // vchord_bm25 query shape:
        //   bm25v <&> to_bm25query('idx_chunks_bm25'::regclass, tokenize($1, 'chunks_tokenizer')::bm25vector)
        // Uses the same custom tokenizer registered for the BM25 model, so query tokens are
        // vocabulary-aligned with indexed tokens.
        const string sql = """
            SELECT chunk_index,
                   bm25v <&> to_bm25query(
                       'idx_chunks_bm25'::regclass,
                       tokenize($1, 'chunks_tokenizer')::bm25vector) AS score
            FROM chunks
            WHERE document_id = 'bm25-doc'
            ORDER BY bm25v <&> to_bm25query(
                'idx_chunks_bm25'::regclass,
                tokenize($1, 'chunks_tokenizer')::bm25vector)
            LIMIT 3;
            """;

        await using var conn = await fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new NpgsqlParameter { Value = "AHV VLAN" });

        var results = new List<(int Idx, float Score)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((reader.GetInt32(0), reader.GetFloat(1)));
        }

        // BM25 index is sparse — only returns chunks with ≥1 matching token. "AHV VLAN"
        // hits chunks 0 ("AHV ... VLAN") and 3 ("AHV ... VLAN"); chunks 1, 2, 4 have
        // zero overlap and are not in the result set.
        results.Should().HaveCountGreaterThanOrEqualTo(2);
        var topIndices = results.Select(r => r.Idx).ToList();
        topIndices.Should().Contain(0);
        topIndices.Should().Contain(3);

        results[0].Score.Should().BeLessThan(0f,
            $"best match must have negative score; got {results[0].Score.ToString(CultureInfo.InvariantCulture)}");
    }
}
