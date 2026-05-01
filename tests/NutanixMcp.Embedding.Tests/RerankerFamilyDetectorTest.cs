using System.IO;
using FluentAssertions;
using NutanixMcp.Core.Retrieval;
using Xunit;

namespace NutanixMcp.Embedding.Tests;

public class RerankerFamilyDetectorTest
{
    [Fact]
    public void DetectReturnsNoneForMissingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nutanix-mcp-test-missing-" + Guid.NewGuid());
        var family = RerankerFamilyDetector.Detect(dir);
        family.Should().Be(RerankerFamilyDetector.RerankerFamily.None);
    }

    [Fact]
    public void DetectReturnsNoneForEmptyDirectory()
    {
        var dir = Directory.CreateTempSubdirectory("nutanix-mcp-empty-").FullName;
        try
        {
            var family = RerankerFamilyDetector.Detect(dir);
            family.Should().Be(RerankerFamilyDetector.RerankerFamily.None);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DetectReturnsXlmRobertaWhenSentencepiecePresent()
    {
        var dir = Directory.CreateTempSubdirectory("nutanix-mcp-spm-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "model.onnx"), [0x4F, 0x4E, 0x4E, 0x58]);
            File.WriteAllBytes(Path.Combine(dir, "sentencepiece.bpe.model"), [0x53, 0x50]);

            var family = RerankerFamilyDetector.Detect(dir);
            family.Should().Be(RerankerFamilyDetector.RerankerFamily.XlmRoberta);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DetectReturnsBertWordPieceWhenVocabTxtPresent()
    {
        var dir = Directory.CreateTempSubdirectory("nutanix-mcp-bert-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "model.onnx"), [0x4F, 0x4E, 0x4E, 0x58]);
            File.WriteAllText(Path.Combine(dir, "vocab.txt"), "[PAD]\n[CLS]\n");

            var family = RerankerFamilyDetector.Detect(dir);
            family.Should().Be(RerankerFamilyDetector.RerankerFamily.BertWordPiece);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildFallsBackToNoopWhenAssetsMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nutanix-mcp-test-missing-" + Guid.NewGuid());
        using var rr = RerankerFamilyDetector.Build(dir, gpu: false);
        rr.Should().BeOfType<NoopReranker>();
    }
}
