# nutanix-mcp

Hybrid retrieval MCP server for Nutanix documentation — .NET 10 + pgvector + ONNX.

Sister project of [arista-mcp](https://github.com/dantte-lp/arista-mcp). Same architecture
(Cli → Server → Core ← Embedding, Data), different corpus (1983 portal PDFs + KB + Bible
+ API + Ports docs).

## Quick start

```bash
podman compose -f docker/compose.yaml up -d postgres
dotnet build
dotnet run --project src/NutanixMcp.Cli -- serve --transport stdio
```

## Layers (strict)

`Cli → Server → Core ← Embedding, Data`

Core has no reference to Data, Embedding, or Server. Tests may reference any layer.

## Status

v0.1.0 — Sprint 001 (scaffold) in progress.

See `docs/plans/plan-v0.1.md` for the full roadmap.
