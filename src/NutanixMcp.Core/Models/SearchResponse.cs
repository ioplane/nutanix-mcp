namespace NutanixMcp.Core.Models;

public sealed record SearchResponse(
    IReadOnlyList<ChunkResult> Results,
    SearchDiagnostics Diagnostics);
