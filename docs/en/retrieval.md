# Retrieval pipeline

The hybrid retriever combines a dense path (pgvector cosine over
`halfvec(1024)` HNSW) and a sparse path (vchord_bm25), fuses results via
Reciprocal Rank Fusion at `k = 60`, hydrates parents for the cross-encoder
reranker, and returns ranked chunks with per-stage diagnostics.

## End-to-end flow

```mermaid
flowchart TD
  q[Query string]
  expand[QueryExpander<br/>Nutanix acronyms]
  cache{Query embedding cache}
  embed[OnnxEmbedder<br/>bge-m3]
  dense[(Postgres dense<br/>HNSW + halfvec)]
  sparse[(Postgres sparse<br/>vchord_bm25)]
  rrf[Reciprocal Rank Fusion<br/>k = 60]
  adapt{Top-5 spread &le; 0.02?}
  hyd[Fetch parent texts<br/>single SELECT]
  rerank[XlmRobertaOnnxReranker<br/>bge-reranker-v2-m3]
  dedup{DedupPerSection?}
  out[Take(Limit) +<br/>SearchDiagnostics]

  q --> expand --> cache
  cache -- miss --> embed --> cache
  cache -- hit --> dense
  cache --> dense
  cache --> sparse
  dense --> rrf
  sparse --> rrf
  rrf --> adapt
  adapt -- yes --> hyd
  adapt -- no --> hyd
  hyd --> rerank --> dedup
  dedup -- yes --> out
  dedup -- no --> out
```

## Inputs

`HybridRetriever.SearchAsync` takes a `RetrievalOptions` struct:

| Option | Default | Effect |
|---|---|---|
| `CandidatePoolSize` | 50 | Top rows pulled from each of dense and sparse before fusion. |
| `RrfK` | 60 | RRF constant; higher values flatten the influence of rank gaps. |
| `RerankTopN` | 30 | Cap on candidates fed to the cross-encoder. |
| `Category` | `null` | SQL filter on `documents.category`. |
| `Product` | `null` | SQL filter on `documents.product`. |
| `Version` | `null` | SQL filter on `documents.version`. |
| `Limit` | 10 | Final result count. |
| `DedupPerSection` | `false` | Keep at most one chunk per `(document_id, section_title)`. |

## Stage details

### Acronym expansion

`QueryExpander.Expand` annotates known Nutanix acronyms on first
occurrence. The expansion is stable, deterministic, and case-insensitive
while preserving the original casing:

```text
"AHV cluster networking"  →  "AHV (Acropolis Hypervisor) cluster networking"
```

The dictionary covers AHV, AOS, NDB, NCM, NKE, NKP, NCC, NUS, LCM, CVM,
IPMI, PE, PC, DSF, RF, DR, and others — see `QueryExpander.cs` for the
authoritative list.

### Embedding cache

`QueryEmbeddingCache` is a bounded concurrent dictionary keyed by the
expanded query. Capacity defaults to 256 entries. When the count exceeds
capacity, the cache evicts down to half capacity by oldest access. Each
entry stores a 1024-dimension `HalfVector` plus a 64-bit access counter.

### Dense and sparse SQL

Both queries filter `chunk_kind = 'leaf'`. The dense query uses the
HNSW index on `chunks.embedding` with the cosine operator
(`<=>` from pgvector). The sparse query uses `bm25v <&> to_bm25query(...)`,
where the negative-score convention means lower distances are better
matches. Both queries also filter on optional `category`, `product`, and
`version`.

### Reciprocal Rank Fusion

For each candidate, the RRF score is `Σ 1 / (k + rank)` over each side
that ranked it. The implementation tracks dense and sparse distances per
co-hit so the diagnostics report carries through honest per-stage values.

### Adaptive rerank

If the top-5 RRF spread (`top1 - top5`) is at or below 0.02, the
candidates are too tight for the cross-encoder to differentiate. The
retriever caps the rerank input at 10 to save roughly 50 ms of
cross-encoder compute. The threshold is tuned against the upstream
`arista-mcp` bench v2; the nutanix-specific bench, when it ships, will
re-confirm or update the constant.

### Parent hydration

For leaves carrying `parent_chunk_id`, the retriever fetches the parent
chunks' `raw_content` in a single batched `SELECT ... WHERE id = ANY($1)`.
The cross-encoder scores `(query, parent_text)` rather than
`(query, leaf_text)`, giving it richer section context.

### Cross-encoder rerank

`XlmRobertaOnnxReranker` constructs the canonical XLM-R pair sequence
`<s> q </s></s> d </s>` and runs the ONNX session with `input_ids` and
`attention_mask`. Output is `logits [B, 1]` — one raw score per pair,
higher is better.

### Stable sort and final pruning

After rerank, the retriever sorts candidates by score (stable, so ties
keep RRF order). When `DedupPerSection` is set, the retriever emits at
most one chunk per `(document_id, section_title)`. Finally it returns
`Limit` results plus a `SearchDiagnostics` record.

## Diagnostics

`SearchDiagnostics` reports counts and timings for each stage:

```text
DenseHits, SparseHits, AfterRrf, AfterRerank,
EmbedMs, DenseQueryMs, SparseQueryMs, RrfMs, RerankMs, TotalMs
```

The `withDiagnostics: true` argument on `search_docs` returns the
diagnostics alongside the result list.

## Excluded levers

Three retrieval levers familiar from the upstream `arista-mcp` line are
intentionally absent here:

- HyDE-style query rewriting via a small local LLM. It depends on a
  sidecar model and the upstream five-lever ceiling work showed it as
  marginal at best on a domain corpus.
- Rule-based multi-query expansion. It regressed top-1 on the upstream
  bench v2.
- Listwise re-rank by a small instruction-tuned LLM. It regressed top-1
  by 4.39 percentage points at 3.4 sigma on the upstream bench.

These are tracked as candidate experiments in
[`training-rag.md`](./training-rag.md). They will be revisited only when
a frontier-class local LLM is reachable through stdio and a refreshed
bench shows non-trivial headroom.
