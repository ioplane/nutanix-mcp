using System.IO;
using FluentAssertions;
using NutanixMcp.Core.Bench;
using Xunit;

namespace NutanixMcp.Core.Tests;

public class BenchQueryParserTest
{
    [Fact]
    public async Task ParsesValidFile()
    {
        var path = WriteFixture("""
            {
              "schema_version": 2,
              "queries": [
                {
                  "query_id": "ahv-001",
                  "query": "Configure VLAN on AHV virtual switch",
                  "expect_chunk_ids": [42, 87, 102],
                  "expect_doc": "AHV-Admin-Guide-v11_0",
                  "category": "ahv",
                  "difficulty": "easy"
                }
              ]
            }
            """);
        try
        {
            var rows = await BenchQueryParser.LoadAsync(path, CancellationToken.None);
            rows.Should().HaveCount(1);
            rows[0].QueryId.Should().Be("ahv-001");
            rows[0].ExpectChunkIds.Should().BeEquivalentTo([42L, 87L, 102L]);
            rows[0].Category.Should().Be("ahv");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsEmptyExpectChunkIds()
    {
        var path = WriteFixture("""
            {"queries": [{"query_id": "x", "query": "q", "expect_chunk_ids": []}]}
            """);
        try
        {
            var act = async () => await BenchQueryParser.LoadAsync(path, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*empty expect_chunk_ids*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsDuplicateQueryId()
    {
        var path = WriteFixture("""
            {
              "queries": [
                {"query_id": "x", "query": "q1", "expect_chunk_ids": [1]},
                {"query_id": "x", "query": "q2", "expect_chunk_ids": [2]}
              ]
            }
            """);
        try
        {
            var act = async () => await BenchQueryParser.LoadAsync(path, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*duplicate query_id*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsEmptyQueriesArray()
    {
        var path = WriteFixture("""{"queries": []}""");
        try
        {
            var act = async () => await BenchQueryParser.LoadAsync(path, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*zero queries*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteFixture(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bench-fixture-{Guid.NewGuid()}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
