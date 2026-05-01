using System.Diagnostics;
using System.Runtime.InteropServices;
using Npgsql;
using NutanixMcp.Core.Models;
using NutanixMcp.Core.Retrieval;
using NutanixMcp.Embedding;
using Pgvector;

namespace NutanixMcp.Server.Retrieval;

// Hybrid retrieval, simplified compared to arista-mcp.
//
// The flow expands the query with Nutanix acronym annotations, embeds it
// once via bge-m3 (no prefix), then runs dense and sparse SQL in parallel.
// Dense uses pgvector cosine over halfvec; sparse uses vchord_bm25's <&>
// operator with the chunks_tokenizer model. Both paths filter
// chunk_kind='leaf' so parent rows never surface in retrieval.
//
// Results fuse via Reciprocal Rank Fusion at k=60 (RrfK), tracking dense
// and sparse distances per co-hit for accurate downstream diagnostics. An
// adaptive rerank cap shrinks the candidate count to AdaptiveRerankFloor
// when the top-5 RRF spread is below AdaptiveSpreadThreshold (saves ~50 ms
// when the top is tight enough that the cross-encoder's signal is noise).
//
// Before scoring with bge-reranker-v2-m3 the retriever hydrates parents:
// for leaves carrying parent_chunk_id, the cross-encoder sees the parent's
// raw_content instead of the leaf body, giving the reranker richer section
// context. Optional per-section dedup happens last; final results are
// Take(Limit).
//
// Three retrieval levers from arista-mcp are intentionally absent here.
// HyDE is excluded because it depends on a local LLM that arista's
// five-lever ceiling work showed as marginal; we will revisit when a
// frontier-class local model is reachable. Rule-based multi-query
// expansion is excluded because arista bench v2 measured a top-1 drop
// when it was on (feedback_multiquery_regression). Listwise re-rank is
// excluded because arista bench measured a 4.39 pp drop at 3.4 sigma
// when it was on (feedback_listwise_regression).
public sealed class HybridRetriever : IHybridRetriever
{
    private readonly IEmbedder _embedder;
    private readonly IReranker _reranker;
    private readonly NpgsqlDataSource _dataSource;
    private readonly QueryEmbeddingCache _queryCache = new(capacity: 256);

    // Below this RRF-score spread between top-1 and top-5, the rerank signal
    // is noise — cap rerank work to AdaptiveRerankFloor to save ~50 ms/query.
    // Threshold tuned on arista bench v2; revisit when bench v2 lands here.
    private const float AdaptiveSpreadThreshold = 0.02f;
    private const int AdaptiveRerankFloor = 10;

    public HybridRetriever(IEmbedder embedder, IReranker reranker, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(dataSource);
        _embedder = embedder;
        _reranker = reranker;
        _dataSource = dataSource;
    }

    public async Task<SearchResponse> SearchAsync(
        string query,
        RetrievalOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);

        var total = Stopwatch.StartNew();
        var expansion = QueryExpander.Expand(query);

        var embedSw = Stopwatch.StartNew();
        if (!_queryCache.TryGet(expansion.Expanded, out HalfVector qVec))
        {
            float[][] emb = await _embedder
                .EmbedAsync([expansion.Expanded], isQuery: true, ct)
                .ConfigureAwait(false);
            Half[] halfArr = [.. emb[0].Select(static f => (Half)f)];
            qVec = new HalfVector(halfArr);
            _queryCache.Add(expansion.Expanded, qVec);
        }
        embedSw.Stop();

        var denseTask = RunDenseAsync(qVec, options, ct);
        var sparseTask = RunSparseAsync(expansion.Expanded, options, ct);
        await Task.WhenAll(denseTask, sparseTask).ConfigureAwait(false);

        var (denseRows, denseMs) = await denseTask.ConfigureAwait(false);
        var (sparseRows, sparseMs) = await sparseTask.ConfigureAwait(false);

        var rrfSw = Stopwatch.StartNew();
        var fused = ReciprocalRankFusion(denseRows, sparseRows, options.RrfK);
        rrfSw.Stop();

        int rerankTopN = ComputeAdaptiveRerankTopN(fused, options.RerankTopN);
        var topForRerank = fused.Take(rerankTopN).ToList();

        // Parent hydration for the cross-encoder: pull each unique parent's
        // raw_content in one batch SELECT so the reranker scores against the
        // richer section text. Leaves with no parent (legacy) keep leaf text.
        long[] distinctParentIds = [.. topForRerank
            .Where(f => f.Row.ParentChunkId.HasValue)
            .Select(f => f.Row.ParentChunkId!.Value)
            .Distinct()];
        var parentTexts = await FetchParentTextsAsync(distinctParentIds, ct).ConfigureAwait(false);

        var rerankSw = Stopwatch.StartNew();
        var rerankInput = topForRerank.Select(f =>
        {
            string rerankText = f.Row.ParentChunkId is long pid
                && parentTexts.TryGetValue(pid, out string? pt)
                    ? pt
                    : f.Row.RawContent;
            return new RerankCandidate(f.Row.ChunkId, rerankText);
        }).ToList();
        var rerankResults = await _reranker
            .RerankAsync(expansion.Expanded, rerankInput, ct)
            .ConfigureAwait(false);
        rerankSw.Stop();

        var rerankScore = rerankResults.ToDictionary(r => r.ChunkId, r => r.Score);
        var rerankedList = topForRerank
            .OrderByDescending(f => rerankScore.TryGetValue(f.Row.ChunkId, out float s) ? s : 0f)
            .ToList();

        var ranked = options.DedupPerSection
            ? DedupPerSection(rerankedList).Take(options.Limit).ToList()
            : [.. rerankedList.Take(options.Limit)];

        var results = ranked.Select(f => Build(f, rerankScore)).ToList();
        total.Stop();

        var diag = new SearchDiagnostics(
            DenseHits: denseRows.Count,
            SparseHits: sparseRows.Count,
            AfterRrf: fused.Count,
            AfterRerank: results.Count,
            EmbedMs: embedSw.Elapsed.TotalMilliseconds,
            DenseQueryMs: denseMs,
            SparseQueryMs: sparseMs,
            RrfMs: rrfSw.Elapsed.TotalMilliseconds,
            RerankMs: rerankSw.Elapsed.TotalMilliseconds,
            TotalMs: total.Elapsed.TotalMilliseconds);

        return new SearchResponse(results, diag);
    }

    private async Task<(List<CandidateRow> Rows, double ElapsedMs)> RunDenseAsync(
        HalfVector qVec,
        RetrievalOptions options,
        CancellationToken ct)
    {
        // Filter chunk_kind='leaf' so the dense path never returns parent rows
        // (their embedding is NULL anyway). Keeps the planner from probing them.
        const string sql = """
            SELECT c.id, c.document_id, c.chunk_index, c.content, c.raw_content,
                   c.section_title, c.section_level, c.page_start, c.page_end,
                   c.parent_chunk_id,
                   d.title, d.slug, d.category, d.product, d.version,
                   c.embedding <=> $1 AS distance
            FROM chunks c
            JOIN documents d ON d.id = c.document_id
            WHERE c.chunk_kind = 'leaf'
              AND ($2::text IS NULL OR d.category = $2)
              AND ($3::text IS NULL OR d.product = $3)
              AND ($4::text IS NULL OR d.version = $4)
            ORDER BY c.embedding <=> $1
            LIMIT $5;
            """;

        var sw = Stopwatch.StartNew();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new NpgsqlParameter { Value = qVec });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = options.Category });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = options.Product });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = options.Version });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = options.CandidatePoolSize });

        var rows = await ReadRowsAsync(cmd, ct).ConfigureAwait(false);
        sw.Stop();
        return (rows, sw.Elapsed.TotalMilliseconds);
    }

    private async Task<(List<CandidateRow> Rows, double ElapsedMs)> RunSparseAsync(
        string query,
        RetrievalOptions options,
        CancellationToken ct)
    {
        const string sql = """
            SELECT c.id, c.document_id, c.chunk_index, c.content, c.raw_content,
                   c.section_title, c.section_level, c.page_start, c.page_end,
                   c.parent_chunk_id,
                   d.title, d.slug, d.category, d.product, d.version,
                   c.bm25v <&> to_bm25query(
                       'idx_chunks_bm25'::regclass,
                       tokenizer_catalog.tokenize($1, 'chunks_tokenizer')::bm25vector) AS distance
            FROM chunks c
            JOIN documents d ON d.id = c.document_id
            WHERE c.chunk_kind = 'leaf'
              AND ($2::text IS NULL OR d.category = $2)
              AND ($3::text IS NULL OR d.product = $3)
              AND ($4::text IS NULL OR d.version = $4)
            ORDER BY c.bm25v <&> to_bm25query(
                'idx_chunks_bm25'::regclass,
                tokenizer_catalog.tokenize($1, 'chunks_tokenizer')::bm25vector)
            LIMIT $5;
            """;

        var sw = Stopwatch.StartNew();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = query });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = options.Category });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = options.Product });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = options.Version });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = options.CandidatePoolSize });

        var rows = await ReadRowsAsync(cmd, ct).ConfigureAwait(false);
        sw.Stop();
        return (rows, sw.Elapsed.TotalMilliseconds);
    }

    private static async Task<List<CandidateRow>> ReadRowsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var rows = new List<CandidateRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new CandidateRow(
                ChunkId: reader.GetInt64(0),
                DocumentId: reader.GetString(1),
                ChunkIndex: reader.GetInt32(2),
                Content: reader.GetString(3),
                RawContent: reader.GetString(4),
                SectionTitle: reader.IsDBNull(5) ? null : reader.GetString(5),
                SectionLevel: reader.IsDBNull(6) ? null : reader.GetInt16(6),
                PageStart: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                PageEnd: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                ParentChunkId: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                DocumentTitle: reader.GetString(10),
                DocumentSlug: reader.GetString(11),
                Category: reader.GetString(12),
                Product: reader.IsDBNull(13) ? null : reader.GetString(13),
                Version: reader.IsDBNull(14) ? null : reader.GetString(14),
                Distance: reader.GetFloat(15)));
        }

        return rows;
    }

    // Pulls raw_content for the given parent chunk ids in one batch select so
    // the cross-encoder reranker scores (query, parent_text) instead of
    // (query, leaf_text). One DB round-trip per search; tiny — typically
    // 10-20 unique parents per query after rerank cap.
    private async Task<Dictionary<long, string>> FetchParentTextsAsync(
        long[] parentIds, CancellationToken ct)
    {
        var map = new Dictionary<long, string>(parentIds.Length);
        if (parentIds.Length == 0)
        {
            return map;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, raw_content FROM chunks WHERE id = ANY($1)";
        cmd.Parameters.Add(new NpgsqlParameter<long[]> { TypedValue = parentIds });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            map[reader.GetInt64(0)] = reader.GetString(1);
        }
        return map;
    }

    // If the top-5 RRF scores cluster within AdaptiveSpreadThreshold, candidates
    // are effectively tied — sending 30 of them to the cross-encoder is wasted
    // compute (the reranker assigns near-equal scores and the RRF order
    // survives anyway). Cap to AdaptiveRerankFloor (10) in that case.
    private static int ComputeAdaptiveRerankTopN(List<FusedCandidate> fused, int configured)
    {
        if (fused.Count < 5 || configured <= AdaptiveRerankFloor)
        {
            return configured;
        }
        float top1 = fused[0].RrfScore;
        float top5 = fused[4].RrfScore;
        float spread = top1 - top5;
        return spread > AdaptiveSpreadThreshold ? configured : AdaptiveRerankFloor;
    }

    private static List<FusedCandidate> ReciprocalRankFusion(
        List<CandidateRow> dense,
        List<CandidateRow> sparse,
        int k)
    {
        var scores = new Dictionary<long, Accumulator>(dense.Count + sparse.Count);

        for (int i = 0; i < dense.Count; i++)
        {
            var row = dense[i];
            float rrf = 1f / (k + i + 1);
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(scores, row.ChunkId, out bool existed);
            slot = existed
                ? slot with { Rrf = slot.Rrf + rrf, DenseRank = i + 1, DenseDistance = row.Distance }
                : new Accumulator(rrf, row, i + 1, null, row.Distance, null);
        }

        for (int i = 0; i < sparse.Count; i++)
        {
            var row = sparse[i];
            float rrf = 1f / (k + i + 1);
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(scores, row.ChunkId, out bool existed);
            slot = existed
                ? slot with { Rrf = slot.Rrf + rrf, SparseRank = i + 1, SparseDistance = row.Distance }
                : new Accumulator(rrf, row, null, i + 1, null, row.Distance);
        }

        return [.. scores.Values
            .OrderByDescending(x => x.Rrf)
            .Select(x => new FusedCandidate(x.Row, x.Rrf, x.DenseRank, x.SparseRank, x.DenseDistance, x.SparseDistance))];
    }

    // Emits at most one chunk per (document_id, section_title ?? "") — the one
    // that scored highest post-rerank. Order of retained chunks is preserved.
    private static IEnumerable<FusedCandidate> DedupPerSection(IEnumerable<FusedCandidate> ranked)
    {
        var seen = new HashSet<(string DocumentId, string SectionKey)>();
        foreach (var f in ranked)
        {
            var key = (f.Row.DocumentId, f.Row.SectionTitle ?? string.Empty);
            if (seen.Add(key))
            {
                yield return f;
            }
        }
    }

    private static ChunkResult Build(FusedCandidate f, Dictionary<long, float> rerankScore) =>
        new(
            ChunkId: f.Row.ChunkId,
            DocumentId: f.Row.DocumentId,
            DocumentTitle: f.Row.DocumentTitle,
            DocumentSlug: f.Row.DocumentSlug,
            Category: f.Row.Category,
            Product: f.Row.Product,
            Version: f.Row.Version,
            SectionTitle: f.Row.SectionTitle,
            SectionLevel: f.Row.SectionLevel,
            PageStart: f.Row.PageStart,
            PageEnd: f.Row.PageEnd,
            RawContent: f.Row.RawContent,
            Score: rerankScore.TryGetValue(f.Row.ChunkId, out float rs) ? rs : f.RrfScore,
            // pgvector's <=> cosine distance ∈ [0, 2]; similarity = 1 - distance.
            DenseSimilarity: f.DenseDistance is null ? null : 1f - f.DenseDistance.Value,
            // vchord_bm25's <&> returns negative BM25 score; flip sign so
            // callers see a conventional "higher = more relevant" value.
            Bm25Score: f.SparseDistance is null ? null : -f.SparseDistance.Value,
            RrfScore: f.RrfScore,
            RerankScore: rerankScore.TryGetValue(f.Row.ChunkId, out float rs2) ? rs2 : null);

    private sealed record CandidateRow(
        long ChunkId,
        string DocumentId,
        int ChunkIndex,
        string Content,
        string RawContent,
        string? SectionTitle,
        short? SectionLevel,
        int? PageStart,
        int? PageEnd,
        long? ParentChunkId,
        string DocumentTitle,
        string DocumentSlug,
        string Category,
        string? Product,
        string? Version,
        float Distance);

    private sealed record FusedCandidate(
        CandidateRow Row,
        float RrfScore,
        int? DenseRank,
        int? SparseRank,
        float? DenseDistance,
        float? SparseDistance);

    private record struct Accumulator(
        float Rrf,
        CandidateRow Row,
        int? DenseRank,
        int? SparseRank,
        float? DenseDistance,
        float? SparseDistance);
}
