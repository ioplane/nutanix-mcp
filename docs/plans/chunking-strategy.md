# Nutanix-mcp chunking strategy

Status: **planning**, target Sprint 002.5–003.
Anchored to arista-mcp's section-aware recipe with Nutanix corpus
adaptations. Captures arista's hard-learned lessons so we don't relearn them.

## 1. Goals

- Section-boundary-aware chunking that survives marker quirks in MD output.
- Parent-child layer compatible with the bge-reranker-v2-m3 cross-encoder.
- ~7000+ leaves across 1983 portal PDFs + KB + API-Ref + Bible + Ports.
- Deterministic re-ingest. Same MD + same options ⇒ identical chunks.
- Multi-version awareness: a query for "AHV 11.0" should not return "AHV 10.0"
  chunks unless the user opted in.

## 2. Non-goals (informed by arista feedback memory)

- **No multi-query expansion** by default. arista regressed top-1 when the
  rule-based query expander was on (`feedback_multiquery_regression`).
  Code path may be retained behind an OFF flag for conservative-rule
  experiments, but never default.
- **No listwise LLM rerank** by default. arista lost 4.39 pp @ 3.4σ with
  Qwen 2.5 3B listwise (`feedback_listwise_regression`). Cross-encoder
  fine-tune is the proven reranker.
- **No reranker swap to bge-base.** arista saw no uplift; the v2-m3 line
  is the path forward (`feedback_reranker_model_swap`).
- **No swap from `halfvec` to full-precision `vector`.** halfvec is 50 %
  smaller at negligible cost (CLAUDE.md `DO NOT`).
- **No mocked Postgres in integration tests.** arista's
  `feedback_dont_stop_user_tasks` context plus the `_test`-suffix fixture
  guard makes this unambiguous.
- **No reingest churn during a bench window.** arista's parent-child rollout
  re-invalidated v2 bench every ingest; we will gate ingest by sprint
  acceptance and cut bench re-runs against the same fingerprint.

## 3. Source corpus shape (sampled 2026-05-01 from /work/.../converted/md)

| bucket | count | example |
|--|--|--|
| <10 KB | 5252 | KB articles, short release notes |
| 10–100 KB | 753 | Hardware-replacement procedures, mid-size guides |
| 100 KB–1 MB | 182 | Admin guides (AHV, Prism Element, Files) |
| >1 MB | 7 | API references, Bible (NDA), large reference manuals |

Header style observed in marker output:

- ATX headings, but Nutanix authors *bold* the title text: `# **AHV
  Administration Guide**`, `### **Verifying That the Node is Part of a
  Cluster**`. CleanHeading must strip `**…**` exactly like arista did
  (`feedback` not needed — we copy the recipe).
- Anchors: `### <span id="page-4-1">**…**</span>` — preserve the id for
  citation; strip the tag for embedding.
- Page boundaries: `{N}------------------------------------------------`.
  Strip with `\{\d+\}-+` regex (arista's `PageMarkerRegex`).
- Empty image refs: `![](_page_N_Picture_M.jpeg)`. arista does NOT strip
  these — but its corpus is largely text-only. Nutanix PDFs have
  hundreds of these per long doc and they hurt embedding signal. **NEW
  RULE**: strip `\!\[\]\(_page_\d+_(Picture|Figure|Image)_\d+\.[a-z]+\)`.
- TOC tables: every doc starts with a `## **Contents**` followed by a
  pipe-delimited table of titles+page numbers. **NEW RULE**: detect by
  heading text "Contents" or "Table of Contents" at L2 directly under
  the L1 doc title; skip section body.
- Procedure DITA structure: `**Before you begin**`, `**About this task**`,
  `**Procedure**` appear as bold paragraphs (not headings). They are
  emitted as part of section bodies, not as section breaks — fine, the
  cross-encoder reranker likes them as context.

## 4. Algorithm — `NutanixSectionAwareChunker`

Direct port of `AristaMcp.Core.Chunking.SectionAwareChunker` with the
following adjustments:

### 4.1 Document loader (`Catalog/DocumentLoader.cs`)

1. CRLF → LF normalisation. **CRITICAL** — arista's Sprint 7 found that
   Windows-written MD silently produced one chunk per doc when this
   wasn't done. We test it explicitly in `MarkdownWithCrlfLineEndings_*`
   regressions (port from arista).
2. Strip marker page markers `\{\d+\}-+`.
3. **NEW**: strip empty marker image refs.
4. Heading regex `^(?<level>#{1,6})[ \t]+(?<title>[^\r\n]+)$` multiline.
5. `CleanHeading`: strip `<tag>`, `**bold**`, `*italic*`, `_under_`,
   collapse whitespace. Mirrors arista one-for-one.
6. Pair MD↔JSON sections via cleaned-title sequence with lookahead=3.
   **Level is ignored** — enrich-time JSON emits all level-1 entries
   while MD keeps real depth (arista lesson, Sprint 7).
7. **NEW**: TOC skip. If the first L2 section under the doc title has
   `CleanHeading` ∈ {"Contents", "Table of Contents", "Содержание"},
   drop it from the section list before chunking.

### 4.2 Section sizes

| param | arista value | nutanix value | rationale |
|--|--|--|--|
| TargetTokens (leaf) | 512 | **512** | bge-m3 sweet spot, same as Snowflake |
| MaxTokens | 1200 | **1200** | bge-m3 max=8192 but reranker pair=512 |
| OverlapTokens | 64 | **64** | unchanged |
| MinTokens (merge) | 40 | **40** | unchanged |
| ParentMaxTokens | 2048 | **2048** | reranker context budget |
| ParentHeadTokens | 1792 | **1792** | head dominates summary |
| ParentTailTokens | 256 | **256** | tail captures conclusions |

Token approximation: `words × 1.3` (BERT-ish).
**NEW**: bge-m3 uses XLM-R SentencePiece with byte fallback — multilingual
inflates a bit for Cyrillic input. For English-dominant Nutanix corpus
the 1.3 ratio holds. We will calibrate empirically against the ONNX
tokenizer in Sprint 003 by measuring `tokens / words` on a 100-doc sample.

### 4.3 Special-case strategies

| corpus slice | strategy | parents emitted? |
|--|--|--|
| KB articles (<10 KB, single L1 + body) | whole-doc chunk if ≤512 tok else 512+64 sliding | parent=whole |
| Hardware replacement (procedural) | per L2 heading section, leaves over the procedure body | yes (full L2) |
| Admin/Reference guides (ATX-tree) | section-aware, default recipe | yes |
| API Reference (per-endpoint) | per L3 endpoint heading; **no parent** (parent would be the whole doc, useless to reranker) | no |
| Multi-version (AHV-Admin v10/v11) | normal recipe; **version filter on query** | yes |

Detection rules:

```text
isApiReference   = title.Contains("API Reference") || title.EndsWith("Reference") && pages > 200
isHardwareReplc  = category == "hardware-replacement" || slug.Contains("Replacement")
isKbShort        = sizeBytes < 10_000 && sectionCount <= 3
```

## 5. Multi-version handling

Nutanix ships near-identical guides for several AOS / AHV / Prism Central
versions. Three docs (`AHV-Admin-Guide-v10_0`, `v10_3`, `v11_0`) generate
three sets of overlapping chunks. We do NOT dedupe — the user might
genuinely need v10.3-specific content.

MCP `search_docs` adds an optional `version` filter:

- If query string contains a version token (regex `v?\d+(\.\d+)+`) we
  AND it into the SQL `WHERE` (matches `documents.version`).
- Otherwise we sort tied scores by version-recency descending so v11.0
  outranks v10.0 on equal relevance. This lets the user override by
  asking explicitly.

`list_documents` exposes `version` in the projection so a model can
disambiguate before the second call.

## 6. Test plan (port + extend arista's)

Direct ports (rename + retarget):

- `MarkdownWithCrlfLineEndings_StripsToSingleSectionList` — CRLF canary.
- `CleanHeading_StripsBoldItalicHtmlTags` — emphasis canary.
- `StampPagesFromJson_LookaheadResyncsAfterMissingMdHeading` — pairing
  resilience canary.
- `SectionAwareChunker_EmitsParentAndLeavesPerSection` — parent-child
  invariants.
- `SectionAwareChunker_TruncatesParentOver2048Tokens` — head/tail budget.
- `MergeTinyNeighbours_DoesNotCrossSectionBoundary` — confinement canary.

Nutanix-specific additions:

- `DocumentLoader_StripsMarkerImageRefs` — `![](_page_3_Picture_5.jpeg)`
  must be removed before sectioning.
- `DocumentLoader_DropsTocSection` — `## **Contents**` table is excluded.
- `DocumentLoader_PreservesPageAnchorIds` — `<span id="page-N-X">`
  tags strip cleanly without losing the id (which we'll move to a
  metadata field for citation).
- `Chunker_ApiReferenceSliceProducesNoParents` — endpoint-per-chunk
  invariant.
- `Chunker_KbShortDocProducesSingleLeaf` — 1-doc-1-chunk invariant.
- `MultiVersionFilter_OnlyMatchesRequestedVersion` — query-time canary.

## 7. Calibration plan

After Sprint 003 ships the ONNX bge-m3 tokenizer:

1. Sample 100 docs across all four size buckets.
2. Measure actual tokens-per-word ratio.
3. If diverges >10 % from 1.3, update `ChunkingOptions.TargetTokens`
   for the corresponding bucket.
4. Sample 50 chunks at the boundary; confirm the L2 heading and
   first paragraph of the section are intact (no mid-sentence cut).
5. Record decisions in `docs/sprints/sprint-003-tokenizer-calibration.md`.

## 8. Bench expansion (informed by arista `feedback_bench_expansion_result`)

Arista's v1 (~30q substring-scoring) bench masked three real regressions
that v2 (588 q chunk-ID style) caught. **We never write a v1-style
bench.** First bench fixture lands in Sprint 006 with chunk-ID gold
labels from day 1.

Targets at first bench cut (educated guess; revise after run):

- Top-1: ≥ 85 % (vs arista 92.86 % — bge-m3 is multilingual, may give
  some away on English-only corpus)
- Top-10: ≥ 99 %
- p95 latency: ≤ 1.5 s (single-doc query against 7 k chunks)

## 9. Open questions

1. **TOC heading text in non-English locales.** If the catalog later
   includes Russian Nutanix docs, `## **Содержание**` needs to be in
   the TOC-skip allow-list. Already pre-listed above.
2. **Per-endpoint API chunking.** API-Ref-AOS-v7_5 has L2 verb groups
   ("Alerts", "VMs") and L3 individual endpoints ("Get the list of
   Alerts"). Decision: cut at L3, parent=L2 verb group. Test in Sprint
   003 with a focused unit test on this single doc.
3. **Bible/Ports docs (NDA-style).** Format may differ from portal PDFs;
   we'll re-audit in Sprint 002.5 when these stream lands. Skip from
   v0.1 ingest if format incompatible — back-fill in v0.2.

## 10. Acceptance criteria

A Sprint 002.5 / Sprint 003 chunker is ready to merge when:

- All 12 chunker tests pass (6 ports + 6 new).
- Manual spot-check on 5 sampled docs shows chunks have:
  - The L2 heading inside `Content` prefix.
  - No marker page markers, no empty image refs.
  - No "Contents" leaf in the chunk stream.
- Token distribution: P50 ≈ 512, P95 ≤ MaxTokens, P99 ≤ ParentMaxTokens.
- Re-ingest yields zero diff on chunk count and chunk content hashes.
