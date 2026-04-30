using System.CommandLine;

namespace NutanixMcp.Cli;

internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        var root = new RootCommand("nutanix-mcp — hybrid retrieval MCP server for Nutanix documentation");
        return root.Parse(args).InvokeAsync();
    }
}
