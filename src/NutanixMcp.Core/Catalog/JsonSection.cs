namespace NutanixMcp.Core.Catalog;

// JSON-side section metadata. Page numbers are optional because not every
// converter pass populates them — the upstream `arista-docs.enrich` flow
// always emits them, the legacy `marker` flow sometimes does not.
public sealed record JsonSection(string Title, int? PageStart, int? PageEnd);
