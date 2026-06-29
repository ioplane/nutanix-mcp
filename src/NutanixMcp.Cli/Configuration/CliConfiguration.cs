using Microsoft.Extensions.Configuration;
using NutanixMcp.Core.Settings;

namespace NutanixMcp.Cli.Configuration;

public static class CliConfiguration
{
    public const string EnvPrefix = "NUTANIX_MCP__";

    // Provider precedence is "last registered wins" in
    // Microsoft.Extensions.Configuration. We add the optional JSON file FIRST so
    // NUTANIX_MCP__* environment variables — set by systemd EnvironmentFile,
    // Quadlet `Environment=`, container runtime, etc. — override a developer's
    // local nutanix-mcp.json. Same shape as ASP.NET Core's default ordering
    // (appsettings.json → env vars).
    public static NutanixMcpSettings Load()
    {
        var builder = new ConfigurationBuilder();

        string cfgFile = Path.Combine(Environment.CurrentDirectory, "nutanix-mcp.json");
        if (File.Exists(cfgFile))
        {
            builder.AddJsonFile(cfgFile, optional: true, reloadOnChange: false);
        }

        builder.AddEnvironmentVariables(EnvPrefix);

        var root = builder.Build();
        return root.Get<NutanixMcpSettings>() ?? new NutanixMcpSettings();
    }
}
