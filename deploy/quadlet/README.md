# Quadlet templates — Podman + systemd

Declarative Podman units for `podman-system-generator(8)`. systemd reads
the `.container`, `.network`, and `.volume` files and synthesizes
native systemd services that pull the image, wire the network, mount
the volume, and run the container — without `ExecStart=podman run …`
boilerplate.

## Why Quadlet rather than `podman compose`?

- **Native systemd integration.** Restart policy, journal, dependency
  graph, sd_notify all work out of the box.
- **Boot order honoured.** `Wants=network-online.target` blocks the PG
  service until the host is online; the server `After=
  nutanix-mcp-postgres.service` ensures correct sequencing.
- **Auto-update.** `Label=io.containers.autoupdate=registry` plus the
  upstream `podman-auto-update.timer` keeps the image current without
  manual `pull` + `restart`.
- **Rootless.** The user-mode flow (`~/.config/containers/systemd/`)
  doesn't require `sudo` for everyday operations. Rootful
  (`/etc/containers/systemd/`) is also supported by the same files.

## What ships here

| File | Purpose |
|---|---|
| `nutanix-mcp.network` | Internal bridge network the server uses to reach PG by name. |
| `nutanix-mcp-postgres.volume` | Named Podman volume for the PG data dir (survives container redeploys). |
| `nutanix-mcp-postgres.container` | Postgres 18 + vchord-suite. Health-checks via `pg_isready`. Auto-updates against `tensorchord/vchord-suite:pg18-latest`. |
| `nutanix-mcp.container` | The MCP server itself. Pulls `ghcr.io/dantte-lp/nutanix-mcp:latest` (after first release-pipeline push). |

## Install — rootless (user-mode)

```bash
# 1. Drop the unit files into the user Quadlet directory.
mkdir -p ~/.config/containers/systemd
cp deploy/quadlet/*.{container,network,volume} ~/.config/containers/systemd/

# 2. Drop init.sql where the PG container expects it (read-only mount).
mkdir -p ~/.config/nutanix-mcp
cp docker/init.sql ~/.config/nutanix-mcp/init.sql

# 3. Create the env-file with your connection string.
sudo install -m 0600 -o "$USER" -g "$USER" \
    deploy/systemd/nutanix-mcp.env.example /etc/nutanix-mcp.env
sudo $EDITOR /etc/nutanix-mcp.env

# 4. Stage the models on the host (read-only mount in the server unit).
mkdir -p ~/.local/share/nutanix-mcp/models/{embedder,reranker}
pwsh scripts/fetch-models.ps1 -ModelsRoot ~/.local/share/nutanix-mcp/models

# 5. Reload systemd so the generator picks up the new units, then
#    enable the stack.
systemctl --user daemon-reload
systemctl --user enable --now nutanix-mcp-postgres.container
systemctl --user enable --now nutanix-mcp.container

# 6. Optional: turn on the auto-update timer.
systemctl --user enable --now podman-auto-update.timer

# 7. Verify.
systemctl --user status nutanix-mcp-postgres.service
systemctl --user status nutanix-mcp.service
journalctl --user -u nutanix-mcp.service -f
curl -fsS http://127.0.0.1:7456/v1/healthz
```

## Install — rootful (system-wide)

Same files, copied into `/etc/containers/systemd/`. Install with
`sudo systemctl daemon-reload && sudo systemctl enable --now …`.
Models + init.sql under `/var/lib/nutanix-mcp/` instead of the user home.

## Auto-bootstrap

The CLI verb `nutanix-mcp bootstrap` automates the whole sequence:

```bash
# Pull the postgres + restore the corpus dump from a release attachment,
# generate Quadlet, enable.
nutanix-mcp bootstrap --quadlet --release v0.3.0
```

See [`docs/en/quickstart.md`](../../docs/en/quickstart.md) for end-to-end
flow + Windows Service equivalent.

## Notes

- **`%h` expansion** in unit files resolves to the user's home (rootless)
  or root (rootful). Move user paths under `/var/lib/nutanix-mcp/`
  for rootful installs.
- **`docker.io` is the registry I pinned** for the upstream
  `tensorchord/vchord-suite` image. Switch to a private mirror by
  editing the `Image=` line; `AutoUpdate=registry` follows the same
  reference.
- **`HealthCmd=`** is honoured by Podman 4.5+; older Podman ignores it
  (PG still starts, but systemd won't gate on the container being
  ready). Quadlet itself needs Podman ≥ 4.4.
