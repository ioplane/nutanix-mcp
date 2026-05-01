"""Annotate each fairness-filtered bench query with multi-positive ground truth.

Adapted from arista-reranker-tune/scripts/annotate_multi_positives.py for
the nutanix corpus. Reads bench-queries-validated.jsonl (output of the C#
verb `filter-bench-queries`) which already has the retriever's top-10
chunk_ids per query. For each query, pulls those 10 chunks' content from
postgres, asks Qwen2.5-3B to label which of the candidates would be a
reasonable answer, and writes the final v2 fixture in nutanix's
BenchQueryFile schema (schema_version=2).

source_chunk_id is always included in expect_chunk_ids — the query was
generated FROM that chunk so by construction it is a valid answer.

Usage on the remote (PG + LLM both on localhost):
  uv run python scripts/annotate-multi-positives.py \\
      --in  data/bench-queries-validated.jsonl \\
      --out data/bench-queries-v2.json
"""

from __future__ import annotations

import argparse
import http.client
import json
import logging
import os
import re
import socket
import sys
import time
from pathlib import Path
from urllib.parse import urlsplit

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger(__name__)


DSN = os.environ.get(
    "NUTANIX_MCP_POSTGRES_DSN",
    "host=127.0.0.1 port=5435 dbname=nutanix user=nutanix password=nutanix",
)
DEFAULT_ENDPOINT = "127.0.0.1:11434/v1/chat/completions"
DEFAULT_SCHEME = "http"
MODEL = "qwen2.5:3b-instruct"
TIMEOUT = 60.0
CONTENT_SNIPPET_LIMIT = 600

SYSTEM_PROMPT = (
    "You are annotating retrieval benchmark ground truth for Nutanix HCI "
    "documentation. Given a user query and candidate passages, identify "
    "every passage that a reasonable Nutanix administrator would accept "
    "as a valid answer — not just THE best one. Multiple correct answers "
    "are the norm (how-to + reference + example all answer one query). "
    "Return ONLY a JSON array of the candidate IDs you label as valid. "
    "No prose."
)

USER_TEMPLATE = (
    "Query: {query}\n\n"
    "Source passage (always valid, id={source_id}):\n{source_excerpt}\n\n"
    "Candidate passages from retrieval:\n{candidates_block}\n\n"
    "Return a JSON array of candidate IDs that would be a valid answer. "
    "Always include {source_id}. Example: [{source_id}, 4567]"
)

JSON_ARRAY_RE = re.compile(r"\[[^\[\]]*\]", re.DOTALL)


def _split_endpoint(endpoint: str) -> tuple[str, int, str]:
    parts = urlsplit(f"http://{endpoint}")
    if parts.scheme != "http" or not parts.hostname:
        raise ValueError(f"invalid endpoint: {endpoint}")
    port = parts.port or 80
    path = parts.path or "/"
    if parts.query:
        path = f"{path}?{parts.query}"
    return parts.hostname, port, path


def post_chat(messages: list[dict], endpoint: str, max_tokens: int = 200) -> str | None:
    body = json.dumps({
        "model": MODEL,
        "messages": messages,
        "max_tokens": max_tokens,
        "temperature": 0.1,
        "stream": False,
    }).encode("utf-8")
    try:
        host, port, path = _split_endpoint(endpoint)
        conn = http.client.HTTPConnection(host, port, timeout=TIMEOUT)
        try:
            conn.request("POST", path, body=body,
                         headers={"Content-Type": "application/json"})
            resp = conn.getresponse()
            if resp.status >= 400:
                log.warning("LLM call HTTP %d: %s", resp.status, resp.reason)
                return None
            payload = json.loads(resp.read().decode("utf-8"))
        finally:
            conn.close()
        return payload["choices"][0]["message"]["content"]
    except (OSError, socket.timeout, TimeoutError,
            KeyError, IndexError, json.JSONDecodeError, ValueError) as e:
        log.warning("LLM call failed: %s", e)
        return None


def parse_id_array(raw: str) -> list[int]:
    if not raw:
        return []
    match = JSON_ARRAY_RE.search(raw)
    if not match:
        return []
    try:
        arr = json.loads(match.group(0))
    except json.JSONDecodeError:
        return []
    out: list[int] = []
    for v in arr:
        if isinstance(v, int):
            out.append(v)
        elif isinstance(v, str) and v.strip().isdigit():
            out.append(int(v.strip()))
    return out


def fetch_chunks(cur, ids: list[int]) -> dict[int, str]:
    if not ids:
        return {}
    cur.execute(
        "SELECT id, raw_content FROM chunks WHERE id = ANY(%s::bigint[])",
        (ids,),
    )
    return {row[0]: row[1] for row in cur.fetchall()}


def main() -> int:
    global MODEL
    parser = argparse.ArgumentParser()
    parser.add_argument("--in", dest="inp", default="data/bench-queries-validated.jsonl")
    parser.add_argument("--out", default="data/bench-queries-v2.json")
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--endpoint", default=DEFAULT_ENDPOINT)
    parser.add_argument("--model", default=MODEL)
    parser.add_argument("--corpus-sha", default=None,
                        help="Catalog sha pinned into the output schema; defaults to last successful ingest_runs row")
    args = parser.parse_args()
    MODEL = args.model

    try:
        import psycopg
    except ImportError:
        log.error("psycopg not installed. Run `uv pip install psycopg[binary]`.")
        return 2

    in_path = Path(args.inp)
    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    rows: list[dict] = []
    with in_path.open("r", encoding="utf-8") as f:
        for line in f:
            if not line.strip():
                continue
            rows.append(json.loads(line))
    if args.limit:
        rows = rows[: args.limit]
    log.info("loaded %d filtered queries", len(rows))

    queries: list[dict] = []
    qid = 0
    t0 = time.time()
    with psycopg.connect(DSN, connect_timeout=10) as conn, conn.cursor() as cur:
        for i, row in enumerate(rows):
            sid = int(row["source_chunk_id"])
            top10 = [int(x) for x in row.get("retriever_top_10_chunk_ids") or []]
            all_ids = list(dict.fromkeys([sid, *top10]))
            content_map = fetch_chunks(cur, all_ids)
            if sid not in content_map:
                log.warning("source chunk %d missing from DB; skipping", sid)
                continue

            source_excerpt = content_map[sid][:CONTENT_SNIPPET_LIMIT]
            candidates_block_lines = []
            for cid in top10:
                if cid == sid or cid not in content_map:
                    continue
                snippet = content_map[cid][:CONTENT_SNIPPET_LIMIT]
                candidates_block_lines.append(f"id={cid}:\n{snippet}\n")
            candidates_block = "\n---\n".join(candidates_block_lines) or "(no other candidates)"

            user = USER_TEMPLATE.format(
                query=row["query"],
                source_id=sid,
                source_excerpt=source_excerpt,
                candidates_block=candidates_block,
            )
            raw = post_chat(
                [
                    {"role": "system", "content": SYSTEM_PROMPT},
                    {"role": "user", "content": user},
                ],
                endpoint=args.endpoint,
            )
            labelled = parse_id_array(raw or "")

            # Always include source_chunk_id (positive by construction);
            # take the LLM's labelled set ∩ retriever top-10 as additional
            # positives. Reject IDs the LLM hallucinates that aren't in
            # the candidate set.
            valid_pool = set(top10) | {sid}
            extras = [cid for cid in labelled if cid in valid_pool and cid != sid]
            expect_chunk_ids = [sid, *extras]

            qid += 1
            queries.append({
                "query_id": f"q-{qid:04d}",
                "query": row["query"],
                "expect_chunk_ids": expect_chunk_ids,
                "expect_doc": row.get("source_doc_id"),
                "expect_section": row.get("source_section_title"),
                "expect_product": None,
                "category": row.get("source_category"),
                "difficulty": "easy",
            })

            if (i + 1) % 10 == 0:
                elapsed = time.time() - t0
                rate = (i + 1) / elapsed if elapsed > 0 else 0
                eta = (len(rows) - i - 1) / rate if rate > 0 else 0
                log.info("[%d/%d] kept=%d rate=%.2f q/s eta=%.0fs",
                         i + 1, len(rows), len(queries), rate, eta)

    corpus_sha = args.corpus_sha
    if corpus_sha is None:
        try:
            with psycopg.connect(DSN, connect_timeout=10) as conn, conn.cursor() as cur:
                cur.execute(
                    "SELECT catalog_sha256 FROM ingest_runs WHERE status='success' "
                    "ORDER BY started_at DESC LIMIT 1"
                )
                row = cur.fetchone()
                corpus_sha = row[0] if row and row[0] else "unknown"
        except Exception:
            corpus_sha = "unknown"

    payload = {
        "schema_version": 2,
        "corpus_sha": corpus_sha,
        "queries": queries,
    }
    out_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2),
                        encoding="utf-8")
    log.info("wrote %d annotated queries → %s (corpus_sha=%s)",
             len(queries), out_path, corpus_sha)
    return 0


if __name__ == "__main__":
    sys.exit(main())
