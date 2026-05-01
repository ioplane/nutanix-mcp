# Быстрый старт

## Требования

- .NET 10 SDK 10.0.201 (управляется через [`global.json`](../../global.json)).
- Podman или Docker (Compose v2).
- PowerShell 7 (скрипт скачивания моделей рассчитан на `pwsh`).

## Поднять PostgreSQL

```bash
podman compose -f docker/compose.yaml up -d postgres
```

Контейнер слушает порт 5435 на хосте. Compose-файл применяет `init.sql`,
который создаёт нужные расширения и анализатор `english_analyzer`.

Проверка:

```bash
podman exec nutanix-mcp-postgres psql -U nutanix -d nutanix -c '\dx'
```

В выводе должны быть `vector`, `vchord`, `vchord_bm25`, `pg_tokenizer`,
`pg_trgm`, `plpgsql`.

## Применить миграции

```bash
dotnet ef database update --project src/NutanixMcp.Data
```

Это применит миграции `Initial` и `AddBm25Column` к базе `nutanix`.
Триггер, поддерживающий `chunks.bm25v`, создаётся в `AddBm25Column`.

## Скачать ONNX-модели

```powershell
pwsh scripts/fetch-models.ps1
```

Скрипт скачивает `bge-m3` (около 2.27 GB — `model.onnx` + `model.onnx_data`)
в `models/embedder/` и `bge-reranker-v2-m3` (около 1.14 GB) в
`models/reranker/`. Идемпотентен, проверяет минимальный размер каждого
файла.

## Запустить тесты

```bash
dotnet test
```

Интеграционный набор Data ожидает PostgreSQL на 5435 и при первом
запуске создаёт базу `nutanix_test`. Embedding-набор гейтит тесты на
реальной модели через `SkippableFact` — пропустит, если файлов нет.

## Запустить сервер

Транспорт stdio (по умолчанию):

```bash
dotnet run --project src/NutanixMcp.Cli -- serve --transport stdio
```

Транспорт HTTP:

```bash
dotnet run --project src/NutanixMcp.Cli -- serve --transport http --port 7456
```

После успешного старта HTTP-эндпоинт доступен по адресу
`http://127.0.0.1:7456/`. Список инструментов:

```bash
curl -sS -X POST -H 'Content-Type: application/json' \
     -H 'Accept: application/json,text/event-stream' \
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' \
     http://127.0.0.1:7456/
```

В ответе семь инструментов с JSON-схемами.

## Конфигурация через окружение

Настройки идут через переменные `NUTANIX_MCP__*`. Самое частое:

| Переменная | По умолчанию | Эффект |
|---|---|---|
| `NUTANIX_MCP__ConnectionString` | `Host=localhost;Port=5435;…` | Строка подключения к PostgreSQL. |
| `NUTANIX_MCP__ModelsDir` | `models` | Корневая директория для `embedder/` и `reranker/`. |
| `NUTANIX_MCP__Gpu` | `false` | Включает CUDA, если выбрана GPU-сборка. |
| `NUTANIX_MCP__HttpPort` | `7456` | Порт HTTP-транспорта. |

Файл `nutanix-mcp.json` в текущей директории также подмешивается в
конфигурацию, если присутствует.
