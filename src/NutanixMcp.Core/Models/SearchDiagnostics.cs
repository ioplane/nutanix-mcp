namespace NutanixMcp.Core.Models;

// Per-stage timings and counts for a single hybrid search call. The shape
// is deliberately narrower than arista-mcp's: nutanix-mcp does not ship
// HyDE, multi-query expansion, or listwise re-rank — see the rationale in
// docs/plans/training-and-rag-roadmap.md (those levers regressed on arista
// bench; revisit only when a frontier-class local LLM is reachable).
public sealed record SearchDiagnostics(
    int DenseHits,
    int SparseHits,
    int AfterRrf,
    int AfterRerank,
    double EmbedMs,
    double DenseQueryMs,
    double SparseQueryMs,
    double RrfMs,
    double RerankMs,
    double TotalMs);
