using System;
using System.IO;
using AwesomeAssertions;
using NutanixMcp.Cli.Configuration;
using Xunit;

namespace NutanixMcp.Cli.Tests;

// Regression guard for code-review finding F3 / S1:
// `Microsoft.Extensions.Configuration` resolves keys by "last registered
// provider wins". `CliConfiguration` MUST add the JSON file before the env-var
// provider so production overrides via `NUTANIX_MCP__*` always take precedence
// over a stray developer `nutanix-mcp.json` in the working directory.
public sealed class CliConfigurationTests : IDisposable
{
    private readonly string _previousCwd = Environment.CurrentDirectory;
    private readonly string _scratchDir;
    private const string EnvKeyModelsDir = "NUTANIX_MCP__ModelsDir";
    private const string EnvKeyRerankerDir = "NUTANIX_MCP__RerankerDir";
    private const string EnvKeyConnString = "NUTANIX_MCP__ConnectionString";

    public CliConfigurationTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), "nutanix-cli-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_scratchDir);
        Environment.CurrentDirectory = _scratchDir;
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _previousCwd;
        Environment.SetEnvironmentVariable(EnvKeyModelsDir, null);
        Environment.SetEnvironmentVariable(EnvKeyRerankerDir, null);
        Environment.SetEnvironmentVariable(EnvKeyConnString, null);
        try { Directory.Delete(_scratchDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public void EnvironmentVariable_overrides_JsonFile_value()
    {
        File.WriteAllText(
            Path.Combine(_scratchDir, "nutanix-mcp.json"),
            """{ "ModelsDir": "from-json", "ConnectionString": "Host=json" }""");

        Environment.SetEnvironmentVariable(EnvKeyModelsDir, "from-env");
        Environment.SetEnvironmentVariable(EnvKeyConnString, "Host=env");

        var settings = CliConfiguration.Load();

        settings.ModelsDir.Should().Be("from-env",
            "NUTANIX_MCP__* env vars must win over nutanix-mcp.json (12-factor); " +
            "see code-review finding F3.");
        settings.ConnectionString.Should().Be("Host=env");
    }

    [Fact]
    public void RerankerDir_env_var_is_bound_to_settings()
    {
        Environment.SetEnvironmentVariable(EnvKeyRerankerDir,
            "/var/lib/nutanix-mcp/models/reranker-finetuned-int8");

        var settings = CliConfiguration.Load();

        // Regression guard for code-review finding F2: NUTANIX_MCP__RERANKERDIR
        // was documented as the production INT8 override but silently ignored
        // because `RerankerDir` did not exist on NutanixMcpSettings.
        settings.RerankerDir.Should().Be(
            "/var/lib/nutanix-mcp/models/reranker-finetuned-int8");
    }

    [Fact]
    public void JsonFile_used_when_no_environment_variable_set()
    {
        File.WriteAllText(
            Path.Combine(_scratchDir, "nutanix-mcp.json"),
            """{ "ModelsDir": "from-json" }""");

        Environment.SetEnvironmentVariable(EnvKeyModelsDir, null);

        var settings = CliConfiguration.Load();

        settings.ModelsDir.Should().Be("from-json");
    }
}
