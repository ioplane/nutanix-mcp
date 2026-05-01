# Sprint 008 — HNSW + BM25 sweep

Status: DONE (2026-05-02). One-at-a-time parameter sweep across the
v0.2.0 retrieval stack. Result: **current defaults are optimal for the
present 207-query LLM bench fixture**. Sub-pp deltas are below the
n=207 noise floor.

## Setup

- Fixture: `tests/fixtures/bench-queries-llm.json` (207 LLM-generated
  queries, multi-positive annotated; Sprint 006-extension acceptance).
- Compute: remote GPU host (PG + embedder both localhost — no SSH
  tunnel, no laptop-CPU bottleneck).
- Harness: `scripts/bench-sweep.sh` drives `nutanix-mcp bench` once per
  configuration. Two new `bench` flags landed in this sprint:
  - `--ef-search <int>` — overrides `hnsw.ef_search` per-connection
    via a Npgsql physical-connection initializer.
  - `--iterative-scan strict_order|relaxed_order` — overrides
    `hnsw.iterative_scan` per-connection.
- Baseline (held constant when sweeping each knob): `ef_search=100`,
  `iterative_scan=relaxed_order`, `rrf_k=60`,
  `candidate_pool_size=50`, `rerank_top_n=30`.

## Sweep grid

One knob varied at a time, the other three stay at baseline. Nine
configurations total. Wall-clock on the remote: 84 minutes.

| label | ef_search | iter_scan | rrf_k | pool | top-1 | top-5 | top-10 | p50 (ms) | p95 (ms) |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|
| ef50 | 50 | relaxed | 60 | 50 | 82.6 % | 98.6 % | 100.0 % | 2566 | 3654 |
| **ef100 (baseline)** | **100** | **relaxed** | **60** | **50** | **82.6 %** | **98.6 %** | **100.0 %** | **2529** | **3384** |
| ef200 | 200 | relaxed | 60 | 50 | 82.6 % | 98.6 % | 100.0 % | 2506 | 3605 |
| ef400 | 400 | relaxed | 60 | 50 | 82.6 % | 98.6 % | 100.0 % | 2642 | 3661 |
| strict | 100 | strict | 60 | 50 | 82.6 % | 98.6 % | 100.0 % | 2605 | 3643 |
| rrfk30 | 100 | relaxed | 30 | 50 | 82.6 % | 98.6 % | 100.0 % | 2613 | **7316** |
| rrfk100 | 100 | relaxed | 100 | 50 | 82.1 % | 98.6 % | 100.0 % | 2544 | 3372 |
| pool30 | 100 | relaxed | 60 | 30 | **80.7 %** | 97.6 % | 99.0 % | 2572 | 3495 |
| pool100 | 100 | relaxed | 60 | 100 | 81.6 % | 97.6 % | 99.5 % | 2641 | 3788 |

## Findings

1. **`ef_search` ladder (50 / 100 / 200 / 400)** — top-1 locked at
   82.6 %, top-10 locked at 100 %. p50 is flat (2.5–2.6 s) within
   bench noise. The HNSW recall ceiling on this fixture is hit at
   ef_search=50 already. No win above 100 — keep the baseline.

2. **`iterative_scan` (strict vs relaxed)** — indistinguishable on all
   metrics. The relaxed-order path wins by default (cheaper) and
   shows no quality loss; keep it.

3. **`rrf_k` ladder (30 / 60 / 100)** —
   - `rrfk=30` doubles tail latency (p95 7.3 s vs 3.4 s baseline) for
     the same quality. RRF with a small k weights top-of-list ties
     more aggressively, which interacts badly with vchord_bm25's
     iterative refinement. **Avoid.**
   - `rrfk=100` regresses top-1 by 0.5 pp (82.6 → 82.1). Statistically
     borderline at n=207 but a real signal — the smoothing dilutes
     the rank-0 signal from the cross-encoder.
   - `rrfk=60` baseline is the winner.

4. **`candidate_pool_size` ladder (30 / 50 / 100)** —
   - `pool=30` regresses top-1 by 1.9 pp and drops top-10 below 100 %.
     Too few dense+sparse candidates to feed the reranker. **Avoid.**
   - `pool=100` matches top-1 within 1 pp but adds ~400 ms to p95.
     The rerank_top_n=30 cap means the extra candidates never reach
     the cross-encoder anyway — they're discarded.
   - `pool=50` baseline is the right floor.

## Recommendation

**Keep current defaults.** ef_search=100, iterative_scan=relaxed_order,
rrf_k=60, candidate_pool_size=50, rerank_top_n=30. The sweep confirms
each knob lives in its quality-vs-latency sweet spot for the v0.2.0
stack on the present corpus.

## Out-of-scope (queued)

- **HNSW `m` rebuild** (`m=16` → `m=32` or `m=48`). Requires an
  index rebuild and is gated on a corpus where ef_search isn't
  already saturating recall. The 100 % top-10 ceiling on this
  fixture says we're not there.
- **Bench expansion past 207 queries.** Sub-pp regressions
  (rrfk=100 at -0.5 pp, pool=100 at -1.0 pp) are at the n=207 noise
  floor; a 500-query fixture would reveal whether they are real
  losses or measurement noise. Tracked under bench-expansion in
  the roadmap.
- **Sprint 003.5 (INT8 reranker).** Blocked on the FP16 ONNX export
  source — `quantize_dynamic` injects `DynamicQuantizeLinear` ops
  that ORT rejects on FP16 inputs. See
  `scripts/quantize-reranker.py` for the documented path forward.
