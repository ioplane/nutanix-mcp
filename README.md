# nutanix-mcp

Hybrid-retrieval Model Context Protocol server for the Nutanix
documentation corpus. Built on .NET 10 with pgvector, vchord, and ONNX
Runtime.

[Russian copy → `README.ru.md`](./README.ru.md)

## What it does

The server combines a dense vector search over `bge-m3` embeddings with
a sparse BM25 path through `vchord_bm25`, fuses results via Reciprocal
Rank Fusion, hydrates parent context, and reranks with
`bge-reranker-v2-m3` (FP32 stock or v0.3.0 fine-tuned INT8 — see
[`training-rag.md`](./docs/en/training-rag.md)). Seven MCP tools expose
search, lookup, listing, and health-reporting over either stdio or
streamable HTTP.

Current production baseline (v0.3.0, 509-query LLM bench fixture):
**top-1 = 80.2 %, top-5 = 99.6 %, top-10 = 100.0 %, p50 = 2.0 s, p95 = 2.9 s**
on a CPU-only laptop reaching the local-host PG.

## Quick start

### Zero-to-search from a release tarball (recommended)

```bash
TAG=v0.3.3
RID=linux-x64
gh release download "$TAG" -R ioplane/nutanix-mcp \
  -p "nutanix-mcp-${TAG}-${RID}.tar.gz"
tar -xzf "nutanix-mcp-${TAG}-${RID}.tar.gz"
cd "nutanix-mcp-${TAG}-${RID}"
sudo bash scripts/install.sh
```

`scripts/install.sh` stages the binary under
`/opt/apps/nutanix-mcp-${TAG}-linux-x64/`, symlinks `/usr/local/bin/nutanix-mcp`,
fetches ONNX models into `/var/lib/nutanix-mcp/models`, runs
`nutanix-mcp bootstrap --release "$TAG"` (Postgres container + corpus
restore + tokenizer rebuild), registers the MCP server in
`~/.claude.json` and via `codex mcp add`, and links the Claude Code
plugin marketplace. Idempotent. Env-var overrides: `TAG=vX.Y.Z`,
`MODELS_DIR=…`, `CONN_STRING=…`, `SKIP_BOOTSTRAP=1`.

If you already have the binary on `PATH`, `nutanix-mcp bootstrap --release v0.3.3`
handles just the PG + corpus phase.

### Developer workflow (from source)

```bash
podman compose -f docker/compose.yaml up -d postgres
dotnet ef database update --project src/NutanixMcp.Data
pwsh scripts/fetch-models.ps1
dotnet test
dotnet run --project src/NutanixMcp.Cli -- serve --transport stdio
```

## Architecture

```text
Cli → Server → Core ← Embedding, Data
```

The `Core` project depends only on the BCL. `Embedding` and `Data` may
not see each other. Tests may reference any layer. Full diagram and
component table in [`docs/en/architecture.md`](./docs/en/architecture.md).

## Documentation

- [`docs/`](./docs/) — index. English in `docs/en/`, Russian in `docs/ru/`.
- [`docs/en/quickstart.md`](./docs/en/quickstart.md) — bring up the
  server, run the test suite, configure via environment.
- [`docs/en/retrieval.md`](./docs/en/retrieval.md) — the hybrid pipeline
  end to end with diagnostics.
- [`docs/en/operations.md`](./docs/en/operations.md) — health checks,
  ingest cadence, failure modes.
- [`docs/en/training-rag.md`](./docs/en/training-rag.md) — bench v2
  schema, fine-tune plan, levers excluded from v0.1.
- [`docs/en/quality-gate.md`](./docs/en/quality-gate.md) — Roslyn
  analyzers, Semgrep, PVS-Studio, format, markdown lint.
- [`docs/en/roadmap.md`](./docs/en/roadmap.md) — sprint timeline and
  status.

## Releases

Single-file binaries are published from the `release` workflow for
Windows, Linux, and macOS on amd64 and arm64. See the
[Releases page](https://github.com/ioplane/nutanix-mcp/releases) for
the latest tag and SHA-256 checksums.

## Claude Code plugin

A first-party plugin lives under [`plugin/`](./plugin/). To install via
the marketplace:

```text
/plugin marketplace add https://github.com/ioplane/nutanix-mcp
/plugin install nutanix-mcp
```

The plugin registers the MCP server, two slash commands
(`/nutanix-search`, `/nutanix-status`), and one skill that auto-engages
for Nutanix questions.

## Licence

MIT. See [`LICENSE`](./LICENSE).
