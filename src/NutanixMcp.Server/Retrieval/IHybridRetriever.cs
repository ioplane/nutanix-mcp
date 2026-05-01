using NutanixMcp.Core.Models;

namespace NutanixMcp.Server.Retrieval;

public interface IHybridRetriever
{
    Task<SearchResponse> SearchAsync(string query, RetrievalOptions options, CancellationToken ct);
}
