namespace NutanixMcp.Core.Catalog;

// Lightweight record bound to the converter's per-document JSON. The
// converter emits at least three shapes (per the 2026-05-01 corpus audit):
//   - `dict-list`: `sections: [{title, page}]`, `toc: [{title, heading_level, page}]`.
//   - `string-list`: `sections: [string]`, no page mapping.
//   - empty: neither `sections` nor `toc`.
// The deserialiser reads both `sections` and `toc` permissively; the loader
// downstream picks whichever is populated.
public sealed record EnrichedDocumentJson
{
    public IReadOnlyList<JsonSection> Sections { get; init; } = [];
    public IReadOnlyList<JsonSection> Toc { get; init; } = [];
}
