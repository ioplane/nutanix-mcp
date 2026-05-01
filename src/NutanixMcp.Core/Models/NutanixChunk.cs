using NutanixMcp.Core.Chunking;

namespace NutanixMcp.Core.Models;

public sealed record NutanixChunk
{
    public long Id { get; init; }
    public required string DocumentId { get; init; }
    public required int ChunkIndex { get; init; }
    public required string Content { get; init; }
    public required string RawContent { get; init; }
    public string? SectionTitle { get; init; }
    public short? SectionLevel { get; init; }
    public int? PageStart { get; init; }
    public int? PageEnd { get; init; }
    public required int TokenCount { get; init; }

    // Parent rows have Embedding == null (parents are never embedded — the
    // dense path filters on chunk_kind = 'leaf'). ParentChunkId is null on
    // parent rows and populated on leaves after the parents have been
    // bulk-inserted and their generated ids selected back.
    public ChunkKind ChunkKind { get; init; } = ChunkKind.Leaf;
    public long? ParentChunkId { get; init; }
    public float[]? Embedding { get; init; }
    public string? EmbeddingModel { get; init; } = "bge-m3";
}
