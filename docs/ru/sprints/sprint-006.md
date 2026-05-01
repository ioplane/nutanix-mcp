# Sprint 006 — Bench v2 first cut

Статус: в работе. Цель: исполняемый бенчмарк, который превращает
качество поиска в одно-два числа на запуск, держит CI smoke-гейт и
пишет append-only историю для будущих диффов между спринтами.

## Почему bench v2 с первого дня

Upstream-линия `arista-mcp` выпустила v1 бенч на substring-сравнении
со slug и section_title в результатах. Три эксперимента-рычага
выглядели победами на v1 и оказались регрессиями, когда v2
(chunk-ID gold labels) встал в полный рост. Не повторяем.

Bench v2 ключуется на `chunk_id`. Каждый запрос несёт объединение
`expect_chunk_ids`; top-N считается hit, если любой допустимый chunk
попал в первые N результатов.

## Схема

```json
{
  "query_id": "ahv-vlan-001",
  "query": "How do I configure a VLAN on AHV virtual switch?",
  "expect_chunk_ids": [102, 487, 1234],
  "expect_doc": "AHV-Admin-Guide-v11_0",
  "expect_section": "AHV Virtual Switch VLAN Configuration",
  "expect_product": "ahv",
  "category": "ahv",
  "difficulty": "easy"
}
```

`expect_doc`, `expect_section`, `expect_product` — подсказки для
триажа промахов; в score не идут.

## Распределение сложности (target)

| Difficulty | Доля | Что это значит |
|---|---|---|
| easy | 50 % | Verbatim-перекрытие токенов с единственным канон-chunk-ом. |
| medium | 30 % | Перефразированный запрос; правильный chunk использует синонимы / иную поверхностную форму. |
| hard | 20 % | Cross-document или terminology drift; верный ответ в chunk-е, который наивный запрос не вытащит. |

## Покрытие (target)

16 категорий каталога должны дать минимум по 4 запроса каждая:
`ahv`, `aos`, `kb`, `files`, `ndb`, `kubernetes`, `flow`,
`prism-central`, `prism-element`, `move`, `disaster-recovery`,
`cloud-clusters`, `nki`, `pulse`, `lcm`, `hardware-replacement`.
Остаток распределяется пропорционально размеру корпуса (admin guides
получают больше запросов, чем минорные продукты).

## Prerequisite — полный ingest

Бенчу нужны настоящие `chunk_id` для gold-label. Первая задача
спринта — `--force` ingest всех 7343 MD/JSON пар в локальную базу
`nutanix`. Цифры этого прогона приземлятся здесь по завершении.

### Эмпирическая пропускная способность (CPU, 2026-05-01)

Первый запуск ingest на машине разработчика без CUDA дал в среднем:

- Около 6 минут на документ для четырёх самых больших admin guides
  (`AHV-Admin-Guide-v1{0_0,0_3,1_0,6_10}`, 690-830 листьев каждый).
- Около 150 листьев в минуту на маленьких документах после
  начального admin-guide-всплеска.

Полный wall-clock для корпуса в 7343 документа на CPU — примерно
2 часа. GPU-сборка (`dotnet build -p:UseGpuOnnx=true`,
`NUTANIX_MCP__Gpu=true`) должна уложиться в ~15 минут на 4070
исходя из CUDA-профиля OnnxRuntime. Production-ingest-пиннинг должен
выбирать GPU на машинах с CUDA; CPU остаётся дефолтом ради
воспроизводимости бенча без CUDA.

### Стабильность chunk_id

`expect_chunk_ids` — это bigint identity-значения, назначаемые при
INSERT. Полный реингест переназначает их. Bench-фикстуру нужно
перепинить после каждого полного реингеста. Стабильный якорь —
`(expect_doc, expect_section)`; follow-up глагол
`validate-bench-queries` (в очереди на Sprint 006.5) перерезолвит
сдрифтовавшие chunk_id на месте.

## CLI

```bash
nutanix-mcp bench \
  --queries tests/fixtures/bench-queries.json \
  --history tests/fixtures/bench-history.jsonl \
  --label v0.2.0 \
  --limit 10
```

| Флаг | Default | Эффект |
|---|---|---|
| `--queries` | (обязателен) | Путь к bench JSON фикстуре. |
| `--history` | `tests/fixtures/bench-history.jsonl` | Append-only файл истории. |
| `--label` | `<git_sha или "local">` | Лейбл прогона в строке истории. |
| `--limit` | 10 | Cap результатов на запрос. |
| `--top-k-cutoffs` | `1,5,10` | Через запятую — cut-off для сводки. |
| `--no-history` | off | Не аппендить (для dry-run отладки). |

Exit code: `0` при прохождении smoke-гейта (top-10 ≥ 0.80), `1` иначе.

## Схема строки истории

```json
{
  "timestamp": "2026-05-12T10:00:00Z",
  "label": "v0.2.0",
  "git_sha": "abc123",
  "queries": 100,
  "top_1": 0.82,
  "top_5": 0.94,
  "top_10": 0.97,
  "p50_ms": 1450,
  "p95_ms": 2300,
  "embedder": "bge-m3",
  "reranker": "bge-reranker-v2-m3-fp32",
  "rrf_k": 60,
  "rerank_top_n": 30,
  "ef_search": 100,
  "candidate_pool_size": 50,
  "limit": 10,
  "by_category": {
    "ahv": {"top_1": 0.86, "top_10": 1.00},
    "kb":  {"top_1": 0.78, "top_10": 0.96}
  }
}
```

## Компоненты

| Файл | Где живёт | Зона ответственности |
|---|---|---|
| `BenchQuery` | `Core.Bench` | Record одной строки фикстуры. |
| `BenchRun` | `Core.Bench` | Агрегированные результаты прогона. |
| `BenchHarness` | `Cli.Bench` | Тянет запросы, гоняет `IHybridRetriever`, скорит против `expect_chunk_ids`, замеряет тайминги, выпускает `BenchRun`. |
| `BenchHistoryWriter` | `Cli.Bench` | Аппендит `BenchRun` в `bench-history.jsonl`. |
| `BenchCommand` | `Cli.Commands` | Глагол `bench`. |

## План тестов

- `BenchQueryParserTest` — round-trip схемы; отвергает строки без
  `query` или `expect_chunk_ids`.
- `BenchScoringTest` — top-1 / top-5 / top-10 на сфабрикованных
  result-сетах; tie-break.
- `BenchHarnessIntegrationTest` — гонится против live `nutanix_test`
  с фикстурой из 5 запросов.
- `BenchHistoryWriterTest` — append-only, atomic, стабильная форма JSON.

## Гейт приёмки

Спринт принят, если:

- Глагол `bench` доходит до конца на наполненной базе `nutanix` со
  seed-фикстурой (≥ 30 запросов на старт; расширение до 100 в очереди
  до начала bench-driven свипов в Sprint 008).
- top-10 ≥ 0.80 на seed-фикстуре.
- Одна строка добавлена в `tests/fixtures/bench-history.jsonl`.
- Все проекты тестов зелёные.

## Сноска про значимость

При `n = 100` дельта 5 п.п. — это около 1σ. Победы под 3 п.п. не
значимы до `n ≥ 300`. Следующие спринты (HNSW свип, INT8 реранкер,
fine-tune) должны давать ≥ 5 п.п. top-1 улучшения на 100-q наборе
либо ждать расширения бенча.
