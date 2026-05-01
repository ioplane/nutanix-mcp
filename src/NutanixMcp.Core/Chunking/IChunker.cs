namespace NutanixMcp.Core.Chunking;

public interface IChunker
{
    ChunkSet Chunk(string documentTitle, IReadOnlyList<Section> sections);
}
