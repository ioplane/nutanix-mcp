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

### Empirical throughput (CPU, 2026-05-01)

The first ingest run on a developer laptop without CUDA averaged:

- About 6 minutes per document on the four largest admin guides
  (`AHV-Admin-Guide-v1{0_0,0_3,1_0,6_10}` at 690-830 leaves each).
- About 150 leaves per minute on small documents past the admin-guide
  burst.

Total wall-clock for the 7343-document corpus on CPU is roughly two
hours. The GPU build (`dotnet build -p:UseGpuOnnx=true`,
`NUTANIX_MCP__Gpu=true`) should land it in ~15 minutes on a 4070,
based on the OnnxRuntime CUDA profile. Production ingest pinning
should select GPU on machines that have it; the CPU path is the
default to keep the bench reproducible without CUDA.

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

## Significance footnote

At `n = 100`, a 5 percentage-point delta is roughly one sigma. Sub-3-pp
wins are not significant until `n ≥ 300`. Sprints that follow (HNSW
sweep, INT8 reranker, fine-tune) must all post a ≥ 5 pp top-1
improvement on the 100-query set or wait for bench expansion.
