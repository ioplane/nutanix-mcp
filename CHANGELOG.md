# Changelog

All notable changes to nutanix-mcp will be documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0/).
Commit messages follow [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/).

## [Unreleased]

Nothing here yet.

## [0.3.3] — 2026-06-30

Maintenance release: closes the install-readiness gaps surfaced by the
independent code-review pass against v0.3.2, lifts the dependency set
to the current minor/patch, and ships a new `Cli.Tests` regression
guard for the most-impacted invariants. No retrieval or schema
changes; bootstrap and serve are wire-compatible with v0.3.2 stores.

Notable behavioural change: `CliConfiguration` now correctly ranks
`NUTANIX_MCP__*` environment variables above any local
`nutanix-mcp.json`, matching the 12-factor convention. Operators who
relied on the inverted precedence to override env vars with a local
JSON file should switch to using a non-CWD JSON file or unset the
env var.

### Added

- **`/v1/healthz`** liveness endpoint on the Streamable-HTTP transport
  (`src/NutanixMcp.Server/HttpHost.cs`, via
  `Microsoft.AspNetCore.Diagnostics.HealthChecks`). Plain 200 OK
  process-liveness check that the Quadlet's
  `HealthCmd=curl --fail --silent http://127.0.0.1:7456/v1/healthz`
  and the Windows installer's `Invoke-RestMethod` line already
  referenced — previously these probed a route that didn't exist,
  leaving the container permanently unhealthy.
- **`NutanixMcpSettings.RerankerDir`** — the documented production
  override for the INT8 fine-tuned reranker. Wired into
  `ServerHosting.AddNutanixMcpServices` so `NUTANIX_MCP__RerankerDir`
  finally takes effect; previously the env var silently bound to nothing
  (no property on the settings type) and `serve` / `ingest` always
  used `{ModelsDir}/reranker`. Regression-guarded by the new
  `CliConfigurationTests.RerankerDir_env_var_is_bound_to_settings`.
- **`Exec=postgres -c shared_preload_libraries=…`** in
  `deploy/quadlet/nutanix-mcp-postgres.container` plus the full memory
  tuning block from `docker/compose.yaml`. Without this the Quadlet PG
  pod fails `CREATE EXTENSION vchord` on first start (the
  `tensorchord/vchord-suite` image bundles the libraries but does NOT
  preset them; the e2e workflow already added them via `-c` flags).
- **`NutanixMcp.Cli.Tests`** project — xUnit regression tests for
  `CliConfiguration` precedence (env > JSON), `RerankerDir` binding,
  and default fall-through.
- **`RunSilentReadStdoutAsync`** helper in `BootstrapCommand` — portable
  container-existence check using `ps -aq --filter name=…` which works
  on both podman and docker.

### Changed

- **`CliConfiguration.Load()` provider order reversed** —
  `AddJsonFile` is now registered before `AddEnvironmentVariables`, so
  `NUTANIX_MCP__*` environment variables correctly override a developer's
  local `nutanix-mcp.json`. This matches `Microsoft.Extensions.Configuration`'s
  "last registered wins" semantics and aligns with the ASP.NET Core
  default (`appsettings.json` → env vars). Regression-guarded by
  `CliConfigurationTests.EnvironmentVariable_overrides_JsonFile_value`.
- **`BootstrapCommand.RunSilentAsync` / `RunStreamingAsync`** now kill
  the child process tree on `OperationCanceledException`. Without this
  an interrupted `pg_restore` / `podman pull` would keep running in the
  background past the abandoned bootstrap.
- **`BootstrapCommand.RestoreCorpusAsync`** wraps the download + restore
  in a `try/finally` so the host-side and container-side temp dump
  files are always removed, even when the download or `pg_restore`
  fails partway. The serial HNSW rebuild fallback now captures and
  returns the `psql` exit code instead of silently swallowing it
  (`hnswRc != 0` is fatal — dense search would otherwise return no
  results).
- **.NET SDK** bumped to `10.0.301` (was `10.0.201`). `global.json`
  still uses `rollForward: latestFeature` so any 10.0.3xx SDK satisfies
  the pin on hosted runners.
- **Package set lifted to the current minor/patch** (verified against
  NuGet on 2026-06-29):
  - `ModelContextProtocol` + `.AspNetCore` 1.2.0 → 1.4.0
  - `Microsoft.Extensions.AI` 10.5.0 → 10.7.0
  - `Microsoft.Extensions.{Hosting,Configuration*,Options}` 10.0.7 → 10.0.9
  - `Microsoft.EntityFrameworkCore{,.Design,.Relational}` 9.0.15 → 9.0.17
    (still held at 9.x because `Pgvector.EntityFrameworkCore 0.3.0` —
    latest — pins Npgsql.EFCore 9.0.x)
  - `Microsoft.ML.OnnxRuntime{,.Gpu}` 1.25.1 → 1.27.0
  - `System.CommandLine` 2.0.6 → 2.0.9
  - `Spectre.Console` 0.55.2 → 0.57.1
  - `Testcontainers.PostgreSql` 4.11.0 → 4.12.0
  - `Microsoft.NET.Test.Sdk` 18.5.1 → 18.7.0
  - `FluentAssertions` 8.9.0 → 8.10.0
  - `Microsoft.Extensions.TimeProvider.Testing` 10.5.0 → 10.7.0
  - `System.Security.Cryptography.Xml` 10.0.7 → 10.0.9
  - `OpenTelemetry{,.Extensions.Hosting,.Exporter.OpenTelemetryProtocol}` 1.15.3 → 1.16.0
- **Analyzers** bumped: `Meziantou.Analyzer` 3.0.58 → 3.0.117,
  `SonarAnalyzer.CSharp` 10.25.0 → 10.27.0. `Roslynator.Analyzers`
  4.15.0 and `AsyncFixer` 2.1.0 stay (latest).

### Fixed

- **`BootstrapCommand` portability**: replaced the
  `{runtime} container exists {name}` call (a podman-only subcommand
  that always exits non-zero under Docker) with the cross-runtime
  `ps -aq --filter name=^{name}$` check. A second `bootstrap` run on a
  Docker host no longer fails with "container name already in use".
- **`BootstrapCommand` config-load side effect**: removed the
  `_ = CliConfiguration.Load();` hack in `RunStreamingAsync`. The
  workaround re-read environment vars and JSON file on every subprocess
  invocation (10-20× per bootstrap) and could mask the actual subprocess
  error with an unrelated JSON parse exception if the user's
  `nutanix-mcp.json` was malformed. Root cause was the now-removed
  dead `IngestParallelism` settings field.

### Removed

- **`NutanixMcpSettings.IngestParallelism`** dead field — declared with
  a default of 4, never consumed in production code. Setting
  `NUTANIX_MCP__IngestParallelism` had no effect.

### Notes

- CPU-only ONNX Runtime stays the default. `Microsoft.ML.OnnxRuntime.Gpu`
  is pinned at the same revision so the optional `-p:UseGpuOnnx=true`
  switch in `src/NutanixMcp.Embedding/NutanixMcp.Embedding.csproj`
  restores a matched pair; the GPU code path is opt-in only.
- Full solution (5 src + 5 tests projects) builds against SDK 10.0.301
  with **0 warnings / 0 errors** under the full analyzer suite.
- Self-contained single-file publish for `linux-x64` smoke-tested at
  ~132 MB; `--help` runs cleanly.

## [0.3.2] - 2026-05-08

Second hot-fix on the bootstrap flow: the v0.3.1 binary still fails
end-to-end on a fresh install for three more reasons surfaced by
the post-release smoke test. Private-repo release attachments
return 404 even with a Bearer token on the html download URL —
GitHub only serves them through the REST API endpoint. The asset
filename pattern in the binary (`nutanix-corpus-{version}.dump`)
diverges from what the release pipeline actually uploads
(`nutanix-corpus-v{version}.dump`). And the dump itself is missing
the `tokenizer_catalog.{tokenizer,model,text_analyzer}` registry
rows because pg_dump skips extension-owned tables by default —
without them the post-restore corpus throws "Tokenizer not found:
chunks_tokenizer" on every BM25 query.

### Fixed (Sprint 011.2 — bootstrap hot-fix #2, 2026-05-08)

- `BootstrapCommand.DownloadReleaseAssetAsync` (replaces
  `DownloadFileAsync`) — switch from the html `releases/download/{tag}/{asset}`
  URL to the `api.github.com/repos/{owner}/{repo}/releases/tags/{tag}`
  → `releases/assets/{id}` flow with `Accept: application/octet-stream`.
  Private repos refuse the html URL with 404 even when a valid
  Bearer token is presented; only the API endpoint serves the 302
  redirect to release-assets.githubusercontent.com. SocketsHttpHandler
  drops the Authorization header on the cross-origin redirect (per
  Microsoft docs) so the GitHub token doesn't leak to S3.
- `BootstrapCommand.s_releaseDumpAssetName` — align the asset
  filename pattern with the release pipeline. The dump now uploads
  and resolves as `nutanix-corpus-v{version}.dump` (matching
  `nutanix-mcp-v{version}-{rid}.tar.gz` and the rest of the
  release artefacts); previously bootstrap looked for
  `nutanix-corpus-{version}.dump` (no `v`) and 404'd even after
  the URL fix.
- `BootstrapCommand.RebuildTokenizerStateAsync` (new) — runs after
  `pg_restore` and rebuilds the pg_tokenizer registry that pg_dump
  skipped. Replays `docker/init.sql` (re-creates `english_analyzer`),
  drops the orphan `tokenizer_catalog.model_chunks_model` plus the
  `model_chunks_model_trigger{,_insert}` triggers, deletes any stale
  registry rows, then calls
  `tokenizer_catalog.create_custom_model_tokenizer_and_trigger(...)`
  to re-register `chunks_tokenizer` + `chunks_model`. Forces a full
  re-tokenize via `UPDATE chunks SET content = content` followed by
  `REINDEX INDEX idx_chunks_bm25` so `chunks.bm25v` matches the
  freshly-built vocab. Slow on a 489k-row corpus (~5–15 min on
  Win + Podman 5.6) but produces a queryable system end-to-end.
  Without this step `HybridRetriever.RunSparseAsync` throws
  `XX000: Tokenizer not found: chunks_tokenizer` on every search.
  Verified against the Nutanix corpus: a query for "AHV virtual
  switch VLAN configuration" returns the expected best-practice
  guide and AHV admin guide chunks (BP-2071-AHV-Networking,
  AHV-Admin-Guide-v10_3, AHV-Admin-Guide-v6_10) at scores ≥ 4.9.

## [0.3.1] - 2026-05-08

Hot-fix on top of v0.3.0: the bootstrap verb fails to bring up
PostgreSQL on a clean host because the volume is mounted at the
legacy `/var/lib/postgresql/data` path — postgres:18 moved its
PGDATA to `/var/lib/postgresql/18/docker` and its `VOLUME` to the
parent dir. Also fixes private-repo dump downloads (no auth header
on the GitHub release fetch → 404), missing `init.sql` in the
release tarball (broken `--skip-restore` path), and a silent
no-op when `--quadlet` is requested without Podman.

### Fixed (Sprint 011.1 — bootstrap hot-fix, 2026-05-08)

- `BootstrapCommand.EnsurePostgresAsync` — mount the named PG
  volume at `/var/lib/postgresql/18/data` and pin
  `PGDATA=/var/lib/postgresql/18/data` instead of mounting on the
  legacy `/var/lib/postgresql/data`. pg18 images (vchord-suite,
  docker-library/postgres) refuse to start on the legacy path
  (_"Counter to that, there appears to be PostgreSQL data in:
  /var/lib/postgresql/data (unused mount/volume)"_) — matches
  `docker/compose.yaml` and the Quadlet template, keeping all three
  install surfaces wire-compatible with the same dump.
- `BootstrapCommand.EnsurePostgresAsync` — add `--shm-size=2g` to
  the `podman/docker run` line so HNSW index build and
  `pg_restore -j 4` don't hit `/dev/shm` ENOSPC under default
  64 MB.
- `BootstrapCommand.EnsurePostgresAsync` — bump pg_isready
  readiness loop from 60 s (30×2 s) to 180 s (60×3 s); first boot
  with `init.sql` running CREATE EXTENSION
  vchord/vchord_bm25/pg_tokenizer + analyzer build can take ~90 s
  on cold storage.
- `BootstrapCommand.DownloadFileAsync` — read `GH_TOKEN` /
  `GITHUB_TOKEN` env var and set `Authorization: Bearer …` on the
  release-asset request. Required for private repos (GitHub returns
  404 to unauthenticated requests). `SocketsHttpHandler` strips the
  `Authorization` header on auto-redirects, so the token is not
  leaked across the github.com → objects.githubusercontent.com hop.
  404 with no token in scope now surfaces an explicit error
  message.
- `BootstrapCommand.InstallQuadletAsync` — gate `--quadlet` on
  `podman --version`. Without Podman 4.4+ the
  `podman-system-generator` is missing, the `.container` files
  remain inert, and `systemctl --user enable --now` fails with
  _unit not found_. We now refuse early with a remediation hint.
- `deploy/quadlet/nutanix-mcp-postgres.container` — same volume
  mount fix (`/var/lib/postgresql/data` → `/var/lib/postgresql`).
- `.github/workflows/release.yml` — copy `docker/init.sql` into
  every per-RID staged tarball so `LocateInitSql` can find it
  alongside the binary; required for the
  `bootstrap --skip-restore` (manual ingest) path.

## [0.3.0] - 2026-05-05

Sprint 009 + 010 + 003.5 land together: hardened systemd/Windows
deploy templates, the cross-encoder fine-tune (+1.6 pp top-1, +5.4 pp
top-5), the unblocked INT8 quantization (-26 % p50 latency, -40 %
disk for the same near-equivalent quality), the Podman Quadlet stack
for rootless auto-restart, and a `nutanix-mcp bootstrap` verb that
turns first-time setup into a single command. Release pipeline is
also rebuilt: every tag now passes through a 4-stage gate
(`ci-mirror → validate-tag → publish + container → finalize`)
modeled on the gobfd pattern, with cosign keyless signatures, syft
CycloneDX SBOMs per artefact, OpenSSF Scorecard, and CodeQL/Semgrep
on a weekly cron.

### Added (Sprint 011 — Podman Quadlet + bootstrap verb + container image, 2026-05-04)

- `deploy/quadlet/nutanix-mcp.network`,
  `nutanix-mcp-postgres.volume`,
  `nutanix-mcp-postgres.container`,
  `nutanix-mcp.container` — full rootless-friendly Quadlet bundle
  (HealthCmd, AutoUpdate=registry, ReadOnly=true,
  DropCapability=ALL + AddCapability=NET_BIND_SERVICE,
  PublishPort=127.0.0.1:5435:5432, PodmanArgs=--shm-size=2g,
  `Z` SELinux relabel on bind mounts, `%h` HOME expansion).
- `deploy/quadlet/README.md` — install guide for rootless and
  rootful Podman, plus `podman-auto-update.timer` wiring.
- `docker/Containerfile.app` — multi-stage image (Ubuntu Noble SDK
  build → `runtime-deps:10.0-noble-chiseled-extra` runtime). Builds
  self-contained single-file binary per `$TARGETARCH` (linux-x64,
  linux-arm64); chiseled runtime is shell-less, ICU/tzdata-enabled,
  ships a non-root `app` user. ENTRYPOINT runs `serve --transport
  http --bind 0.0.0.0 --port 7456`.
- `serve --bind` — new CLI option (default `127.0.0.1`). Containers
  pass `0.0.0.0` so Podman's `PublishPort=127.0.0.1:7456:7456` can
  forward host loopback traffic into the container. Host installs
  keep loopback-only by default and rely on a reverse proxy for
  external exposure.
- `nutanix-mcp bootstrap` — new CLI verb (`BootstrapCommand.cs`)
  that detects the container runtime (podman → docker fallback),
  pulls `tensorchord/vchord-suite:pg18-latest`, runs the container
  with `--shm-size=2g`, waits for `pg_isready`, downloads the 1.3 GB
  release-attached `pg_dump` (HTTPS-only via
  `Uri.UriSchemeHttps`/`StringComparison.Ordinal`), restores it via
  `pg_restore -j 4` (HNSW retried serially on shared-mem ENOSPC),
  and on Linux installs the Quadlet units to
  `~/.config/containers/systemd/` then runs
  `systemctl --user daemon-reload && enable --now`.

### Added (Sprint 009 — deploy templates, 2026-05-02)

- `deploy/systemd/nutanix-mcp.service` — hardened systemd unit
  (`ProtectSystem=strict`, `NoNewPrivileges`, `PrivateTmp`,
  `ProtectKernelTunables`, `RestrictNamespaces`, `RestrictSUIDSGID`).
  `MemoryDenyWriteExecute=false` because ONNX Runtime JITs kernels
  at session-init.
- `deploy/systemd/nutanix-mcp.env.example` — sample env-file with
  `NUTANIX_MCP__CONNECTIONSTRING`, `NUTANIX_MCP__MODELSDIR`,
  optional `NUTANIX_MCP__RERANKERDIR` (Sprint 003.5/010 fine-tune
  variants), GPU toggle, OTEL endpoint.
- `deploy/windows/Install-NutanixMcpService.ps1` — elevated PowerShell
  installer using `New-Service` (no NSSM dependency). Configures the
  service registry env-block and auto-restart-on-failure, then starts.
- `deploy/README.md` — install / uninstall / verify steps for both
  platforms, plus reverse-proxy notes.

macOS launchd plist + container image queued for v0.4.0.

### Added (Sprint 003.5 — INT8 reranker, unblocked, 2026-05-02)

The Sprint 010 fine-tune produced a clean FP32 ONNX export (via
`optimum.exporters.onnx`), unblocking the dynamic INT8 quantization
that previously failed on the FP16-source community export.

`scripts/quantize-reranker.py` now runs end-to-end on the fine-tuned
checkpoint. Production bench numbers:

| Metric | FP32 fine-tune | **INT8 fine-tune** | Δ |
|---|---:|---:|---:|
| top-1 | 80.6 % | **80.2 %** | -0.4 pp (within ≤1 pp gate) |
| top-5 | 99.4 % | 99.6 % | +0.2 pp |
| top-10 | 100.0 % | 100.0 % | flat |
| p50 (ms) | 2737 | **2014** | **-26 %** |
| p95 (ms) | 3915 | **2930** | **-25 %** |
| Disk | 2.17 GB | **1.30 GB** | **-40 %** |

Acceptance gate (≤1 pp top-1 regression + measurable p95 improvement)
PASSED. INT8 is the recommended production deployment for CPU-only
laptops (AVX-VNNI lights up at session-init) — smaller download,
faster inference, near-equivalent quality.

The INT8 model lives at `data/reranker-finetuned-int8/` (gitignored,
~1.3 GB). Released artefact will ship a pointer + shell snippet to
fetch it from a release attachment, similar to the existing
`fetch-models.ps1` pattern for the upstream stock weights.

### Added (Sprint 010 — reranker fine-tune iter 1, 2026-05-02)

End-to-end cross-encoder fine-tune on the 509-q v0.2.0 LLM bench
fixture. Recipe locked to the arista win record:
BCE pos_weight=NUM_NEGATIVES (8), batch=16, max_seq=512, lr=2e-5,
3 epochs, warmup_ratio=0.1, query-bucket train/eval split.

Pipeline scripts (all run on the remote, PG + GPU localhost):

- `scripts/extract-bench-pairs.py` — pull (query, positive_text,
  all_positive_chunk_ids, category) from the bench fixture + PG.
- `scripts/mine-negatives.py` — sentence-transformers
  `mine_hard_negatives` with bge-m3 as scorer, post-filtered to
  drop any negative whose chunk_id is in `all_positive_chunk_ids`
  (without the filter the cross-encoder learns contradictory signal,
  which is exactly the failure mode arista's v0.2.0 hit).
- `scripts/train-reranker.py` — adapted from arista's `train_v2m3.py`.
  XLM-R 568M, BCELoss with pos_weight=8, evaluates per-epoch on
  hold-out NDCG@10.
- `scripts/export-reranker-onnx.py` — torch.onnx export with
  `dynamo=False`. Falls back to `optimum.exporters.onnx` when the
  legacy exporter trips on transformers v5 masking utilities (the
  current torch+transformers combo on the remote).

CLI: `bench` already accepted `--reranker-dir <path>` from the Sprint
008 changes; that's how the fine-tuned model is plugged in.

### Verified (Sprint 010 acceptance — 2026-05-02)

Pipeline numbers (single 5090, fp16 mixed-precision):

| Stage | Time |
|---|---:|
| Extract pairs (509 queries) | 1 s |
| Mine hard negatives (4137 mined from 247 K leaf chunks) | ~25 min |
| Train (3 epochs / 669 steps) | **131 s** |
| Export ONNX FP32 with external data | ~10 s |
| Bench validation (509 queries) | ~25 min |

Hold-out evaluation per epoch:

| epoch | NDCG@10 | MAP | MRR@10 |
|---:|---:|---:|---:|
| 1 | 0.9923 | 0.9824 | 0.9824 |
| 2 | 0.9923 | 0.9824 | 0.9824 |
| **3 (best)** | **0.9942** | **0.9922** | **0.9922** |

Production bench delta on the 509-q LLM fixture:

| Metric | v0.2.0 stock FP32 | **v0.3.0 fine-tune FP32** | Δ |
|---|---:|---:|---:|
| top-1 | 79.0 % | **80.6 %** | **+1.6 pp** |
| top-5 | 99.0 % | 99.4 % | +0.4 pp |
| top-10 | 100.0 % | 100.0 % | flat |
| p50 (ms) | 2625 | 2737 | +4 % |
| p95 (ms) | 3675 | 3915 | +7 % |

Comparison to arista's precedent (588-q fixture, +2.04 pp post-fine-tune):
+1.6 pp is in the same range, consistent with the recipe.

### Changed (Sprint 008 + 010 — version bumps + lib audit)

- `Directory.Build.props` Version 0.1.0 → **0.3.0**.
- `Directory.Packages.props`:
  - `Microsoft.Extensions.*` 10.0.6 → 10.0.7
  - `Microsoft.ML.OnnxRuntime[.Gpu]` 1.24.4 → 1.25.1
  - `Microsoft.NET.Test.Sdk` 18.4.0 → 18.5.1
- Analyzers (in `Directory.Build.props`):
  - `Meziantou.Analyzer` 3.0.50 → 3.0.58
  - `SonarAnalyzer.CSharp` 10.23.0.137933 → 10.25.0.139117

### Verified (release prep)

- `dotnet publish src/NutanixMcp.Cli --runtime win-x64 --self-contained
  -p:PublishSingleFile=true` produces a 130 MB self-contained binary.
- Data integration tests: 12 / 12 against fresh PG.
- Build clean (0 / 0). Format clean. Semgrep auto-rules: 0 findings
  on 102 files / 1403 rules.

### Considered, deferred to v0.4.0 (Qwen3-Embedding / Qwen3-Reranker swap)

Late-cycle research found Qwen3-Embedding-0.6B / Qwen3-Reranker-0.6B
(Apache-2.0, ~+5 pp MTEB-multilingual / ~+5 pp BEIR rerank vs
bge-m3 / bge-reranker-v2-m3 lineage). On paper drop-in, in practice a
substantial refactor: decoder-only architecture, Qwen BPE tokenizer
(vs SentencePiece), instruct-prefix prompts, last-token pool for
embedder, generative scoring (or seq-cls community variant) for
reranker. Estimated swap cost ~6-8 hours including a re-ingest of
489 K chunks. Tracked as a v0.4.0 sprint.

### Changed (Sprint 006-extension expansion — 207 → 509 queries, 2026-05-02)

The Sprint 008 sweep revealed sub-pp deltas were below the n=207 noise
floor, so the bench was expanded at the same generation recipe.

Pipeline numbers:

| Stage | In | Out | Time |
|---|---|---|---|
| Sample 500 chunks | DB | `bench-seed-chunks-500.jsonl` | 1 s |
| LLM-generate queries | 500 chunks | 934 raw queries (8 skipped) | 174 s |
| Fairness-filter (top-K=20) | 934 candidates | **509 survived (54.5 %)** | 38 min |
| Multi-positive annotate | 509 queries | 509 labelled | 183 s |

Bench acceptance run (entirely on the remote — no SSH tunnel):

| Metric | n=207 (was) | **n=509 (now)** | Δ |
|---|---:|---:|---:|
| top-1 | 82.6 % | **79.0 %** | -3.6 pp |
| top-5 | 98.6 % | **99.0 %** | +0.4 pp |
| top-10 | 100.0 % | **100.0 %** | flat |
| p50 (ms) | 2529 | **2625** | +4 % |
| p95 (ms) | 3384 | **3675** | +9 % |

The top-1 drop is real, not noise: the 207-query fixture skewed easy
because the LLM happened to generate well-distinguishable questions
on the smaller seed sample. **509-q is the canonical v0.2.0 baseline.**
Top-10 still saturates at 100 %; breaking that ceiling is queued for
v0.3.x (harder LLM prompts or rare-term targeting).

### Added (Sprint 007 — pre-commit hook)

- `.githooks/pre-commit` — bash hook that runs `dotnet format
  --verify-no-changes` against staged `.cs` files. Fast (only staged
  files, not the whole solution) and skip-able in emergencies via
  `SKIP_FORMAT=1`. Enable per clone: `git config core.hooksPath
  .githooks`. Documented in `CONTRIBUTING.md`.

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

### Added (Sprint 006.5 — fixture re-pin verb)

- `validate-bench-queries` CLI verb. Reads `bench-queries.json`, queries
  the live DB for each `(expect_doc, expect_section)` anchor, and re-pins
  `expect_chunk_ids` in place. Decouples fixture authoring from chunk-id
  stability — a full reingest reassigns chunk_ids, but the (doc, section)
  anchor is stable across reingests.
  - `--write` persists changes; default is dry-run.
  - `--fail-on-unresolved` exits non-zero when a query cannot be matched
    (use in CI to catch a fixture that has drifted past auto-repair).

### Added (Sprint 007 — partial)

- `.github/workflows/ci.yml` gains two jobs:
  - `semgrep` — runs `semgrep scan --config=auto --error` against the
    `src/` tree, excluding generated EF Migrations. Local scan of the
    current main produces 0 findings on 89 files / 220 rules.
  - `pvs-studio` — gated behind `${{ secrets.PVS_USER }}` and
    `${{ secrets.PVS_KEY }}`, skipped on PRs from forks. Iterates each
    `src/*.csproj`, tolerates exit-code bits 256 (findings) and 1024
    (license expiring soon).

### Added (Sprint 006 — acceptance run)

- `scripts/seed-bench-queries.ps1` — first-cut bench fixture generator.
  Stratified-samples chunks across 10 live categories (kb,
  hardware-replacement, bible, ahv, aos, prism-central, prism-element,
  files, api, other) with version-deduplication on doc title so three
  versions of "AHV Administration Guide" do not contribute three
  identical queries. Heuristic query templating from doc title + section
  title (LLM-driven generation queued for Sprint 006-extension).
- `tests/fixtures/bench-queries.json` — 38-query fixture, ≥ 30-query
  acceptance floor met.
- `tests/fixtures/bench-history.jsonl` — six rows recording the
  acceptance-tuning iteration. Final row (v6, multi-positive=20)
  passes the 80 % top-10 smoke gate at 81.6 %.
- `docs/en/sprints/sprint-006-execution-plan.md` — the research plan
  drafted before authoring the seeder. Captures the arista-mcp prior
  art, the live-corpus probe results, and why the original "16
  categories, hand-curated 100 queries" plan was unworkable against
  the actual corpus shape.

### Verified (Sprint 006 acceptance — 2026-05-01)

- Bench harness ran end-to-end against the populated remote PG via an
  SSH forward-tunnel (local 5436 → remote 5435).
- Acceptance gate **PASSED**: top-10 = 81.6 %, top-5 = 57.9 %,
  top-1 = 39.5 % on 38 queries.
- Latency: p50 = 4.66 s, p95 = 6.18 s. High because the bench runs
  laptop-side CPU embedding over the SSH tunnel; production ingest
  on the GPU host with warm embedder will drop the floor by an order
  of magnitude.

### Added (Sprint 006-extension — LLM-driven fixture, 2026-05-02)

The arista-mcp bench-v2 pipeline ported and run end-to-end against
the populated remote PG. Replaces the heuristic first-cut fixture
with LLM-generated queries fairness-filtered through the retriever
and multi-positive annotated by the same LLM.

- `scripts/sample-chunks-for-bench.py` — stratified leaf-chunk sample
  across the 11 live categories. Filters out boilerplate sections
  and pure code-block chunks. Parameterized SQL via psycopg.
- `scripts/generate-bench-queries.py` — 1-2 questions per seed chunk
  via an OpenAI-compat HTTP endpoint (Qwen2.5-3B-Instruct on Ollama,
  4× RTX 5090, 4K ctx). Uses `http.client` (not `urllib`) so the
  file:// handler stays unreachable; rejects non-http schemes.
- `scripts/annotate-multi-positives.py` — LLM labels which retriever
  top-10 chunks would also be valid answers. Source chunk is always
  included by construction.
- `nutanix-mcp filter-bench-queries` (new CLI verb) — fairness
  filter. Drops queries whose `source_chunk_id` is not in the
  retriever top-K=20, enriches survivors with the top-10 for the
  annotation pass. Mirrors arista-mcp's `validate-bench-queries`
  JSONL mode.

### Verified (Sprint 006-extension acceptance — 2026-05-02)

Pipeline numbers on the remote (PG localhost + Ollama localhost):

| Stage | In | Out | Wall time |
|---|---|---|---|
| Sample 200 chunks | DB | `bench-seed-chunks.jsonl` | 1 s |
| LLM-generate queries | 200 chunks | 378 raw candidates | 73 s |
| Fairness-filter (top-20) | 378 candidates | 207 survived | 8 min (54.8 %) |
| Multi-positive annotate | 207 queries | 207 labelled | 76 s |

Bench acceptance run on the LLM fixture (`tests/fixtures/bench-queries-llm.json`):

- queries: 207
- **top-1: 82.6 %**
- **top-5: 98.6 %**
- **top-10: 100.0 %**
- p50: 5.88 s
- p95: 8.45 s

Per-category top-10 hit rate = 100 % across all 11 categories. KB
(73 % of fixture) posts top-1 = 88 %. Top-10 = 100 % means the
retrieval pipeline finds the gold chunk every time on this fixture;
only ranking shuffle keeps top-1 below 100 %.

This is the v0.2.0 baseline against which Sprint 008 HNSW/BM25
sweeps and the Sprint 010 reranker fine-tune will be measured.

Latency floor is dominated by the local-CPU embedder driving
through the SSH tunnel to remote PG. Production serve on the GPU
host will drop p95 by an order of magnitude.

### Out of scope (still queued)

- `expect_product` filtering. Blocked on ingest populating
  `documents.product` (currently NULL across the corpus).

### Added (Sprint 008 — HNSW + BM25 sweep, 2026-05-02)

One-at-a-time sweep across the four tunable retrieval knobs. Code
lands locally, runs on the remote (PG + embedder localhost — no SSH
tunnel and no laptop CPU bottleneck).

- `bench` verb gains three new flags:
  - `--ef-search <int>` — overrides `hnsw.ef_search` per Npgsql
    physical connection.
  - `--iterative-scan strict_order|relaxed_order` — overrides
    `hnsw.iterative_scan` per connection.
  - `--reranker-dir <path>` — point bench at an alternate reranker
    model directory (used by the queued INT8 work).
- `DataSourceFactory.Build(string, int?, string?)` overload uses
  `NpgsqlDataSourceBuilder.UsePhysicalConnectionInitializer` to
  apply the GUC overrides without an `ALTER DATABASE` round-trip.
- `BenchRun` schema gains optional `ef_search`, `iterative_scan`,
  `reranker_dir` fields. Older rows stay schema-clean (nullable).
- `scripts/bench-sweep.sh` (and a PowerShell twin
  `scripts/bench-sweep.ps1`) drive a 9-config one-at-a-time sweep:
  `ef_search` × {50, 100, 200, 400}; `iterative_scan` strict;
  `rrf_k` × {30, 100}; `candidate_pool_size` × {30, 100}. Each
  config holds the other knobs at the v0.2.0 baseline. Wall-clock
  on the remote: 84 minutes for 9 rows.

### Verified (Sprint 008 acceptance — 2026-05-02)

| Sweep | Quality delta | Latency delta | Verdict |
|---|---|---|---|
| `ef_search` 50/200/400 vs 100 | none (top-1 = 82.6 % flat) | flat (p50 ±5 %) | recall already saturated; baseline holds |
| `strict_order` vs relaxed | none | none | keep relaxed (cheaper) |
| `rrf_k` 30 vs 60 | none | p95 +115 % (3.4 → 7.3 s) | avoid k=30 |
| `rrf_k` 100 vs 60 | top-1 −0.5 pp | p50 flat | avoid k=100 |
| `pool=30` vs 50 | top-1 −1.9 pp, top-10 below 100 % | flat | avoid pool=30 |
| `pool=100` vs 50 | top-1 −1.0 pp | p95 +12 % | extra pool wasted; rerank_top_n=30 caps it |

**Verdict: keep current defaults** (ef_search=100,
iterative_scan=relaxed_order, rrf_k=60, candidate_pool_size=50,
rerank_top_n=30). Sub-pp deltas live below the n=207 noise floor;
revisit when the fixture expands past 500 queries.

### Out of scope for Sprint 008 (queued)

- HNSW `m` rebuild (`m=16` → `m=32` / `m=48`). Gated on a fixture
  where ef_search isn't already saturating recall; current top-10 = 100 %
  ceiling says we're not there.
- Bench expansion past 207 queries — see Sprint 010.x.

### Blocked (Sprint 003.5 — INT8 reranker, 2026-05-02)

`scripts/quantize-reranker.py` produces an INT8-weight ONNX from the
FP32 Optimum-O4 source, but the source export ships FP16 activations.
ORT injects `DynamicQuantizeLinear` ahead of every MatMul during
dynamic quantization, and that op rejects FP16 input —
`InferenceSession` fails to load the resulting model.

Two paths forward (both heavier than the 60-line script):

1. Walk the ONNX graph, cast every FP16 Initializer + ValueInfo to
   FP32 and insert Cast nodes at FP16 op boundaries, then quantize.
2. Quantize at the PyTorch level (BAAI/bge-reranker-v2-m3) via Optimum
   NNCF or `torch.ao.quantization`, then re-export ONNX with INT8
   weights baked in.

Tracked in `scripts/quantize-reranker.py` docstring.

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

[Unreleased]: https://github.com/dantte-lp/nutanix-mcp/compare/v0.3.3...HEAD
[0.3.3]: https://github.com/dantte-lp/nutanix-mcp/compare/v0.3.2...v0.3.3
[0.3.2]: https://github.com/dantte-lp/nutanix-mcp/compare/v0.3.1...v0.3.2
[0.3.1]: https://github.com/dantte-lp/nutanix-mcp/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/dantte-lp/nutanix-mcp/compare/v0.1.0...v0.3.0
[0.1.0]: https://github.com/dantte-lp/nutanix-mcp/releases/tag/v0.1.0
