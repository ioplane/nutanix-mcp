"""Sprint 010 — hard-negative mining for the Nutanix v0.2.0 bench fixture.

Reads data/bench-pairs.jsonl (output of extract-bench-pairs.py) and
mines hard negatives via sentence-transformers' built-in
mine_hard_negatives, using bge-m3 as the candidate scorer (matches
the production embedder so mined negatives reflect real ranking
mistakes the live retriever would make).

Critical guard: every chunk in `all_positive_chunk_ids` is excluded
from the negative pool. Without this, the cross-encoder learns
contradictory signal — "this chunk is NOT the answer" for chunks the
LLM annotator already labelled as a valid answer.

Output JSONL (label-pair format consumed by train-reranker.py):
    {"query": "...", "passage": "...", "label": 1.0}    # positive
    {"query": "...", "passage": "...", "label": 0.0}    # mined negative

Usage (remote):
    uv run python scripts/mine-negatives.py \\
        --in  data/bench-pairs.jsonl \\
        --out data/triples.jsonl
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import sys
from collections import defaultdict
from pathlib import Path

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("mine")


DSN = os.environ.get(
    "NUTANIX_MCP_POSTGRES_DSN",
    "host=127.0.0.1 port=5435 dbname=nutanix user=nutanix password=nutanix",
)


def load_corpus() -> list[dict]:
    import psycopg
    log.info("pulling corpus from postgres ...")
    with psycopg.connect(DSN, connect_timeout=10) as conn, conn.cursor() as cur:
        # Limit to leaves only — parents are summary chunks that the
        # bench harness scores against by composition. Hard-neg pool
        # of 247K leaves is plenty.
        cur.execute("SELECT id, raw_content FROM chunks WHERE chunk_kind = 'leaf' ORDER BY id")
        rows = cur.fetchall()
    log.info("  %d leaf chunks loaded", len(rows))
    return [{"chunk_id": r[0], "text": r[1]} for r in rows]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--in", dest="input", type=Path, default=Path("data/bench-pairs.jsonl"))
    parser.add_argument("--out", dest="output", type=Path, default=Path("data/triples.jsonl"))
    parser.add_argument("--num-negatives", type=int, default=8)
    parser.add_argument("--range-min", type=int, default=10)
    parser.add_argument("--range-max", type=int, default=100)
    parser.add_argument("--margin", type=float, default=0.02)
    parser.add_argument("--scorer", default="BAAI/bge-m3",
                        help="Embedder used for candidate ranking during mining (matches production)")
    args = parser.parse_args()

    if not args.input.exists():
        log.error("input not found: %s", args.input)
        return 2

    from datasets import Dataset
    from sentence_transformers import SentenceTransformer
    from sentence_transformers.util import mine_hard_negatives

    pairs: list[dict] = []
    with args.input.open(encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                pairs.append(json.loads(line))
    log.info("loaded %d query/positive pairs", len(pairs))

    corpus = load_corpus()
    text_by_id = {c["chunk_id"]: c["text"] for c in corpus}
    id_by_text: dict[str, int] = {c["text"]: c["chunk_id"] for c in corpus}
    log.info("text→id lookup built (%d entries)", len(id_by_text))

    # all_positive map for post-filter exclusion
    all_pos_by_query: dict[str, set[int]] = {
        p["query"]: set(p["all_positive_chunk_ids"]) for p in pairs
    }

    ds = Dataset.from_dict({
        "query": [p["query"] for p in pairs],
        "answer": [p["positive"] for p in pairs],
    })
    log.info("loading scorer model: %s", args.scorer)
    # max_seq_length=512 keeps memory bounded — production embedder also
    # truncates at 512 (chunker target ≤ 350 tokens with overlap).
    scorer = SentenceTransformer(args.scorer, trust_remote_code=False)
    scorer.max_seq_length = 512

    log.info("mining hard negatives (num=%d range=%d..%d margin=%.2f, batch=8)",
             args.num_negatives, args.range_min, args.range_max, args.margin)
    # batch=8 keeps the encoder peak under 8 GB on 5090. Still fast on
    # GPU because corpus encoding dominates and is parallelised across
    # cores.
    mined = mine_hard_negatives(
        dataset=ds,
        model=scorer,
        corpus=[c["text"] for c in corpus],
        num_negatives=args.num_negatives,
        range_min=args.range_min,
        range_max=args.range_max,
        absolute_margin=args.margin,
        sampling_strategy="top",
        batch_size=8,
        output_format="labeled-pair",
        use_faiss=False,
    )
    log.info("mined %d labelled rows", len(mined))

    # Post-filter: drop any negative whose chunk_id is in
    # all_positive_chunk_ids for that query.
    args.output.parent.mkdir(parents=True, exist_ok=True)
    kept_pos = kept_neg = filtered = 0
    drops_per_query: dict[str, int] = defaultdict(int)
    with args.output.open("w", encoding="utf-8", newline="\n") as f_out:
        for row in mined:
            query = row["query"]
            passage = row["passage"] if "passage" in row else row.get("answer")
            label = float(row["label"])
            if label == 1.0:
                f_out.write(json.dumps({"query": query, "passage": passage, "label": 1.0},
                                       ensure_ascii=False) + "\n")
                kept_pos += 1
                continue
            cid = id_by_text.get(passage)
            if cid is not None and cid in all_pos_by_query.get(query, ()):
                filtered += 1
                drops_per_query[query] += 1
                continue
            f_out.write(json.dumps({"query": query, "passage": passage, "label": 0.0},
                                   ensure_ascii=False) + "\n")
            kept_neg += 1

    log.info("done — kept %d pos + %d neg = %d rows; filtered %d positives masquerading as negatives",
             kept_pos, kept_neg, kept_pos + kept_neg, filtered)
    if drops_per_query:
        log.info("queries with filter drops: %d", len(drops_per_query))
    return 0


if __name__ == "__main__":
    sys.exit(main())
