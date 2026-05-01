"""Stratified random sample of nutanix corpus chunks for bench-set generation.

Adapted from arista-reranker-tune/scripts/sample_chunks_for_bench.py.
Stratifies by documents.category (since documents.product is NULL across
the nutanix corpus). Filters out boilerplate sections (**Procedure**,
**Note:**, etc.) and pure code-block chunks. Per-category floor/ceiling
keeps small categories represented and stops `kb` (5490 docs) from
dominating.

Output: data/bench-seed-chunks.jsonl
  {chunk_id, document_id, doc_title, section_title, category, token_count, content}

Usage on the remote (PG localhost):
  uv run python scripts/sample-chunks-for-bench.py \\
      --out data/bench-seed-chunks.jsonl --target 200
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import random
import sys
from pathlib import Path

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger(__name__)


DSN = os.environ.get(
    "NUTANIX_MCP_POSTGRES_DSN",
    "host=127.0.0.1 port=5435 dbname=nutanix user=nutanix password=nutanix",
)

CATEGORY_FLOOR = 5
CATEGORY_CEILING = 80

BOILERPLATE_SECTIONS = {
    "**Procedure**", "**About this task**", "**Before you begin**",
    "**Note:**", "**What to do next**", "**Warning:**",
    "**Required arguments**", "**Optional arguments**",
    "**Tools and supplies required:**", "**Contents**", "**Template**",
    "Procedure", "About this task",
}


def allocate_quota(pool_sizes: dict[str, int], target_total: int) -> dict[str, int]:
    total_pool = sum(pool_sizes.values())
    raw: dict[str, int] = {}
    for cat, n in pool_sizes.items():
        prop = round(target_total * n / total_pool)
        raw[cat] = max(CATEGORY_FLOOR, min(CATEGORY_CEILING, prop, n))
    diff = target_total - sum(raw.values())
    if diff != 0:
        order = sorted(raw, key=raw.get, reverse=(diff > 0))
        for cat in order:
            room = (CATEGORY_CEILING - raw[cat]) if diff > 0 else (raw[cat] - CATEGORY_FLOOR)
            nudge = min(abs(diff), room, pool_sizes[cat] - raw[cat] if diff > 0 else raw[cat])
            if nudge == 0:
                continue
            raw[cat] += nudge if diff > 0 else -nudge
            diff -= nudge if diff > 0 else -nudge
            if diff == 0:
                break
    return raw


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default="data/bench-seed-chunks.jsonl")
    parser.add_argument("--target", type=int, default=200)
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()

    random.seed(args.seed)

    try:
        import psycopg
    except ImportError:
        log.error("psycopg not installed. Run `uv sync` or `uv add psycopg[binary]`.")
        return 2

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    # All filters are parameterized — never interpolate user / set-derived
    # data into the SQL string. The boilerplate set ships as a list bound
    # to a `text[]` placeholder.
    boilerplate_list = list(BOILERPLATE_SECTIONS)

    with psycopg.connect(DSN, connect_timeout=10) as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT d.category, COUNT(c.id)
            FROM chunks c
            JOIN documents d ON c.document_id = d.id
            WHERE c.chunk_kind = 'leaf'
              AND c.token_count BETWEEN 200 AND 700
              AND COALESCE(c.section_title, '') <> ALL (%s::text[])
              AND c.raw_content NOT LIKE '```%%'
            GROUP BY d.category
            """,
            (boilerplate_list,),
        )
        pool: dict[str, int] = {row[0]: row[1] for row in cur.fetchall()}

    log.info("eligible pool: %d chunks across %d categories", sum(pool.values()), len(pool))
    quotas = allocate_quota(pool, args.target)
    log.info("quota allocation: %s (total=%d)",
             dict(sorted(quotas.items(), key=lambda x: -x[1])), sum(quotas.values()))

    written = 0
    with psycopg.connect(DSN, connect_timeout=10) as conn, conn.cursor() as cur, \
         out_path.open("w", encoding="utf-8", newline="\n") as f_out:
        for cat, quota in quotas.items():
            if quota == 0:
                continue
            cur.execute(
                """
                SELECT c.id, c.document_id, d.title, c.section_title, d.category,
                       c.token_count, c.raw_content
                FROM chunks c
                JOIN documents d ON c.document_id = d.id
                WHERE c.chunk_kind = 'leaf'
                  AND d.category = %s
                  AND c.token_count BETWEEN 200 AND 700
                  AND COALESCE(c.section_title, '') <> ALL (%s::text[])
                  AND c.raw_content NOT LIKE '```%%'
                ORDER BY random()
                LIMIT %s
                """,
                (cat, boilerplate_list, quota),
            )
            for row in cur.fetchall():
                record = {
                    "chunk_id": row[0],
                    "document_id": row[1],
                    "doc_title": row[2],
                    "section_title": row[3],
                    "category": row[4],
                    "token_count": row[5],
                    "content": row[6],
                }
                f_out.write(json.dumps(record, ensure_ascii=False) + "\n")
                written += 1

    log.info("wrote %d chunks → %s", written, out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
