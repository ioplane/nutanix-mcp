# Sprint 006 execution plan — first bench-history.jsonl row

Status: in progress (2026-05-01).

## Research summary

### arista-mcp prior art (`docs/en/benchmarking.md`, `arista-reranker-tune/scripts/`)

Bench v2 was authored via a four-step pipeline:

1. `sample_chunks_for_bench.py` — stratified random sample of 488 chunks
   from postgres. Filters `token_count BETWEEN 150 AND 900`, excludes
   pure code-block chunks (`raw_content LIKE '```%'`). Per-product
   floor=12 / ceiling=120 keeps small products represented and stops
   `eos` from dominating.
2. `generate_bench_queries.py` — Qwen2.5-3B-Instruct via llama.cpp
   sidecar. System prompt: 1-2 questions per passage, 5-12 words, no
   yes/no, no metadata questions. 943 raw candidates.
3. `validate-bench-queries` (C# CLI) — fairness filter. Drops queries
   whose `source_chunk_id` is not in the current retriever's top-20.
   Survivors: 588.
4. `annotate_multi_positives.py` — Qwen labels which top-10 chunks
   would also be valid answers. 65 % of queries end up multi-positive.

### Live nutanix corpus structure (probed via tunnel)

- 7343 documents, 489 531 chunks.
- Categories (real, not the 16 in the original plan): `kb` (5490),
  `hardware-replacement` (993), `other` (743), `bible` (61),
  `prism-central` (13), `api` (12), `ahv` (10), `aos` (8),
  `prism-element` (7), `files` (5), `ports` (1).
- `documents.product` is NULL for every row. Ingest does not populate
  it.
- `chunks.section_title` is dominated by markdown-bold boilerplate:
  `**Procedure**` (31 k), `**About this task**` (23 k),
  `**Before you begin**` (21 k), `Description` (5 k), `Solution`
  (5 k), `**Note:**` (4.7 k). High-signal section titles
  (`**AHV OVERVIEW**`, `Notes and Known Issues`) are rare.

### Implication for the first cut

- The original "16-category, 100-query, hand-curated, difficulty-mixed"
  plan does not survive contact with the live corpus: 6 of 16
  categories have zero documents, `product` is unusable, most
  `section_title` values are non-anchoring boilerplate.
- The `validate-bench-queries` verb authored in this sprint matches on
  `(expect_doc, expect_section)`, which fails on boilerplate section
  titles. Fixture authoring therefore needs `expect_chunk_ids`
  populated **directly** from a chunk-id query, not via the
  doc+section anchor.

## First-cut plan

### Step 1 — author `tests/fixtures/bench-queries.json`

Pipeline lives in `scripts/seed-bench-queries.ps1` (PowerShell, runs
through the local-side tunnel):

1. Connect to the live remote PG via the SSH tunnel
   (`Host=127.0.0.1;Port=5436`).
2. Pull 30 candidate chunks with this stratification:
   - 8 from `kb` (largest pool)
   - 6 from `hardware-replacement`
   - 4 from `bible` (high-quality narrative content)
   - 3 each from `ahv`, `aos`, `prism-central`, `prism-element`,
     `files` (smaller pools)
3. Filters: `chunks.token_count BETWEEN 200 AND 700`,
   `chunk_kind='leaf'`, `section_title NOT IN ('**Procedure**',
   '**About this task**', '**Before you begin**', '**Note:**',
   '**What to do next**', '**Warning:**', '**Required arguments**',
   '**Optional arguments**', '**Tools and supplies required:**',
   '**Contents**', '**Template**')`,
   `raw_content NOT LIKE '```%'`.
4. For each candidate, generate a query via heuristic templating on
   `section_title`:
   - "How to|Configuring|Configure {X}" → "How do I {X}?"
   - "{X} Overview|About {X}" → "What is {X}?"
   - else → "What does {section_title} describe in {category}?"
5. `expect_chunk_ids = [chunk_id, parent_chunk_id (if any)]` —
   parent provides a forgiving multi-positive when retrieval picks
   the parent over the leaf.
6. Write `tests/fixtures/bench-queries.json` with `schema_version=2`,
   `corpus_sha` set to current ingest_run sha.

### Step 2 — run the bench

```powershell
$env:NUTANIX_MCP__CONNECTIONSTRING='Host=127.0.0.1;Port=5436;Database=nutanix;Username=nutanix;Password=nutanix'
dotnet run --no-build --project src/NutanixMcp.Cli -- bench `
  --queries tests/fixtures/bench-queries.json `
  --history tests/fixtures/bench-history.jsonl `
  --label v0.1.0-bench-first-cut `
  --limit 10
```

Exit code 0 → smoke gate passed (top-10 ≥ 0.80). Exit 1 → record the
row anyway (history is append-only) and treat as a measurement
baseline, not a regression.

### Step 3 — commit and tag

- `feat(bench): Sprint 006 acceptance — 30-query first-cut fixture +
  first bench-history.jsonl row`
- Update `CHANGELOG.md` Sprint 006 section.
- `git tag sprint-006-review`.

### Out of scope for first cut (queued for Sprint 006-extension)

- LLM-generated queries via Qwen2.5-3B (the arista pattern). Needs a
  Qwen GGUF on the remote GPU host + a `generate_bench_queries.ps1`
  port. Tracked separately because the heuristic template is enough
  for a smoke gate.
- Multi-positive annotation. Same blocker.
- Bench expansion to 100 queries. Same blocker (LLM dependency).
- `expect_product` filtering. Blocked on ingest populating
  `documents.product` — currently NULL.

## Acceptance gate

- `tests/fixtures/bench-queries.json` exists with ≥ 30 queries.
- `tests/fixtures/bench-history.jsonl` has at least one row.
- `dotnet build` clean, `dotnet test` clean for non-integration tests.
- Sprint 006 doc updated to reflect what shipped vs what's queued.
