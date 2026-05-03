using System.CommandLine;
using NutanixMcp.Cli.Commands;

namespace NutanixMcp.Cli;

internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        var root = new RootCommand("nutanix-mcp — hybrid retrieval MCP server for Nutanix documentation")
        {
            ServeCommand.Build(),
            IngestCommand.Build(),
            BenchCommand.Build(),
            ValidateBenchQueriesCommand.Build(),
            FilterBenchQueriesCommand.Build(),
            BootstrapCommand.Build(),
        };
        return root.Parse(args).InvokeAsync();
    }
}
