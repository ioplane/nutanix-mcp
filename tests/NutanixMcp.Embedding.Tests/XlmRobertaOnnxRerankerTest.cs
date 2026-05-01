using FluentAssertions;
using NutanixMcp.Core.Retrieval;
using Xunit;

namespace NutanixMcp.Embedding.Tests;

public class XlmRobertaOnnxRerankerTest
{
    [SkippableFact]
    public async Task RelevantBeatsIrrelevant()
    {
        var paths = ModelPaths.Reranker();
        Skip.If(paths is null, "reranker model assets not present");

        using var rr = new XlmRobertaOnnxReranker(new RerankerOptions
        {
            ModelPath = paths!.Value.ModelPath,
            VocabPath = paths.Value.VocabPath,
        });

        const string query = "How do I configure a VLAN on AHV virtual switch?";
        var candidates = new RerankCandidate[]
        {
            new(1L, "On AHV, the virtual switch supports VLAN tagging via "
                  + "`acli net.create vs0 vlan=42` followed by interface attachment."),
            new(2L, "The NX-3035-G10 chassis supports up to 24 SATA drive bays "
                  + "and is replaced via the front service slide."),
            new(3L, "Pacemaker STONITH agent configuration for RHEL HA clusters "
                  + "requires Nutanix Fence Agent RPMs."),
        };

        var scores = await rr.RerankAsync(query, candidates, CancellationToken.None);
        scores.Should().HaveCount(3);

        var byId = scores.ToDictionary(s => s.ChunkId, s => s.Score);
        byId[1].Should().BeGreaterThan(byId[2],
            "VLAN-on-AHV candidate should outrank hardware-replacement candidate");
        byId[1].Should().BeGreaterThan(byId[3],
            "VLAN-on-AHV candidate should outrank Pacemaker candidate");
    }

    [SkippableFact]
    public async Task EmptyCandidatesReturnsEmpty()
    {
        var paths = ModelPaths.Reranker();
        Skip.If(paths is null, "reranker model assets not present");

        using var rr = new XlmRobertaOnnxReranker(new RerankerOptions
        {
            ModelPath = paths!.Value.ModelPath,
            VocabPath = paths.Value.VocabPath,
        });

        var scores = await rr.RerankAsync("anything", [], CancellationToken.None);
        scores.Should().BeEmpty();
    }
}
