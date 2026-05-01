# Changelog

All notable changes to nutanix-mcp will be documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0/).
Commit messages follow [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/).

## [Unreleased]

### Added
- `CONTRIBUTING.md` documenting Conventional Commits 1.0.0, SemVer 2.0.0,
  Keep a Changelog 1.1.0, compose-spec, Containerfile.5 and containerignore.5
  conventions used by this repo.
- `docker/.containerignore` per containerignore.5 — limits build context to
  `Containerfile` + `init.sql`.

- Sprint 002 — Data layer:
  - `DocumentEntity`, `ChunkEntity`, `IngestRunEntity`. Chunk embedding type is
    `halfvec(1024)` for bge-m3 (vs arista-mcp's 768-dim Snowflake Arctic).
  - `NutanixDbContext` with HNSW index (`m=16`, `ef_construction=200`,
    `halfvec_cosine_ops`), parent-child FK + `chunk_kind ∈ {leaf, parent}` check.
  - `DocumentEntity.KbNumber` (nullable int) + partial unique index
    `uq_documents_kb_number` to support the `get_kb_article` MCP tool.
  - `DataSourceFactory.Build` (mandatory `UseVector()` for binary COPY of `HalfVector`).
  - `DesignTimeDbContextFactory` reads `NUTANIX_MCP_CS`,
    default `Host=localhost;Port=5435;Database=nutanix;Username=nutanix;Password=nutanix`.
  - EF migrations: `20260501004302_Initial` (full schema incl. parent-child),
    `20260501004324_AddBm25Column` (hand-written: `bm25vector` column +
    `tokenizer_catalog.create_custom_model_tokenizer_and_trigger` + BM25 index).
    Parent-child support is folded into Initial — no separate
    `AddParentChunkSupport` migration is needed because the schema lands with
    `parent_chunk_id` on day 1.
  - `NutanixMcp.Core.Models.NutanixDocument`, `NutanixChunk` (1024-dim);
    `NutanixMcp.Core.Chunking.ChunkKind`.
  - Repositories: `IDocumentRepository`/`DocumentRepository` (incl.
    `GetByKbNumberAsync`), `IChunkRepository`/`ChunkRepository` (binary COPY
    bulk insert with `HalfVector` writer + `chunk_kind` mapping),
    `IIngestRunRepository`/`IngestRunRepository` (`TimeProvider`-driven).
  - Tests: `PgvectorFixture` with `_test` DB-name guard
    (`NUTANIX_MCP_TEST_CS` override), `HalfVectorRoundtripTest`,
    `HnswIndexSearchTest`, `Bm25IndexSearchTest`,
    `ChunkRepositoryBulkInsertTest`, `IngestRunRepositoryTest`,
    `FakeTimeProviderIngestRunTest`. All 6 build green; live execution
    pending a running Postgres at `localhost:5435`.

### Verified
- Sprint 002 gate: 9/9 Data tests pass against `nutanix_test` on
  `localhost:5435` (vchord-suite pg18). HNSW returns the seeded chunk as
  nearest, BM25 ranks AHV-VLAN co-hits first, COPY 1000×1024-dim halfvec
  in 2s, FakeTimeProvider drives ingest_run timestamps deterministically.

### Added (Sprint 003 prep)
- Chunking strategy doc (`nutanix-mcp-plan/chunking-strategy.md`) capturing
  arista-mcp lessons (CRLF, level-ignored MD↔JSON pairing, no multi-query,
  no listwise) and Nutanix-specific rules (TOC skip, marker image-ref
  stripping, multi-version awareness, API-Reference per-endpoint chunking,
  KB-short whole-doc chunking).
- Sprint 003 plan (`nutanix-mcp-plan/sprint-003-embedding.md`) detailing
  bge-m3 vs Snowflake Arctic delta (XLM-R SentencePiece, no query prefix,
  must mean-pool last_hidden_state on .NET side, 1024-dim) and reranker
  source (`hooman650/bge-reranker-v2-m3-onnx-o4` Optimum-O4, FP32; INT8
  conversion queued for Sprint 003.5).
- `scripts/fetch-models.ps1` updated for the ONNX external-data layout
  (`model.onnx` + `model.onnx_data` for both embedder and reranker).
  Verified: 3.3 GB total, idempotent size guard, downloads complete in
  ~3 min on Cloudflare (un-throttled local IP).
- `NutanixMcp.Embedding` skeleton: `IEmbedder`, `IReranker`,
  `EmbeddingOptions` (default `QueryPrefix=""`, `BatchSize=8`),
  `RerankerOptions`, `XlmRobertaTokenizer` (HF fairseq id remap),
  `NoopReranker` (degraded-mode fallback). Build green.

### Added (Sprint 003 — Embedding layer)
- `NutanixMcp.Core.Retrieval.IReranker` + `RerankCandidate`/`RerankResult`
  records (Core-level contract; Embedding implements).
- `OnnxEmbedder` — bge-m3 1024-dim. The BAAI ONNX export ships a pre-pooled
  `sentence_embedding [B, 1024]` output (verified via the `InspectModel`
  diagnostic: outputs are `token_embeddings [B, L, 1024]` and
  `sentence_embedding [B, 1024]`). We use the latter and re-normalise
  defensively via `TensorPrimitives`.
- `XlmRobertaOnnxReranker` — bge-reranker-v2-m3 cross-encoder. Pair encoding
  `<s> q </s></s> d </s>` per HF/XLM-R convention. Output `logits [B, 1]`.
- `XlmRobertaTokenizer.EncodeBatchForEmbedding` — pads/truncates to a flat
  `[batch * seqLen]` int64 layout for ONNX.
- `RerankerFamilyDetector` (XLM-R / BERT-WordPiece / None) + `Build()` factory
  that wires in the right reranker or falls back to `NoopReranker`.
- `NoopReranker` — score 0 across the board, preserves chunk-ID order
  (degraded-mode fallback when no reranker model is configured).
- Tests (15/15 green): `OnnxEmbedderTest` (Dimension=1024, UnitNorm,
  SameTopicCloser, EN↔RU CrossLingualSanity), `XlmRobertaOnnxRerankerTest`
  (RelevantBeatsIrrelevant, EmptyCandidates), `RerankerFamilyDetectorTest`
  ×4, `NoopRerankerTest` ×2, `InspectModel` diagnostic ×2.

### Verified
- 15/15 Embedding tests pass against locally-fetched models on Windows
  CPU. Run wall-clock: ~10s. bge-m3 cross-lingual EN↔RU sanity holds
  (cosine well above 0.5 on AHV/VLAN topic).

### Planned
- Sprint 002 follow-on: `DocumentRepositoryTest` (incl. `KbNumber` lookup).
- Sprint 003.5 (queued): INT8 reranker quantization via Optimum.
- Sprint 004: `HybridRetriever` (dense + sparse RRF + rerank), 7 MCP tools,
  stdio + http hosts.
- Sprint 003: bge-m3 ONNX embedder + bge-reranker-v2-m3 cross-encoder.
- Evaluation: `pg_textsearch` (Tapir) as a v0.2/v0.3 alternative BM25 engine.

## [0.1.0] - 2026-04-30

Initial scaffold sprint. Solution skeleton, package pins, Postgres docker stack.

### Added
- Solution skeleton: 5 src projects (`Core`, `Data`, `Embedding`, `Server`, `Cli`)
  and 5 test projects, layered as `Cli → Server → Core ← Embedding, Data`.
- Centralised package versions via `Directory.Packages.props`
  (`ManagePackageVersionsCentrally=true`).
- `Directory.Build.props` — `TreatWarningsAsErrors=true`,
  `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`,
  SARIF analyzer log.
- Roslyn analyzer suite as `<GlobalPackageReference>`:
  Meziantou.Analyzer 3.0.50, Roslynator.Analyzers 4.15.0,
  SonarAnalyzer.CSharp 10.23.0.137933, AsyncFixer 2.1.0,
  Microsoft.CodeAnalysis.BannedApiAnalyzers 4.14.0.
- `BannedSymbols.txt` mirroring arista-mcp
  (`DateTime.Now`, `WebClient`, `Thread.Abort`, `BinaryFormatter`, `MD5/SHA1.Create`).
- Postgres docker stack on port 5435: `tensorchord/vchord-suite:pg18-latest`
  with `init.sql` provisioning `english_analyzer`, BM25 GUCs, and HNSW tuning.
- Optional `llm` profile in compose for Qwen 2.5 3B GGUF (HyDE / listwise experiments;
  default OFF).
- `nutanix-mcp.slnx` solution file, `global.json` pinning .NET SDK 10.0.201,
  `CLAUDE.md` architecture notes, `README.md`.
- Trivial `Program.cs` in CLI for build smoke; stub `Program.Main` in Server
  (Web SDK requires entry point even though hosting runs via CLI verbs).

### Notes
- EF Core held back to 9.0.15 because `Pgvector.EntityFrameworkCore` 0.3.0
  pins `Microsoft.EntityFrameworkCore.Relational` 9.0.1; direct overpin to 9.0.15.
- Postgres port 5435 chosen because 5434 is taken by arista-mcp + ntx-mcp on the
  shared dev host.
- Compose project name explicitly set to `nutanix-mcp` to avoid clobbering
  arista-mcp's `docker/` directory namespace under Podman compose.

[Unreleased]: https://github.com/dantte-lp/nutanix-mcp/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/dantte-lp/nutanix-mcp/releases/tag/v0.1.0
