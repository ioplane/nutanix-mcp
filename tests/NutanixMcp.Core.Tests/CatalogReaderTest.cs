using System.IO;
using FluentAssertions;
using NutanixMcp.Core.Catalog;
using Xunit;

namespace NutanixMcp.Core.Tests;

public class CatalogReaderTest
{
    [Fact]
    public void EmptyDirectoryYieldsZeroEntries()
    {
        var dir = Directory.CreateTempSubdirectory("nutanix-catalog-empty-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "md"));
            var entries = CatalogReader.Read(new CatalogReader.ReadOptions { CatalogBaseDir = dir });
            entries.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MdWithoutJsonFallsBackToFilenameHeuristics()
    {
        var dir = Directory.CreateTempSubdirectory("nutanix-catalog-md-only-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "md"));
            File.WriteAllText(Path.Combine(dir, "md", "KB-9999.md"), "# Hello\n\nbody");
            var entries = CatalogReader.Read(new CatalogReader.ReadOptions { CatalogBaseDir = dir });
            entries.Should().HaveCount(1);
            entries[0].Id.Should().Be("KB-9999");
            entries[0].Category.Should().Be("kb");
            entries[0].Title.Should().Be("KB-9999");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RichJsonProvidesCategoryAndKbNumber()
    {
        var dir = Directory.CreateTempSubdirectory("nutanix-catalog-rich-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "md"));
            Directory.CreateDirectory(Path.Combine(dir, "json"));
            File.WriteAllText(Path.Combine(dir, "md", "KB-1234.md"), "# Real Title\n\nbody");
            File.WriteAllText(Path.Combine(dir, "json", "KB-1234.json"), """
                {
                  "title": "Real Title",
                  "category": "kb",
                  "tags": "Self-Service, Networking",
                  "kb_number": 1234,
                  "num_pages": 1,
                  "section_count": 2
                }
                """);

            var entries = CatalogReader.Read(new CatalogReader.ReadOptions { CatalogBaseDir = dir });
            entries.Should().HaveCount(1);
            var e = entries[0];
            e.Title.Should().Be("Real Title");
            e.Category.Should().Be("kb");
            e.KbNumber.Should().Be(1234);
            e.Tags.Should().BeEquivalentTo("Self-Service", "Networking");
            e.Pages.Should().Be(1);
            e.SectionCount.Should().Be(2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EntriesAreSortedByIdForDeterministicCatalogSha()
    {
        var dir = Directory.CreateTempSubdirectory("nutanix-catalog-sort-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "md"));
            File.WriteAllText(Path.Combine(dir, "md", "z.md"), "# z");
            File.WriteAllText(Path.Combine(dir, "md", "a.md"), "# a");
            File.WriteAllText(Path.Combine(dir, "md", "m.md"), "# m");
            var entries = CatalogReader.Read(new CatalogReader.ReadOptions { CatalogBaseDir = dir });
            entries.Select(e => e.Id).Should().ContainInOrder("a", "m", "z");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
