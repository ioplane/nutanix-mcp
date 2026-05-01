using System.ComponentModel.DataAnnotations.Schema;
using Pgvector;

namespace NutanixMcp.Data.Entities;

[Table("chunks")]
public sealed class ChunkEntity
{
    [Column("id")] public long Id { get; set; }
    [Column("document_id")] public string DocumentId { get; set; } = "";
    [Column("chunk_index")] public int ChunkIndex { get; set; }
    [Column("content")] public string Content { get; set; } = "";
    [Column("raw_content")] public string RawContent { get; set; } = "";
    [Column("section_title")] public string? SectionTitle { get; set; }
    [Column("section_level")] public short? SectionLevel { get; set; }
    [Column("page_start")] public int? PageStart { get; set; }
    [Column("page_end")] public int? PageEnd { get; set; }
    [Column("token_count")] public int TokenCount { get; set; }

    // Parent rows carry kind 'parent' and have Embedding null — they exist
    // only to provide richer context to the cross-encoder reranker during the
    // hydration step. Leaves point at their parent via ParentChunkId.
    [Column("parent_chunk_id")] public long? ParentChunkId { get; set; }
    [Column("chunk_kind")] public string ChunkKind { get; set; } = "leaf";

    // halfvec(1024) for bge-m3. Nullable because parent chunks are not embedded.
    [Column("embedding", TypeName = "halfvec(1024)")]
    public HalfVector? Embedding { get; set; }

    [Column("embedding_model")] public string? EmbeddingModel { get; set; } = "bge-m3";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }

    public DocumentEntity Document { get; set; } = null!;
    public ChunkEntity? Parent { get; set; }
}
