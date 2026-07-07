using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NutanixMcp.Core.Settings;
using NutanixMcp.Server.Retrieval;

namespace NutanixMcp.Server;

public static class StdioHost
{
    public static async Task RunAsync(NutanixMcpSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var builder = Host.CreateApplicationBuilder();

        // stdout is the MCP transport; all logs must go to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddNutanixMcpServices(settings);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(StdioHost).Assembly);

        using var host = builder.Build();

        // Warm the retriever (ONNX embedder plus reranker load, tens of seconds) in the BACKGROUND so
        // the MCP initialize handshake is answered immediately. A synchronous resolve here blocks
        // startup long enough that stdio clients (Claude Code, Codex) time out the handshake and
        // report a failed connection. DI singleton resolution is thread-safe, so the first
        // search request shares this warm-up instead of racing a second load. The continuation observes
        // any load failure so it is not raised as an unobserved-task fault; a genuine model-load
        // error still resurfaces on the first tool call.
        _ = Task.Run(() => host.Services.GetRequiredService<IHybridRetriever>(), ct)
            .ContinueWith(static faulted => faulted.Exception?.Handle(_ => true), TaskScheduler.Default);

        await host.RunAsync(ct).ConfigureAwait(false);
    }
}
