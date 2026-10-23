using AwesomeAssertions;
using NutanixMcp.Core.Catalog;
using Xunit;

namespace NutanixMcp.Core.Tests;

public class DocumentLoaderTest
{
    [Theory]
    [InlineData("```yaml", "```")]
    [InlineData("~~~~yaml", "~~~~")]
    [InlineData(" ```yaml", "   ````")]
    [InlineData("````yaml", "`````")]
    public void PreservesFencedSourceAndIgnoresCodeHeadings(string opening, string closing)
    {
        const string code = "ownerUuid:\n  pattern: \"^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{12}$\"\n# source comment\n{42}---\n![](_page_3_Picture_5.jpeg)";
        string fenced = opening + "\n" + code + "\n" + closing;
        var sections = DocumentLoader.ExtractSectionsFromMarkdown(
            "# Reference\n\n" + fenced + "\n\n## Prose\n\n{9}---\nbody", "Fallback");

        sections.Should().HaveCount(2);
        sections[0].Title.Should().Be("Reference");
        sections[0].Content.Should().Be(fenced.Trim());
        sections[1].Title.Should().Be("Prose");
        sections[1].Content.Should().Be("body");
    }

    [Fact]
    public void ShorterAndMismatchedFencesDoNotEndSource()
    {
        const string fenced = "````yaml\n{8}---\n```\n# code heading\n~~~~\n## another code heading\n````";
        var sections = DocumentLoader.ExtractSectionsFromMarkdown(
            "# Reference\n\n" + fenced + "\n\n## Prose\n\nbody", "Fallback");

        sections.Should().HaveCount(2);
        sections[0].Content.Should().Be(fenced);
        sections[1].Title.Should().Be("Prose");
    }

    [Fact]
    public void UnclosedFencePreservesSourceToEndOfDocument()
    {
        const string fenced = "~~~yaml\npattern: \"{8}-{4}-\"\n# source comment\n![](_page_3_Picture_5.jpeg)";
        var sections = DocumentLoader.ExtractSectionsFromMarkdown("# Reference\n\n" + fenced, "Fallback");

        sections.Should().HaveCount(1);
        sections[0].Content.Should().Be(fenced);
    }

    [Fact]
    public void StripsCrlfBeforeMatchingHeadings()
    {
        string crlf = "# Title\r\n\r\nbody one\r\n\r\n## Section\r\n\r\nbody two";
        var sections = DocumentLoader.ExtractSectionsFromMarkdown(crlf, "Fallback");
        sections.Should().HaveCount(2);
        sections[0].Title.Should().Be("Title");
        sections[1].Title.Should().Be("Section");
    }

    [Fact]
    public void StripsMarkerPageTokens()
    {
        string md = "# Title\n\n{0}--------\n\nbody one\n\n{42}---\n\n## Section\n\nbody two";
        var sections = DocumentLoader.ExtractSectionsFromMarkdown(md, "Fallback");
        sections.Should().HaveCount(2);
        sections[0].Content.Should().NotContain("{0}");
        sections[1].Content.Should().NotContain("{42}");
    }

    [Fact]
    public void StripsEmptyImageReferences()
    {
        string md = "# Title\n\n![](_page_3_Picture_5.jpeg)\n![](_page_4_Figure_2.png)\n\nactual body";
        var sections = DocumentLoader.ExtractSectionsFromMarkdown(md, "Fallback");
        sections.Should().HaveCount(1);
        sections[0].Content.Should().Be("actual body");
    }

    [Fact]
    public void DropsContentsTableSection()
    {
        string md = "# Title\n\ndoc body\n\n## **Contents**\n\n| Section | Page |\n|--|--|\n| One | 1 |\n\n## Real Section\n\nreal body";
        var sections = DocumentLoader.ExtractSectionsFromMarkdown(md, "Fallback");
        sections.Select(s => s.Title).Should().ContainInOrder("Title", "Real Section");
        sections.Select(s => s.Title).Should().NotContain(t => t.Contains("Content", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CleanHeadingStripsBoldItalicHtmlAndCollapsesWhitespace()
    {
        DocumentLoader.CleanHeading("**AHV**  *Cluster*").Should().Be("AHV Cluster");
        DocumentLoader.CleanHeading("<span id=\"x\">Title</span>").Should().Be("Title");
        DocumentLoader.CleanHeading("_emphasis_").Should().Be("emphasis");
    }

    [Fact]
    public void StampPagesLooksUpJsonByCleanedTitleAndIgnoresLevel()
    {
        var md = new[]
        {
            new Chunking.Section { Title = "**AHV Overview**", Level = 1, Content = "a" },
            new Chunking.Section { Title = "VLAN Configuration", Level = 3, Content = "b" },
        };
        var json = new[]
        {
            new JsonSection("AHV Overview", 5, 7),
            new JsonSection("VLAN Configuration", 8, 10),
        };
        var stamped = DocumentLoader.StampPagesFromJson(md, json);
        stamped.Should().HaveCount(2);
        stamped[0].PageStart.Should().Be(5);
        stamped[0].PageEnd.Should().Be(7);
        stamped[1].PageStart.Should().Be(8);
        stamped[1].PageEnd.Should().Be(10);
    }

    [Fact]
    public void StampPagesSurvivesMissingMdMatchAndResyncsOnNext()
    {
        var md = new[]
        {
            new Chunking.Section { Title = "Intro", Level = 1, Content = "a" },
            new Chunking.Section { Title = "Stray Heading", Level = 2, Content = "b" },
            new Chunking.Section { Title = "Conclusion", Level = 1, Content = "c" },
        };
        var json = new[]
        {
            new JsonSection("Intro", 1, 1),
            new JsonSection("Conclusion", 5, 5),
        };
        var stamped = DocumentLoader.StampPagesFromJson(md, json);
        stamped[0].PageStart.Should().Be(1);
        stamped[1].PageStart.Should().BeNull();
        stamped[2].PageStart.Should().Be(5);
    }
}
