# nutanix-mcp .NET — план v0.1

Реплика arista-mcp на корпус Nutanix (1983 portal PDF + KB + Bible + API + Ports).
Цель: production-grade MCP-сервер для семантического поиска по документации Nutanix
с гибридным retrieval (HNSW + BM25), инкрементальным ingest и строгим quality gate.

## 0. Решения, отличающиеся от arista-mcp

| | arista-mcp | nutanix-mcp |
|--|--|--|
| Корпус | ~159 docs (manuals + AVD + supported-features) | ~7000+ chunks (1983 PDF + KB + Bible + API + Ports) |
| Embedder | snowflake-arctic-embed-m-v1.5 (768d) | **bge-m3** (1024d, multilingual — RU+EN запросы) |
| Reranker | ms-marco-MiniLM-L6-v2 + опц. v2-m3 fine-tune | bge-reranker-v2-m3 (XLM-R), сразу из коробки |
| Категории | avd / manual / supported-features | ahv / aos / kb / files / ndb / kubernetes / flow / prism-central / prism-element / move / disaster-recovery / cloud-clusters / nki / pulse / lcm / hardware-replacement |
| Quality gate | Meziantou+Roslynator+Sonar+BannedApi | + **Semgrep Pro** + **PVS-Studio** + **pg-aiguide MCP** |
| Дисплей-имя | arista-mcp | nutanix-mcp (бинарь `nutanix-mcp.exe`) |
| Порт Postgres | 5434 | **5435** (чтоб параллелили с arista) |

Что **сохраняем** один-в-один: layered Cli→Server→Core←Data,Embedding;
HalfVec(dim) + vchord HNSW + bm25vector + tokenizer trigger; stdio+http MCP transports;
EF Core 9.0.15/Npgsql 9.0.4/Pgvector 0.3.0 пины; TimeProvider; init.sql idempotent;
xUnit+FluentAssertions+NSubstitute; PgvectorFixture с `_test` guard.

## 1. Solution layout

```
C:\SHARE\nutanix-mcp\
├── nutanix-mcp.slnx
├── global.json                       # SDK 10.0.201
├── Directory.Build.props
├── Directory.Packages.props          # central pinning, transitive=true
├── BannedSymbols.txt
├── README.md
├── CHANGELOG.md
├── docker/
│   ├── Containerfile                 # FROM tensorchord/vchord-suite:pg18-latest
│   ├── compose.yaml                  # 5435:5432, profile llm для Qwen
│   ├── compose.otel.yaml
│   └── init.sql                      # english_analyzer + russian_analyzer
├── docs/
│   ├── plans/plan-v0.1.md            # этот файл
│   └── sprints/                      # sprint-001..N
├── scripts/
│   ├── fetch-models.ps1              # bge-m3 + bge-reranker-v2-m3 + qwen GGUF
│   └── build-gpu.ps1
├── src/
│   ├── NutanixMcp.Core/              # leaf, no refs
│   ├── NutanixMcp.Data/              # EF Core + Pgvector + 16 категорий
│   ├── NutanixMcp.Embedding/         # ONNX bge-m3 + bge-reranker-v2-m3
│   ├── NutanixMcp.Server/            # Microsoft.NET.Sdk.Web, MCP transports
│   └── NutanixMcp.Cli/               # AssemblyName=nutanix-mcp, OutputType=Exe
└── tests/
    ├── Directory.Build.props         # NoWarn CA1707;CA1711;MA0004
    ├── NutanixMcp.Core.Tests/
    ├── NutanixMcp.Embedding.Tests/
    ├── NutanixMcp.Data.Tests/        # PgvectorFixture, _test guard
    ├── NutanixMcp.Server.Tests/
    ├── NutanixMcp.E2E/               # stdio+http transport e2e
    └── e2e/                          # bash scripts (01-fresh-deploy, 02-cat-ingest, 03-smoke)
```

Layered ref графа (строго):

```
Cli  → Server → Core ← Embedding, Data
                    \← Server (тоже зависит от Core)
```

Core зависит **только** от BCL. Тесты могут ссылаться на любой слой.

## 2. Postgres / vchord-suite (docker)

Образ: `tensorchord/vchord-suite:pg18-latest` — несёт `vector 0.8.2 + vchord 1.1.1 +
vchord_bm25 0.3.0 + pg_tokenizer 0.1.1`.

Порт хоста — `5435` (arista занимает 5434, ntx-mcp тоже 5434).
Пользователь/БД/пароль — `nutanix/nutanix/nutanix`.

`shared_preload_libraries=vector,vchord,vchord_bm25,pg_tokenizer` — критично, без
этого `CREATE EXTENSION vchord` падает на старте.

`init.sql` — идемпотентный, через `current_database()`:
1. `CREATE EXTENSION` для vector / vchord / pg_tokenizer / vchord_bm25 / pg_trgm
2. `ALTER DATABASE … SET search_path = "$user", public, tokenizer_catalog, bm25_catalog`
3. `hnsw.iterative_scan=relaxed_order`, `hnsw.max_scan_tuples=20000`,
   `hnsw.ef_search=100`, `maintenance_work_mem=4GB`, `jit=off`
4. **Два анализатора**: `english_analyzer` (для KB/Bible/API на английском)
   и `russian_analyzer` (если есть RU описания, маловероятно — может пропустить)
   ```
   tokenizer_catalog.create_text_analyzer('english_analyzer', $$
     pre_tokenizer="unicode_segmentation";
     character_filters=[to_lowercase, unicode_normalization=nfkd];
     token_filters=[skip_non_alphanumeric, stopwords="nltk_english", stemmer="english_porter2"]
   $$);
   ```

Опц. `compose.yaml` profile `llm`: `ghcr.io/ggml-org/llama.cpp:server` на 8091 для
HyDE / listwise rerank (оставляем выключенным — feedback memory: listwise регрессирует на 4.39pp).

## 3. EF Core data layer

- **`NutanixDbContext`** с `DbSet<DocumentEntity>`, `DbSet<ChunkEntity>`, `DbSet<IngestRunEntity>`.
- **`ChunkEntity`**: `Id long`, `DocumentId long`, `ParentChunkId long?`, `ChunkKind` (`leaf`/`parent`),
  `Content string`, `Embedding HalfVector` (1024-dim для bge-m3),
  `bm25v bm25vector` (через trigger), `Section string?`, `TokenCount int`,
  `CreatedAt DateTimeOffset`.
- **HNSW index**: `m=16`, `ef_construction=200`, op `halfvec_cosine_ops`.
- **BM25 index** добавляется отдельной миграцией:
  ```sql
  ALTER TABLE chunks ADD COLUMN bm25v bm25vector;
  SELECT tokenizer_catalog.create_custom_model_tokenizer_and_trigger(
    tokenizer_name=>'chunks_tokenizer',
    model_name=>'chunks_model',
    text_analyzer_name=>'english_analyzer',
    table_name=>'chunks',
    source_column=>'content',
    target_column=>'bm25v');
  CREATE INDEX idx_chunks_bm25 ON chunks USING bm25(bm25v bm25_ops);
  ```
- **`DataSourceFactory.Build(cs)` = `NpgsqlDataSourceBuilder(cs).UseVector().Build()`** — обязательно для binary COPY HalfVector.
- **Test fixture `PgvectorFixture`**: `[CollectionDefinition("Pgvector")]`, отказывается работать пока БД не оканчивается на `_test`. Применяет `init.sql` потом `dotnet ef database update`. ENV `NUTANIX_MCP_TEST_CS`.
- **DesignTimeDbContextFactory** читает `NUTANIX_MCP_CS` (default `Host=localhost;Port=5435;Database=nutanix;Username=nutanix;Password=nutanix`).
- **Гочча**: Pgvector.EFCore 0.3.0 пинит EF Core 9.0.1 — direct-pin Microsoft.EntityFrameworkCore.Relational 9.0.15.

## 4. Embedding pipeline

- **`bge-m3`** (BAAI) — 568M params, 8192 context, multilingual, 1024d. Скачиваем ONNX через `Optimum`/`onnx-export` если HF не выдаёт готовый, либо берём из `bge-m3-onnx`.
- **Tokenizer**: SentencePiece (XLM-R-родственный). `Microsoft.ML.Tokenizers 2.0.0` поддерживает SentencePiece через `SentencePieceTokenizer.Create(stream)`. Файл `sentencepiece.bpe.model`.
- **Reranker**: `BAAI/bge-reranker-v2-m3` (cross-encoder, тоже XLM-R-base). Через `XlmRobertaOnnxReranker` + `XlmRobertaRerankerTokenizer` (есть в arista, переносим).
- **`RerankerFamilyDetector`**: автодетект BERT/XLM-R по наличию `vocab.txt` vs `sentencepiece.bpe.model`. Если ни одного — `NoopReranker`.
- **GPU opt-in**: `dotnet build -p:UseGpuOnnx=true` переключает csproj на `Microsoft.ML.OnnxRuntime.Gpu`.
- **Защитная re-нормализация L2** на выходе.
- **Query prefix**: для bge-m3 не нужен (отличие от Snowflake!). Документы embedded raw.
- **Fetch models**: `pwsh scripts/fetch-models.ps1` — SHA size-guard в `models/embedder/` и `models/reranker/`.

## 5. Server layer

- `ModelContextProtocol 1.2.0` + `.AspNetCore 1.2.0`.
- **Stdio**: `Host.CreateApplicationBuilder()`, `LogToStandardErrorThreshold=Trace` (stdout = JSON-RPC), `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly`.
- **HTTP**: `WebApplication`, `UseUrls("http://127.0.0.1:{port}")` (default 7456), `WithHttpTransport(o => o.Stateless=true).MapMcp()`.
- **Tools** (расширяем arista 5 → 7):
  - `search_docs(query, limit=10, category?)` — гибрид
  - `lookup_section(doc_stem, section_pattern)`
  - `list_documents(category?, version?)`
  - `get_document(stem)` — md content + metadata
  - `get_status` — health: db, embedder, reranker, version
  - **`list_categories`** (новое) — все 16 категорий с counts
  - **`get_kb_article(kb_number)`** (новое) — прямой доступ к KB-NNNNN
- **`ServerHosting.AddNutanixMcpServices(settings)`** — единая DI surface (NpgsqlDataSource singleton, DbContext scoped, IEmbedder warmup, IReranker family-detected, IHybridRetriever singleton, OTel opt-in).

## 6. CLI layer

- `System.CommandLine 2.0.6` + `Spectre.Console 0.55.2`.
- Бинарь — `nutanix-mcp.exe` (через `<AssemblyName>nutanix-mcp</AssemblyName>`).
- **Verbs**:
  - `serve --transport stdio|http --port 7456`
  - `ingest --catalog data/catalog.json [--category ahv] [--force] [--dry-run] [--verbose]`
  - `bench --queries tests/fixtures/bench-queries.json --limit 10 [--label vX.Y]`
  - `validate-bench-queries`
  - `curate-triples` (для будущего fine-tune reranker)
  - **`migrate-from-python` (новое)** — одноразовая утилита миграции из ChromaDB/bm25s старого Python-проекта nutanix-docs-mcp в Postgres
- **Config**: `ConfigurationBuilder().AddEnvironmentVariables("NUTANIX_MCP__")` + опц. `nutanix-mcp.json`.

## 7. Code quality gate (расширенный vs arista)

### Слой 1: Roslyn analyzers (как у arista)
- `Meziantou.Analyzer 3.0.50`
- `Roslynator.Analyzers 4.15.0`
- `SonarAnalyzer.CSharp 10.23.0.137933`
- `AsyncFixer 2.1.0`
- `Microsoft.CodeAnalysis.BannedApiAnalyzers 4.14.0`
- `BannedSymbols.txt` — DateTime.Now, WebClient, Thread.Abort, BinaryFormatter, MD5/SHA1.Create
- `Directory.Build.props`: TreatWarningsAsErrors=true, AnalysisLevel=latest-recommended, EnforceCodeStyleInBuild=true, ErrorLog=…/analyzers.sarif

### Слой 2: Semgrep Pro (новое для нашего стека)
- `semgrep --config=p/csharp --config=p/security-audit --config=p/owasp-top-ten src/`
- Дополнительно `p/secrets` (никаких токенов в коде).
- Запускаем: (a) локально через `make semgrep`, (b) в CI как отдельный job.
- Rule overrides в `.semgrep/rules.yaml` если шумит.

### Слой 3: PVS-Studio (deep static analysis)
- Конфиг: `pvs-studio.cfg` в корне с лицензией.
- Запуск: `PVS-Studio_Cmd.exe -t nutanix-mcp.slnx -o pvs-results.plog -c Release` → `HtmlGenerator.exe -t pvs-results.plog -o pvs-report -r`.
- Не блокирует сборку (отдельный отчёт), но **CI шаг ругается на новые W-уровень warnings** против baseline.

### Слой 4: dotnet format + style
- `dotnet format --verify-no-changes` в pre-commit hook.

### Слой 5: pg-aiguide MCP (consult-only)
- Когда пишем DDL/миграции/query — Claude через `mcp__pg-aiguide__view_skill` смотрит best practices (HNSW tuning, partial indexes, BM25 patterns) перед коммитом. Не код-инструмент, а consultancy.

## 8. Tests

Зеркальные arista-mcp: 5 проектов. Унаследовать:
- xUnit 2.9.3, FluentAssertions 8.9.0, NSubstitute 5.3.0, Testcontainers.PostgreSql 4.11.0, Xunit.SkippableFact 1.5.61, Microsoft.Extensions.TimeProvider.Testing 10.5.0.
- `[Collection("Pgvector")]` + `PgvectorFixture` (init.sql + EF migrations + TRUNCATE).
- Time через `TimeProvider.System` в DI; `FakeTimeProvider` в тестах с `AutoAdvanceAmount=Zero`.
- Bench v2 — 588-q chunk-ID style (см. arista feedback `feedback_bench_expansion_result.md`).
- E2E shell scripts: `01-fresh-deploy.sh`, `02-category-ingest.sh`, `03-post-reconvert-smoke.sh`.

## 9. CI

- `.github/workflows/ci.yml` — ubuntu-latest, dotnet 10.0.201 (через global.json), кеш на `Directory.Packages.props` + `**/*.csproj`. Этапы: restore → build -c Release → unit tests (фильтр `!~Data&!~E2E`).
- `.github/workflows/e2e.yml` — services: tensorchord/vchord-suite на 5435, init.sql, кеш models, ef database update, build, unit+integration, потом E2E.
- **Новые jobs**: `semgrep` (отдельный, fail on findings) и `pvs-studio` (information-only, артефакт + delta vs main).
- `.github/dependabot.yml` для NuGet + Actions.

## 10. Roadmap (sprint-driven)

### Sprint 001 — scaffold + docker ✅ DONE (2026-04-30)
- `dotnet new sln && projects` (5 src + 5 tests), AssemblyName, OutputType.
- `Directory.Build.props/Packages.props/BannedSymbols.txt` (копируем из arista, заменяем версии где нужно).
- `docker/{Containerfile,compose.yaml,init.sql}` (порт 5435, db `nutanix`).
- `pg_isready` + `psql -c "\dx"` smoke.
- **Acceptance**: 0 warnings / 0 errors; 6 PG extensions installed; ALTER DATABASE settings applied; `english_analyzer` row in `tokenizer_catalog.text_analyzer`.

### Sprint 002 — Data layer ✅ DONE (2026-05-01) — 9/9 tests green
- ✅ Entities + DbContext + миграции (Initial с baked-in parent-child;
  AddBm25Column hand-written с SQL-block для bm25vector + trigger + bm25-index).
- ✅ DataSourceFactory + DesignTimeDbContextFactory + PgvectorFixture (`_test` guard).
- ✅ Core models (NutanixDocument с KbNumber, NutanixChunk, ChunkKind).
- ✅ Repositories (Document, Chunk binary COPY, IngestRun с TimeProvider).
- ✅ Тесты (9/9 green в реальном postgres): HalfVectorRoundtripTest, HnswIndexSearchTest,
  Bm25IndexSearchTest, ChunkRepositoryBulkInsertTest x2, IngestRunRepositoryTest x2,
  FakeTimeProviderIngestRunTest x2.
- ⏳ Sprint 002.1 (опц.): DocumentRepositoryTest (включая GetByKbNumberAsync canary).
- **Note**: `AddParentChunkSupport` отдельная миграция НЕ генерится — схема
  с `parent_chunk_id` приземляется в Initial (отличие от arista, который добавлял
  поле в Sprint 15.1).

### Sprint 003 — Embedding layer ✅ DONE (2026-05-01) — 15/15 tests green
- ✅ Plan (`sprint-003-embedding.md`) — bge-m3 vs Snowflake delta documented.
- ✅ `fetch-models.ps1` — fixed для ONNX external-data layout
  (`model.onnx` + `model.onnx_data`). 3.3 GB downloaded локально (4070).
- ✅ Reranker source: `hooman650/bge-reranker-v2-m3-onnx-o4` (Optimum O4, FP32).
  INT8 conversion → Sprint 003.5.
- ✅ Embedding interfaces: `IEmbedder`, `IReranker`, `EmbeddingOptions`
  (без QueryPrefix), `RerankerOptions`, `XlmRobertaTokenizer` (fairseq remap),
  `NoopReranker`. Build green.
- ✅ `OnnxEmbedder` — bge-m3 ONNX export ships pre-pooled `sentence_embedding [B, 1024]`
  (verified через `InspectModel` диагностику — mean-pool + normalize zapeчены в графе).
  Defensive re-normalize в .NET для resilience.
- ✅ `XlmRobertaOnnxReranker` (XLM-R pair `<s> q </s></s> d </s>`).
- ✅ `RerankerFamilyDetector` (XLM-R / BERT-WordPiece / None) + `Build()` factory.
- ✅ `IReranker`/`RerankCandidate`/`RerankResult` в `Core.Retrieval`.
- ✅ `NoopReranker` — returns 0 score, preserves chunk-ID order.
- ✅ Тесты под `SkippableFact`: Dimension=1024, UnitNorm, SameTopicCloser,
  MultilingualCrossLingual (EN↔RU), RerankerRelevantBeatsIrrelevant,
  RerankerEmpty, FamilyDetector x4, NoopReranker x2, InspectModel x2.

### Sprint 003.5 — INT8 reranker (queued)
- Calibration набор: 200 (query, positive) пар.
- `optimum-cli onnxruntime quantize --avx512_vnni`.
- Bench against FP32 baseline; merge if delta < 1 pp top-1.

### Sprint 004 — Server layer + Hybrid retriever
- HybridRetriever (dense + sparse RRF + rerank).
- 7 MCP tools.
- Stdio + Http hosts, OTel opt-in.
- Тесты: HybridRetrieverIntegration, Bm25Score_OnDenseSparseCoHit.

### Sprint 005 — CLI + миграция данных
- 6 verbs (serve, ingest, bench, validate, curate, **migrate-from-python**).
- migrate-from-python: читает `C:/SHARE/nutanix-docs-mcp/data/converted/{md,json}` (после sync с remote), переливает в Postgres с парсингом markdown секций.
- Bench v1 (~30q) для smoke.

### Sprint 006 — bench v2 expansion + tuning
- 588-q chunk-ID style bench fixture.
- Прогон baseline, фиксация top-1/top-10 в bench-history.jsonl.
- HNSW ef_search sweep, BM25 weight sweep, RRF k sweep.

### Sprint 007 — semgrep + PVS gate
- `.semgrep/rules.yaml` (overrides для шума).
- `pvs-studio.cfg` + baseline.
- CI integration.

### Sprint 008 — production deploy
- Single-file publish workflow (win-x64, linux-x64).
- Systemd unit / Windows service.
- Deploy на dantte-agents-01.

## 11. Зависимости (Directory.Packages.props pins)

| Package | Version | Why |
|--|--|--|
| Microsoft.EntityFrameworkCore | 9.0.15 | locked by Pgvector.EFCore 0.3.0 (pins 9.0.1) — overpin to 15 |
| Microsoft.EntityFrameworkCore.Design | 9.0.15 | tool support |
| Microsoft.EntityFrameworkCore.Relational | 9.0.15 | binary-compat patch for Pgvector.EFCore |
| Npgsql.EntityFrameworkCore.PostgreSQL | 9.0.4 | matches EF 9 |
| Pgvector | 0.3.2 | HalfVector |
| Pgvector.EntityFrameworkCore | 0.3.0 | only release |
| ModelContextProtocol | 1.2.0 | MCP SDK |
| ModelContextProtocol.AspNetCore | 1.2.0 | http transport |
| Microsoft.ML.OnnxRuntime | 1.24.4 | CPU |
| Microsoft.ML.OnnxRuntime.Gpu | 1.24.4 | optional via `-p:UseGpuOnnx=true` |
| Microsoft.ML.Tokenizers | 2.0.0 | SentencePiece via `SentencePieceTokenizer.Create` |
| Microsoft.Extensions.AI | 10.5.0 | abstractions |
| Microsoft.Extensions.Hosting | 10.0.x | DI host |
| Microsoft.Extensions.TimeProvider.Testing | 10.5.0 | FakeTimeProvider |
| OpenTelemetry | 1.15.3 | tracing |
| System.CommandLine | 2.0.6 | NEW API (Description=, DefaultValueFactory=) |
| Spectre.Console | 0.55.2 | UI |
| xUnit | 2.9.3 | tests |
| FluentAssertions | 8.9.0 | assertions |
| NSubstitute | 5.3.0 | mocks |
| Testcontainers.PostgreSql | 4.11.0 | tests (referenced but PgvectorFixture preferred in dev) |
| Xunit.SkippableFact | 1.5.61 | guard E2E |

Анализаторы (`<GlobalPackageReference>`):
- Meziantou.Analyzer 3.0.50, Roslynator.Analyzers 4.15.0, SonarAnalyzer.CSharp 10.23.0.137933, AsyncFixer 2.1.0, Microsoft.CodeAnalysis.BannedApiAnalyzers 4.14.0.

## 12. Что НЕ делаем

- **NO Python**: arista-mcp правило, переносим. Никаких Python скриптов в src/. Только PS1 для fetch-models.
- **NO `vector` (full-precision)** — только `halfvec`. 50% меньше, эквивалентная точность.
- **NO mock БД в integration**: память user'а строго запрещает (feedback `feedback_dont_stop_user_tasks` контекст ⇒ user уже горел на mock/prod divergence в другом проекте). Все Data-тесты идут в реальный Postgres через PgvectorFixture.
- **NO Claude/AI mention в commit/code/docs** — память user'а.
- **NO listwise rerank по умолчанию** — память: регресс 4.39pp на arista.
- **NO multi-query expansion по умолчанию** — память: регресс на arista, default OFF.
- **NO docling** — память: OOM на 4070.
- **NO удаление старого Python проекта** `C:/SHARE/nutanix-docs-mcp/` — память: keep history rule.

## 13. pg_textsearch (Tapir) — отдельная оценка для v0.2/v0.3

[Источник](https://github.com/timescale/pg_textsearch) — Timescale's BM25 extension, "Tapir"
(Textual Analysis for Postgres Information Retrieval). v1.2.0-dev, ~3.7k stars, PG-OSS license.
PostgreSQL 17/18 only — наша база `tensorchord/vchord-suite:pg18-latest` совместима.

**Сравнение с текущим vchord_bm25 0.3.0:**

| | vchord_bm25 0.3.0 (v0.1) | pg_textsearch 1.2.0-dev |
|--|--|--|
| Тип хранения | `bm25vector` (предтокенизированный) + trigger | прямой text-индекс на колонке |
| Запрос | `bm25v <&> to_bm25query(...)` (negative score, ASC) | `content <@> 'query'` (auto-detect index) |
| Тюнинг | через analyzer (stopwords, stemmer) | k1, b как WITH-параметры (`k1=1.5, b=0.8`) |
| Top-k speedup | стандартный bm25 | **Block-Max WAND** |
| Multilingual | через свой analyzer/stemmer | через text_config (postgres FTS configs) |
| Phrase queries | нет | нет (post-filter workaround) |
| Партиционирование | OK | OK (но статистика per-partition) |
| Зрелость в стэке | работает в arista-mcp v0.2.5 | новый, не в вchord-suite образе |

**Решение для v0.1**: остаёмся на vchord_bm25, чтобы зеркально повторить рецепт arista-mcp
(top-1 92.86% @ v2-m3 reranker — feedback memory `feedback_v2m3_finetune_lands.md`).
Замена BM25-движка в момент запуска проекта = размывание эксперимента "арктэст переносится на nutanix".

**v0.2 candidate** — добавить опц. контейнерный layer с pg_textsearch (отдельный extension
помимо vchord_bm25), сравнить top-1/top-10 на bench v2-style. Конкретная гипотеза:
Block-Max WAND даст p95 -30% при равной точности → если подтвердится, мигрируем основной
индекс. Если нет — оставляем vchord_bm25.

**Внедрение для эксперимента (v0.3 если поднимем)**:
```sql
CREATE EXTENSION pg_textsearch;
-- alongside vchord_bm25, не вместо. Двойная индексация на тестовой БД.
CREATE INDEX chunks_pgts_idx ON chunks USING bm25(content)
  WITH (text_config='english', k1=1.2, b=0.75);
SELECT bm25_force_merge('chunks_pgts_idx');  -- консолидация сегментов
```

Билд из source — `tensorchord/vchord-suite` не содержит pg_textsearch. Возможные пути:
(а) `Containerfile.experimental` с `apt-get install postgresql-18-pg-textsearch`,
(б) форк `vchord-suite` + multi-stage build с pre-built бинарей с GitHub Releases.

## 14. Открытые вопросы

- **Embedder dim 1024 vs 768**: bge-m3 даёт 1024, плюс multilingual. Если корпус чисто английский (а почти весь Nutanix английский) — оптимальнее snowflake-arctic (768d, быстрее, меньше). Решение: **сделать configurable** через `EmbeddingModel` setting, default = bge-m3 для совместимости с RU-запросами user'а, но fallback на arctic-embed-m через `NUTANIX_MCP__EmbeddingModel=snowflake-arctic-embed-m-v1.5`.
- **Категория**: 16 захардкожена или из catalog.json? Делаем gener из ingest-time meta (расширяемо).
- **HF mirror для скачивания моделей**: Cloudflare блочит remote (15 KB/s), на dev-машине 18 MB/s. Скрипт `fetch-models.ps1` запускается локально, потом артефакт грузится через xsync на prod. Документировать.
- **Section-aware chunking для KB**: arista использует heading-based; KB короткие, может имеет смысл whole-doc chunks без splitting? Решить в Sprint 002 при просмотре статистики.

---

**Готов начать**: Sprint 001 после завершения конвертации + sync результатов.
ETA полного MVP (Sprints 001-005): ~3-4 дня активной работы.
