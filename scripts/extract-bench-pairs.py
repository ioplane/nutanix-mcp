"""Sprint 010 — extract (query, positive, all_positive_ids) from the
LLM-fixture for downstream hard-negative mining.

Reads tests/fixtures/bench-queries-llm.json (509-q v0.2.0 baseline,
multi-positive annotated). For each row, resolves the source-positive
text from PG and carries the full expect_chunk_ids list forward so
mining can exclude every annotator-marked positive.

Output JSONL:
    {
      "query":                "...",
      "positive":             "<raw_content of expect_chunk_ids[0]>",
      "all_positive_chunk_ids": [...],
      "category":             "ahv" | "kb" | …
    }

Usage (remote):
    uv run python scripts/extract-bench-pairs.py \\
        --bench tests/fixtures/bench-queries-llm.json \\
        --out  data/bench-pairs.jsonl
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import sys
from pathlib import Path

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger(__name__)


DSN = os.environ.get(
    "NUTANIX_MCP_POSTGRES_DSN",
    "host=127.0.0.1 port=5435 dbname=nutanix user=nutanix password=nutanix",
)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bench", default="tests/fixtures/bench-queries-llm.json")
    parser.add_argument("--out", default="data/bench-pairs.jsonl")
    args = parser.parse_args()

    try:
        import psycopg
    except ImportError:
        log.error("psycopg not installed")
        return 2

    with open(args.bench, encoding="utf-8") as f:
        fixture = json.load(f)
    queries = fixture["queries"]
    log.info("loaded %d queries from %s", len(queries), args.bench)

    chunk_ids = sorted({cid for q in queries for cid in q.get("expect_chunk_ids", [])})
    log.info("resolving %d distinct chunk_ids", len(chunk_ids))

    with psycopg.connect(DSN, connect_timeout=10) as conn, conn.cursor() as cur:
        cur.execute(
            "SELECT id, raw_content FROM chunks WHERE id = ANY(%s::bigint[])",
            (chunk_ids,),
        )
        text_by_id = {row[0]: row[1] for row in cur.fetchall()}
    log.info("  resolved %d/%d chunks", len(text_by_id), len(chunk_ids))

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    written = skipped = 0
    with out_path.open("w", encoding="utf-8", newline="\n") as f_out:
        for q in queries:
            ids = q.get("expect_chunk_ids", [])
            if not ids:
                skipped += 1
                continue
            pos_text = text_by_id.get(ids[0])
            if not pos_text:
                skipped += 1
                continue
            f_out.write(json.dumps({
                "query": q["query"],
                "positive": pos_text,
                "all_positive_chunk_ids": ids,
                "category": q.get("category"),
            }, ensure_ascii=False) + "\n")
            written += 1

    log.info("wrote %d pairs to %s (skipped %d)", written, out_path, skipped)
    return 0


if __name__ == "__main__":
    sys.exit(main())
