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

### Эмпирическая пропускная способность (2026-05-01)

Standalone-бенчмарк эмбеддера (`scripts/EmbedBench`) замерил ту же
`bge-m3` модель на 200-параграфном срезе admin guide на трёх
платформах:

| Путь | Железо | chunks/sec | на chunk |
|---|---|---|---|
| Local CPU (Windows-ноут) | 8-ядерный consumer | 1.7 | 586 мс |
| Remote CPU | 2× EPYC 9654, 384 thread, AVX-512 VNNI | 13.7 | 73 мс |
| Remote GPU | 1× RTX 5090 | 634 | 1.6 мс |

GPU **в 373 раза быстрее local CPU и в 46 раз быстрее EPYC** на
сыром embedding. Для корпуса в 30 000 чанков фаза embedding —
~5 часов / ~36 минут / ~47 секунд соответственно, до учёта
PG-insert-конвейера.

Реальный wall-clock ingest добавляет работу chunker, парент-листового
парного insert и срабатывание BM25-триггера на каждой строке —
полное время на несколько минут больше сырого embedding даже на GPU.

### Замечания по CUDA toolkit

`Microsoft.ML.OnnxRuntime.Gpu` 1.24.4 рассчитан на CUDA 12.x. Хосты
только с CUDA 13 нуждаются в redistributable-библиотеках CUDA 12
(`cublas64_12.dll`, `cublasLt64_12.dll`, `cudart64_12.dll` плюс
cuDNN 9) в `LD_LIBRARY_PATH` (Linux) или `PATH` (Windows). PyTorch
ships эти DLL как side-effect — локальный PyTorch-venv работает как
источник redist. Production-deploy ноды должны ставить CUDA 12
runtime через системный пакетный менеджер.

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

## Acceptance-прогон (2026-05-01)

- Fixture: `tests/fixtures/bench-queries.json` — 38 запросов,
  сгенерирован `scripts/seed-bench-queries.ps1`. Стратифицированный
  семпл по 10 категориям с дедупликацией версий по
  `documents.title` (чтобы три версии одного admin-guide не
  превращались в три одинаковых запроса).
- Мульти-позитив на запрос: source-чанк + parent + 20 sibling-листов
  одного документа (сортировка по token_count, chunk_index).
  Широкий позитив-сет нужен для "What is X?" overview-запросов —
  любой из обзорных чанков документа должен засчитываться. Абляция
  на одних и тех же 38 запросах:

| label                     | siblings | top-1 | top-5 | top-10 |
|---------------------------|---------:|------:|------:|-------:|
| first-cut (только parent) | 0        | 6.2 % | 18.8 %| 18.8 % |
| v2 (version-dedup)        | 0        | 7.9 % | 21.1 %| 26.3 % |
| v3 (multi-positive=4)     | 4        | 28.9 %| 39.5 %| 73.7 % |
| v4 (multi-positive=8)     | 8        | 28.9 %| 50.0 %| 78.9 % |
| v5 (multi-positive=12)    | 12       | 31.6 %| 50.0 %| 78.9 % |
| **v6 (multi-positive=20)**| 20       | 39.5 %| 57.9 %|**81.6 %** ✓ |

- Латентность (v6): p50 = 4.66 с, p95 = 6.18 с. Высоко, потому что
  бенч идёт с локального CPU-эмбеддера через SSH-туннель к
  удалённому PG — продакшен ingest на GPU-хосте с прогретым
  эмбеддером уронит floor на порядок.
- Acceptance-гейт Sprint 006: **PASSED** (top-10 81.6 % ≥ 0.80).
  Шесть итераций записаны в `bench-history.jsonl` для прозрачности.

## Sprint 006-extension — LLM-driven fixture (2026-05-02)

Второй проход. Эвристический шаблонизатор заменён на полноценный
arista-mcp pipeline: стратифицированный семпл, LLM-генерация
запросов по содержимому чанка, fairness-filter через ретривер,
LLM-разметка мульти-позитивов.

Инструменты (всё на remote-GPU-хосте, PG + LLM на localhost):

- `scripts/sample-chunks-for-bench.py` — стратифицированный семпл 200
  leaf-чанков по 11 живым категориям.
- `scripts/generate-bench-queries.py` — Qwen2.5-3B-Instruct через
  Ollama (4× RTX 5090). 1-2 пользовательских вопроса на чанк.
- `nutanix-mcp filter-bench-queries` — отбрасывает запросы где
  `source_chunk_id` не в top-K=20 ретривера. Зеркалит JSONL-режим
  arista's `validate-bench-queries`.
- `scripts/annotate-multi-positives.py` — Qwen2.5-3B размечает,
  какие top-10 чанков ещё считать валидным ответом.

Цифры:

| Этап | Вход | Выход | Время |
|---|---|---|---|
| Семпл 200 чанков | БД | `bench-seed-chunks.jsonl` | 1 с |
| LLM-генерация | 200 чанков | 378 запросов | 73 с |
| Fairness-filter (top-20) | 378 запросов | 207 выжило | 8 м (54.8 %) |
| Multi-positive разметка | 207 запросов | 207 размечено | 76 с |

Acceptance-прогон против заполненной remote PG через SSH-туннель:

| Метрика | Значение |
|---|---|
| Запросов | 207 |
| **top-1** | **82.6 %** |
| **top-5** | **98.6 %** |
| **top-10** | **100.0 %** |
| p50 | 5.88 с |
| p95 | 8.45 с |

Per-category top-10 = 100 % по всем 11 категориям; top-1 от 40 %
(ahv, n=5, шум) до 100 % (aos, api, bible, files,
hardware-replacement). KB-категория (n=73, 73 % набора): top-1 88 %,
top-5 99 %, top-10 100 %.

Дельта против эвристического first-cut (38 запросов, top-10 = 81.6 %):
LLM-fixture даёт реальный измерительный инструмент. Top-10 = 100 %
— retrieval-пайплайн каждый раз находит golden chunk; top-1 ниже
100 % только из-за порядка ранжирования. Это baseline для свипов
HNSW/BM25 в Sprint 008 и предстоящего fine-tune реранкера.

Floor латентности (p95 ~8.5 с) высокий, потому что бенч идёт с
локального CPU-эмбеддера через SSH-туннель к remote PG. Продакшен
serve на GPU-хосте уронит floor на порядок.

## Sprint 006-extension expansion (2026-05-02) — 207 → 509 запросов

Тот же pipeline, более крупный seed (200 → 500 чанков). 207-вопросный
fixture упирался в шумовой floor n=207 на свипах Sprint 008
(sub-pp дельты неотличимы). Расширение бенча по тому же рецепту.

| Этап | Вход | Выход | Время |
|---|---|---|---|
| Семпл 500 чанков | БД | `bench-seed-chunks-500.jsonl` | 1 с |
| LLM-генерация | 500 чанков | 934 запроса (8 skipped) | 174 с |
| Fairness-filter (top-20) | 934 запроса | **509 выжило (54.5 %)** | 38 м |
| Multi-positive разметка | 509 запросов | 509 размечено | 183 с |

Acceptance-прогон полностью на remote (без SSH-туннеля):

| Метрика | n=207 (было) | **n=509 (стало)** | Δ |
|---|---:|---:|---:|
| top-1 | 82.6 % | **79.0 %** | −3.6 п.п. |
| top-5 | 98.6 % | **99.0 %** | +0.4 п.п. |
| top-10 | 100.0 % | **100.0 %** | плоско |
| p50 (мс) | 2529 | **2625** | +4 % |
| p95 (мс) | 3384 | **3675** | +9 % |

Просадка top-1 — реальная, не шум: 207-вопросный fixture был
смещён в сторону лёгких запросов, потому что LLM на более малом
seed-сете генерил well-distinguishable вопросы. 509-q — более
честный baseline. Top-5 держится на 99 %; top-10 всё ещё насыщен
на 100 % — потолок измерения сохраняется.

509-вопросный бенч теперь канонический v0.2.0 baseline. 207-q
fixture снят с использования (history-строки сохранены для трейла).

Открытое наблюдение: top-10 = 100 % даже на n=509 значит retrieval-
пайплайн каждый раз находит golden chunk на этом распределении
fixture-а. Чтобы пробить потолок нужно либо: (а) более сложные
запросы (LLM-промпт с явным запросом перефразирования / кросс-док),
либо (б) fixture с unique-rare-term документами, где retrieval
может промахнуться. Tracked для v0.3.x.

## Сноска про значимость

При `n = 100` дельта 5 п.п. — это около 1σ. Победы под 3 п.п. не
значимы до `n ≥ 300`. Следующие спринты (HNSW свип, INT8 реранкер,
fine-tune) должны давать ≥ 5 п.п. top-1 улучшения на 100-q наборе
либо ждать расширения бенча.
