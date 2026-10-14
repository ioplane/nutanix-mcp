using FluentAssertions;
using Npgsql;
using NutanixMcp.Data.Tests.Fixtures;
using Xunit;

namespace NutanixMcp.Data.Tests;

[Collection("Pgvector")]
public class DataSourceFactoryHnswTest(PgvectorFixture fx)
{
    [Theory]
    [InlineData("strict_order", "strict_order")]
    [InlineData("STRICT_ORDER", "strict_order")]
    [InlineData("anything-else", "relaxed_order")]
    public async Task AsyncOpenAppliesHnswOverrides(string requested, string expected)
    {
        await using var ds = DataSourceFactory.Build(fx.ConnectionString, hnswEfSearch: 123, hnswIterativeScan: requested);
        await using var conn = await ds.OpenConnectionAsync(TestContext.Current.CancellationToken);

        Setting(conn, "hnsw.ef_search").Should().Be("123");
        Setting(conn, "hnsw.iterative_scan").Should().Be(expected);
    }

    [Fact]
    public async Task SyncOpenAppliesHnswOverrides()
    {
        await using var ds = DataSourceFactory.Build(fx.ConnectionString, hnswEfSearch: 77, hnswIterativeScan: "strict_order");
        using var conn = ds.OpenConnection();

        Setting(conn, "hnsw.ef_search").Should().Be("77");
        Setting(conn, "hnsw.iterative_scan").Should().Be("strict_order");
    }

    private static string? Setting(NpgsqlConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT current_setting(@n)";
        cmd.Parameters.AddWithValue("n", name);
        return (string?)cmd.ExecuteScalar();
    }
}
