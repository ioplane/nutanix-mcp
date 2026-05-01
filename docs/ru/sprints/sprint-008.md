# Sprint 008 — свипы HNSW + BM25

Статус: DONE (2026-05-02). One-at-a-time свип параметров retrieval-стека
v0.2.0. Результат: **текущие дефолты оптимальны на 207-вопросном LLM
bench-фикстуре**. Дельты sub-pp — ниже шумового floor'а при n=207.

## Условия

- Fixture: `tests/fixtures/bench-queries-llm.json` (207 LLM-сгенерированных
  запросов, multi-positive аннотация; acceptance Sprint 006-extension).
- Compute: remote GPU host (PG + embedder на localhost — без SSH-туннеля
  и без bottleneck'а на CPU ноута).
- Harness: `scripts/bench-sweep.sh` гоняет `nutanix-mcp bench` по одной
  строке на конфигурацию. Два новых флага `bench`:
  - `--ef-search <int>` — пер-коннекшн override `hnsw.ef_search` через
    Npgsql physical-connection initializer.
  - `--iterative-scan strict_order|relaxed_order` — пер-коннекшн
    override `hnsw.iterative_scan`.
- Baseline (фиксируется когда свипим один knob): `ef_search=100`,
  `iterative_scan=relaxed_order`, `rrf_k=60`, `candidate_pool_size=50`,
  `rerank_top_n=30`.

## Сетка свипов

Один knob варьируется, остальные три = baseline. 9 конфигов.
Wall-clock на remote: 84 минуты.

| label | ef_search | iter_scan | rrf_k | pool | top-1 | top-5 | top-10 | p50 (мс) | p95 (мс) |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|
| ef50 | 50 | relaxed | 60 | 50 | 82.6 % | 98.6 % | 100.0 % | 2566 | 3654 |
| **ef100 (baseline)** | **100** | **relaxed** | **60** | **50** | **82.6 %** | **98.6 %** | **100.0 %** | **2529** | **3384** |
| ef200 | 200 | relaxed | 60 | 50 | 82.6 % | 98.6 % | 100.0 % | 2506 | 3605 |
| ef400 | 400 | relaxed | 60 | 50 | 82.6 % | 98.6 % | 100.0 % | 2642 | 3661 |
| strict | 100 | strict | 60 | 50 | 82.6 % | 98.6 % | 100.0 % | 2605 | 3643 |
| rrfk30 | 100 | relaxed | 30 | 50 | 82.6 % | 98.6 % | 100.0 % | 2613 | **7316** |
| rrfk100 | 100 | relaxed | 100 | 50 | 82.1 % | 98.6 % | 100.0 % | 2544 | 3372 |
| pool30 | 100 | relaxed | 60 | 30 | **80.7 %** | 97.6 % | 99.0 % | 2572 | 3495 |
| pool100 | 100 | relaxed | 60 | 100 | 81.6 % | 97.6 % | 99.5 % | 2641 | 3788 |

## Выводы

1. **Лестница `ef_search` (50 / 100 / 200 / 400)** — top-1 залочено на
   82.6 %, top-10 залочено на 100 %. p50 плоское (2.5–2.6 с) в шуме
   бенча. Recall HNSW saturates уже на ef_search=50. Выше 100 не даёт —
   держим baseline.

2. **`iterative_scan` (strict vs relaxed)** — неотличимо по всем
   метрикам. Relaxed-order дешевле и не теряет качество — оставляем.

3. **Лестница `rrf_k` (30 / 60 / 100)** —
   - `rrfk=30` удваивает tail-latency (p95 7.3 с против 3.4 с baseline)
     при том же качестве. RRF с малым k агрессивнее склеивает top-of-list
     ties — плохо взаимодействует с iterative refinement vchord_bm25.
     **Избегаем.**
   - `rrfk=100` теряет 0.5 pp top-1 (82.6 → 82.1). На границе значимости
     при n=207, но реальный сигнал — сглаживание разбавляет cross-encoder
     rank-0.
   - `rrfk=60` baseline — победитель.

4. **Лестница `candidate_pool_size` (30 / 50 / 100)** —
   - `pool=30` теряет 1.9 pp top-1 и опускает top-10 ниже 100 %. Слишком
     мало dense+sparse кандидатов на cross-encoder. **Избегаем.**
   - `pool=100` совпадает с top-1 в пределах 1 pp но добавляет ~400 мс
     к p95. С `rerank_top_n=30` лишние кандидаты до cross-encoder не
     доходят — отбрасываются.
   - `pool=50` baseline — правильный floor.

## Рекомендация

**Сохранить текущие дефолты.** ef_search=100, iterative_scan=relaxed_order,
rrf_k=60, candidate_pool_size=50, rerank_top_n=30. Свип подтвердил, что
каждый knob лежит в своей quality-vs-latency sweet spot для стека v0.2.0
на текущем корпусе.

## Не в этом спринте (в очередь)

- **HNSW `m` rebuild** (`m=16` → `m=32` или `m=48`). Требует пересборки
  индекса и имеет смысл когда ef_search ещё не насытил recall. 100 %
  top-10 ceiling на текущем fixture говорит — мы не там.
- **Расширение бенча выше 207 запросов.** Sub-pp регрессии (rrfk=100
  на -0.5 pp, pool=100 на -1.0 pp) сидят на шумовом floor'е n=207;
  500-вопросный fixture покажет, реальные ли это потери. Tracked под
  bench-expansion в roadmap'е.
- **Sprint 003.5 (INT8 реранкер).** Заблокирован FP16-источником ONNX
  экспорта — `quantize_dynamic` инжектит `DynamicQuantizeLinear`,
  которые ORT отвергает на FP16 входах. См.
  `scripts/quantize-reranker.py` — путь решения задокументирован.
