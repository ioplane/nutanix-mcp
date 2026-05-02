# Roadmap

The project ships in waterfall sprints. Each sprint has an explicit
acceptance criterion and a `git tag sprint-NNN-review` marker once it
passes. Sprints listed here as DONE have shipped on `main`.

## Sprint timeline

```mermaid
gantt
  title nutanix-mcp v0.1.0 sprints
  dateFormat YYYY-MM-DD
  section Foundation
  Sprint 001 — scaffold + docker     :done, s001, 2026-04-30, 1d
  Sprint 002 — Data layer            :done, s002, 2026-05-01, 1d
  section Retrieval
  Sprint 003 — Embedding             :done, s003, 2026-05-01, 1d
  Sprint 003.5 — INT8 reranker       :         s0035, after s003, 2d
  Sprint 004 — Hybrid retriever      :done, s004, 2026-05-01, 1d
  section Ingest and bench
  Sprint 005 — Cli + ingest          :done, s005, 2026-05-01, 1d
  Sprint 006 — Bench v2 first cut    :done, s006, 2026-05-01, 2d
  section Hardening
  Sprint 007 — Semgrep + PVS gate    :done, s007, 2026-05-01, 1d
  Sprint 008 — HNSW + BM25 sweeps    :done, s008, 2026-05-02, 1d
  section Production
  Sprint 009 — Deploy                :         s009, after s008, 2d
  Sprint 010 — Reranker fine-tune    :         s010, after s009, 5d
  Sprint 011 — Reranker iter 2       :         s011, after s010, 5d
```

## Status

| Sprint | Status | Key artefacts |
|---|---|---|
| 001 | DONE | Solution skeleton, package pinning, docker stack, init.sql with `english_analyzer`. |
| 002 | DONE | EF entities, repositories, `PgvectorFixture`, `Initial` and `AddBm25Column` migrations. 9/9 Data tests green. |
| 003 | DONE | bge-m3 embedder, bge-reranker-v2-m3 reranker, `RerankerFamilyDetector`, model fetch script. 15/15 Embedding tests green. |
| 003.5 | DONE (v0.3.0) | INT8 dynamic quantization of the fine-tuned reranker FP32 ONNX. 2.17 GB → 1.30 GB (1.67× compression). Production bench: top-1 80.2 % (-0.4 pp vs FP32, within ≤1 pp gate), p50 -26 %, p95 -25 %. Acceptance PASSED. |
| 004 | DONE | `HybridRetriever`, 7 MCP tools, stdio + HTTP hosts, `serve` Cli verb. 37/37 tests green plus live HTTP smoke. |
| 005 | DONE | `CatalogReader`, `DocumentLoader` (CRLF/marker/TOC strip, MD↔JSON pair), `SectionAwareChunker`, `IngestService`, `ingest` Cli verb. 52/52 tests green; live ingest + MCP search verified. |
| 006 | DONE | Bench v2 harness + history. ntx-mcp quality stack adopted. Code-review fixes merged. 3-platform throughput study (1.7/13.7/634 chunks/s). Remote ingest of 7343 documents complete (2026-05-01). First-cut: 38-query heuristic fixture, top-10 = 81.6 %. **Extension (2026-05-02)**: arista-mcp pipeline ported. **509-query LLM fixture (canonical baseline)**: top-1 = 79.0 %, top-5 = 99.0 %, top-10 = 100.0 %, p50 = 2.6 s, p95 = 3.7 s. |
| 007 | DONE | `scripts/pvs-studio-scan.ps1` runs locally. `semgrep` and `pvs-studio` CI jobs in `ci.yml` (PVS gated behind a license secret). `markdownlint-cli2` job. `.githooks/pre-commit` runs `dotnet format` against staged C# files. |
| 008 | DONE | One-at-a-time sweep across `ef_search` × `iterative_scan` × `rrf_k` × `candidate_pool_size`. 9 rows in `bench-history.jsonl`. Result: current defaults (ef=100, relaxed, rrf=60, pool=50) are at the quality-vs-latency sweet spot on the 207-q fixture. Sub-pp deltas below n=207 noise floor. See [`sprints/sprint-008.md`](./sprints/sprint-008.md). |
| 009 | Pending | Single-file publish for Windows, Linux, macOS on amd64 and arm64. Systemd unit and Windows service templates. |
| 010 | DONE | Cross-encoder fine-tune iteration 1: 4137 triples mined via bge-m3 hard-neg with multi-positive exclusion. XLM-R 568M × 3 epochs, BCE pos_weight=8, single 5090, 131 s. Hold-out NDCG@10 = 0.9942. **Production bench 509-q: top-1 = 80.6 % (+1.6 pp), top-5 = 99.4 %, top-10 = 100 %, p50 = 2.74 s.** See [`sprints/sprint-010.md`](./sprints/sprint-010.md). |
| 011 | Pending | Fine-tune iteration 2 with production-log hard negatives. |

## Key decisions and dependencies

| Decision | Driver | Recorded in |
|---|---|---|
| Use `halfvec(1024)` for `chunks.embedding` | Half precision halves storage at negligible recall cost. | [`architecture.md`](./architecture.md) |
| Pin EF Core to 9.0.15 | `Pgvector.EntityFrameworkCore` 0.3.0 transitively pulls 9.0.1; direct override is required. | [`architecture.md`](./architecture.md) |
| Postgres on port 5435 | Coexistence with the upstream `arista-mcp` deployment on 5434. | [`quickstart.md`](./quickstart.md) |
| HyDE, multi-query, listwise excluded from v0.1 | All three regressed on the upstream bench. | [`training-rag.md`](./training-rag.md) |
| Bench v2 from day one | Bench v1 substring scoring masked three real regressions on the upstream line. | [`training-rag.md`](./training-rag.md) |
| `pg_textsearch` BM25 swap deferred | Hold the v2-m3 win recipe constant; A/B against the swap when bench infrastructure is ready. | [`training-rag.md`](./training-rag.md) |
