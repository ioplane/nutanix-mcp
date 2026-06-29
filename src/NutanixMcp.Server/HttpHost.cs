using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NutanixMcp.Core.Settings;

namespace NutanixMcp.Server;

public static class HttpHost
{
    public static Task RunAsync(NutanixMcpSettings settings, int port, CancellationToken ct)
        => RunAsync(settings, "127.0.0.1", port, ct);

    public static Task RunAsync(NutanixMcpSettings settings, string bindAddress, int port, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindAddress);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://{bindAddress}:{port}");

        builder.Services.AddNutanixMcpServices(settings);

        // Liveness probe consumed by `deploy/quadlet/nutanix-mcp.container`'s
        // `HealthCmd=curl --fail --silent http://127.0.0.1:7456/v1/healthz`
        // and by the Windows installer verification step. The probe is a
        // bare process-liveness check — if the HTTP listener can accept the
        // request and route to the MapHealthChecks middleware, the server
        // process is up. Backend dependency status (DB reachable, models
        // loaded) is surfaced separately by the `get_status` MCP tool.
        builder.Services.AddHealthChecks();

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithToolsFromAssembly(typeof(HttpHost).Assembly);

        var app = builder.Build();
        app.MapMcp();
        app.MapHealthChecks("/v1/healthz");

        return app.RunAsync(ct);
    }
}
