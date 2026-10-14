using System.IO;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace NutanixMcp.Data.Tests.Fixtures;

// Shared fixture. Tests run against an isolated `nutanix_test` database so a
// developer's prod ingest is never touched. On first use:
//   1. connects to maintenance `postgres` DB,
//   2. CREATE DATABASE nutanix_test if it does not exist,
//   3. connects to nutanix_test,
//   4. applies docker/init.sql (extensions + english_analyzer),
//   5. runs EF migrations (schema + bm25v trigger),
//   6. truncates leftover rows.
//
// Override the test DB with NUTANIX_MCP_TEST_CS in CI or against a non-default
// postgres location (e.g. a WSL IP).
public sealed class PgvectorFixture : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5435;Database=nutanix_test;Username=nutanix;Password=nutanix";

    public string ConnectionString { get; }
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public PgvectorFixture()
    {
        ConnectionString = Environment.GetEnvironmentVariable("NUTANIX_MCP_TEST_CS")
            ?? DefaultConnectionString;
    }

    public async ValueTask InitializeAsync()
    {
        await EnsureTestDatabaseAsync();

        DataSource = DataSourceFactory.Build(ConnectionString);
        await EnsureExtensionsAsync();

        var opt = new DbContextOptionsBuilder<NutanixDbContext>()
            .UseNpgsql(DataSource, o => o.UseVector())
            .Options;
        await using var ctx = new NutanixDbContext(opt);
        await ctx.Database.MigrateAsync();

        await ResetAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }
    }

    public NutanixDbContext CreateContext()
    {
        var opt = new DbContextOptionsBuilder<NutanixDbContext>()
            .UseNpgsql(DataSource, o => o.UseVector())
            .Options;
        return new NutanixDbContext(opt);
    }

    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE TABLE chunks, documents, ingest_runs RESTART IDENTITY CASCADE;";
        await cmd.ExecuteNonQueryAsync();
    }

    // Ensure target test DB exists. Reject any non-_test name early — the safety
    // guard is the whole point: tests TRUNCATE, prod must never see a TRUNCATE.
    private async Task EnsureTestDatabaseAsync()
    {
        var target = new NpgsqlConnectionStringBuilder(ConnectionString);
        string dbName = target.Database
            ?? throw new InvalidOperationException("Test connection string must specify Database=");

        if (!dbName.EndsWith("_test", StringComparison.Ordinal)
            && !string.Equals(dbName, "nutanix_test", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to run tests against '{dbName}'. Use a DB name ending in '_test' "
                + "(override NUTANIX_MCP_TEST_CS if you really need a different name).");
        }

        var admin = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" };

        await using var conn = new NpgsqlConnection(admin.ToString());
        await conn.OpenAsync();
        await using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM pg_database WHERE datname = @n";
        probe.Parameters.Add(new NpgsqlParameter<string>("n", dbName));
        object? exists = await probe.ExecuteScalarAsync();
        if (exists is null)
        {
            await using var create = conn.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{dbName.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
            await create.ExecuteNonQueryAsync();
        }
    }

    private async Task EnsureExtensionsAsync()
    {
        string path = ResolveRepoFile("docker/init.sql");
        string sql = await File.ReadAllTextAsync(path);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        // init.sql is idempotent for CREATE EXTENSION but create_text_analyzer errors
        // when the analyzer already exists. Swallow that specific class of error.
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (
            string.Equals(ex.SqlState, "XX000", StringComparison.Ordinal)
            || string.Equals(ex.SqlState, "42710", StringComparison.Ordinal))
        {
            _ = ex;
        }
    }

    private static string ResolveRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".gitignore"))
                && Directory.Exists(Path.Combine(dir.FullName, "docker")))
            {
                return Path.Combine(dir.FullName, relativePath);
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate repo root while resolving '{relativePath}' from {AppContext.BaseDirectory}");
    }
}
