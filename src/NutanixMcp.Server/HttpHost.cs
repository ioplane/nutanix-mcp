using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NutanixMcp.Core.Settings;

namespace NutanixMcp.Server;

public static class HttpHost
{
    public static Task RunAsync(NutanixMcpSettings settings, int port, CancellationToken ct)
        => RunAsync(settings, "127.0.0.1", port, ct);

    public static async Task RunAsync(NutanixMcpSettings settings, string bindAddress, int port, CancellationToken ct)
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

        await using var app = builder.Build();

        // DNS-rebinding defense (MCP security guidance; cf. CVE-2025-49596): a browser page on a
        // remote origin could otherwise POST to the loopback MCP endpoint. Reject any request that
        // carries a non-local Origin. Non-browser clients (the MCP SDK, the curl healthz probe) send
        // no Origin and pass through.
        app.Use(async (context, next) =>
        {
            string? origin = context.Request.Headers.Origin;
            if (!string.IsNullOrEmpty(origin) && !IsLocalOrigin(origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync("forbidden origin", context.RequestAborted).ConfigureAwait(false);
                return;
            }

            await next().ConfigureAwait(false);
        });

        app.MapMcp();
        app.MapHealthChecks("/v1/healthz");

        await app.RunAsync(ct).ConfigureAwait(false);
    }

    /// <summary>True when the browser <c>Origin</c> header names a loopback host (any scheme/port).
    /// A malformed Origin is treated as non-local and rejected.</summary>
    public static bool IsLocalOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string host = uri.Host.Trim('[', ']');
        return host is "localhost" or "127.0.0.1" or "::1"
            || uri.IsLoopback;
    }
}
