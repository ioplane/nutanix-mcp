using FluentAssertions;
using Xunit;

namespace NutanixMcp.Embedding.Tests;

public class OnnxEmbedderTest
{
    [SkippableFact]
    public async Task EmbeddingDimensionIs1024()
    {
        var paths = ModelPaths.Embedder();
        Skip.If(paths is null, "embedder model assets not present");

        using var emb = new OnnxEmbedder(new EmbeddingOptions
        {
            ModelPath = paths.Value.ModelPath,
            VocabPath = paths.Value.VocabPath,
        });

        emb.Dimension.Should().Be(1024);

        float[][] v = await emb.EmbedAsync(["Nutanix AHV cluster networking"], isQuery: false, CancellationToken.None);
        v.Should().HaveCount(1);
        v[0].Length.Should().Be(1024);
    }

    [SkippableFact]
    public async Task NormalizedEmbeddingsHaveUnitNorm()
    {
        var paths = ModelPaths.Embedder();
        Skip.If(paths is null, "embedder model assets not present");

        using var emb = new OnnxEmbedder(new EmbeddingOptions
        {
            ModelPath = paths.Value.ModelPath,
            VocabPath = paths.Value.VocabPath,
        });

        float[][] v = await emb.EmbedAsync(
            ["AHV virtual switch VLAN configuration", "Prism Element user authentication"],
            isQuery: false,
            CancellationToken.None);

        foreach (float[] vec in v)
        {
            double sumSq = 0.0;
            foreach (float x in vec)
            {
                sumSq += x * x;
            }

            Math.Sqrt(sumSq).Should().BeApproximately(1.0, 1e-3);
        }
    }

    [SkippableFact]
    public async Task SimilarTopicsLandCloserThanUnrelatedTopics()
    {
        var paths = ModelPaths.Embedder();
        Skip.If(paths is null, "embedder model assets not present");

        using var emb = new OnnxEmbedder(new EmbeddingOptions
        {
            ModelPath = paths.Value.ModelPath,
            VocabPath = paths.Value.VocabPath,
        });

        float[][] v = await emb.EmbedAsync(
            [
                "AHV virtual switch VLAN configuration on a Nutanix cluster",
                "Configure VLAN tagging on AHV vSwitch for guest VMs",
                "Replace a SATA drive on the NX-3035-G10 hardware platform",
            ],
            isQuery: false,
            CancellationToken.None);

        float sim01 = Cosine(v[0], v[1]);
        float sim02 = Cosine(v[0], v[2]);

        sim01.Should().BeGreaterThan(sim02,
            "two AHV-VLAN sentences should be closer than AHV-VLAN vs hardware-replacement");
    }

    [SkippableFact]
    public async Task MultilingualCrossLingualSanity()
    {
        var paths = ModelPaths.Embedder();
        Skip.If(paths is null, "embedder model assets not present");

        using var emb = new OnnxEmbedder(new EmbeddingOptions
        {
            ModelPath = paths.Value.ModelPath,
            VocabPath = paths.Value.VocabPath,
        });

        float[][] en = await emb.EmbedAsync(
            ["Configure VLAN on AHV virtual switch"],
            isQuery: true,
            CancellationToken.None);
        float[][] ru = await emb.EmbedAsync(
            ["Настроить VLAN на виртуальном коммутаторе AHV"],
            isQuery: true,
            CancellationToken.None);

        float sim = Cosine(en[0], ru[0]);
        // bge-m3 is multilingual; cross-lingual cosine on the same topic should
        // be well above the random baseline (~0.3 for unit vectors of 1024 dim
        // with shared embedder priors). 0.5 is conservative.
        sim.Should().BeGreaterThan(0.5f,
            $"EN↔RU same-topic cosine should be high (got {sim})");
    }

    private static float Cosine(float[] a, float[] b)
    {
        float dot = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }

        return dot;  // vectors are unit-norm, so dot == cosine.
    }
}
