using AwesomeAssertions;
using NutanixMcp.Core.Chunking;
using Xunit;

namespace NutanixMcp.Core.Tests;

public class SectionAwareChunkerTest
{
    [Fact]
    public void EmitsOneParentAndOneLeafForShortSection()
    {
        var chunker = new SectionAwareChunker(new ChunkingOptions
        {
            TargetTokens = 100,
            MaxTokens = 200,
            MinTokens = 5,
        });

        var sections = new[]
        {
            new Section { Title = "AHV", Level = 1, Content = "Short body about AHV." },
        };

        var set = chunker.Chunk("Doc", sections);
        set.Parents.Should().HaveCount(1);
        set.Leaves.Should().HaveCount(1);
        set.Parents[0].Kind.Should().Be(ChunkKind.Parent);
        set.Leaves[0].Kind.Should().Be(ChunkKind.Leaf);
        set.Leaves[0].ParentIndex.Should().Be(0);
    }

    [Fact]
    public void SplitsOversizedSectionWithOverlapAndAttachesEachLeafToTheParent()
    {
        string words = string.Join(' ', Enumerable.Repeat("token", 1000));
        var chunker = new SectionAwareChunker(new ChunkingOptions
        {
            TargetTokens = 100,
            MaxTokens = 200,
            OverlapTokens = 20,
            MinTokens = 5,
        });

        var sections = new[]
        {
            new Section { Title = "Big", Level = 1, Content = words },
        };

        var set = chunker.Chunk("Doc", sections);
        set.Parents.Should().HaveCount(1);
        set.Leaves.Should().HaveCountGreaterThan(1);
        set.Leaves.Should().OnlyContain(l => l.ParentIndex == 0);
    }

    [Fact]
    public void TruncatesParentBeyondParentMaxTokens()
    {
        string words = string.Join(' ', Enumerable.Repeat("word", 5000));
        var chunker = new SectionAwareChunker(new ChunkingOptions
        {
            TargetTokens = 100,
            MaxTokens = 200,
            ParentMaxTokens = 1000,
            ParentHeadTokens = 800,
            ParentTailTokens = 200,
            MinTokens = 5,
        });

        var sections = new[]
        {
            new Section { Title = "Huge", Level = 1, Content = words },
        };

        var set = chunker.Chunk("Doc", sections);
        set.Parents.Should().HaveCount(1);
        set.Parents[0].RawContent.Should().Contain("[…section continues…]");
    }

    [Fact]
    public void EmptySectionsAreSkipped()
    {
        var chunker = new SectionAwareChunker(new ChunkingOptions { MinTokens = 1 });
        var set = chunker.Chunk("Doc", [new Section { Title = "X", Level = 1, Content = "  " }]);
        set.Parents.Should().BeEmpty();
        set.Leaves.Should().BeEmpty();
    }
}
