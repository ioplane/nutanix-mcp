#!/usr/bin/env pwsh
# nutanix-mcp — Windows Service installer.
#
# Registers the single-file binary as a Windows Service so it launches at
# boot and auto-restarts on crash. Uses the built-in `New-Service` cmdlet
# (no NSSM dependency).
#
# Run elevated:
#
#   pwsh -File deploy/windows/Install-NutanixMcpService.ps1 `
#       -BinaryPath "C:\Program Files\nutanix-mcp\nutanix-mcp.exe" `
#       -ConnectionString 'Host=127.0.0.1;Port=5432;Database=nutanix;Username=nutanix;Password=...' `
#       -ModelsDir "C:\Program Files\nutanix-mcp\models" `
#       -RerankerDir "C:\Program Files\nutanix-mcp\models\reranker-finetuned-int8" `
#       -Port 7456
#
# To uninstall:
#
#   Stop-Service nutanix-mcp
#   Remove-Service nutanix-mcp
#   Remove-Item HKLM:\System\CurrentControlSet\Services\nutanix-mcp -Recurse -Force

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BinaryPath,

    [Parameter(Mandatory)]
    [string]$ConnectionString,

    [Parameter(Mandatory)]
    [string]$ModelsDir,

    [string]$RerankerDir = '',
    [int]$Port = 7456,
    [string]$ServiceName = 'nutanix-mcp',
    [string]$DisplayName = 'nutanix-mcp — hybrid retrieval MCP server',
    [string]$Description = 'Hybrid-retrieval MCP server for Nutanix documentation. Built on .NET 10 with pgvector, vchord, and ONNX Runtime.'
)

$ErrorActionPreference = 'Stop'

# Elevation guard.
$current = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($current)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must run elevated. Open PowerShell as Administrator and re-run.'
}

if (-not (Test-Path -LiteralPath $BinaryPath)) {
    throw "BinaryPath not found: $BinaryPath"
}
if (-not (Test-Path -LiteralPath $ModelsDir -PathType Container)) {
    throw "ModelsDir not found: $ModelsDir"
}

# Stop + remove an existing service before re-registering.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "[install] stopping existing service '$ServiceName'"
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Write-Host "[install] removing existing service '$ServiceName'"
    Remove-Service -Name $ServiceName
    Start-Sleep -Seconds 1
}

# Persist environment variables to the service registry key (machine-scoped
# Environment value). New-Service does not accept env directly, so we set
# them via the registry.
$svcKey = "HKLM:\System\CurrentControlSet\Services\$ServiceName"

$env_lines = @(
    "NUTANIX_MCP__CONNECTIONSTRING=$ConnectionString"
    "NUTANIX_MCP__MODELSDIR=$ModelsDir"
)
if ($RerankerDir) {
    $env_lines += "NUTANIX_MCP__RERANKERDIR=$RerankerDir"
}

# Compose the binary path quoting to survive sc.exe argument parsing.
$argString = "serve --transport http --port $Port"
$cmdLine = "`"$BinaryPath`" $argString"

Write-Host "[install] registering service '$ServiceName'"
New-Service `
    -Name $ServiceName `
    -DisplayName $DisplayName `
    -Description $Description `
    -BinaryPathName $cmdLine `
    -StartupType Automatic | Out-Null

# Wire env-block (REG_MULTI_SZ).
Set-ItemProperty -LiteralPath $svcKey -Name 'Environment' -Type MultiString -Value $env_lines

# Configure auto-restart on failure (3 attempts, 5s delay).
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Write-Host "[install] starting service '$ServiceName'"
Start-Service -Name $ServiceName

Get-Service -Name $ServiceName | Format-List Name, Status, StartType, DisplayName

Write-Host ""
Write-Host "[install] OK. Service is listening on http://127.0.0.1:$Port/."
Write-Host "[install] Logs:  Get-WinEvent -LogName Application -ProviderName '$ServiceName' -MaxEvents 50"
Write-Host "[install] Health: Invoke-RestMethod http://127.0.0.1:$Port/v1/healthz"
