# Overview

`nutanix-mcp` is a Model Context Protocol server that exposes hybrid
retrieval (dense vector search plus BM25 plus a cross-encoder reranker)
over the Nutanix documentation corpus. The server speaks the standard
MCP transport over either stdio or streamable HTTP and exposes seven tools
for search, lookup, listing, and health reporting.

## Goals

- Hybrid retrieval over 1900-plus portal PDFs, the Knowledge Base, the
  Bible, the API reference, and the ports list.
- Multilingual queries against an English-dominant corpus.
- Strict layered architecture and a strong static-analysis quality gate.
- Reproducible builds across Windows, Linux, and macOS on amd64 and arm64.

## Non-goals for v0.1

- HyDE-style query rewriting.
- Rule-based multi-query expansion.
- Listwise re-ranking by a small local language model.

These three levers measurably regressed retrieval quality on the upstream
arista-mcp benchmark and are tracked for re-evaluation in
[`training-rag.md`](./training-rag.md).

## Components

- A .NET 10 layered solution: `Cli` → `Server` → `Core` ← `Embedding`,
  `Data`. The `Core` project depends only on the BCL.
- A PostgreSQL 18 image bundling pgvector 0.8.2, vchord 1.1.1,
  vchord_bm25 0.3.0, and pg_tokenizer 0.1.1.
- Two ONNX models on disk: `bge-m3` for embedding (XLM-R, 1024-dim,
  multilingual) and `bge-reranker-v2-m3` for reranking (XLM-R cross-encoder).
- Seven MCP tools: `search_docs`, `lookup_section`, `list_documents`,
  `get_document`, `get_status`, `list_categories`, `get_kb_article`.
