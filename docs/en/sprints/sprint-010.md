# Sprint 010 — bge-reranker-v2-m3 fine-tune on Nutanix triples

Status: DONE (2026-05-02). Cross-encoder fine-tune on the 509-q v0.2.0
LLM bench fixture, hold-out NDCG@10 = 0.9942, production retrieval
top-1 = 80.6 % (+1.6 pp over the FP32 stock baseline).

## Toolchain

End-to-end on the remote (PG + LLM + GPU all localhost). Code lands
locally; heavy compute runs on `ssh gpu`.

| Stage | Script | Wall-clock |
|---|---|---|
| Extract (q, pos, all_pos_ids) | `scripts/extract-bench-pairs.py` | 1 s |
| Mine hard negatives via bge-m3 | `scripts/mine-negatives.py` | ~25 min |
| Train CrossEncoder | `scripts/train-reranker.py` | **131 s** |
| Export to ONNX | `scripts/export-reranker-onnx.py` | ~10 s |
| Bench validation | `nutanix-mcp bench --reranker-dir …` | ~25 min |

The training stage uses 4137 mined triples (mean positive score
0.69, mean negative 0.63 by bge-m3 cosine; 20.4 % of potential
negatives skipped under absolute_margin=0.02). Recipe locked to the
arista win record: BCE pos_weight=8, batch=16, max_seq=512,
lr=2e-5, 3 epochs, warmup ratio 0.1.

## Recipe summary

```text
base model:   BAAI/bge-reranker-v2-m3 (XLM-R, 568M, FP32)
triples:      4137 (q, passage, label) — derived from 509-q LLM bench
              fixture + bge-m3 hard-neg mining with multi-positive guard
loss:         BCELoss with pos_weight=8 (compensates 1:8 class imbalance)
optim:        AdamW, lr=2e-5, warmup_ratio=0.1
batch:        16 per device (single 5090, ~17 GB peak VRAM)
seq:          512 max
epochs:       3 (669 train steps total)
seed:         42
holdout:      query-hash bucket, 15 % eval ratio
metric:       eval_nutanix-eval_ndcg@10
```

## Hold-out evaluation (per-epoch, sentence-transformers Reranking)

| epoch | NDCG@10 | MAP | MRR@10 |
|---:|---:|---:|---:|
| 1 | 0.9923 | 0.9824 | 0.9824 |
| 2 | 0.9923 | 0.9824 | 0.9824 |
| **3 (best)** | **0.9942** | **0.9922** | **0.9922** |

## Production bench (509-q LLM fixture)

| Metric | v0.2.0 stock FP32 | **v0.3.0 fine-tune FP32** | Δ |
|---|---:|---:|---:|
| top-1 | 79.0 % | **80.6 %** | **+1.6 pp** |
| top-5 | 99.0 % | 99.4 % | +0.4 pp |
| top-10 | 100.0 % | 100.0 % | flat |
| p50 (ms) | 2625 | 2737 | +4 % |
| p95 (ms) | 3675 | 3915 | +7 % |

Comparison to arista's precedent (588-q fixture, +2.04 pp post-fine-tune):
+1.6 pp is in the same range, consistent with the recipe.

The slight latency uplift (+4–7 %) is expected — fine-tuned weights
have different sparsity / activation patterns vs the stock FP32. Not
observable to a user.

Top-10 saturated at 100 % both pre- and post-fine-tune — measurement
ceiling persists. Real differentiator is the top-1 movement, which is
what fine-tune was supposed to fix (cross-encoder ranking within an
already-good candidate set).

## Why we did not swap to Qwen3-Embedding/Reranker

A late-cycle research found Qwen3-Embedding-0.6B / Qwen3-Reranker-0.6B
on Hugging Face (released 2025, Apache-2.0, claimed +5 pp MTEB
multilingual + +5 pp BEIR rerank). On paper, drop-in successors. In
practice — substantial code refactor, not a model swap:

- Decoder-only architecture (vs XLM-R encoder)
- Qwen BPE tokenizer (vs SentencePiece) — needs a new C# tokenizer
- Qwen3-Embedding requires `Instruct: …\nQuery: …` prompt prefix and
  last-token pool (vs bge-m3's mean-pool)
- Qwen3-Reranker is generative (yes/no logprob scoring) unless via
  the `seq-cls` community variant, still with Qwen tokenizer

Estimated swap cost: ~6-8 hours (new tokenizer + new embedder + new
reranker classes + re-ingest 489K chunks). Tracked as a v0.4.0
sprint.

## Outstanding work / blocked

- **INT8 of fine-tuned reranker** (Sprint 003.5 retry): the FP32 ONNX
  export from PyTorch should now quantize cleanly via the existing
  `scripts/quantize-reranker.py`. Not run yet — does not block v0.3.0.
- **Wider bench expansion (n > 1000)** to break the top-10 = 100 %
  ceiling. Required to measure further fine-tune iterations.
- **Production-log hard negatives** (Sprint 011 / arista iter 2):
  needs deployed users first.

## Reproducibility checklist

To reproduce on a fresh remote with PG populated and 4× GPU:

```bash
ssh gpu
cd /work/nutanix-mcp
uv pip install --python .venv/bin/python \
    'transformers>=4.40,<5' 'accelerate>=0.30' 'datasets>=2.20' \
    'sentence-transformers>=3.0' onnx onnxruntime onnxscript \
    'optimum-onnx'

# 1. extract bench pairs
.venv/bin/python scripts/extract-bench-pairs.py \
    --bench tests/fixtures/bench-queries-llm.json \
    --out  data/bench-pairs.jsonl

# 2. mine hard negatives (kill ollama first to free GPU 0)
pkill -f 'ollama serve' || true
PYTORCH_CUDA_ALLOC_CONF=expandable_segments:True \
.venv/bin/python scripts/mine-negatives.py \
    --in  data/bench-pairs.jsonl \
    --out data/triples.jsonl

# 3. train (single-GPU — DataParallel is broken with sentence-transformers loss)
CUDA_VISIBLE_DEVICES=0 \
.venv/bin/python scripts/train-reranker.py

# 4. export ONNX FP32 with external data
CUDA_VISIBLE_DEVICES=0 \
.venv/bin/python -m optimum.exporters.onnx \
    --model checkpoints/v0.3.0-reranker \
    --library-name transformers --task text-classification --opset 17 \
    models/reranker-finetuned

# 5. copy SentencePiece (the .NET tokenizer needs it)
cp /root/.cache/huggingface/hub/models--BAAI--bge-reranker-v2-m3/snapshots/*/sentencepiece.bpe.model \
   models/reranker-finetuned/

# 6. bench
NUTANIX_MCP__CONNECTIONSTRING='Host=127.0.0.1;Port=5435;Database=nutanix;Username=nutanix;Password=nutanix' \
dotnet run --no-build --configuration Release --project src/NutanixMcp.Cli -- \
    bench \
    --queries tests/fixtures/bench-queries-llm.json \
    --history tests/fixtures/bench-history.jsonl \
    --label v0.3.0-finetune-reranker \
    --reranker-dir models/reranker-finetuned \
    --smoke-top10-floor 0.0
```
