# Sprint 003 — Embedding layer

Status: planning, model fetch in progress.

## Scope

ONNX-backed bge-m3 embedder + bge-reranker-v2-m3 cross-encoder. Both are
multilingual XLM-RoBERTa derivatives — we share a single SentencePiece
tokenizer family, different from arista's BERT WordPiece path.

## Asset inventory (after fetch-models.ps1 succeeds)

```
models/
├── embedder/
│   ├── model.onnx                # 725 KB graph
│   ├── model.onnx_data           # 2.27 GB external weights (must co-locate)
│   ├── sentencepiece.bpe.model   # 5 MB SentencePiece BPE
│   ├── tokenizer_config.json
│   └── special_tokens_map.json
└── reranker/
    ├── model.onnx                # 108 KB graph (Optimum O4)
    ├── model.onnx.data           # 1.14 GB external weights
    ├── sentencepiece.bpe.model
    ├── tokenizer_config.json
    └── special_tokens_map.json
```

Source: `BAAI/bge-m3` (official) and `hooman650/bge-reranker-v2-m3-onnx-o4`
(community Optimum-optimized export — BAAI has no official ONNX). The arista
v2-m3 win recipe used INT8 ONNX; producing our own INT8 export is queued for
Sprint 003.5 (`optimum-cli onnxruntime quantize --avx512_vnni`).

## Differences from arista

| | arista | nutanix |
|--|--|--|
| Embedder | snowflake-arctic-embed-m-v1.5 (BERT, 768d) | bge-m3 (XLM-R, 1024d) |
| Tokenizer | `BertTokenizer` (WordPiece, vocab.txt) | SentencePiece (`sentencepiece.bpe.model`) |
| Query prefix | `"Represent this sentence for searching relevant passages: "` | **none** (bge-m3 doesn't need it) |
| Output | model emits pre-pooled `sentence_embedding [B, 768]` | bge-m3 emits raw `last_hidden_state [B, T, 1024]` — must mean-pool + L2-normalize on .NET side |
| Token type ids | not used | not used (XLM-R has none) |
| Reranker tokenizer | shared BERT WordPiece (vocab.txt) | shared XLM-R SentencePiece — same as embedder |
| Output dim | 768 | 1024 |
| Default reranker | NoopReranker | `XlmRobertaOnnxReranker` against bge-reranker-v2-m3 |

## Layer plan

### `NutanixMcp.Embedding` (port + adapt)

| file | source | adaptation |
|--|--|--|
| `IEmbedder.cs` | port | identical |
| `IReranker.cs` | port (split out of arista's `OnnxReranker.cs` via `IReranker`) | identical |
| `EmbeddingOptions.cs` | port | drop `QueryPrefix` default to `""` |
| `RerankerOptions.cs` | port | identical |
| `XlmRobertaTokenizer.cs` | adapt arista's `XlmRobertaRerankerTokenizer` | generalised — reused by embedder + reranker |
| `OnnxEmbedder.cs` | adapt arista's | bge-m3 emits `last_hidden_state` → mean-pool over `attention_mask`, L2-normalize via `TensorPrimitives` |
| `XlmRobertaOnnxReranker.cs` | port | identical (XLM-R contract) |
| `RerankerFamilyDetector.cs` | port | XLM-R always (since both models are XLM-R), but keep the BERT branch for future swap |
| `NoopReranker.cs` | port | identical |

### `NutanixMcp.Embedding.Tests`

- `OnnxEmbedderTest.EmbeddingDimension` — 1024
- `OnnxEmbedderTest.QueryAndDocumentEqualityWithoutPrefix` — bge-m3 doesn't
  diverge query/doc embeddings (sanity check: cosine ≈ 1 for identical input
  with isQuery=true vs isQuery=false)
- `OnnxEmbedderTest.MultilingualCrossLingualSanity` — RU query vs EN passage
  about same topic gives cosine > 0.5 (multilingual canary)
- `XlmRobertaOnnxRerankerTest.ScoreShapeAndRange` — single score per pair,
  reasonable range
- `XlmRobertaOnnxRerankerTest.RelevantBeatsIrrelevant` — ground-truth pair
  scores higher than off-topic pair
- `RerankerFamilyDetectorTest.DetectsXlmRoberta` — `sentencepiece.bpe.model`
  present → XLM-R
- `RerankerFamilyDetectorTest.FallsBackToNoopWithoutModel` — empty dir →
  NoopReranker

All tests guarded by `[SkippableFact]` over `nutanix-mcp/models/embedder/model.onnx`
existence so CI without models stays green.

## Performance envelope

| metric | arista (Snowflake 768) | nutanix target (bge-m3 1024) |
|--|--|--|
| Embed batch=16 latency (CPU) | ~120 ms / batch | ~280 ms / batch (1.7 GB model, larger embed) |
| Embed batch=16 latency (GPU 4070, FP32) | ~25 ms | ~55 ms |
| Reranker batch=8 latency (CPU) | ~95 ms (MiniLM-L6) | ~620 ms (XLM-R-base, 568 M) |
| Reranker batch=8 latency (GPU 4070, FP32) | ~12 ms | ~70 ms |

bge-m3 is ~5× heavier than Snowflake Arctic. Plan accordingly:
- Default `BatchSize=8` for embedder (vs arista 16). Memory pressure on the
  4070 with 1024-dim FP32 weights makes bigger batches risky during ingest.
- Reranker batch stays at `BatchSize=8`.
- GPU opt-in via `dotnet build -p:UseGpuOnnx=true` swaps to
  `Microsoft.ML.OnnxRuntime.Gpu` (already pinned in
  `Directory.Packages.props`).

## Sprint 003.5 — INT8 reranker

Once the FP32 path is green and bench has a baseline:
1. `uv tool install optimum[onnxruntime]`
2. Calibration set: 200 (query, positive) pairs from a curated sample.
3. `optimum-cli onnxruntime quantize --avx512_vnni --calibration_data ...`
4. Drop the INT8 build into `models/reranker-int8/` and add a runtime toggle
   `RerankerOptions.IntQuantization`.
5. Bench against FP32 baseline; merge if delta < 1 pp top-1.

## Acceptance

- `dotnet test --filter Category=Embedding` green when models present.
- `dotnet test --filter Category=Embedding` skips cleanly when models absent.
- Manual smoke: embed "Nutanix AHV cluster" + embed "AHV кластер Nutanix"
  → cosine > 0.7 (multilingual works).
- 0 warnings / 0 errors in the solution build.
