using NutanixMcp.Core.Chunking;
using NutanixMcp.Core.Models;

namespace NutanixMcp.Core.Catalog;

// Output of `DocumentLoader.LoadAsync`. Carries the metadata that the ingest
// pipeline upserts into the `documents` table plus the parsed sections that
// the chunker turns into parent and leaf chunks.
public sealed record LoadedDocument
{
    public required NutanixDocument Metadata { get; init; }
    public required IReadOnlyList<Section> Sections { get; init; }
}
