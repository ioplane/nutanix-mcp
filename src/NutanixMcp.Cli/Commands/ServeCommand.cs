using System.CommandLine;
using NutanixMcp.Cli.Configuration;
using NutanixMcp.Server;

namespace NutanixMcp.Cli.Commands;

public static class ServeCommand
{
    public static Command Build()
    {
        var transport = new Option<string>("--transport")
        {
            Description = "Transport: 'stdio' (MCP over stdio, default) or 'http' (Streamable HTTP)",
            DefaultValueFactory = _ => "stdio",
        };

        var port = new Option<int>("--port")
        {
            Description = "HTTP port (ignored for stdio transport). Default 7456.",
            DefaultValueFactory = _ => 7456,
        };

        var cmd = new Command("serve", "Run the nutanix-mcp MCP server")
        {
            transport,
            port,
        };

        cmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            var t = pr.GetValue(transport) ?? "stdio";
            var p = pr.GetValue(port);
            var settings = CliConfiguration.Load();

            try
            {
                if (string.Equals(t, "stdio", StringComparison.OrdinalIgnoreCase))
                {
                    await StdioHost.RunAsync(settings, ct).ConfigureAwait(false);
                }
                else if (string.Equals(t, "http", StringComparison.OrdinalIgnoreCase))
                {
                    await HttpHost.RunAsync(settings, p, ct).ConfigureAwait(false);
                }
                else
                {
                    Console.Error.WriteLine($"Unknown transport '{t}' — expected 'stdio' or 'http'");
                    return 2;
                }

                return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        });

        return cmd;
    }
}
