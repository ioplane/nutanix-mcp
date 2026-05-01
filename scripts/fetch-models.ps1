#!/usr/bin/env pwsh
# Fetch ONNX model + tokenizer assets for nutanix-mcp.
# Idempotent — skips files that already exist with matching size guard.
#
# Default embedder: bge-m3 (BAAI) — 568M params, 8192 ctx, multilingual, 1024d.
# Multilingual matters for the user's Russian-language queries against the
# English Nutanix corpus (cross-lingual retrieval works out of the box).
#
# Default reranker: bge-reranker-v2-m3 — XLM-R-base cross-encoder, multilingual.
# Pairs with bge-m3 dense; same XLM-R family avoids vocab mismatch artefacts.
# (Detection in NutanixMcp.Embedding.RerankerFamilyDetector picks XLM-R when
# sentencepiece.bpe.model is present, BERT WordPiece when only vocab.txt is.)

param(
    [string]$ModelsRoot = (Join-Path $PSScriptRoot "..\models"),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$files = @(
    # bge-m3 ONNX (1024-dim multilingual embedder).
    # The ONNX export uses the external-data pattern: model.onnx is a tiny
    # graph file (~725 KB) that REFERENCES model.onnx_data (~2.27 GB) holding
    # the actual weights. Both files must sit in the same directory or
    # ONNX Runtime fails at session-create with "Failed to load external data".
    @{
        Url = 'https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/model.onnx'
        Dest = Join-Path $ModelsRoot 'embedder\model.onnx'
        MinBytes = 100KB
    },
    @{
        Url = 'https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/model.onnx_data'
        Dest = Join-Path $ModelsRoot 'embedder\model.onnx_data'
        MinBytes = 2.0GB
    },
    @{
        Url = 'https://huggingface.co/BAAI/bge-m3/resolve/main/sentencepiece.bpe.model'
        Dest = Join-Path $ModelsRoot 'embedder\sentencepiece.bpe.model'
        MinBytes = 4MB
    },
    @{
        Url = 'https://huggingface.co/BAAI/bge-m3/resolve/main/tokenizer_config.json'
        Dest = Join-Path $ModelsRoot 'embedder\tokenizer_config.json'
        MinBytes = 100
    },
    @{
        Url = 'https://huggingface.co/BAAI/bge-m3/resolve/main/special_tokens_map.json'
        Dest = Join-Path $ModelsRoot 'embedder\special_tokens_map.json'
        MinBytes = 100
    },
    # bge-reranker-v2-m3 — XLM-R-base cross-encoder. BAAI does not ship an ONNX
    # build, so we use hooman650's Optimum-O4-optimized export (graph-level fused
    # ops, kept FP32). It uses the same external-data split: model.onnx graph +
    # model.onnx.data weights. Producing our own INT8 export is queued as a
    # Sprint 003.5 follow-up (arista shipped INT8 in the v2-m3 win recipe).
    @{
        Url = 'https://huggingface.co/hooman650/bge-reranker-v2-m3-onnx-o4/resolve/main/model.onnx'
        Dest = Join-Path $ModelsRoot 'reranker\model.onnx'
        MinBytes = 50KB
    },
    @{
        Url = 'https://huggingface.co/hooman650/bge-reranker-v2-m3-onnx-o4/resolve/main/model.onnx.data'
        Dest = Join-Path $ModelsRoot 'reranker\model.onnx.data'
        MinBytes = 1.0GB
    },
    @{
        Url = 'https://huggingface.co/hooman650/bge-reranker-v2-m3-onnx-o4/resolve/main/sentencepiece.bpe.model'
        Dest = Join-Path $ModelsRoot 'reranker\sentencepiece.bpe.model'
        MinBytes = 4MB
    },
    @{
        Url = 'https://huggingface.co/hooman650/bge-reranker-v2-m3-onnx-o4/resolve/main/tokenizer_config.json'
        Dest = Join-Path $ModelsRoot 'reranker\tokenizer_config.json'
        MinBytes = 100
    },
    @{
        Url = 'https://huggingface.co/hooman650/bge-reranker-v2-m3-onnx-o4/resolve/main/special_tokens_map.json'
        Dest = Join-Path $ModelsRoot 'reranker\special_tokens_map.json'
        MinBytes = 100
    }
)

foreach ($f in $files) {
    $dir = Split-Path $f.Dest -Parent
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    if ((-not $Force) -and (Test-Path $f.Dest)) {
        $size = (Get-Item $f.Dest).Length
        if ($size -ge $f.MinBytes) {
            Write-Host "✓ $($f.Dest) ($([math]::Round($size/1MB,1)) MB) — skip"
            continue
        }
        Write-Host "⚠ $($f.Dest) too small ($size bytes); re-downloading"
    }
    Write-Host "↓ $($f.Url)"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Invoke-WebRequest -Uri $f.Url -OutFile $f.Dest -UserAgent "Mozilla/5.0"
    $sw.Stop()
    $size = (Get-Item $f.Dest).Length
    Write-Host "  → $([math]::Round($size/1MB,1)) MB in $([math]::Round($sw.Elapsed.TotalSeconds,1))s"
    if ($size -lt $f.MinBytes) {
        throw "Downloaded $($f.Dest) is only $size bytes; expected ≥ $($f.MinBytes)"
    }
}

Write-Host ""
Write-Host "All models present at $ModelsRoot"
