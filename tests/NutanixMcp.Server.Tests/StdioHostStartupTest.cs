using AwesomeAssertions;
using Xunit;

namespace NutanixMcp.Server.Tests;

public class StdioHostStartupTest
{
    [Fact]
    public void Stdio_host_does_not_eagerly_resolve_heavy_retriever()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourcePath = Path.Combine(
            repositoryRoot,
            "src",
            "NutanixMcp.Server",
            "StdioHost.cs");

        string source = File.ReadAllText(sourcePath);

        source.Should().NotContain(
            "GetRequiredService<IHybridRetriever>()",
            "stdio startup must stay lightweight for clients that create one MCP process per session");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "nutanix-mcp.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the nutanix-mcp repository root.");
    }
}
