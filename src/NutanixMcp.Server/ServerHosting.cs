using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NutanixMcp.Core.Retrieval;
using NutanixMcp.Core.Settings;
using NutanixMcp.Data;
using NutanixMcp.Data.Repositories;
using NutanixMcp.Embedding;
using NutanixMcp.Server.Retrieval;

namespace NutanixMcp.Server;

public static class ServerHosting
{
    // Single source of truth for DI lifetimes used by both the stdio and HTTP
    // hosts. Keep this here so the two transports never drift on, e.g.,
    // whether the embedder is a singleton (it must be — ONNX session startup
    // is expensive, especially for bge-m3 at 2.27 GB).
    public static IServiceCollection AddNutanixMcpServices(
        this IServiceCollection services,
        NutanixMcpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<NpgsqlDataSource>(_ => DataSourceFactory.Build(settings.ConnectionString));

        services.AddDbContext<NutanixDbContext>((sp, o) =>
        {
            var ds = sp.GetRequiredService<NpgsqlDataSource>();
            o.UseNpgsql(ds, n => n.UseVector());
        });

        services.AddSingleton<IEmbedder>(_ => BuildAndWarmEmbedder(settings));
        // `RerankerDir` overrides `{ModelsDir}/reranker` so production deployments
        // can point at the INT8 fine-tune (e.g. /var/lib/nutanix-mcp/models/
        // reranker-finetuned-int8) without re-laying out the on-disk model tree.
        // See deploy/systemd/nutanix-mcp.env.example.
        string rerankerDir = !string.IsNullOrWhiteSpace(settings.RerankerDir)
            ? settings.RerankerDir
            : ModelPaths.RerankerDir(settings.ModelsDir);
        services.AddSingleton<IReranker>(_ => RerankerFamilyDetector.Build(rerankerDir, settings.Gpu));

        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IChunkRepository>(sp => new ChunkRepository(
            sp.GetRequiredService<NutanixDbContext>()));
        services.AddScoped<IIngestRunRepository, IngestRunRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddSingleton<IHybridRetriever>(sp => new HybridRetriever(
            sp.GetRequiredService<IEmbedder>(),
            sp.GetRequiredService<IReranker>(),
            sp.GetRequiredService<NpgsqlDataSource>()));

        return services;
    }

    private static OnnxEmbedder BuildEmbedder(NutanixMcpSettings settings)
    {
        return new OnnxEmbedder(new EmbeddingOptions
        {
            ModelPath = ModelPaths.EmbedderModel(settings),
            VocabPath = ModelPaths.EmbedderVocab(settings),
            Gpu = settings.Gpu,
        });
    }

    // Warm the ONNX session during DI resolution so the first real request
    // doesn't pay the ~600 ms graph-init cost (bge-m3 is heavier than arista's
    // Snowflake Arctic; ~2.27 GB external data file).
    private static OnnxEmbedder BuildAndWarmEmbedder(NutanixMcpSettings settings)
    {
        var embedder = BuildEmbedder(settings);
        try
        {
            _ = embedder.EmbedAsync(["warmup"], isQuery: false, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Warm-up failure should not block host startup. The real request
            // will surface whatever underlying error this hit (model missing,
            // GPU disabled, etc.) with a proper exception path. OOM/stack
            // overflow still propagate.
            System.Diagnostics.Debug.WriteLine(
                $"[nutanix-mcp] embedder warm-up failed (non-fatal): {ex.Message}");
        }
        return embedder;
    }
}
