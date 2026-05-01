#!/usr/bin/env bash
# Sprint 008 — sweep harness (bash port for the Linux remote that has no pwsh).
# Drives `nutanix-mcp bench` across a one-at-a-time parameter grid. One row
# per configuration appended to bench-history.jsonl.
#
# Run on the remote (PG + embedder both localhost — no SSH tunnel and no
# laptop-CPU bottleneck).
#
# Usage:
#   bash scripts/bench-sweep.sh

set -euo pipefail

QUERIES="${QUERIES:-tests/fixtures/bench-queries-llm.json}"
HISTORY="${HISTORY:-tests/fixtures/bench-history.jsonl}"
PROJECT="${PROJECT:-src/NutanixMcp.Cli}"
LABEL_PREFIX="${LABEL_PREFIX:-sprint-008}"

export NUTANIX_MCP__CONNECTIONSTRING="${NUTANIX_MCP__CONNECTIONSTRING:-Host=127.0.0.1;Port=5435;Database=nutanix;Username=nutanix;Password=nutanix}"
export NUTANIX_MCP__MODELSDIR="${NUTANIX_MCP__MODELSDIR:-models}"

# Baseline = the v0.2.0 LLM-bench acceptance config. Sweep one knob at a time
# (the rest stay at baseline) so deltas are attributable to a single change.
EF_BASE=100
SCAN_BASE=relaxed_order
RRF_BASE=60
POOL_BASE=50
RERANK_TOPN=30

# 9 configs total: ef ladder (4), iterative_scan strict (1), rrf_k ladder (2,
# rrf=60 is baseline), pool ladder (2, pool=50 is baseline).
configs=(
    "ef50    50  $SCAN_BASE   $RRF_BASE  $POOL_BASE"
    "ef100   100 $SCAN_BASE   $RRF_BASE  $POOL_BASE"
    "ef200   200 $SCAN_BASE   $RRF_BASE  $POOL_BASE"
    "ef400   400 $SCAN_BASE   $RRF_BASE  $POOL_BASE"
    "strict  $EF_BASE   strict_order $RRF_BASE  $POOL_BASE"
    "rrfk30  $EF_BASE   $SCAN_BASE   30         $POOL_BASE"
    "rrfk100 $EF_BASE   $SCAN_BASE   100        $POOL_BASE"
    "pool30  $EF_BASE   $SCAN_BASE   $RRF_BASE  30"
    "pool100 $EF_BASE   $SCAN_BASE   $RRF_BASE  100"
)

t0=$(date +%s)
i=0
total=${#configs[@]}
echo "[sweep] $total configurations starting at $(date)"

for cfg in "${configs[@]}"; do
    i=$((i+1))
    set -- $cfg
    name=$1; ef=$2; scan=$3; rrfk=$4; pool=$5
    label="${LABEL_PREFIX}-${name}"
    echo ""
    echo "[sweep $i/$total] $label  (ef=$ef scan=$scan rrf=$rrfk pool=$pool)"
    dotnet run --no-build --configuration Release --project "$PROJECT" -- \
        bench \
        --queries "$QUERIES" \
        --history "$HISTORY" \
        --label "$label" \
        --limit 10 \
        --ef-search "$ef" \
        --iterative-scan "$scan" \
        --rrf-k "$rrfk" \
        --candidate-pool-size "$pool" \
        --rerank-top-n "$RERANK_TOPN" \
        --smoke-top10-floor 0.0 2>&1 | grep -E "(results:|history appended|smoke-gate)" || true
done

t1=$(date +%s)
echo ""
echo "[sweep] done. $total runs in $(( (t1 - t0) / 60 )) min"
