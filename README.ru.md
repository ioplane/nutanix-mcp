# nutanix-mcp

MCP-сервер с гибридным поиском по корпусу документации Nutanix.
Построен на .NET 10, pgvector, vchord и ONNX Runtime.

[English copy → `README.md`](./README.md)

## Что делает

Сервер сочетает плотный векторный поиск по эмбеддингам `bge-m3` и
разреженный BM25-путь через `vchord_bm25`, сливает результаты через
Reciprocal Rank Fusion, гидратирует родительский контекст и
реранкирует `bge-reranker-v2-m3` (stock FP32 или v0.3.0 fine-tuned
INT8 — см. [`training-rag.md`](./docs/ru/training-rag.md)). Семь
MCP-инструментов отдают поиск, просмотр секций, листинги и
health-репорт по транспорту stdio или streamable HTTP.

Текущий production baseline (v0.3.0, 509-вопросный LLM bench fixture):
**top-1 = 80.2 %, top-5 = 99.6 %, top-10 = 100.0 %, p50 = 2.0 с, p95 = 2.9 с**
на CPU-only ноутбуке против locahost-PG.

## Быстрый старт

```bash
podman compose -f docker/compose.yaml up -d postgres
dotnet ef database update --project src/NutanixMcp.Data
pwsh scripts/fetch-models.ps1
dotnet test
dotnet run --project src/NutanixMcp.Cli -- serve --transport stdio
```

## Архитектура

```text
Cli → Server → Core ← Embedding, Data
```

Проект `Core` зависит только от BCL. `Embedding` и `Data` не видят друг
друга. Тестовые проекты могут ссылаться на любой слой. Полная схема и
таблица компонентов — в
[`docs/ru/architecture.md`](./docs/ru/architecture.md).

## Документация

- [`docs/`](./docs/) — индекс. Английский в `docs/en/`, русский в
  `docs/ru/`.
- [`docs/ru/quickstart.md`](./docs/ru/quickstart.md) — поднять сервер,
  прогнать тесты, настроить через окружение.
- [`docs/ru/retrieval.md`](./docs/ru/retrieval.md) — гибридный конвейер
  целиком, с диагностикой.
- [`docs/ru/operations.md`](./docs/ru/operations.md) — health-чек, ритм
  ingest, режимы отказа.
- [`docs/ru/training-rag.md`](./docs/ru/training-rag.md) — схема
  bench v2, план fine-tune, рычаги вне v0.1.
- [`docs/ru/quality-gate.md`](./docs/ru/quality-gate.md) — Roslyn-анализаторы,
  Semgrep, PVS-Studio, format, markdown lint.
- [`docs/ru/roadmap.md`](./docs/ru/roadmap.md) — тайм-лайн и статус
  спринтов.

## Релизы

Однофайловые бинарники собирает workflow `release` для Windows, Linux и
macOS на amd64 и arm64. Последний тег и SHA-256 — на странице
[Releases](https://github.com/ioplane/nutanix-mcp/releases).

## Плагин для Claude Code

Первоклассный плагин лежит в [`plugin/`](./plugin/). Установка через
marketplace:

```text
/plugin marketplace add https://github.com/ioplane/nutanix-mcp
/plugin install nutanix-mcp
```

Плагин регистрирует MCP-сервер, два слэш-команды (`/nutanix-search`,
`/nutanix-status`) и одну skill, которая автоактивируется на вопросах
по Nutanix.

## Лицензия

MIT. См. [`LICENSE`](./LICENSE).
