using System.Globalization;
using Npgsql;

namespace NutanixMcp.Data;

public static class DataSourceFactory
{
    public static NpgsqlDataSource Build(string connectionString)
    {
        var b = new NpgsqlDataSourceBuilder(connectionString);
        b.UseVector();
        return b.Build();
    }

    // Build with per-connection HNSW GUC overrides. Applied via a physical
    // connection initializer so every pooled connection inherits the same
    // ef_search / iterative_scan setting for the lifetime of the data source.
    // Used by Sprint 008 sweep harness to vary HNSW behaviour without
    // ALTER DATABASE round-trips.
    public static NpgsqlDataSource Build(
        string connectionString,
        int? hnswEfSearch,
        string? hnswIterativeScan)
    {
        var b = new NpgsqlDataSourceBuilder(connectionString);
        b.UseVector();

        if (hnswEfSearch.HasValue || !string.IsNullOrEmpty(hnswIterativeScan))
        {
            // Whitelist iterative_scan to a known-safe enum value before
            // interpolating into the SET command (defense-in-depth — the
            // value comes from a CLI flag, not user input, but cheap to
            // pin).
            string? scan;
            if (string.IsNullOrEmpty(hnswIterativeScan))
            {
                scan = null;
            }
            else
            {
                scan = hnswIterativeScan.Equals("strict_order", StringComparison.OrdinalIgnoreCase)
                    ? "strict_order"
                    : "relaxed_order";
            }
            int? ef = hnswEfSearch;

            b.UsePhysicalConnectionInitializer(
                connectionInitializer: conn =>
                {
                    if (ef is { } v)
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = $"SET hnsw.ef_search = {v.ToString(CultureInfo.InvariantCulture)}";
                        cmd.ExecuteNonQuery();
                    }
                    if (scan is not null)
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = $"SET hnsw.iterative_scan = '{scan}'";
                        cmd.ExecuteNonQuery();
                    }
                },
                connectionInitializerAsync: async conn =>
                {
                    if (ef is { } v)
                    {
                        await using var cmd = conn.CreateCommand();
                        cmd.CommandText = $"SET hnsw.ef_search = {v.ToString(CultureInfo.InvariantCulture)}";
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    if (scan is not null)
                    {
                        await using var cmd = conn.CreateCommand();
                        cmd.CommandText = $"SET hnsw.iterative_scan = '{scan}'";
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                });
        }

        return b.Build();
    }
}
