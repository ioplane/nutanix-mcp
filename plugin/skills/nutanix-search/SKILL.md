---
name: nutanix-search
description: Use this skill whenever the user asks a question that can be answered from the Nutanix documentation — AHV, AOS, Prism Element, Prism Central, Nutanix Files, NDB, Move, LCM, Flow, hardware replacement procedures, KB articles, the Bible, the API reference, or the ports list. Invoke the `mcp__nutanix-mcp__search_docs` MCP tool to retrieve grounded passages, quote them with citations, and refuse to speculate beyond what the tool returns.
---

# nutanix-search

The `nutanix-mcp` MCP server runs a hybrid retrieval pipeline (dense
bge-m3 plus BM25 plus a cross-encoder reranker) over the entire Nutanix
documentation corpus. Use it as the source of truth for any Nutanix
question.

## Decision tree

- The user names a KB number such as `KB-9876` → call
  `mcp__nutanix-mcp__get_kb_article` with `kbNumber`.
- The user asks an open-ended question about a Nutanix product → call
  `mcp__nutanix-mcp__search_docs`.
- The user wants a particular section verbatim → call
  `mcp__nutanix-mcp__lookup_section`.
- The user wants to know what categories exist or how many docs each
  contains → call `mcp__nutanix-mcp__list_categories`.
- The user wants to enumerate documents in a category, product, or
  version → call `mcp__nutanix-mcp__list_documents`.

## search_docs usage

Always set `topK` to 5 by default; raise to 20 only if the first call
returns weak hits. Set `category`, `product`, or `version` whenever the
user mentions one — the SQL filter is cheap and improves precision.

| User signal | Argument |
|---|---|
| "AHV", "Acropolis Hypervisor" | `category: "ahv"` |
| "AOS" | `category: "aos"` |
| "KB-NNNN", "knowledge base" | `category: "kb"` |
| "Files", "AFS" | `category: "files"` |
| "NDB", "Era", "database service" | `category: "ndb"` |
| "Karbon", "Kubernetes" | `category: "kubernetes"` |
| "Flow", "microsegmentation" | `category: "flow"` |
| "Prism Central", "PC" | `category: "prism-central"` |
| "Prism Element", "PE" | `category: "prism-element"` |
| "Move" | `category: "move"` |
| "DR", "disaster recovery" | `category: "disaster-recovery"` |
| "Cloud Clusters", "NC2" | `category: "cloud-clusters"` |
| "NKI" | `category: "nki"` |
| "Pulse" | `category: "pulse"` |
| "LCM", "lifecycle manager" | `category: "lcm"` |
| "replace", "hardware swap" | `category: "hardware-replacement"` |

The server is multilingual: a Russian user query will retrieve relevant
English chunks. Do not translate the query before sending it.

## Citation contract

Every claim must cite a chunk. Use the document title and the slug, plus
the page span when present:

```text
Cite: "AHV Administration Guide", AHV-Admin-Guide-v11_0, p. 142–144
```

Refuse to fabricate. If the search returns nothing relevant, say so and
suggest the user broaden the query.

## Diagnostics

Pass `withDiagnostics: true` only when the user asks why a search behaves
the way it does. The diagnostics reveal per-stage timings (embed, dense,
sparse, RRF, rerank) and counts (`DenseHits`, `SparseHits`, `AfterRrf`,
`AfterRerank`).
