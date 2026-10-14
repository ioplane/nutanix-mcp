using System.Globalization;
using Npgsql;

namespace NutanixMcp.Data;

public static class DataSourceFactory
{
    // The provisioned container runs `max_connections=20`; Npgsql's default pool ceiling of 100 would
    // let a burst of concurrent searches (each opens up to ~3 physical connections: dense + sparse +
    // hydration) exhaust the server with `53300 too many connections`. Cap the pool below
    // `max_connections` unless the caller already set an explicit size (e.g. the test fixture).
    private const int DefaultMaxPoolSize = 12;

    public static NpgsqlDataSource Build(string connectionString)
    {
        var b = new NpgsqlDataSourceBuilder(CapPoolSize(connectionString));
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

        var gucs = new List<(string Name, string Value)>(2);
        if (hnswEfSearch is { } ef)
        {
            gucs.Add(("hnsw.ef_search", ef.ToString(CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrEmpty(hnswIterativeScan))
        {
            // Unknown values fall back to relaxed_order, the pgvector default mode.
            gucs.Add(("hnsw.iterative_scan",
                hnswIterativeScan.Equals("strict_order", StringComparison.OrdinalIgnoreCase)
                    ? "strict_order"
                    : "relaxed_order"));
        }

        if (gucs.Count > 0)
        {
            b.UsePhysicalConnectionInitializer(
                connectionInitializer: conn =>
                {
                    foreach (var (name, value) in gucs)
                    {
                        using var cmd = SetConfigCommand(conn, name, value);
                        cmd.ExecuteNonQuery();
                    }
                },
                connectionInitializerAsync: async conn =>
                {
                    foreach (var (name, value) in gucs)
                    {
                        await using var cmd = SetConfigCommand(conn, name, value);
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                });
        }

        return b.Build();
    }

    // set_config(name, value, is_local => false) is the session-level SET, but with bind
    // parameters instead of a value spliced into the SQL text (PostgreSQL docs § 9.28.1).
    private static NpgsqlCommand SetConfigCommand(NpgsqlConnection conn, string name, string value)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config(@name, @value, false)";
        cmd.Parameters.Add(new NpgsqlParameter<string>("name", name));
        cmd.Parameters.Add(new NpgsqlParameter<string>("value", value));
        return cmd;
    }

    private static string CapPoolSize(string connectionString)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        if (!csb.ContainsKey("Maximum Pool Size"))
        {
            csb.MaxPoolSize = DefaultMaxPoolSize;
        }

        return csb.ConnectionString;
    }
}
