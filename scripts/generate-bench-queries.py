"""Generate bench queries from nutanix-mcp seed chunks via a local LLM.

Adapted from arista-reranker-tune/scripts/generate_bench_queries.py. The
nutanix corpus differs in three ways:

  - documents.product is NULL across the corpus, so the prompt cannot
    condition on a product family. Substituting documents.category.
  - 75 % of docs are KB articles whose titles already state the user
    question ("How do I configure VLAN tagging on AHV?"); the prompt
    leans on raw_content rather than title to avoid trivially echoing
    the title back as the query.
  - Output JSONL is consumed by the nutanix-mcp `validate-bench-queries`
    verb, which uses the field name `source_chunk_id` (matching arista).

Usage on the remote GPU host (PG + LLM both on localhost):

    uv run python scripts/generate-bench-queries.py \\
        --in  data/bench-seed-chunks.jsonl \\
        --out data/bench-queries-raw.jsonl \\
        --resume

Run sample_chunks_for_bench (or its PowerShell equivalent on the local
side) first to populate the input.
"""

from __future__ import annotations

import argparse
import http.client
import json
import logging
import re
import socket
import sys
import time
from pathlib import Path
from urllib.parse import urlsplit

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger(__name__)


# Default endpoint is loopback-only Ollama. We use http.client directly
# rather than urllib.request because http.client cannot be tricked into
# reading file:// or other non-HTTP schemes — urlsplit + a scheme
# allowlist enforce that, and the connection class is chosen at call time
# so a malicious --endpoint cannot smuggle in a file:// handler.
DEFAULT_ENDPOINT = "127.0.0.1:11434/v1/chat/completions"
DEFAULT_SCHEME = "http"  # localhost only; production deployments must override
MODEL = "qwen2.5:3b-instruct"
TIMEOUT = 60.0

SYSTEM_PROMPT = (
    "You are helping build a retrieval benchmark for Nutanix HCI / AHV / "
    "Prism / NDB / Files / Move documentation. Given a passage from the "
    "documentation, generate natural end-user questions that the passage "
    "answers. Rules:\n"
    "  - 1 or 2 questions (never more)\n"
    "  - Short queries, 5 to 15 words each\n"
    "  - Real-user phrasing — what an admin or operator would actually type\n"
    "  - No yes/no questions, no questions about metadata\n"
    "  - No generic questions ('what is Nutanix') — the passage itself must be the answer\n"
    "  - Prefer specific phrasing that anchors on the passage content, not on the section title alone\n"
    "Respond with ONLY a JSON array of strings. No preface, no prose, no markdown."
)

USER_TEMPLATE = (
    "Document: {doc_title}\n"
    "Section: {section_title}\n"
    "Category: {category}\n\n"
    "Passage:\n{content}\n\n"
    "Generate 1 or 2 end-user questions as a JSON array."
)

JSON_ARRAY_RE = re.compile(r"\[\s*(?:\"[^\"]*\"\s*,?\s*)+\]", re.DOTALL)


def _split_endpoint(endpoint: str, scheme: str) -> tuple[str, int, str]:
    """Validate and split <host:port>/<path>. Rejects everything that
    isn't an http(s)-style host:port pair so the file:// handler stays
    unreachable.
    """
    if scheme not in ("http", "https"):
        raise ValueError(f"unsupported scheme: {scheme}")
    parts = urlsplit(f"{scheme}://{endpoint}")
    if parts.scheme not in ("http", "https") or not parts.hostname:
        raise ValueError(f"invalid endpoint: {endpoint}")
    port = parts.port or (443 if parts.scheme == "https" else 80)
    path = parts.path or "/"
    if parts.query:
        path = f"{path}?{parts.query}"
    return parts.hostname, port, path


def post_chat(
    messages: list[dict],
    endpoint: str,
    scheme: str,
    max_tokens: int = 256,
) -> str | None:
    body = json.dumps({
        "model": MODEL,
        "messages": messages,
        "max_tokens": max_tokens,
        "temperature": 0.3,
        "stream": False,
    }).encode("utf-8")
    # HTTP only — this script is a localhost-loopback tool (Ollama on the
    # same host). HTTPS to remote endpoints is intentionally unsupported;
    # if you need it, route through a TLS-terminating reverse proxy on the
    # same host (or rewrite to use the `requests` library).
    if scheme != "http":
        raise ValueError("only http:// scheme is supported (loopback tool)")
    try:
        host, port, path = _split_endpoint(endpoint, scheme)
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


def parse_queries(raw: str) -> list[str]:
    if not raw:
        return []
    match = JSON_ARRAY_RE.search(raw)
    if not match:
        return []
    try:
        arr = json.loads(match.group(0))
    except json.JSONDecodeError:
        return []
    return [q.strip() for q in arr if isinstance(q, str) and 5 <= len(q.strip()) <= 200]


def main() -> int:
    global MODEL
    parser = argparse.ArgumentParser()
    parser.add_argument("--in", dest="inp", default="data/bench-seed-chunks.jsonl")
    parser.add_argument("--out", default="data/bench-queries-raw.jsonl")
    parser.add_argument("--limit", type=int, default=0, help="0 = all chunks")
    parser.add_argument("--resume", action="store_true",
                        help="skip chunks whose source_chunk_id already appears in the output")
    parser.add_argument("--endpoint", default=DEFAULT_ENDPOINT,
                        help="host:port/path of the OpenAI-compat chat endpoint (no scheme)")
    parser.add_argument("--scheme", default=DEFAULT_SCHEME, choices=("http",),
                        help="only http is supported (loopback tool)")
    parser.add_argument("--model", default=MODEL)
    args = parser.parse_args()
    MODEL = args.model

    in_path = Path(args.inp)
    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    seen: set[int] = set()
    if args.resume and out_path.exists():
        with out_path.open("r", encoding="utf-8") as f:
            for line in f:
                try:
                    seen.add(json.loads(line)["source_chunk_id"])
                except (json.JSONDecodeError, KeyError):
                    continue
        log.info("resume: %d chunks already processed", len(seen))

    with in_path.open("r", encoding="utf-8") as f:
        chunks = [json.loads(line) for line in f]
    if args.limit:
        chunks = chunks[: args.limit]
    log.info("loaded %d seed chunks", len(chunks))

    written = 0
    skipped = 0
    queries_total = 0
    t0 = time.time()

    mode = "a" if args.resume else "w"
    with out_path.open(mode, encoding="utf-8", newline="\n") as f_out:
        for i, chunk in enumerate(chunks):
            cid = chunk["chunk_id"]
            if cid in seen:
                continue

            user = USER_TEMPLATE.format(
                doc_title=chunk.get("doc_title") or "(untitled)",
                section_title=chunk.get("section_title") or "(no section)",
                category=chunk.get("category") or "(unspecified)",
                content=chunk["content"][:2400],
            )
            raw = post_chat(
                [
                    {"role": "system", "content": SYSTEM_PROMPT},
                    {"role": "user", "content": user},
                ],
                endpoint=args.endpoint,
                scheme=args.scheme,
            )
            queries = parse_queries(raw or "")
            if not queries:
                skipped += 1
                if i % 10 == 0:
                    log.info("[%d/%d] skipped (bad LLM output) chunk=%d", i + 1, len(chunks), cid)
                continue

            for q in queries:
                record = {
                    "query": q,
                    "source_chunk_id": cid,
                    "source_doc_id": chunk.get("document_id"),
                    "source_doc_title": chunk.get("doc_title"),
                    "source_section_title": chunk.get("section_title"),
                    "source_category": chunk.get("category"),
                    "generation_model": MODEL,
                }
                f_out.write(json.dumps(record, ensure_ascii=False) + "\n")
                queries_total += 1

            written += 1
            if written % 10 == 0:
                elapsed = time.time() - t0
                rate = written / elapsed if elapsed > 0 else 0
                remaining = (len(chunks) - i - 1) / rate if rate > 0 else 0
                log.info("[%d/%d] queries=%d rate=%.2f chunks/s eta=%.0fs",
                         i + 1, len(chunks), queries_total, rate, remaining)
            f_out.flush()

    log.info("done — %d chunks → %d queries (%d skipped) in %.0fs",
             written, queries_total, skipped, time.time() - t0)
    return 0


if __name__ == "__main__":
    sys.exit(main())
