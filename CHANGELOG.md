# Changelog

All notable changes to nutanix-mcp will be documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0/).
Commit messages follow [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/).

## [Unreleased]

### Added (Sprint 006 — bench v2 harness scaffold)

- Sprint plan in `docs/en/sprints/sprint-006.md` and `docs/ru/sprints/`
  with the chunk-ID-keyed v2 schema, 16-category coverage target,
  difficulty mix (50 % easy / 30 % medium / 20 % hard), append-only
  history schema, and the 80 % top-10 smoke gate.
- `NutanixMcp.Core.Bench`:
  - `BenchQuery`, `BenchQueryFile` — JSON fixture row + envelope.
  - `BenchScoring.FirstHitRank` (1-based, accepts a union of
    acceptable chunk ids) and `BenchScoring.IsHit`.
  - `BenchRun`, `BenchCategoryStats` — append-only history record.
  - `BenchQueryParser` — strict validation; rejects empty
    `expect_chunk_ids`, duplicate `query_id`, empty queries arrays.
    Loud-fail by design (silent drop is what hid arista's three v1
    regressions).
  - `BenchHistoryWriter` — append-only JSONL writer; auto-creates
    parent directories.
- `NutanixMcp.Cli.Bench`:
  - `BenchHarness` — drives `IHybridRetriever`, scores against
    `expect_chunk_ids`, computes `top_1`/`top_5`/`top_10` plus p50/p95
    latency (linear interpolation, matches NumPy `linear` method).
  - `IBenchProgress` + `NullBenchProgress` + `SpectreBenchProgress`.
- `NutanixMcp.Cli.Commands.BenchCommand`:
  - `bench` verb with `--queries --history --label --limit`,
    `--candidate-pool-size --rerank-top-n --rrf-k`,
    `--smoke-top10-floor --no-history`. Auto-resolves the run label
    from the short git SHA when one is reachable.
  - Exit code: `0` on smoke-gate pass, `1` otherwise.
- Tests in `NutanixMcp.Core.Tests`: `BenchScoringTest` (7),
  `BenchQueryParserTest` (4), `BenchHistoryWriterTest` (2).
- Documentation lessons from arista-mcp folded into
  `docs/en/training-rag.md` (and Russian copy):
  reingest invalidates chunk_ids, top-1 saturates before top-10, CPU
  ingest cost on the Nutanix corpus is ~2 hours and motivates a
  GPU-build production path.

### Added (Sprint 006 — quality stack + ingest path benchmark)

- Adopted the ntx-mcp quality stack:
  - `.editorconfig` — CRLF, file-scoped namespaces, Allman braces,
    naming rules including `s_camelCase` for private static fields
    and `PascalCase` for constants (a const-specific rule is layered
    in front of the static rule because both match a `private const`
    field).
  - `Directory.Build.props` — `CodeAnalysisTreatWarningsAsErrors=true`,
    SARIF analyzer log per project, `NuGetAudit=true` at moderate
    level for transitive CVE detection.
  - `src/NutanixMcp.Data/Migrations/.editorconfig` — suppresses
    CA1707/CA1825/CA1861/MA0005/MA0048/MA0051 for EF-generated code.
- `scripts/pvs-studio-scan.ps1` — local PVS-Studio runner. Iterates
  each `src/*.csproj` (PVS-Studio_Cmd does not accept `.slnx`),
  merges per-project `.plog`s, renders the HTML report. Exit-code
  mask treats codes 256/1024 as success-with-findings.
- `scripts/Directory.Build.props` — opts the standalone embedder
  microbenchmark project (`scripts/EmbedBench.csproj`) into the
  shared analyzer/banned-symbols suite.
- Empirical 3-platform embedding throughput study folded into
  `docs/en/sprints/sprint-006.md` and `docs/ru/sprints/sprint-006.md`:
  Local CPU 1.7 chunks/s, Remote EPYC 9654 CPU 13.7 chunks/s,
  Remote 5090 GPU 634 chunks/s. Determines that production ingest
  must run on the rented GPU host; CPU-only is a 2-hour wall-clock
  alternative for emergency rebuilds.

### Fixed (Sprint 006 — code review)

- `BenchHistoryWriter` — switched `FileShare.Read` → `ReadWrite` and
  guarded the append with a per-path `SemaphoreSlim` (via
  `ConcurrentDictionary<string, SemaphoreSlim>`) so simultaneous
  callers in the same process cannot interleave bytes mid-line.
- `BenchQueryParser` — now rejects unknown `schema_version` values
  (current bench schema is v2).
- `SpectreBenchProgress` — escapes `query_id` via `Markup.Escape`
  before passing it through Spectre interpolation, closing a markup-
  injection hole if a fixture row ever carried `[`/`]` characters.
  Column alignment for the rank marker switched to a `(rendered,
  plainLength)` tuple because Spectre format width specifiers count
  markup characters, not rendered glyphs.
- `BenchCommand` — `ResolveGitSha` is now memoised in a
  `Lazy<string?>` so the bench command does not fork `git`
  rev-parse twice per run.
- `DocumentLoader` — replaced `Replace("\r", "\n", StringComparison.Ordinal)`
  with the char overload (`Replace('\r', '\n')`) per MA0089 to skip
  the StringComparison overhead on the hot path.

### Pending (Sprint 006 — runtime gate)

- Full-corpus ingest of 7343 documents into the populated remote
  `nutanix` database (completed 2026-05-01 with docs=7323).
- Authoring of the 100-query bench v2 fixture against the populated
  corpus.
- First `bench-history.jsonl` row.
- `validate-bench-queries` verb (Sprint 006.5) to re-pin chunk_ids
  via `(expect_doc, expect_section)` after a reingest.

### Added (Sprint 005 — Cli + ingest pipeline)

- Corpus audit of the local 7343-document snapshot at
  `data/converted/`. Findings recorded in `docs/en/sprints/sprint-005.md`
  (and `docs/ru/`): 81 % CRLF line endings, 24 % surviving marker
  page-break tokens, 24 % empty marker image refs, 16 % start with a
  `## **Contents**` table, three JSON enrichment shapes (`dict-list`,
  `string-list`, empty), zero duplicate stems, zero zero-heading docs.
- `NutanixMcp.Core.Catalog`:
  - `CatalogEntry`, `JsonSection`, `EnrichedDocumentJson`,
    `LoadedDocument` records.
  - `CatalogReader` — walks `<base>/md/` and pairs each stem with the
    optional `<base>/json/<stem>.json`. Reads category, KB number,
    tags, and version from the JSON; falls back to filename heuristics
    for the 24 % of docs without enrichment. Sorts entries by id for
    deterministic catalog SHA.
  - `DocumentLoader` — CRLF normalise, strip page markers
    (`{N}-+`), strip empty marker image refs
    (`![](_page_N_(Picture|Figure|Image)_M.ext)`), drop the TOC L2
    section (`Contents`/`Table of Contents`/`Содержание`), parse ATX
    headings, MD↔JSON pair via cleaned-title sequence with a small
    lookahead, fall back to MD-only when JSON is empty.
    Path-traversal guard: `ResolveInside` refuses any catalog-relative
    path that escapes the base directory (Semgrep CWE-22 finding).
- `NutanixMcp.Core.Chunking`:
  - `Section`, `ChunkDraft`, `ChunkSet`, `IChunker`, `ChunkingOptions`.
  - `SectionAwareChunker` — emits one parent and one or more leaves
    per section, slides a target+overlap window over over-budget
    sections, merges tiny neighbours within a section, truncates
    parents over budget with a head/tail summary.
- `NutanixMcp.Cli.Ingest`:
  - `IngestOptions`, `IngestResult`, `IIngestProgress` /
    `NullIngestProgress`, `SpectreIngestProgress`.
  - `IngestService` — catalog-level skip (sha-match short-circuit),
    per-document skip (`pdf_sha256` match), parent-then-leaf insert
    sequence with bigint id read-back for the parent FK.
- CLI `ingest` verb wired with `--catalog`, `--category`, `--force`,
  `--dry-run`, `--limit`, `--verbose`. Exit code: 0 on success or
  catalog-skipped, 2 partial, 3 unhandled, 130 cancelled.

### Verified (Sprint 005)

- 52/52 tests green: Core 21 (incl. 6 DocumentLoader, 4
  SectionAwareChunker, 4 CatalogReader), Server 4, Embedding 15,
  Data 12.
- Real ingest of 10 KB articles ran end-to-end against the local
  PostgreSQL: 30 leaves embedded with bge-m3, 30 parents, 60 chunks
  total, ingest_run row written.
- Live `search_docs` over HTTP returned three Karbon/NKE-related KB
  articles for "Nutanix Kubernetes Engine self-service", top-1 hit
  `KB-10048` with cross-encoder score −0.28 and per-stage diagnostics
  attached.

### Added — release prep

- `LICENSE` — MIT.
- `README.md` and `README.ru.md` — declarative overview, installation,
  link out to `docs/`.
- `docs/` reorganised under `docs/en/` and `docs/ru/`. Eight canonical
  topics: overview, architecture, quickstart, operations, retrieval,
  chunking, training-rag, quality-gate, roadmap. All schemas authored
  in Mermaid. Top-level `docs/README.md` and `docs/README.ru.md` index.
- `.markdownlint.jsonc` and `.markdownlintignore` — strict ruleset for
  Markdown linting.
- `.github/workflows/ci.yml` — build + format + unit + integration +
  docs lint, every PR.
- `.github/workflows/release.yml` — single-file publish for
  `linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, `osx-x64`,
  `osx-arm64`. Tag-validated against a strict semver regex. Each
  artefact ships with a `.sha256` sidecar.
- `plugin/` — Claude Code plugin with `.claude-plugin/plugin.json`
  manifest, `.mcp.json` server registration, two slash commands
  (`/nutanix-search`, `/nutanix-status`), one auto-engaging skill
  (`nutanix-search`), and a plugin README.
- `.claude-plugin/marketplace.json` — marketplace descriptor that
  exposes the plugin under the `dantte-lp/nutanix-mcp` GitHub repo.

### Removed — release prep

- `CLAUDE.md` — content migrated into `docs/en/architecture.md` and
  `docs/en/quality-gate.md`.

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

### Added (Sprint 004 — Server layer + hybrid retriever)

- Core models: `ChunkResult`, `SearchResponse`, `SearchDiagnostics` (a tighter
  shape than arista's — Hyde/Listwise/MultiQuery fields are intentionally
  omitted, see `docs/plans/training-and-rag-roadmap.md`).
- Core retrieval helpers: `QueryExpansion` record + `QueryExpander` with 26
  Nutanix-specific acronyms (AHV, AOS, NDB, NCM, NKE, NKP, NCC, NUS, LCM, CVM,
  IPMI, PE, PC, DSF, RF, DR, … ) backed by a `FrozenDictionary` for fast
  lookup on the query hot path.
- `NutanixMcp.Server.Retrieval` — `IHybridRetriever`, `RetrievalOptions` (incl.
  Nutanix-specific `Version` filter for multi-version corpora),
  `QueryEmbeddingCache` (256-entry, capacity-half eviction), `HybridRetriever`
  with adaptive-rerank floor (top-5 RRF spread ≤ 0.02 → cap N to 10), parent
  hydration before cross-encoder scoring, optional per-section dedup.
- HyDE / multi-query / listwise re-rank are **deliberately absent** from the
  retriever — all three regressed on arista bench v2 (memory entries
  feedback_multiquery_regression, feedback_listwise_regression). The
  surrounding doc (HybridRetriever class summary) records the rationale.
- `NutanixMcp.Core.Settings` — `NutanixMcpSettings`, `ModelPaths`,
  `McpTransport`. Default conn-string targets local `localhost:5435`.
- `ServerHosting.AddNutanixMcpServices` — single DI registration surface used
  by both Stdio and Http hosts; embedder warmed during DI resolution to
  amortise the ~600 ms ONNX session init.
- 7 MCP tools (5 ported from arista + 2 new for Nutanix):
  - `search_docs` (hybrid dense + BM25 + cross-encoder rerank;
    `version` filter exposed)
  - `lookup_section`, `list_documents` (`version` filter), `get_document`,
    `get_status`
  - `list_categories` (NEW) — discovery of all 16 Nutanix categories with
    document and chunk counts
  - `get_kb_article` (NEW) — O(1) lookup by KB-NNNNN identifier, hits the
    partial unique index `uq_documents_kb_number`
- `StdioHost` (`Host.CreateApplicationBuilder` with stderr-only logs) +
  `HttpHost` (`WebApplication`, port 7456 default, `Stateless = true`).
- `NutanixMcp.Cli` `serve` verb wired to both transports via
  `System.CommandLine 2.0.6` (`SetAction((ParseResult, CancellationToken) => …)`).
- `CliConfiguration.Load()` reads `NUTANIX_MCP__*` env vars + optional
  `nutanix-mcp.json`.

### Verified (Sprint 004)

- Full test suite green: **37/37** — 6 Core (QueryExpander), 4 Server
  (QueryEmbeddingCache), 15 Embedding (bge-m3 + reranker), 12 Data
  (incl. 3 HybridRetriever integration tests against live `nutanix_test`).
- Live HTTP e2e: `dotnet run --project src/NutanixMcp.Cli -- serve
  --transport http --port 7461` registers all 7 tools, `tools/list`
  returns full schemas, `get_status` returns
  `{healthy:true, documents:0, chunks:0}` against the `nutanix` database
  after a clean `dotnet ef database update`.

### Planned

- Sprint 002 follow-on: `DocumentRepositoryTest` (incl. `KbNumber` lookup).
- Sprint 003.5 (queued): INT8 reranker quantization via Optimum.
- Sprint 005: CLI verbs `ingest`, `bench`, `validate`, `curate-triples`,
  `migrate-from-python` + ingest pipeline (CatalogReader → DocumentLoader →
  SectionAwareChunker → IEmbedder → repos). Production seed of the
  `nutanix` DB once the remote conversion finishes.
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
  architecture notes under `docs/en/architecture.md`, `README.md`.
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
