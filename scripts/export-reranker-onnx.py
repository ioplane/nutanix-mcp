"""Sprint 010 — export a fine-tuned CrossEncoder checkpoint to ONNX
that the .NET XlmRobertaOnnxReranker can load.

The .NET side reads `models/reranker/model.onnx` (XLM-R encoder +
classification head, output = single logit per [query, passage] pair)
plus `sentencepiece.bpe.model`. We export the PyTorch checkpoint as
FP32 ONNX with external-data, then copy the SentencePiece tokenizer
file alongside.

Bypasses optimum.onnxruntime because torch-nightly + optimum interactions
have been brittle in this repo (see arista's export_v2m3_int8.py
note). torch.onnx.export is the stable path.

Usage on the remote:
    uv run python scripts/export-reranker-onnx.py \\
        --src checkpoints/v0.3.0-reranker \\
        --dst models/reranker-finetuned
"""

from __future__ import annotations

import argparse
import logging
import shutil
import sys
from pathlib import Path

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("export")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--src", default="checkpoints/v0.3.0-reranker")
    parser.add_argument("--dst", default="models/reranker-finetuned")
    parser.add_argument("--max-seq", type=int, default=512)
    args = parser.parse_args()

    import torch
    from transformers import AutoModelForSequenceClassification, AutoTokenizer

    src = Path(args.src)
    dst = Path(args.dst)
    dst.mkdir(parents=True, exist_ok=True)

    log.info("loading checkpoint at %s", src)
    tokenizer = AutoTokenizer.from_pretrained(str(src))
    model = AutoModelForSequenceClassification.from_pretrained(str(src))
    model.eval()

    # Copy the SentencePiece + tokenizer config files the .NET side
    # expects.
    for fname in (
        "sentencepiece.bpe.model",
        "tokenizer_config.json",
        "special_tokens_map.json",
        "tokenizer.json",
        "config.json",
    ):
        srcp = src / fname
        if srcp.exists():
            shutil.copy2(srcp, dst / fname)
            log.info("copied %s", fname)

    # Build a representative input pair for tracing. Use realistic
    # token counts so dynamic axes resolve properly.
    pair = ("How do I configure VLAN tagging on AHV?",
            "AHV virtual switch supports VLAN tagging via the acli " * 8)
    inputs = tokenizer(pair[0], pair[1], return_tensors="pt",
                       truncation=True, max_length=args.max_seq, padding=True)

    onnx_path = dst / "model.onnx"
    log.info("exporting to %s", onnx_path)

    # XLM-R cross-encoder has input_ids + attention_mask. token_type_ids
    # exists in the model but is optional for XLM-R (always zeros).
    # Force the legacy TorchScript exporter (`dynamo=False`). The new
    # dynamo-based path (default in torch 2.6+) produces graphs with
    # hardcoded shape constants that fail at inference under dynamic
    # batch/seq, even with dynamic_axes set — observed as
    # "MatMul dimension mismatch" in ORT.
    torch.onnx.export(
        model,
        (inputs["input_ids"], inputs["attention_mask"]),
        str(onnx_path),
        input_names=["input_ids", "attention_mask"],
        output_names=["logits"],
        dynamic_axes={
            "input_ids": {0: "batch", 1: "seq"},
            "attention_mask": {0: "batch", 1: "seq"},
            "logits": {0: "batch"},
        },
        opset_version=17,
        do_constant_folding=True,
        dynamo=False,
    )
    log.info("ONNX FP32 written: %d MB", onnx_path.stat().st_size // 1024 // 1024)
    log.info("  done — %s", dst)
    return 0


if __name__ == "__main__":
    sys.exit(main())
