namespace NutanixMcp.Embedding;

public sealed class EmbeddingOptions
{
    public required string ModelPath { get; init; }

    // SentencePiece BPE model file (`sentencepiece.bpe.model`). bge-m3 is XLM-R
    // family — no `vocab.txt` to load. Property name kept as `VocabPath` for
    // symmetry with `RerankerOptions` and to ease a future BERT-WordPiece swap.
    public required string VocabPath { get; init; }

    public int MaxSequenceLength { get; init; } = 512;
    public int BatchSize { get; init; } = 8;
    public bool Gpu { get; init; }

    // Empty by default. Snowflake Arctic Embed required the
    // "Represent this sentence for searching relevant passages: " prefix; bge-m3
    // does NOT (its model card explicitly says queries and documents share the
    // same encoder, no prefix). Override only if you swap in a model that needs one.
    public string QueryPrefix { get; init; } = "";
}
