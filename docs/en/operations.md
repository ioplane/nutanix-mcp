# Operations

## Health check

`get_status` returns counts and the last ingest run summary:

```json
{
  "healthy": true,
  "documents": 1983,
  "chunks": 7141,
  "last_run": {
    "id": 12,
    "status": "success",
    "started_at": "2026-05-01T12:00:00Z",
    "finished_at": "2026-05-01T12:14:30Z",
    "docs_total": 1983,
    "docs_skipped": 1421,
    "docs_upserted": 562,
    "chunks_upserted": 14210
  }
}
```

`healthy: true` proves only that the server can reach PostgreSQL and
read the two tables. It does not validate model availability or HNSW
recall. Treat the metric as a liveness probe.

### HTTP liveness endpoint

When the server is running with `--transport http`, a plain
`GET /v1/healthz` endpoint is available for external monitors:

```bash
curl -fsS http://127.0.0.1:7456/v1/healthz
# {"status":"ok"}
```

The endpoint returns `200 OK` with `{"status":"ok"}` when the embedder,
reranker, and PostgreSQL connection pool are all reachable. It returns
`503 Service Unavailable` on any of those checks failing. It is a bare
HTTP endpoint — no MCP framing, no authentication — safe to poll from
`kube-probe`, `wget --spider`, or a systemd `ExecStartPre`.

## Ingest cadence

The `ingest` verb (introduced in Sprint 005) drives ingestion. Two
skip layers shorten incremental runs:

- Catalog level: if the SHA-256 of `catalog.json` matches the last
  successful run, the entire run short-circuits.
- Per-document level: if `documents.pdf_sha256` matches the catalog,
  the document is skipped without parsing.

`--force` bypasses both layers and reprocesses everything.

## PostgreSQL memory tuning

Three layers control PostgreSQL memory: the command-line flags in
`docker/compose.yaml`, the per-database `ALTER DATABASE` statements
in `init.sql`, and the live state of an already-running database.
Live `source = 'database'` overrides command-line values.

When tuning:

1. Edit `compose.yaml` for the new defaults.
2. Mirror the change in `init.sql` so a fresh container picks it up.
3. For an existing container, either run `ALTER DATABASE nutanix RESET <name>`
   and rely on the container restart, or wipe the volume and
   re-provision.

## Logging and observability

The stdio transport routes all framework logs to standard error
(`LogToStandardErrorThreshold = Trace`). Standard out is reserved for
the JSON-RPC stream — never write to it from application code.

The HTTP transport uses the default ASP.NET Core console logger.

OpenTelemetry export is opt-in. When `OTEL_EXPORTER_OTLP_ENDPOINT` or
`NUTANIX_MCP__Otel__Endpoint` is set, the host registers a tracer
provider with an OTLP exporter and emits per-stage spans for the
hybrid retrieval pipeline.

## Backups

Take a logical dump of the `nutanix` database with
`pg_dump --format=custom`. The HNSW index can be rebuilt from the
embedding column, so a logical dump is sufficient. Store the binary
model files separately — they are not part of the database state.

## Failure modes

| Symptom | Likely cause | First check |
|---|---|---|
| `relation "documents" does not exist` | Migrations not applied to the target database. | `dotnet ef database update --project src/NutanixMcp.Data`. |
| `Failed to load external data` from ONNX Runtime | `model.onnx_data` not co-located with `model.onnx`. | `ls models/embedder/`. |
| `unrecognized configuration parameter "hnsw.iterative_scan"` | `shared_preload_libraries` does not list `vchord`. | `psql -c 'SHOW shared_preload_libraries'`. |
| `BM25 query returns zero rows` | The trigger has not been provisioned for the target database. | `\d+ chunks` should list `model_chunks_model_trigger`. |
| Ingest hangs at warm-up | The ONNX session is initialising on first request. | The first request after process start can take 600 ms; subsequent calls do not. |
