namespace NutanixMcp.Core.Chunking;

// Pre-embed chunk. Content carries the document+section prefix; RawContent
// is the raw body for BM25 + display. Embedding is filled in by the ingest
// pipeline (Leaf only — Parent rows skip the embedder).
public sealed record ChunkDraft
{
    public required string Content { get; init; }
    public required string RawContent { get; init; }
    public required string SectionTitle { get; init; }
    public required short SectionLevel { get; init; }
    public int? PageStart { get; init; }
    public int? PageEnd { get; init; }
    public required int TokenCount { get; init; }
    public ChunkKind Kind { get; init; } = ChunkKind.Leaf;

    // Index into the parent list emitted by the chunker — the ingest layer
    // resolves it to a real `parent_chunk_id` once parents are persisted and
    // their generated bigint ids are read back. Null on parent rows and on
    // legacy single-pass output.
    public int? ParentIndex { get; init; }
}
