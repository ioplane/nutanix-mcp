namespace NutanixMcp.Embedding;

public sealed class RerankerOptions
{
    public required string ModelPath { get; init; }

    // Tokenizer asset path. For bge-reranker-v2-m3 (XLM-R) this is the
    // `sentencepiece.bpe.model` file. Keeping the name `VocabPath` keeps a
    // future swap to a BERT-family WordPiece reranker (`vocab.txt`) a
    // one-line change.
    public required string VocabPath { get; init; }

    // Query+doc pair is truncated to this total length (including special tokens).
    public int MaxSequenceLength { get; init; } = 512;

    // Cross-encoders are heavier per token than bi-encoders.
    public int BatchSize { get; init; } = 8;

    public bool Gpu { get; init; }
}
