# Training & RAG roadmap — nutanix-mcp

Status: **planning**, anchored 2026-05-01. Living document — every sprint
that ships a retrieval-quality lever updates the bench history table at the
bottom and the next sprint's hypothesis row.

## Audience

The "training" surface here is the cross-encoder fine-tune loop. We do
not retrain the bi-encoder (bge-m3) — its multilingual prior is the
whole reason we picked it, and arista-mcp's experiment shows
domain-fine-tuning a small embedder is more risk than reward for a
specialised tech corpus. The reranker is where leverage lives, and
arista's `feedback_v2m3_finetune_lands` proves it (top-1 +5 pp from a
single fine-tune cycle).

## Guiding constraints (informed by arista-mcp memory)

1. **No multi-query expansion.** `feedback_multiquery_regression` —
   rule-based expansion dilutes precision; default OFF.
2. **No listwise rerank.** `feedback_listwise_regression` — Qwen2.5-3B
   listwise lost 4.39 pp at 3.4σ; small general LLMs lack domain prior.
3. **No reranker swap to bge-base.** `feedback_reranker_model_swap` —
   no uplift on arista; v2-m3 is the path.
4. **Bench v2 from day 1.** `feedback_bench_expansion_result` — v1
   substring scoring masked three real regressions on arista. We start
   with chunk-ID gold labels.
5. **Top-1 measurement ceiling exists.** `feedback_top1_measurement_ceiling`
   — five levers converged to 72-74 % top-1 / 99.10 % top-10 before
   bench expansion revealed the wall. Re-bench *every* lever, not just
   the new ones.
6. **Reingest invalidates bench.** `feedback_parent_child_marginal` —
   marginal lever (+1 pp top-1, +0 pp top-10) ate 130 ms p95. Sub-σ at
   n=570. We run bench v2 after every reingest.

## Three pillars

### Pillar A — Retrieval (bi-encoder + BM25 + RRF)

**Sprint 003 — Embedding layer.** bge-m3 (1024d XLM-R) ONNX, zero
query prefix, mean-pooled `last_hidden_state`. Fixed hyperparameters:

- HNSW: `m=16`, `ef_construction=200`, `ef_search=100`,
  `iterative_scan=relaxed_order`, `max_scan_tuples=20000`.
- BM25: `english_analyzer` (stopwords nltk_english + porter2 stemmer).
- RRF k=60 (arista default).
- Halfvec storage (50 % smaller, equivalent recall).

**Sprint 006 — first bench cut.** Curated 100-q seed set with chunk-ID
gold labels. Baseline measurement before any tuning. Acceptance:
top-1 ≥ 80 %, top-10 ≥ 95 %.

**Sprint 008 — HNSW + BM25 sweeps.** Once a baseline is locked:

| sweep | grid | hypothesis |
|--|--|--|
| `ef_search` | 50, 100, 200, 400 | top-10 ↑ at the cost of p95 |
| `m` (rebuild) | 16, 32, 48 | recall ↑ on rare-token queries |
| RRF k | 30, 60, 100 | sparse-only-hit weighting tradeoff |
| BM25 weight | 0.4, 0.5, 0.6 (dense complement) | technical-term-heavy queries |
| `iterative_scan` | strict_order vs relaxed_order | latency floor |

Each sweep is one bench run; we keep the winner, then re-run the
prior winners against the new baseline (the "re-bench every lever"
rule).

### Pillar B — Reranking (cross-encoder)

**Sprint 003 — bge-reranker-v2-m3 (FP32, off-the-shelf).**
`hooman650/bge-reranker-v2-m3-onnx-o4` Optimum-O4 export. No fine-tune
yet. Acceptance: top-1 over Pillar A baseline by ≥ 3 pp.

**Sprint 003.5 — INT8 quantization.** `optimum-cli onnxruntime
quantize --avx512_vnni` on a 200-pair calibration set. Acceptance:
delta < 1 pp top-1 vs FP32, p95 ↓.

**Sprint 010 — domain fine-tune (the v2-m3 win recipe).** Replicates
arista's recipe with Nutanix triples:

1. Curate triples via `nutanix-mcp curate-triples`. Same hard-negative
   filter as arista (positive vs negative differ in BOTH doc AND product).
2. Target: 4000–6000 (q, pos, neg) triples. Arista used 4937.
3. Hyperparameters (locked to arista's win recipe):
   - Backbone: `BAAI/bge-reranker-v2-m3` (568M XLM-R-base).
   - Loss: BCE with `pos_weight=8`.
   - Multi-positive mining (each query may have ≥1 positive doc).
   - LR: 2e-5, batch 32, epochs 3, warmup 0.06.
   - Mixed precision FP16 on the 4070; arista did this on 5090s.
4. Export: ONNX → INT8 with avx512_vnni.
5. Acceptance gate: top-1 ≥ +3 pp over off-the-shelf v2-m3 on bench v2.
   Below that, we do not ship — arista's gate is the same.

**Sprint 011 — fine-tune iteration #2.** Add curated hard negatives from
production query logs (anonymised, 4-week window). Re-bench. Merge if
`Δ top-1 ≥ +1 pp` and no top-10 regression.

### Pillar C — Evaluation

Bench is the ground truth; everything else is hypothesis.

**Bench v2 schema** (chunk-ID gold labels):

```json
{
  "query_id": "ahv-vlan-001",
  "query": "How do I configure a VLAN on AHV virtual switch?",
  "expect_chunk_ids": [102, 487, 1234],
  "expect_doc": "AHV-Admin-Guide-v11_0",
  "expect_section": "AHV Virtual Switch VLAN Configuration",
  "expect_product": "ahv",
  "category": "ahv",
  "difficulty": "easy" | "medium" | "hard"
}
```

`expect_chunk_ids` is the union of acceptable hits — a query may have
several legitimate answer chunks (intro, procedure, reference). Top-N
metric is "any of `expect_chunk_ids` appears in the first N results".

**Initial query budget**: 100 hand-curated queries spread across all
16 categories (ahv, aos, kb, files, ndb, kubernetes, flow, prism-central,
prism-element, move, disaster-recovery, cloud-clusters, nki, pulse, lcm,
hardware-replacement). Targets:

- 50 % easy (verbatim term match).
- 30 % medium (paraphrased).
- 20 % hard (cross-doc, multi-hop, or terminology drift).

**Bench expansion trigger**: when top-10 saturates ≥ 99 % we expand
the bench (arista hit this wall at 588 q). Don't tune further until
the bench is harder.

**Bench history file**: `tests/fixtures/bench-history.jsonl`,
append-only, one row per `bench` invocation:

```json
{"timestamp": "2026-05-12T10:00:00Z", "label": "v0.2.0",
 "queries": 100, "top_1": 0.82, "top_5": 0.94, "top_10": 0.97,
 "p50_ms": 1450, "p95_ms": 2300,
 "embedder": "bge-m3", "reranker": "bge-reranker-v2-m3-fp32",
 "rrf_k": 60, "ef_search": 100, "rerank_top_n": 30,
 "git_sha": "abc123", "notes": "first cut, no fine-tune"}
```

Significance: at n=100 a 5 pp delta is ~1.0σ. We need n=300+ before
declaring a sub-3 pp win significant. This is the same trap arista
fell into at n=570.

### CI integration

| job | trigger | runs |
|--|--|--|
| unit | every PR | dotnet test (no Pgvector collection) |
| integration | every PR | dotnet test + `nutanix_test` PG |
| bench-smoke | every PR (label "bench") | 30-q smoke set, fail if top-10 < 80 % |
| bench-full | weekly cron | 100+ q, write bench-history.jsonl, post diff to issue |

## Sprint mapping

| sprint | pillar | what ships |
|--|--|--|
| 001 | infra | scaffold + docker |
| 002 | infra | data layer + migrations |
| **003** | **A + B** | **bge-m3 embedder + v2-m3 reranker (FP32)** |
| 003.5 | B | reranker INT8 quantization |
| 004 | A | hybrid retriever + 7 MCP tools |
| 005 | infra | CLI + migrate-from-python |
| **006** | **C** | **bench v2 first cut (100 q)** |
| 007 | infra | semgrep + PVS gate |
| **008** | **A** | **HNSW / BM25 sweeps** |
| 009 | infra | production deploy |
| **010** | **B** | **reranker fine-tune #1** |
| 011 | B | reranker fine-tune #2 (production-log triples) |
| 012 | C | bench expansion (300 q if top-10 saturates) |

## Out-of-scope (deferred to v0.3+)

- Embedder fine-tune. arista didn't try; bge-m3's multilingual prior
  is the user's stated requirement (RU queries vs EN docs). We don't
  have a meaningful Nutanix-specific embedder training set without
  ingesting customer logs, which we don't have.
- LLM rewriting / HyDE. Both regress on small models (`feedback`
  memories). Re-evaluate when a frontier-class model is reachable
  via local stdio (Qwen3 / Llama4 era).
- pg_textsearch BM25 swap. Tracked in `plan-v0.1.md` § 13. The
  v0.2/v0.3 experiment will re-bench the swap; do not pre-commit.

## Bench history (table-of-truth)

| date | label | git sha | n | top-1 | top-5 | top-10 | p50 | p95 | reranker | rrf k | ef_search |
|--|--|--|--|--|--|--|--|--|--|--|--|
| | | | | | | | | | | | |

(empty until Sprint 006 ships the first bench cut)
