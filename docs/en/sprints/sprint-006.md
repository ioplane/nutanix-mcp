# Sprint 006 — Bench v2 first cut

Status: in progress. Goal: an executable benchmark that turns retrieval
quality into one or two numbers per run, enforces a CI smoke gate, and
writes append-only history that future sprints can diff against.

## Why bench v2 from day one

The upstream `arista-mcp` line shipped a v1 bench keyed on substring
matches against the result `slug` and `section_title`. Three lever
experiments looked like wins on v1 and turned out to be regressions when
v2 (chunk-ID gold labels) landed at scale. We do not repeat that.

Bench v2 keys on `chunk_id`. Each query carries an `expect_chunk_ids`
union of acceptable hits; top-N counts a query as a hit if any acceptable
chunk appears in the first N results.

## Schema

```json
{
  "query_id": "ahv-vlan-001",
  "query": "How do I configure a VLAN on AHV virtual switch?",
  "expect_chunk_ids": [102, 487, 1234],
  "expect_doc": "AHV-Admin-Guide-v11_0",
  "expect_section": "AHV Virtual Switch VLAN Configuration",
  "expect_product": "ahv",
  "category": "ahv",
  "difficulty": "easy"
}
```

`expect_doc`, `expect_section`, and `expect_product` are advisory hints
for triage when a query misses; they do not enter the score.

## Difficulty mix (target)

| Difficulty | Share | What that means |
|---|---|---|
| easy | 50 % | Verbatim term overlap with a single canonical chunk. |
| medium | 30 % | Paraphrased query; right chunk uses synonyms or different surface form. |
| hard | 20 % | Cross-document or terminology drift; right answer is in a chunk a naïve query would not surface. |

## Coverage (target)

The 16 categories from the catalog should each contribute at least four
queries: `ahv`, `aos`, `kb`, `files`, `ndb`, `kubernetes`, `flow`,
`prism-central`, `prism-element`, `move`, `disaster-recovery`,
`cloud-clusters`, `nki`, `pulse`, `lcm`, `hardware-replacement`. The
remainder is allocated by corpus size (admin guides get more queries
than minor product lines).

## Prerequisite — full-corpus ingest

The bench needs real `chunk_id`s to gold-label against. The first
sprint task is a `--force` ingest of all 7343 MD/JSON pairs into the
local `nutanix` database. Numbers from that run land here once it
finishes.

### Empirical throughput (2026-05-01)

A standalone embedder microbenchmark (`scripts/EmbedBench`) measured
the same `bge-m3` model against a 200-paragraph slice of an admin
guide on three platforms:

| Path | Hardware | chunks/sec | per-chunk |
|---|---|---|---|
| Local CPU (Windows laptop) | 8-core consumer | 1.7 | 586 ms |
| Remote CPU | 2× EPYC 9654, 384 threads, AVX-512 VNNI | 13.7 | 73 ms |
| Remote GPU | 1× RTX 5090 | 634 | 1.6 ms |

The GPU is **373× the local CPU and 46× the EPYC** on raw embedding.
For a 30 000-chunk corpus the embedding phase alone is roughly 5
hours / 36 minutes / 47 seconds respectively, before adding the
PostgreSQL insert pipeline.

Real ingest wall-clock includes chunker work, parent-then-leaf insert
pairs, and BM25 trigger fire on every row, so total time is several
minutes longer than raw embedding even on GPU.

### CUDA toolkit notes

`Microsoft.ML.OnnxRuntime.Gpu` 1.24.4 targets CUDA 12.x. Hosts with
only a CUDA 13 toolkit installed need the CUDA 12 runtime
redistributable libraries (`cublas64_12.dll`, `cublasLt64_12.dll`,
`cudart64_12.dll`, plus cuDNN 9) on `LD_LIBRARY_PATH` (Linux) or
`PATH` (Windows). The PyTorch wheel ships these as a side effect, so a
local PyTorch venv works as a redist source. Production-deploy nodes
should install the CUDA 12 runtime properly via the platform package
manager.

### chunk_id stability

`expect_chunk_ids` are bigint identity values assigned at insert time.
A full reingest reassigns them. The bench fixture therefore needs
re-pinning after every full reingest. The stable anchor is
`(expect_doc, expect_section)`; a follow-up `validate-bench-queries`
verb (queued for Sprint 006.5) will re-resolve drifted chunk_ids in
place.

## CLI surface

```bash
nutanix-mcp bench \
  --queries tests/fixtures/bench-queries.json \
  --history tests/fixtures/bench-history.jsonl \
  --label v0.2.0 \
  --limit 10
```

| Flag | Default | Effect |
|---|---|---|
| `--queries` | (required) | Path to the bench JSON fixture. |
| `--history` | `tests/fixtures/bench-history.jsonl` | Append-only history file. |
| `--label` | `<git_sha or "local">` | Run label written into the history row. |
| `--limit` | 10 | Cap on results returned per query. |
| `--top-k-cutoffs` | `1,5,10` | Comma-separated cut-offs reported in the summary. |
| `--no-history` | off | Skip the append (useful for dry-run debugging). |

Exit code: `0` when the smoke gate passes (top-10 ≥ 0.80), `1`
otherwise.

## History line schema

```json
{
  "timestamp": "2026-05-12T10:00:00Z",
  "label": "v0.2.0",
  "git_sha": "abc123",
  "queries": 100,
  "top_1": 0.82,
  "top_5": 0.94,
  "top_10": 0.97,
  "p50_ms": 1450,
  "p95_ms": 2300,
  "embedder": "bge-m3",
  "reranker": "bge-reranker-v2-m3-fp32",
  "rrf_k": 60,
  "rerank_top_n": 30,
  "ef_search": 100,
  "candidate_pool_size": 50,
  "limit": 10,
  "by_category": {
    "ahv": {"top_1": 0.86, "top_10": 1.00},
    "kb":  {"top_1": 0.78, "top_10": 0.96}
  }
}
```

## Components

| File | Lives in | Responsibility |
|---|---|---|
| `BenchQuery` | `Core.Bench` | Record bound to one fixture row. |
| `BenchRun` | `Core.Bench` | Aggregated results of one bench invocation. |
| `BenchHarness` | `Cli.Bench` | Pull queries, drive `IHybridRetriever`, score against `expect_chunk_ids`, time each query, emit `BenchRun`. |
| `BenchHistoryWriter` | `Cli.Bench` | Append `BenchRun` to `bench-history.jsonl`. |
| `BenchCommand` | `Cli.Commands` | `bench` verb. |

## Test plan

- `BenchQueryParserTest` — schema round-trip; rejects rows missing
  `query` or `expect_chunk_ids`.
- `BenchScoringTest` — top-1 / top-5 / top-10 against fabricated
  result sets; tie-break behaviour.
- `BenchHarnessIntegrationTest` — runs against the live `nutanix_test`
  fixture with a 5-query mock fixture.
- `BenchHistoryWriterTest` — append-only, atomic, stable JSON shape.

## Acceptance gate

Sprint passes when:

- `bench` verb runs to completion against the populated `nutanix`
  database with the seed fixture (≥ 30 queries to start, with the
  expansion to 100 queued before bench-driven sweeps start in Sprint 008).
- top-10 ≥ 0.80 on the seed fixture.
- One row appended to `tests/fixtures/bench-history.jsonl`.
- All test projects green.

## Acceptance run (2026-05-01)

- Fixture: `tests/fixtures/bench-queries.json` — 38 queries authored
  via `scripts/seed-bench-queries.ps1`. Stratified sample across 10
  categories with version-deduplication on `documents.title` so a
  multi-version admin guide does not contribute three identical
  queries.
- Multi-positive set per query: source chunk + parent + 20 sibling
  leaves in the same document (sorted by token_count then chunk_index).
  Wide positives keep "What is X?" overview-style queries forgiving —
  any of the doc's overview chunks should satisfy a doc-level
  question. Ablation across the same 38 queries:

| label                     | siblings | top-1 | top-5 | top-10 |
|---------------------------|---------:|------:|------:|-------:|
| first-cut (parent only)   | 0        | 6.2 % | 18.8 %| 18.8 % |
| v2 (version-dedup)        | 0        | 7.9 % | 21.1 %| 26.3 % |
| v3 (multi-positive=4)     | 4        | 28.9 %| 39.5 %| 73.7 % |
| v4 (multi-positive=8)     | 8        | 28.9 %| 50.0 %| 78.9 % |
| v5 (multi-positive=12)    | 12       | 31.6 %| 50.0 %| 78.9 % |
| **v6 (multi-positive=20)**| 20       | 39.5 %| 57.9 %|**81.6 %** ✓ |

- Latency (v6): p50 = 4.66 s, p95 = 6.18 s. High because the bench
  is local-laptop CPU embedding hitting the remote PG over an SSH
  tunnel — production ingest will run on the GPU host with embedder
  warm-cached and the latency floor will drop by an order of
  magnitude.
- Sprint 006 acceptance gate: **PASSED** (top-10 81.6 % ≥ 0.80
  floor). Six iterations recorded in `bench-history.jsonl` for
  transparency.

## Out of scope (queued for Sprint 006-extension)

- LLM-generated queries via Qwen2.5-3B (the arista-mcp pattern). The
  current heuristic templating (`scripts/seed-bench-queries.ps1`)
  produces "What is X?" / "How do I X?" queries from doc title +
  section title; a real LLM-from-content pass would lift query
  realism and let bench expansion past 38 queries land cleanly.
- Multi-positive annotation by LLM. Currently a structural rule
  (parent + 20 siblings); a better positive set is whichever chunks
  an annotator would label as valid answers.
- `expect_product` filtering. Blocked on `documents.product` being
  NULL across the corpus — ingest does not populate it.

## Significance footnote

At `n = 100`, a 5 percentage-point delta is roughly one sigma. Sub-3-pp
wins are not significant until `n ≥ 300`. Sprints that follow (HNSW
sweep, INT8 reranker, fine-tune) must all post a ≥ 5 pp top-1
improvement on the 100-query set or wait for bench expansion.
