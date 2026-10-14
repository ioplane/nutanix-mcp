# Быстрый старт

## Установка пользователем (release-тарбол)

```bash
TAG=v0.3.3
RID=linux-x64      # или linux-arm64, win-x64, win-arm64, osx-x64, osx-arm64
gh release download "$TAG" -R ioplane/nutanix-mcp \
  -p "nutanix-mcp-${TAG}-${RID}.tar.gz"
tar -xzf "nutanix-mcp-${TAG}-${RID}.tar.gz"
cd "nutanix-mcp-${TAG}-${RID}"
sudo bash scripts/install.sh
```

Восемь фаз, которые выполняет `scripts/install.sh`:

1. Раскладывает бинарник в `/opt/apps/nutanix-mcp-${TAG}-linux-x64/`,
   ставит симлинки `/opt/apps/nutanix-mcp-current` и
   `/usr/local/bin/nutanix-mcp`.
2. Тянет ONNX-модели в `${MODELS_DIR:-/var/lib/nutanix-mcp/models}`
   (пропускается, если `embedder/model.onnx` уже на месте).
3. Дёргает `nutanix-mcp bootstrap --release "$TAG"` — поднимает
   Podman-контейнер с Postgres, качает дамп корпуса из релизного
   attachment через GitHub API, накатывает `pg_restore --clean
   --if-exists -j 4` и восстанавливает реестр `pg_tokenizer`.
4. Регистрирует `mcpServers.nutanix-mcp` в `~/.claude.json` (через `jq`).
5. Симлинкует `${INSTALL_DIR}` в
   `~/.claude/plugins/marketplaces/nutanix-mcp/`, чтобы
   `/plugin install nutanix-mcp` внутри Claude Code активировал
   слэш-команды и скилл.
6. `codex mcp add nutanix-mcp -- …` регистрирует MCP-сервер для Codex.
7. Симлинкует `plugin/skills/*` в `~/.codex/skills/`.
8. Печатает summary и подсказки по smoke-проверке.

Скрипт идемпотентен. Переменные окружения для переопределения:

- `TAG=vX.Y.Z` — поставить конкретный релиз (по умолчанию — самый
  свежий).
- `MODELS_DIR=/custom/path` — направить модели в общее место.
- `CONN_STRING="Host=…;Port=…;Database=…;Username=…;Password=…"` — для
  внешнего Postgres; bootstrap-контейнер PG пропускается.
- `SKIP_BOOTSTRAP=1` — переустановить на хосте, где Postgres уже
  наполнен (обновить бинарник и конфиги, не трогая БД).

Отдельно вызванный `nutanix-mcp bootstrap --release "$TAG"` выполняет
только фазу 3 — пропускает шаги регистрации в CLI. Полезно, чтобы
обновить корпус после смены тега.

## Разработчикам (сборка из исходников)

Требования:

- .NET 10 SDK 10.0.402 (управляется через [`global.json`](../../global.json)).
- Podman или Docker (Compose v2).
- PowerShell 7 (скрипт скачивания моделей рассчитан на `pwsh`).

### Поднять PostgreSQL

```bash
podman compose -f docker/compose.yaml up -d postgres
```

Контейнер слушает порт 5435 на хосте, чтобы уживаться с системным PG
на 5432. Если бинарник смотрит на внешний PG на 5432, переопределите
`NUTANIX_MCP__ConnectionString`. Compose-файл применяет `init.sql`,
который создаёт нужные расширения и анализатор `english_analyzer`.

Проверка:

```bash
podman exec nutanix-mcp-postgres psql -U nutanix -d nutanix -c '\dx'
```

В выводе должны быть `vector`, `vchord`, `vchord_bm25`, `pg_tokenizer`,
`pg_trgm`, `plpgsql`.

### Применить миграции

```bash
dotnet ef database update --project src/NutanixMcp.Data
```

Это применит миграции `Initial` и `AddBm25Column` к базе `nutanix`.
Триггер, поддерживающий `chunks.bm25v`, создаётся в `AddBm25Column`.

### Скачать ONNX-модели

```powershell
pwsh scripts/fetch-models.ps1
```

Скрипт скачивает `bge-m3` (около 2.27 GB — `model.onnx` + `model.onnx_data`)
в `models/embedder/` и `bge-reranker-v2-m3` (около 1.14 GB) в
`models/reranker/`. Идемпотентен, проверяет минимальный размер каждого
файла.

### Запустить тесты

```bash
dotnet test
```

Интеграционный набор Data ожидает PostgreSQL на 5435 и при первом
запуске создаёт базу `nutanix_test`. Embedding-набор гейтит тесты на
реальной модели через `Assert.SkipWhen` (xUnit v3) — пропустит, если файлов нет.

Тесты запускаются на Microsoft.Testing.Platform (`global.json` → `test.runner`),
поэтому отдельный проект запускается через `dotnet test --project <путь>`.
Data-набор направлять на временную базу через `NUTANIX_MCP_TEST_CS`, а не на
сервер с боевым корпусом.

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
