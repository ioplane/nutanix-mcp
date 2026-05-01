---
description: Search the Nutanix documentation corpus via the nutanix-mcp MCP server.
argument-hint: <free-text query>
---

Use the `nutanix-mcp` MCP server to answer the user's question about Nutanix
products, KB articles, AHV, AOS, Prism, NDB, Files, Move, LCM, hardware
replacement procedures, or anything else covered by the corpus.

User query: $ARGUMENTS

Steps:

1. Call the `mcp__nutanix-mcp__search_docs` tool with `query` set to the user
   query, `topK` 10, and `withDiagnostics` false.
2. If the user query mentions a specific Nutanix product or category (for
   example, "AHV", "Files", "NDB", "Prism Central"), pass the matching value
   in `category` or `product`.
3. If the query mentions a specific version (for example, "AHV 11.0",
   "AOS 7.5"), pass it in `version`.
4. Read the returned chunks. Quote the most relevant passages and cite the
   document title, slug, and page span.
5. If no chunk seems sufficient, call `mcp__nutanix-mcp__lookup_section` to
   pull the full section text, or `mcp__nutanix-mcp__get_kb_article` for KB
   numbers, or `mcp__nutanix-mcp__list_documents` to broaden the lens.
6. Avoid speculation — every claim must trace back to a returned chunk.
