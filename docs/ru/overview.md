# Обзор

`nutanix-mcp` — это сервер Model Context Protocol, который предоставляет
гибридный поиск (плотный векторный поиск + BM25 + cross-encoder реранкер)
по корпусу документации Nutanix. Сервер говорит по стандартному транспорту
MCP — stdio или streamable HTTP — и отдаёт семь инструментов: поиск,
просмотр секций, листинги, отчёт о здоровье.

## Цели

- Гибридный поиск по 1900+ portal PDF, базе знаний (KB), Bible, API
  reference и списку портов.
- Многоязычные запросы по англоязычному корпусу.
- Строгая слоистая архитектура и сильный гейт статанализа.
- Воспроизводимые сборки на Windows, Linux и macOS, на amd64 и arm64.

## Что не делаем в v0.1

- Перепись запроса в стиле HyDE.
- Правиловое расширение запроса (multi-query).
- Listwise-реранк маленькой локальной языковой моделью.

Эти три рычага измеримо ухудшили качество поиска на upstream-бенчмарке
arista-mcp; они зарезервированы под повторную оценку в
[`training-rag.md`](./training-rag.md).

## Компоненты

- Слоистая .NET 10 решение: `Cli` → `Server` → `Core` ← `Embedding`,
  `Data`. Проект `Core` зависит только от BCL.
- Образ PostgreSQL 18 с pgvector 0.8.2, vchord 1.1.1, vchord_bm25 0.3.0
  и pg_tokenizer 0.1.1 на борту.
- Две ONNX-модели на диске: `bge-m3` для эмбеддера (XLM-R, 1024 dim,
  multilingual) и `bge-reranker-v2-m3` для реранкера (XLM-R cross-encoder).
- Семь MCP-инструментов: `search_docs`, `lookup_section`,
  `list_documents`, `get_document`, `get_status`, `list_categories`,
  `get_kb_article`.
