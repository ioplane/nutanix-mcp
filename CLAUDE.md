# nutanix-mcp — architecture notes

## Quick reference

    podman compose -f docker/compose.yaml up -d postgres
    dotnet test
    dotnet run --project src/NutanixMcp.Cli -- serve --transport stdio

## Layers (strict)

    Cli → Server → Core ← Embedding, Data

Core has no reference to Data, Embedding, or Server. Tests may reference any layer.

## DO NOT

- Reference Python; nutanix-mcp is pure .NET.
- Switch `halfvec` back to `vector`; halfvec is 50% smaller at negligible cost.
- Skip `NpgsqlDataSource.UseVector()` — binary COPY of `HalfVector` depends on it.
- Touch the legacy Python project at `C:\SHARE\nutanix-docs-mcp\` — keep for history (user rule).
- Add Claude/Anthropic/AI attribution in commits, code, or docs (user rule).
- Default-on multi-query expansion or listwise rerank — both regressed on arista bench.

## Version pins (Sprint 001)

- **.NET 10** SDK 10.0.201 / TFM `net10.0`.
- **EF Core 9.0.15** + **Npgsql.EntityFrameworkCore.PostgreSQL 9.0.4** — held back because
  `Pgvector.EntityFrameworkCore` (0.3.0, the only release) pins Npgsql.EFCore 9.0.1. Bump
  when Pgvector.EFCore ships an EF Core 10 build.
- **pgvector 0.8.2**, **vchord 1.1.1**, **vchord_bm25 0.3.0**, **pg_tokenizer 0.1.1** —
  all bundled in the `tensorchord/vchord-suite:pg18-latest` base image.
- Analyzers: Meziantou 3.0.50, Roslynator 4.15.0, SonarAnalyzer 10.23.0,
  AsyncFixer 2.1.0, Microsoft.CodeAnalysis.BannedApiAnalyzers 4.14.0.
- **Embedder**: bge-m3 (1024-dim multilingual). Reranker: bge-reranker-v2-m3 (XLM-R).
- **Postgres port**: 5435 (5434 is taken by arista-mcp + ntx-mcp).

## Postgres gotchas

- `shared_preload_libraries` must include `vector,vchord,vchord_bm25,pg_tokenizer` or
  `CREATE EXTENSION vchord` fails at startup and `ALTER DATABASE SET hnsw.*` fails with
  "unrecognized configuration parameter".
- `tokenizer_catalog.tokenize(...)` is `STABLE`, not `IMMUTABLE` → a `GENERATED ALWAYS AS
  (tokenize(...)) STORED` column is rejected. Use `tokenizer_catalog.create_custom_model_tokenizer_and_trigger`,
  which provisions a trigger that writes the `bm25vector` target column on INSERT/UPDATE.
- BM25 query shape: `bm25v <&> to_bm25query('idx_chunks_bm25'::regclass, tokenize($1, 'chunks_tokenizer')::bm25vector)`.
  The `<&>` operator returns the negative BM25 score, so `ORDER BY … ASC` ranks best matches first.
- Memory knobs are 3 pieces (compose.yaml command-line, init.sql `ALTER DATABASE`, live
  per-DB state from earlier runs). Keep all three in sync; per-DB override (`source='database'`)
  wins over command-line.

## Windows / Podman caveats

- WSL2 binds at `0.0.0.0` inside the VM, not Windows localhost. Use 0.0.0.0 in compose.yaml
  port mappings (already done) — WSL interop forwards back.
- `podman compose` delegates to `docker-compose.exe`. Project name derives from compose-file
  directory by default. Both arista-mcp and nutanix-mcp use `docker/` — set `name:` at the
  top of compose.yaml to disambiguate (already done: `name: nutanix-mcp`).
- Testcontainers races with two-stage init on Podman/WSL; integration tests will hit a
  shared local container instead (see `tests/NutanixMcp.Data.Tests/Fixtures/PgvectorFixture.cs`
  in Sprint 002 and the `NUTANIX_MCP_TEST_CS` env var override).

## Quality gate (extended vs arista-mcp)

- **Roslyn analyzers** in Directory.Build.props (same as arista).
- **BannedSymbols.txt** — DateTime.Now, WebClient, Thread.Abort, BinaryFormatter, MD5/SHA1.Create.
- **Semgrep** — installed locally (1.157.0) + MCP plugin available; run via `semgrep --config=p/csharp src/`.
- **PVS-Studio** — installed locally; CI step planned in Sprint 007.
- **pg-aiguide MCP** — consult Postgres patterns before writing DDL/migrations/queries.
- **Microsoft Learn MCP** — verify .NET / EF Core / MCP SDK signatures before generation.
- **dotnet format --verify-no-changes** in pre-commit hook (Sprint 007).

## Sprint cadence

Waterfall sprints, each gated by `dotnet test` green + extension check + bench delta.
Don't start Sprint N+1 until Sprint N's gate passes and `git tag sprint-N-review` exists.

## Sprint 001 (scaffold)

- Solution skeleton (5 src + 5 tests), TWaE, central package versions.
- Postgres docker stack on 5435, init.sql with `english_analyzer`.
- Trivial `Program.cs` in CLI for build smoke; `Program.cs` in Server is a stub
  (Web SDK requires Main; actual hosting goes through CLI verbs).
- Verified: 0 warnings / 0 errors; all 6 PG extensions installed; ALTER DATABASE
  settings applied; `english_analyzer` row present in `tokenizer_catalog.text_analyzer`.
