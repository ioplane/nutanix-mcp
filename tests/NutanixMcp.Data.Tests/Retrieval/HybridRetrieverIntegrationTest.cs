using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NutanixMcp.Core.Models;
using NutanixMcp.Data.Entities;
using NutanixMcp.Data.Tests.Fixtures;
using NutanixMcp.Embedding;
using NutanixMcp.Server.Retrieval;
using Pgvector;
using Xunit;

namespace NutanixMcp.Data.Tests.Retrieval;

[Collection("Pgvector")]
public class HybridRetrieverIntegrationTest(PgvectorFixture fx)
{
    [Fact]
    public async Task SearchReturnsExpectedChunk_FromSeededCorpus()
    {
        await fx.ResetAsync();

        // Seed three documents covering distinct topics. The mock embedder
        // hashes content into the vector space, so identical content as
        // query → dense top-1; the BM25 trigger handles sparse on insert.
        using var embedder = new DeterministicMockEmbedder();

        await SeedDocAsync("ahv-doc", "ahv", "AHV Admin Guide", "AHV Virtual Switch VLAN",
            "AHV virtual switch supports VLAN tagging via acli net.create vs0 vlan=42 followed by interface attach.",
            embedder);
        await SeedDocAsync("nx-doc", "hardware-replacement", "NX-3035-G10 NIC Replacement", "Network Card Replacement",
            "The NX-3035-G10 chassis supports up to 24 SATA drive bays and replaces network cards via the front service slide.",
            embedder);
        await SeedDocAsync("kb-doc", "kb", "KB-9876 NCC Health Check", "Cluster Check Output",
            "Run ncc health_checks run_all to inspect Nutanix cluster health and surface alerts via Prism.",
            embedder);

        using var reranker = new NoopReranker();
        var retriever = new HybridRetriever(embedder, reranker, fx.DataSource);

        // The query mirrors AHV chunk text. The dense path should pin
        // doc=ahv-doc and BM25 will agree because AHV, VLAN, and switch all
        // appear in that chunk.
        var response = await retriever.SearchAsync(
            "AHV virtual switch VLAN tagging",
            new RetrievalOptions { Limit = 5, CandidatePoolSize = 10, RerankTopN = 5 },
            CancellationToken.None);

        // The deterministic mock embedder is content-addressable, not
        // semantic — same string yields the same vector but unrelated strings
        // produce vectors with arbitrary cosine similarity. RRF fusion of an
        // arbitrary dense ordering with the BM25 ordering gives a result set
        // that contains ahv-doc but does not necessarily rank it first across
        // platforms. The honest claim is therefore narrower:
        //   1. ahv-doc must appear in the result list.
        //   2. Both retrieval sides must return at least one row.
        //   3. The rerank cap must be honoured.
        response.Results.Should().NotBeEmpty();
        response.Results.Select(r => r.DocumentId).Should().Contain("ahv-doc",
            "BM25 hits AHV/VLAN/switch/tagging tokens in the seeded ahv-doc chunk; "
            + "the dense path is content-addressable in this test, so RRF order is "
            + "platform-dependent and only the membership claim is stable");
        response.Diagnostics.SparseHits.Should().BeGreaterThan(0, "BM25 must hit AHV/VLAN tokens");
        response.Diagnostics.DenseHits.Should().BeGreaterThan(0, "dense side must return candidates");
        response.Diagnostics.AfterRerank.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task CategoryFilterRestrictsResults()
    {
        await fx.ResetAsync();
        using var embedder = new DeterministicMockEmbedder();

        await SeedDocAsync("ahv-doc", "ahv", "AHV Admin Guide", "AHV Virtual Switch",
            "AHV virtual switch supports VLAN tagging via acli net.create.", embedder);
        await SeedDocAsync("kb-doc", "kb", "KB-9876", "AHV VLAN troubleshooting",
            "When AHV VLAN tagging fails on an interface, check vs0 with manage_ovs.", embedder);

        using var reranker = new NoopReranker();
        var retriever = new HybridRetriever(embedder, reranker, fx.DataSource);

        var response = await retriever.SearchAsync(
            "AHV VLAN",
            new RetrievalOptions { Limit = 5, Category = "kb" },
            CancellationToken.None);

        response.Results.Should().NotBeEmpty();
        response.Results.Should().OnlyContain(r => r.Category == "kb",
            "the kb category filter must drop ahv-category hits");
    }

    [Fact]
    public async Task DiagnosticsReportTimingsAndCounts()
    {
        await fx.ResetAsync();
        using var embedder = new DeterministicMockEmbedder();

        await SeedDocAsync("doc1", "ahv", "AHV", "Test section",
            "Distributed Storage Fabric provides resilient block storage across cluster nodes.", embedder);

        using var reranker = new NoopReranker();
        var retriever = new HybridRetriever(embedder, reranker, fx.DataSource);

        var response = await retriever.SearchAsync(
            "DSF resilient storage",
            new RetrievalOptions { Limit = 5 },
            CancellationToken.None);

        var d = response.Diagnostics;
        d.TotalMs.Should().BeGreaterThan(0);
        d.EmbedMs.Should().BeGreaterThanOrEqualTo(0);
        d.DenseQueryMs.Should().BeGreaterThan(0);
        d.SparseQueryMs.Should().BeGreaterThan(0);
        d.RrfMs.Should().BeGreaterThanOrEqualTo(0);
        d.RerankMs.Should().BeGreaterThanOrEqualTo(0);
    }

    private async Task SeedDocAsync(
        string docId, string category, string title, string sectionTitle, string content,
        DeterministicMockEmbedder embedder)
    {
        await using var ctx = fx.CreateContext();
        var doc = new DocumentEntity
        {
            Id = docId,
            Url = $"https://portal.nutanix.com/{docId}",
            Category = category,
            Title = title,
            Slug = docId,
            MdPath = $"{docId}.md",
            JsonPath = $"{docId}.json",
        };
        ctx.Documents.Add(doc);

        float[][] emb = await embedder.EmbedAsync([content], isQuery: false, CancellationToken.None);
        Half[] halfArr = [.. emb[0].Select(static f => (Half)f)];

        ctx.Chunks.Add(new ChunkEntity
        {
            DocumentId = docId,
            ChunkIndex = 0,
            Content = content,
            RawContent = content,
            SectionTitle = sectionTitle,
            SectionLevel = 1,
            TokenCount = content.Split(' ').Length,
            Embedding = new HalfVector(halfArr),
            ChunkKind = "leaf",
        });
        await ctx.SaveChangesAsync();
    }
}
