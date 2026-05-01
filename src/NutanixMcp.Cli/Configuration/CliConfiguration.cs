using Microsoft.Extensions.Configuration;
using NutanixMcp.Core.Settings;

namespace NutanixMcp.Cli.Configuration;

public static class CliConfiguration
{
    public const string EnvPrefix = "NUTANIX_MCP__";

    public static NutanixMcpSettings Load()
    {
        var builder = new ConfigurationBuilder()
            .AddEnvironmentVariables(EnvPrefix);

        string cfgFile = Path.Combine(Environment.CurrentDirectory, "nutanix-mcp.json");
        if (File.Exists(cfgFile))
        {
            builder.AddJsonFile(cfgFile, optional: true, reloadOnChange: false);
        }

        var root = builder.Build();
        return root.Get<NutanixMcpSettings>() ?? new NutanixMcpSettings();
    }
}
