# Quickstart

## End-user install (release tarball)

```bash
TAG=v0.3.3
RID=linux-x64      # or linux-arm64, win-x64, win-arm64, osx-x64, osx-arm64
gh release download "$TAG" -R dantte-lp/nutanix-mcp \
  -p "nutanix-mcp-${TAG}-${RID}.tar.gz"
tar -xzf "nutanix-mcp-${TAG}-${RID}.tar.gz"
cd "nutanix-mcp-${TAG}-${RID}"
sudo bash scripts/install.sh
```

Eight phases performed by `scripts/install.sh`:

1. Stages the binary at `/opt/apps/nutanix-mcp-${TAG}-linux-x64/`,
   symlinks `/opt/apps/nutanix-mcp-current` and `/usr/local/bin/nutanix-mcp`.
2. Fetches the ONNX models into `${MODELS_DIR:-/var/lib/nutanix-mcp/models}`
   (skipped when `embedder/model.onnx` already exists).
3. Calls `nutanix-mcp bootstrap --release "$TAG"` — provisions a Podman
   Postgres container, downloads the corpus dump from the release
   attachment via the GitHub API, runs `pg_restore --clean --if-exists -j 4`,
   and rebuilds the `pg_tokenizer` registry.
4. Registers `mcpServers.nutanix-mcp` in `~/.claude.json` (via `jq`).
5. Symlinks `${INSTALL_DIR}` into `~/.claude/plugins/marketplaces/nutanix-mcp/`
   so `/plugin install nutanix-mcp` inside Claude Code activates the
   slash commands + skill.
6. `codex mcp add nutanix-mcp -- …` registers the MCP server for Codex.
7. Symlinks `plugin/skills/*` into `~/.codex/skills/`.
8. Prints a summary + smoke-check hints.

Idempotent. Env-var overrides:

- `TAG=vX.Y.Z` — install a specific release (default: highest release).
- `MODELS_DIR=/custom/path` — point the models at a shared location.
- `CONN_STRING="Host=…;Port=…;Database=…;Username=…;Password=…"` — for
  externally-managed Postgres; skip the bootstrap PG container.
- `SKIP_BOOTSTRAP=1` — re-install on a host whose Postgres is already
  populated (upgrade the binary and configs without touching the DB).

`nutanix-mcp bootstrap --release "$TAG"` on its own runs phase 3 only,
skipping the CLI-registration steps — use it to refresh the corpus
after a tag bump.

## Developer workflow (from source)

Prerequisites:

- .NET 10 SDK 10.0.301 (managed by [`global.json`](../../global.json)).
- Podman or Docker (Compose v2).
- PowerShell 7 (the model fetch script targets `pwsh`).

### Bring up PostgreSQL

```bash
podman compose -f docker/compose.yaml up -d postgres
```

The container exposes port 5435 on the host so it can coexist with a
system PG on 5432. If you're pointing the binary at an externally-managed
PG on 5432, override `NUTANIX_MCP__ConnectionString` accordingly.
The compose file applies `init.sql`, which provisions the required
extensions and the `english_analyzer` text analyzer.

Verify:

```bash
podman exec nutanix-mcp-postgres psql -U nutanix -d nutanix -c '\dx'
```

The output must list `vector`, `vchord`, `vchord_bm25`, `pg_tokenizer`,
`pg_trgm`, and `plpgsql`.

### Apply migrations

```bash
dotnet ef database update --project src/NutanixMcp.Data
```

This applies the `Initial` and `AddBm25Column` migrations to the
`nutanix` database. The trigger that maintains `chunks.bm25v` is
provisioned as part of `AddBm25Column`.

### Fetch the ONNX models

```powershell
pwsh scripts/fetch-models.ps1
```

The script downloads `bge-m3` (about 2.27 GB, with `model.onnx` plus
`model.onnx_data`) into `models/embedder/` and `bge-reranker-v2-m3`
(about 1.14 GB) into `models/reranker/`. The script is idempotent and
guards each file by minimum size.

### Run the test suite

```bash
dotnet test
```

The Data integration suite expects a running PostgreSQL on port 5435 and
provisions a `nutanix_test` database on first use. The Embedding suite
gates real-model tests on the presence of the model files via
`SkippableFact`.

## Start the server

Stdio transport (default):

```bash
dotnet run --project src/NutanixMcp.Cli -- serve --transport stdio
```

HTTP transport:

```bash
dotnet run --project src/NutanixMcp.Cli -- serve --transport http --port 7456
```

A successful HTTP start exposes the MCP endpoint at
`http://127.0.0.1:7456/`. List the tool surface with:

```bash
curl -sS -X POST -H 'Content-Type: application/json' \
     -H 'Accept: application/json,text/event-stream' \
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' \
     http://127.0.0.1:7456/
```

The response lists all seven tools with their JSON schemas.

## Configuration via environment

Settings flow through `NUTANIX_MCP__*` environment variables. The most
common overrides:

| Variable | Default | Effect |
|---|---|---|
| `NUTANIX_MCP__ConnectionString` | `Host=localhost;Port=5435;…` | PostgreSQL connection string. |
| `NUTANIX_MCP__ModelsDir` | `models` | Root directory for `embedder/` and `reranker/`. |
| `NUTANIX_MCP__Gpu` | `false` | Enables CUDA when the GPU build is selected. |
| `NUTANIX_MCP__HttpPort` | `7456` | HTTP transport port. |

A file `nutanix-mcp.json` in the working directory is also merged into
configuration if present.
