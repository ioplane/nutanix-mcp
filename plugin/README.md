# nutanix-mcp Claude Code plugin

This plugin wires the `nutanix-mcp` MCP server into Claude Code,
together with two slash commands and one skill that enforce a
ground-everything-in-search policy for Nutanix questions.

## Layout

```text
plugin/
├── .claude-plugin/plugin.json   # Manifest
├── .mcp.json                    # MCP server registration
├── commands/
│   ├── nutanix-search.md        # /nutanix-search <query>
│   └── nutanix-status.md        # /nutanix-status
└── skills/
    └── nutanix-search/SKILL.md
```

## Prerequisites

- The `nutanix-mcp` binary on `PATH`. Three options:
  - `bash scripts/install.sh` from an extracted release tarball
    (also stages the plugin marketplace symlink automatically).
  - `gh release download` + manual extract + `nutanix-mcp bootstrap --release vX.Y.Z`.
  - `dotnet publish` from a source checkout.
- A populated PostgreSQL database that the binary can reach, plus the
  ONNX models on disk (see [`docs/en/quickstart.md`](../docs/en/quickstart.md)).
- Environment variables exported before launching Claude Code:
  - `NUTANIX_MCP__ConnectionString` — required.
  - `NUTANIX_MCP__ModelsDir` — required (path holding `embedder/` +
    `reranker/`).
  - `NUTANIX_MCP__RerankerDir` — optional; point at
    `.../models/reranker-finetuned-int8` to run the recommended production
    reranker. Leave unset to fall back to `${ModelsDir}/reranker` (stock
    FP32 `bge-reranker-v2-m3`).
    **Requires v0.3.3+** — earlier releases documented this variable but
    silently ignored it (no matching property on `NutanixMcpSettings`).
  - `NUTANIX_MCP__ModelIdleUnloadMinutes` — optional, default `10`; minutes
    without a query before the server releases the ONNX models (~2.7 GB →
    ~260 MB). `0` keeps them loaded. See
    [`docs/en/operations.md`](../docs/en/operations.md#server-memory).

## Install

The plugin lives inside the `nutanix-mcp` repository. Two paths:

**Marketplace + `/plugin install`** (Claude Code REPL):

```text
/plugin marketplace add https://github.com/ioplane/nutanix-mcp
/plugin install nutanix-mcp@nutanix-mcp
```

The `@nutanix-mcp` suffix disambiguates the plugin against other
marketplaces that might expose a plugin of the same name; when there's
no collision `/plugin install nutanix-mcp` also works.

**Shell** (already covered by `scripts/install.sh`):

If you ran `bash scripts/install.sh` from a release tarball, the
marketplace symlink is already staged under
`~/.claude/plugins/marketplaces/nutanix-mcp/`. Restart Claude Code once
and `/plugin install nutanix-mcp` will find it locally without hitting
the network.

If the repository is private, ensure your Claude Code installation can
reach it (for example, via `gh auth login` so the underlying `git clone`
can use the GitHub credential helper).

## Usage

```text
/nutanix-search how do I configure VLAN tagging on AHV vSwitch
/nutanix-status
```

The bundled skill (`nutanix-search`) auto-activates whenever the user
asks a question that can be answered from the Nutanix corpus. The skill
maps user signals to the right `category` and `version` filters, and
holds the ground-and-cite contract.
