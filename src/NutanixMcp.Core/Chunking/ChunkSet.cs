namespace NutanixMcp.Core.Chunking;

public sealed record ChunkSet(
    IReadOnlyList<ChunkDraft> Parents,
    IReadOnlyList<ChunkDraft> Leaves);
