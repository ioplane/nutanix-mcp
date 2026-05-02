"""Sprint 003.5 — INT8 dynamic quantization of bge-reranker-v2-m3.

UNBLOCKED in v0.3.0 (2026-05-02): the original blocker was the FP16
source export shipping with `optimum.exporters.onnx --library-name
transformers --task text-classification` produces a clean FP32 ONNX
that quantizes to INT8 without the DynamicQuantizeLinear-on-FP16
issue.

Production v0.3.0 acceptance run (Sprint 003.5):

  Source:  fine-tuned XLM-R 568M (Sprint 010 checkpoint)
  Output:  INT8 weights via `quantize_dynamic` on MatMul ops
  Size:    2.17 GB FP32 → 1.30 GB INT8 (1.67x compression)
  Bench:   top-1 80.2 % (vs 80.6 % FP32, -0.4 pp — within the
           ≤ 1 pp acceptance gate)
  Latency: p50 2014 ms (vs 2737 ms FP32, -26 %)
           p95 2930 ms (vs 3915 ms FP32, -25 %)

Reads <src>/model.onnx (XLM-R cross-encoder, FP32, external-data
layout) and writes <dst>/model.onnx with INT8 weights for MatMul ops.
Skips Gather (used to fail on FP16 dequant scales — not relevant for
the FP32 source but kept skipped for forward-compat).

Dynamic quantization is the right pick for transformer cross-encoders here:

  - Weight-heavy model: ~95% of inference cost is MatMul against the
    weight matrices, which dynamic quantization compresses 4x.
  - No calibration data required (static quantization would need a few
    hundred representative input pairs to capture activation ranges; we
    skip that complexity for first-cut INT8).
  - ORT picks up AVX-512-VNNI automatically at session-init time when
    the CPU supports it — no model-side flag needed.

Acceptance gate (in docs/en/training-rag.md): top-1 regression ≤ 1pp
versus FP32 baseline, with measurable p95 improvement.

Usage on the remote (or any host with optimum + onnxruntime):
  uv pip install --python .venv/bin/python 'onnxruntime>=1.18'
  .venv/bin/python scripts/quantize-reranker.py \\
      --src  models/reranker \\
      --dst  models/reranker-int8
"""

from __future__ import annotations

import argparse
import logging
import shutil
import sys
import time
from pathlib import Path

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger(__name__)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--src", default="models/reranker",
                        help="Source dir with model.onnx (+ optional model.onnx_data and tokenizer)")
    parser.add_argument("--dst", default="models/reranker-int8",
                        help="Destination dir; created if missing")
    parser.add_argument("--per-channel", action="store_true",
                        help="Per-channel weight quantization (slightly higher accuracy, slightly slower)")
    parser.add_argument("--reduce-range", action="store_true",
                        help="Reduce activation range to int7 — required for some non-VNNI CPUs")
    args = parser.parse_args()

    try:
        import onnx
        from onnxruntime.quantization import quantize_dynamic, QuantType
    except ImportError:
        log.error("onnxruntime / onnx not installed; run `uv pip install onnxruntime onnx`")
        return 2

    src = Path(args.src)
    dst = Path(args.dst)
    src_model = src / "model.onnx"
    dst_model = dst / "model.onnx"

    if not src_model.exists():
        log.error("source model not found: %s", src_model)
        return 2

    dst.mkdir(parents=True, exist_ok=True)

    # Copy tokenizer + auxiliary files (sentencepiece.bpe.model, config.json,
    # tokenizer.json, special_tokens_map.json, etc.) — the .NET-side
    # XlmRobertaTokenizer reads sentencepiece.bpe.model from the model dir.
    aux_files = [
        "sentencepiece.bpe.model",
        "tokenizer.json",
        "tokenizer_config.json",
        "config.json",
        "special_tokens_map.json",
    ]
    for name in aux_files:
        srcp = src / name
        if srcp.exists():
            shutil.copy2(srcp, dst / name)
            log.info("copied %s", name)

    # bge-reranker-v2-m3's symbolic shape inference fails on a broadcast
    # case (Min(8194, sequence_length) vs sequence_length). We skip the
    # ORT pre-process pass and rely on DefaultTensorType=FLOAT during
    # dynamic quantization — works because every MatMul has FP32 inputs
    # in this XLM-R cross-encoder.
    log.info("quantizing %s -> %s (per_channel=%s reduce_range=%s)",
             src_model, dst_model, args.per_channel, args.reduce_range)
    t0 = time.time()
    quantize_dynamic(
        model_input=str(src_model),
        model_output=str(dst_model),
        weight_type=QuantType.QInt8,
        per_channel=args.per_channel,
        reduce_range=args.reduce_range,
        # MatMul only — bge-reranker-v2-m3 is exported with FP16 weights,
        # which produces FP16 dequantize-scales when Gather is quantized.
        # ONNX Runtime rejects FP16 scales on DequantizeLinear at session
        # init. Skipping Gather keeps the embedding lookup at FP16 (still
        # 2x smaller than FP32) and quantizes only the bulk-of-compute
        # MatMul layers.
        op_types_to_quantize=["MatMul"],
        extra_options={"DefaultTensorType": onnx.TensorProto.FLOAT},
    )
    elapsed = time.time() - t0

    fp32_size = src_model.stat().st_size
    fp32_data = src / "model.onnx_data"
    if fp32_data.exists():
        fp32_size += fp32_data.stat().st_size
    int8_size = dst_model.stat().st_size
    int8_data = dst / "model.onnx_data"
    if int8_data.exists():
        int8_size += int8_data.stat().st_size

    log.info("done in %.1fs", elapsed)
    log.info("FP32 total: %.1f MB", fp32_size / 1024 / 1024)
    log.info("INT8 total: %.1f MB", int8_size / 1024 / 1024)
    log.info("compression: %.2fx", fp32_size / max(1, int8_size))
    return 0


if __name__ == "__main__":
    sys.exit(main())
