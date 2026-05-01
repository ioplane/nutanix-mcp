using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NutanixMcp.Core.Settings;

namespace NutanixMcp.Server;

public static class HttpHost
{
    public static Task RunAsync(NutanixMcpSettings settings, int port, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        builder.Services.AddNutanixMcpServices(settings);

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithToolsFromAssembly(typeof(HttpHost).Assembly);

        var app = builder.Build();
        app.MapMcp();

        return app.RunAsync(ct);
    }
}
