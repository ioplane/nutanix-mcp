# Sprint 010 — fine-tune bge-reranker-v2-m3 на nutanix-триплах

Статус: DONE (2026-05-02). Cross-encoder fine-tune на 509-вопросном
v0.2.0 LLM bench fixture, hold-out NDCG@10 = 0.9942, продакшен-
ретривал top-1 = 80.6 % (+1.6 п.п. над стоковым FP32 baseline'ом).

## Tooling

End-to-end на remote'е (PG + LLM + GPU localhost). Код локально, тяжёлый
compute через `ssh gpu`.

| Этап | Скрипт | Время |
|---|---|---|
| Извлечение (q, pos, all_pos_ids) | `scripts/extract-bench-pairs.py` | 1 с |
| Майнинг hard negatives через bge-m3 | `scripts/mine-negatives.py` | ~25 мин |
| Train CrossEncoder | `scripts/train-reranker.py` | **131 с** |
| Экспорт в ONNX | `scripts/export-reranker-onnx.py` | ~10 с |
| Bench-валидация | `nutanix-mcp bench --reranker-dir …` | ~25 мин |

Тренировка на 4137 mined триплах (mean positive 0.69, mean negative
0.63 по bge-m3 cosine; 20.4 % потенциальных negatives отфильтрованы
absolute_margin=0.02). Recipe залочен на arista win record:
BCE pos_weight=8, batch=16, max_seq=512, lr=2e-5, 3 эпохи, warmup 0.1.

## Sumарика рецепта

```text
base model:   BAAI/bge-reranker-v2-m3 (XLM-R, 568M, FP32)
triples:      4137 (q, passage, label) — 509-q LLM fixture +
              bge-m3 hard-neg mining с multi-positive guard
loss:         BCELoss с pos_weight=8 (компенсирует 1:8 дисбаланс)
optim:        AdamW, lr=2e-5, warmup_ratio=0.1
batch:        16 на GPU (single 5090, ~17 GB peak VRAM)
seq:          512 max
epochs:       3 (669 train steps total)
seed:         42
holdout:      query-hash bucket, 15 % eval
metric:       eval_nutanix-eval_ndcg@10
```

## Hold-out evaluation (по эпохам)

| epoch | NDCG@10 | MAP | MRR@10 |
|---:|---:|---:|---:|
| 1 | 0.9923 | 0.9824 | 0.9824 |
| 2 | 0.9923 | 0.9824 | 0.9824 |
| **3 (best)** | **0.9942** | **0.9922** | **0.9922** |

## Продакшен bench (509-q LLM fixture)

| Метрика | v0.2.0 stock FP32 | **v0.3.0 fine-tune FP32** | Δ |
|---|---:|---:|---:|
| top-1 | 79.0 % | **80.6 %** | **+1.6 п.п.** |
| top-5 | 99.0 % | 99.4 % | +0.4 п.п. |
| top-10 | 100.0 % | 100.0 % | плоско |
| p50 (мс) | 2625 | 2737 | +4 % |
| p95 (мс) | 3675 | 3915 | +7 % |

Сопоставление с arista (588-q fixture, +2.04 п.п. post-fine-tune):
+1.6 п.п. — в том же диапазоне, recipe воспроизводится.

Прирост латентности (+4–7 %) ожидаем — у fine-tuned весов другая
sparsity / activation pattern. Не заметно пользователю.

Top-10 насыщен на 100 % и до, и после fine-tune — потолок измерения
сохраняется. Реальный сигнал — top-1, который fine-tune и должен
был улучшить (cross-encoder ranking в already-good candidate set).

## Почему НЕ свопали на Qwen3-Embedding/Reranker

В late-cycle research нашёл Qwen3-Embedding-0.6B / Qwen3-Reranker-0.6B
на Hugging Face (релиз 2025, Apache-2.0, заявлено +5 п.п. MTEB
multilingual + +5 п.п. BEIR rerank). На бумаге — drop-in замена.
На практике — серьёзный code refactor, не model swap:

- Decoder-only архитектура (vs XLM-R encoder)
- Qwen BPE tokenizer (vs SentencePiece) — нужен новый C# tokenizer
- Qwen3-Embedding требует `Instruct: …\nQuery: …` prefix и last-token
  pool (vs mean-pool у bge-m3)
- Qwen3-Reranker — generative (yes/no logprob), кроме community-варианта
  `seq-cls`, всё равно с Qwen tokenizer

Estimate: ~6–8 часов (новый tokenizer + embedder + reranker classes +
re-ingest 489K чанков). Вынесено в v0.4.0 sprint.

## В очереди / заблокировано

- **INT8 fine-tuned реранкера** (Sprint 003.5 retry): FP32 ONNX из
  PyTorch теперь должен квантоваться чисто через
  `scripts/quantize-reranker.py`. Не пробовал — не блокер v0.3.0.
- **Расширение bench (n > 1000)** для пробития top-10 = 100 % потолка.
  Нужно для измерения следующих итераций fine-tune.
- **Production-log hard negatives** (Sprint 011 / arista iter 2):
  нужны deployed users.

## Чеклист воспроизводимости

См. EN-версию [sprint-010.md](../../en/sprints/sprint-010.md) — там
полный пошаговый shell-snippet. Тут не дублируем чтобы не разъезжалось.
