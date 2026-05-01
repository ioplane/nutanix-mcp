#!/usr/bin/env pwsh
# Sprint 008 — sweep harness. Drives the bench verb across a one-at-a-time
# parameter grid (ef_search, iterative_scan, rrf_k, candidate_pool_size).
# One bench-history.jsonl row per configuration. Each run takes ~5-10 minutes
# on the remote, so a 12-config sweep finishes in ~1-2 hours.
#
# Designed to run on the remote host (`ssh gpu`) where PG and the embedder
# share localhost — no SSH tunnel and no local-CPU embedder bottleneck.
#
# Usage on the remote:
#   pwsh scripts/bench-sweep.ps1
#       -Queries tests/fixtures/bench-queries-llm.json
#       -History tests/fixtures/bench-history.jsonl
#       -ConnectionString 'Host=127.0.0.1;Port=5435;Database=nutanix;Username=nutanix;Password=nutanix'

param(
    [string]$Queries = 'tests/fixtures/bench-queries-llm.json',
    [string]$History = 'tests/fixtures/bench-history.jsonl',
    [string]$ConnectionString = 'Host=127.0.0.1;Port=5435;Database=nutanix;Username=nutanix;Password=nutanix',
    [string]$ModelsDir = 'models',
    [string]$Project = 'src/NutanixMcp.Cli',
    [string]$LabelPrefix = 'sprint-008'
)

$ErrorActionPreference = 'Stop'
$env:NUTANIX_MCP__CONNECTIONSTRING = $ConnectionString
$env:NUTANIX_MCP__MODELSDIR = $ModelsDir

# Baseline = the v0.2.0 LLM-bench acceptance config (matches sprint-006-ext).
# Sweep one knob at a time, hold the rest at baseline, so deltas are
# attributable to a single change.
$baseline = @{
    EfSearch     = 100
    IterScan     = 'relaxed_order'
    RrfK         = 60
    PoolSize     = 50
    RerankTopN   = 30
}

# Sweep grid. Baseline values appear once each (they will run once total
# rather than four times — we deduplicate at the bottom).
$grid = @(
    # ef_search ladder
    @{ Name = 'ef50';    EfSearch = 50;  IterScan = $baseline.IterScan; RrfK = $baseline.RrfK; PoolSize = $baseline.PoolSize },
    @{ Name = 'ef100';   EfSearch = 100; IterScan = $baseline.IterScan; RrfK = $baseline.RrfK; PoolSize = $baseline.PoolSize },
    @{ Name = 'ef200';   EfSearch = 200; IterScan = $baseline.IterScan; RrfK = $baseline.RrfK; PoolSize = $baseline.PoolSize },
    @{ Name = 'ef400';   EfSearch = 400; IterScan = $baseline.IterScan; RrfK = $baseline.RrfK; PoolSize = $baseline.PoolSize },
    # iterative_scan
    @{ Name = 'strict';  EfSearch = $baseline.EfSearch; IterScan = 'strict_order';  RrfK = $baseline.RrfK; PoolSize = $baseline.PoolSize },
    # rrf_k ladder
    @{ Name = 'rrfk30';  EfSearch = $baseline.EfSearch; IterScan = $baseline.IterScan; RrfK = 30;  PoolSize = $baseline.PoolSize },
    @{ Name = 'rrfk100'; EfSearch = $baseline.EfSearch; IterScan = $baseline.IterScan; RrfK = 100; PoolSize = $baseline.PoolSize },
    # candidate_pool_size ladder
    @{ Name = 'pool30';  EfSearch = $baseline.EfSearch; IterScan = $baseline.IterScan; RrfK = $baseline.RrfK; PoolSize = 30 },
    @{ Name = 'pool100'; EfSearch = $baseline.EfSearch; IterScan = $baseline.IterScan; RrfK = $baseline.RrfK; PoolSize = 100 }
)

$startedAt = Get-Date
Write-Host "[sweep] $($grid.Count) configurations starting at $startedAt"

$i = 0
foreach ($cfg in $grid) {
    $i++
    $label = "$LabelPrefix-$($cfg.Name)"
    $args = @(
        'run', '--no-build', '--configuration', 'Release', '--project', $Project, '--',
        'bench',
        '--queries', $Queries,
        '--history', $History,
        '--label', $label,
        '--limit', '10',
        '--ef-search', "$($cfg.EfSearch)",
        '--iterative-scan', $cfg.IterScan,
        '--rrf-k', "$($cfg.RrfK)",
        '--candidate-pool-size', "$($cfg.PoolSize)",
        '--rerank-top-n', "$($baseline.RerankTopN)",
        '--smoke-top10-floor', '0.0'  # do not fail mid-sweep
    )
    Write-Host ""
    Write-Host "[sweep $i/$($grid.Count)] $label  (ef=$($cfg.EfSearch) scan=$($cfg.IterScan) rrf=$($cfg.RrfK) pool=$($cfg.PoolSize))"
    & dotnet @args 2>&1 | Select-String -Pattern '(results:|history appended|smoke-gate)' | ForEach-Object { Write-Host "  $_" }
}

$finishedAt = Get-Date
$elapsed = $finishedAt - $startedAt
Write-Host ""
Write-Host "[sweep] done. $($grid.Count) runs in $($elapsed.TotalMinutes.ToString('F1')) min"
