# Конвейер поиска

Гибридный ретривер сочетает плотный путь (pgvector cosine по HNSW над
`halfvec(1024)`) и разреженный путь (vchord_bm25), сливает результаты
через Reciprocal Rank Fusion при `k = 60`, гидратирует родителей для
cross-encoder-реранкера и возвращает упорядоченные chunks вместе с
per-stage диагностикой.

## Сквозной поток

```mermaid
flowchart TD
  q[Query string]
  expand[QueryExpander<br/>Nutanix acronyms]
  cache{Кэш эмбеддингов запроса}
  embed[OnnxEmbedder<br/>bge-m3]
  dense[(Postgres dense<br/>HNSW + halfvec)]
  sparse[(Postgres sparse<br/>vchord_bm25)]
  rrf[Reciprocal Rank Fusion<br/>k = 60]
  adapt{Top-5 spread &le; 0.02?}
  hyd[Подтянуть тексты родителей<br/>один SELECT]
  rerank[XlmRobertaOnnxReranker<br/>bge-reranker-v2-m3]
  dedup{DedupPerSection?}
  out[Take(Limit) +<br/>SearchDiagnostics]

  q --> expand --> cache
  cache -- miss --> embed --> cache
  cache -- hit --> dense
  cache --> dense
  cache --> sparse
  dense --> rrf
  sparse --> rrf
  rrf --> adapt
  adapt -- да --> hyd
  adapt -- нет --> hyd
  hyd --> rerank --> dedup
  dedup -- да --> out
  dedup -- нет --> out
```

## Входы

`HybridRetriever.SearchAsync` принимает `RetrievalOptions`:

| Опция | По умолчанию | Эффект |
|---|---|---|
| `CandidatePoolSize` | 50 | Сколько строк тянуть из dense и sparse до фьюзинга. |
| `RrfK` | 60 | Константа RRF; больше — слабее влияние различий рангов. |
| `RerankTopN` | 30 | Лимит кандидатов на cross-encoder. |
| `Category` | `null` | SQL-фильтр по `documents.category`. |
| `Product` | `null` | SQL-фильтр по `documents.product`. |
| `Version` | `null` | SQL-фильтр по `documents.version`. |
| `Limit` | 10 | Финальное число результатов. |
| `DedupPerSection` | `false` | Не более одного chunk на `(document_id, section_title)`. |

## Стадии

### Расширение акронимов

`QueryExpander.Expand` аннотирует известные Nutanix-акронимы при первом
вхождении. Стабильно, детерминировано, регистронезависимо при сохранении
исходного регистра:

```text
"AHV cluster networking"  →  "AHV (Acropolis Hypervisor) cluster networking"
```

В словаре AHV, AOS, NDB, NCM, NKE, NKP, NCC, NUS, LCM, CVM, IPMI, PE,
PC, DSF, RF, DR и другие — авторитетный список в `QueryExpander.cs`.

### Кэш эмбеддингов

`QueryEmbeddingCache` — bounded concurrent dictionary по расширённому
запросу. Capacity по умолчанию 256. При превышении — eviction до
половины capacity по самому старому доступу. Запись хранит
`HalfVector` 1024-dim и 64-битный счётчик доступа.

### Dense и sparse SQL

Оба запроса фильтруют `chunk_kind = 'leaf'`. Dense использует HNSW на
`chunks.embedding` с cosine-оператором (`<=>` из pgvector). Sparse —
`bm25v <&> to_bm25query(...)`, где negative-score-конвенция означает,
что меньшая дистанция = лучше совпадение. Оба фильтруют по опциональным
`category`, `product`, `version`.

### Reciprocal Rank Fusion

Для каждого кандидата RRF-скор — `Σ 1 / (k + rank)` по сторонам, где
он встретился. Реализация запоминает dense- и sparse-дистанции
co-hit-кандидатов, чтобы диагностика отражала честные значения по
стадиям.

### Адаптивный рерэнк

Если top-5 RRF spread (`top1 - top5`) ≤ 0.02, кандидаты слишком тесные,
чтобы cross-encoder их различил. Ретривер режет вход реранкера до 10,
экономя ~50 мс. Порог откалиброван против upstream-бенча `arista-mcp` v2
и удержался на nutanix 509-q бенче (свип Sprint 008) — оставляем 0.02.

### Гидратация родителей

Для листьев с `parent_chunk_id` ретривер тянет `raw_content` родителей
одним батчем `SELECT ... WHERE id = ANY($1)`. Cross-encoder скорит
`(query, parent_text)`, а не `(query, leaf_text)` — больше контекста.

### Cross-encoder рерэнк

`XlmRobertaOnnxReranker` собирает каноничную XLM-R пару
`<s> q </s></s> d </s>` и запускает ONNX-сессию с `input_ids` и
`attention_mask`. Выход `logits [B, 1]` — сырой скор на пару, больше = лучше.

Директория с моделью реранкера переключается флагом
`bench --reranker-dir <path>` и эквивалентной env-переменной
`NUTANIX_MCP__RERANKERDIR`. v0.3.0 шипит три варианта:

- `models/reranker/` — stock FP32 (`bge-reranker-v2-m3` Optimum-O4,
  ~1.1 GB). Дефолт без override.
- `models/reranker-finetuned/` — Sprint 010 fine-tune FP32 ONNX,
  ~2.3 GB, +1.6 п.п. top-1 над stock.
- `models/reranker-finetuned-int8/` — Sprint 003.5 INT8 fine-tune,
  ~1.3 GB, -26 % p50 vs FP32 fine-tune при -0.4 п.п. top-1.
  **Рекомендованный production-дефолт для CPU-only ноутбуков.**

### Stable sort и финальное обрезание

После реранка кандидаты сортируются по скору stably (равные скоры
сохраняют порядок RRF). При `DedupPerSection` ретривер выпускает не
более одного chunk на `(document_id, section_title)`. На выходе
`Limit` результатов и `SearchDiagnostics`.

## Диагностика

`SearchDiagnostics` отдаёт счётчики и тайминги по стадиям:

```text
DenseHits, SparseHits, AfterRrf, AfterRerank,
EmbedMs, DenseQueryMs, SparseQueryMs, RrfMs, RerankMs, TotalMs
```

Аргумент `withDiagnostics: true` у `search_docs` возвращает диагностику
рядом со списком результатов.

## Исключённые рычаги

Три рычага, знакомые по upstream-линии `arista-mcp`, намеренно
отсутствуют:

- HyDE-стиль перепись запроса маленькой локальной LLM. Зависит от
  sidecar-модели; upstream-эксперимент с пятью рычагами показал, что
  выгода маргинальна на доменном корпусе.
- Правиловое расширение запроса (multi-query). Регрессировало top-1 на
  upstream-бенче v2.
- Listwise-рерэнк маленькой instruction-tuned LLM. Регрессировало
  top-1 на 4.39 п.п. при 3.4σ на upstream-бенче.

Они трекаются как кандидаты для будущих экспериментов в
[`training-rag.md`](./training-rag.md). Возвращаемся к ним только при
наличии доступной локально frontier-class LLM и обновлённого бенча,
показывающего нетривиальный headroom.
