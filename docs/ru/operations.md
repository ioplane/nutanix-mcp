# Эксплуатация

## Health check

`get_status` возвращает счётчики и сводку по последнему ingest-запуску:

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

`healthy: true` подтверждает только то, что сервер достучался до
PostgreSQL и прочитал две таблицы. Доступность моделей и качество
HNSW-recall этим не валидируются. Считайте этот эндпоинт liveness-пробой.

## Ритм ingest

Глагол `ingest` (появляется в Sprint 005) запускает индексацию. Два
слоя пропуска укорачивают инкрементальные прогоны:

- Уровень каталога: если SHA-256 файла `catalog.json` совпадает с
  последним успешным запуском, прогон закорачивается целиком.
- Уровень документа: если `documents.pdf_sha256` совпадает с каталогом,
  документ пропускается без парсинга.

`--force` обходит оба слоя и перепрогоняет всё.

## Тюнинг памяти PostgreSQL

Память PostgreSQL управляется тремя слоями: флаги командной строки в
`docker/compose.yaml`, `ALTER DATABASE` per-DB в `init.sql`, и живое
состояние уже работающей базы. Живой `source = 'database'` побеждает
значения командной строки.

При тюнинге:

1. Поправить `compose.yaml` под новые дефолты.
2. Зеркально обновить `init.sql`, чтобы свежий контейнер подхватил
   значения.
3. Для уже работающего контейнера — либо `ALTER DATABASE nutanix RESET <name>`
   и рестарт, либо стереть volume и переинициализировать.

## Логирование и наблюдаемость

Транспорт stdio пишет все логи фреймворка в stderr
(`LogToStandardErrorThreshold = Trace`). stdout зарезервирован под
JSON-RPC поток — никогда не пишите туда из прикладного кода.

Транспорт HTTP использует стандартный console-логгер ASP.NET Core.

Экспорт OpenTelemetry — opt-in. При установленном
`OTEL_EXPORTER_OTLP_ENDPOINT` или `NUTANIX_MCP__Otel__Endpoint` хост
регистрирует tracer-провайдер с OTLP-экспортёром и пишет per-stage
spans для конвейера гибридного поиска.

## Бэкапы

Делать логический дамп базы `nutanix` через
`pg_dump --format=custom`. HNSW-индекс восстанавливается из колонки
эмбеддингов, поэтому логического дампа достаточно. Бинарные файлы
моделей храните отдельно — они не часть состояния БД.

## Режимы отказа

| Симптом | Вероятная причина | Первая проверка |
|---|---|---|
| `relation "documents" does not exist` | Миграции не применены к целевой БД. | `dotnet ef database update --project src/NutanixMcp.Data`. |
| `Failed to load external data` от ONNX Runtime | `model.onnx_data` не лежит рядом с `model.onnx`. | `ls models/embedder/`. |
| `unrecognized configuration parameter "hnsw.iterative_scan"` | В `shared_preload_libraries` нет `vchord`. | `psql -c 'SHOW shared_preload_libraries'`. |
| BM25-запрос возвращает ноль строк | Триггер не выставлен на целевой БД. | `\d+ chunks` должен показать `model_chunks_model_trigger`. |
| Ingest зависает на warm-up | Сессия ONNX инициализируется на первом запросе. | Первый запрос после старта процесса может занять 600 мс; следующие — нет. |
