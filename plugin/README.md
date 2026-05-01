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

- The `nutanix-mcp` binary on `PATH`. Either install one of the release
  artefacts from the GitHub Releases page or build from source via
  `dotnet publish`.
- A populated PostgreSQL database that the binary can reach, plus the
  ONNX models on disk (see [`docs/en/quickstart.md`](../docs/en/quickstart.md)).
- Two environment variables exported before launching Claude Code:
  - `NUTANIX_MCP__ConnectionString`
  - `NUTANIX_MCP__ModelsDir`

## Install

The plugin lives inside the `nutanix-mcp` repository. Add this repository
as a Claude Code marketplace, then install the plugin:

```bash
# In Claude Code:
/plugin marketplace add https://github.com/dantte-lp/nutanix-mcp
/plugin install nutanix-mcp
```

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
