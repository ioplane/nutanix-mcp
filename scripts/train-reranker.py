"""Sprint 010 — fine-tune bge-reranker-v2-m3 on Nutanix triples.

Adapted from arista-reranker-tune/scripts/train_v2m3.py. Recipe locked
to the arista win record (BCE pos_weight=NUM_NEGATIVES, batch=16,
max_seq=512, lr=2e-5, 3 epochs, warmup 0.1).

Reads data/triples.jsonl (output of mine-negatives.py), splits by
query hash, trains, evaluates per epoch on hold-out NDCG@10, saves
best checkpoint.

Usage on the remote:
    uv run python scripts/train-reranker.py
"""

from __future__ import annotations

import json
import logging
import random
import sys
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("train")


TRIPLES_PATH = Path("data/triples.jsonl")
OUTPUT_DIR = Path("checkpoints/v0.3.0-reranker")
BASE_MODEL_ID = "BAAI/bge-reranker-v2-m3"
EVAL_RATIO = 0.15
SEED = 42
BATCH_SIZE = 16
EPOCHS = 3
LR = 2e-5
WARMUP_RATIO = 0.1
MAX_SEQ = 512
NUM_NEGATIVES = 8


@dataclass
class Row:
    query: str
    passage: str
    label: float


def _load_rows(path: Path) -> list[Row]:
    rows: list[Row] = []
    bad = 0
    with path.open("rb") as f:
        for lineno, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except json.JSONDecodeError as e:
                bad += 1
                log.warning("skip malformed row %d: %s", lineno, e)
                continue
            rows.append(Row(obj["query"], obj["passage"], float(obj["label"])))
    if bad:
        log.warning("loaded %d rows, skipped %d malformed", len(rows), bad)
    return rows


def _split_by_query(rows: list[Row]) -> tuple[list[Row], list[Row]]:
    """Deterministic query-level split — all rows for one query land
    in the same bucket so evaluation never sees a paraphrase of
    training data."""
    import hashlib

    def _bucket(q: str) -> str:
        h = hashlib.sha256(f"{SEED}:{q}".encode()).digest()
        return "eval" if (h[0] / 255.0) < EVAL_RATIO else "train"

    train, eval_ = [], []
    for r in rows:
        (eval_ if _bucket(r.query) == "eval" else train).append(r)
    return train, eval_


def _build_rerank_eval_samples(eval_rows: list[Row]) -> list[dict]:
    grouped: dict[str, dict] = defaultdict(lambda: {"positive": [], "negative": []})
    for r in eval_rows:
        key = "positive" if r.label == 1.0 else "negative"
        grouped[r.query][key].append(r.passage)
    samples = []
    for query, pn in grouped.items():
        if not pn["positive"] or not pn["negative"]:
            continue
        samples.append({"query": query, "positive": pn["positive"], "negative": pn["negative"]})
    return samples


def main() -> int:
    random.seed(SEED)
    import torch
    torch.manual_seed(SEED)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(SEED)
    log.info("torch %s | cuda=%s | gpus=%d",
             torch.__version__, torch.cuda.is_available(), torch.cuda.device_count())

    if not TRIPLES_PATH.exists():
        log.error("triples missing at %s — run scripts/mine-negatives.py first", TRIPLES_PATH)
        return 2

    rows = _load_rows(TRIPLES_PATH)
    log.info("rows=%d  pos=%d  neg=%d",
             len(rows), sum(1 for r in rows if r.label == 1.0),
             sum(1 for r in rows if r.label == 0.0))

    train_rows, eval_rows = _split_by_query(rows)
    log.info("train=%d  eval=%d", len(train_rows), len(eval_rows))

    from datasets import Dataset
    from sentence_transformers import CrossEncoder
    from sentence_transformers.cross_encoder import (
        CrossEncoderTrainer,
        CrossEncoderTrainingArguments,
    )
    from sentence_transformers.cross_encoder.evaluation import CrossEncoderRerankingEvaluator
    from sentence_transformers.cross_encoder.losses import BinaryCrossEntropyLoss

    log.info("loading CrossEncoder: %s", BASE_MODEL_ID)
    model = CrossEncoder(BASE_MODEL_ID, num_labels=1, max_length=MAX_SEQ)

    train_ds = Dataset.from_dict({
        "query":   [r.query for r in train_rows],
        "passage": [r.passage for r in train_rows],
        "label":   [r.label for r in train_rows],
    })

    # pos_weight=NUM_NEGATIVES addresses 1:N class imbalance from
    # hard-neg mining. Without it BCE trivially minimises by predicting
    # low logit everywhere — same failure mode as arista's v0.2.0.
    loss = BinaryCrossEntropyLoss(model=model, pos_weight=torch.tensor(float(NUM_NEGATIVES)))

    rerank_samples = _build_rerank_eval_samples(eval_rows)
    log.info("rerank-eval samples: %d queries", len(rerank_samples))
    evaluator = CrossEncoderRerankingEvaluator(
        samples=rerank_samples,
        at_k=10,
        mrr_at_k=10,
        name="nutanix-eval",
        always_rerank_positives=True,
        show_progress_bar=False,
    )

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    args = CrossEncoderTrainingArguments(
        output_dir=str(OUTPUT_DIR),
        num_train_epochs=EPOCHS,
        per_device_train_batch_size=BATCH_SIZE,
        learning_rate=LR,
        warmup_ratio=WARMUP_RATIO,
        fp16=True,
        logging_steps=10,
        eval_strategy="epoch",
        save_strategy="epoch",
        save_total_limit=2,
        seed=SEED,
        load_best_model_at_end=True,
        metric_for_best_model=f"eval_nutanix-eval_ndcg@10",
        greater_is_better=True,
    )
    trainer = CrossEncoderTrainer(
        model=model, args=args, train_dataset=train_ds, loss=loss, evaluator=evaluator,
    )
    log.info("▸ training")
    trainer.train()
    log.info("▸ saving best checkpoint to %s", OUTPUT_DIR)
    model.save_pretrained(OUTPUT_DIR)
    print(f"OK: {OUTPUT_DIR}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
