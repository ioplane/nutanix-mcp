# Deployment templates

Production-deploy scaffolding for nutanix-mcp v0.3.0+. Pick the template
that matches your host OS, copy, edit the connection string + models
path, install.

## Linux — systemd

```bash
# 1. Place the single-file binary.
sudo install -m 0755 nutanix-mcp /usr/local/bin/nutanix-mcp

# 2. Models into a state directory.
sudo install -d -o nutanix-mcp -g nutanix-mcp /var/lib/nutanix-mcp/models
# unpack/fetch the models tarball here (see scripts/fetch-models.ps1
# or attach the release model bundle).

# 3. Install the unit + env file.
sudo cp deploy/systemd/nutanix-mcp.service     /etc/systemd/system/
sudo cp deploy/systemd/nutanix-mcp.env.example /etc/nutanix-mcp.env
sudo chmod 0600 /etc/nutanix-mcp.env
sudo $EDITOR    /etc/nutanix-mcp.env   # set DB conn-string + models path

# 4. Service user + enable.
sudo useradd --system --no-create-home --shell /usr/sbin/nologin nutanix-mcp
sudo systemctl daemon-reload
sudo systemctl enable --now nutanix-mcp

# 5. Verify.
systemctl status nutanix-mcp
journalctl -u nutanix-mcp -f
curl -fsS http://127.0.0.1:7456/v1/healthz
```

The unit hardens the process (`ProtectSystem=strict`, `NoNewPrivileges`,
`PrivateTmp`, `ProtectKernelTunables`, `RestrictNamespaces`,
`RestrictSUIDSGID`). `MemoryDenyWriteExecute` is intentionally **off**
because ONNX Runtime JITs kernels at session-init.

## Windows — Service

Run elevated PowerShell:

```powershell
pwsh -File deploy\windows\Install-NutanixMcpService.ps1 `
    -BinaryPath "C:\Program Files\nutanix-mcp\nutanix-mcp.exe" `
    -ConnectionString 'Host=127.0.0.1;Port=5432;Database=nutanix;Username=nutanix;Password=...' `
    -ModelsDir "C:\Program Files\nutanix-mcp\models" `
    -RerankerDir "C:\Program Files\nutanix-mcp\models\reranker-finetuned-int8" `
    -Port 7456
```

The script:

- Validates the binary + models directory exist
- Removes any prior installation of the service
- Registers the service with `New-Service` (no NSSM dependency)
- Wires the `Environment` registry key so the binary picks up the
  connection string and model paths
- Configures auto-restart on failure (3 attempts, 5 s delay)
- Starts the service and reports status

To uninstall:

```powershell
Stop-Service nutanix-mcp
Remove-Service nutanix-mcp
Remove-Item HKLM:\System\CurrentControlSet\Services\nutanix-mcp -Recurse -Force
```

## macOS — `launchd`

A `launchd` plist template is queued for v0.3.x. For now, run the binary
under `tmux` or `caddy run`-style supervision; the same env-vars apply.

## Reverse proxy + TLS

The `serve --transport http` listener is HTTP-only. For external
exposure put a reverse proxy (caddy, nginx, traefik) in front. Sample
caddy block:

```caddy
mcp.example.com {
    reverse_proxy 127.0.0.1:7456
}
```

The MCP protocol uses Streamable HTTP — most reverse proxies pass it
through unchanged.

## Container

The repo's `docker/Containerfile` (compose service `app`) is for the
**Postgres** stack and the dev CLI build, not a production server image.
Use the single-file binary with systemd or Windows Service for production.
A first-party container image is queued for v0.4.0.
