using System.IO;
using FluentAssertions;
using NutanixMcp.Core.Bench;
using Xunit;

namespace NutanixMcp.Core.Tests;

public class BenchHistoryWriterTest
{
    [Fact]
    public async Task AppendsOneJsonLinePerCall()
    {
        var dir = Directory.CreateTempSubdirectory("nutanix-bench-history-").FullName;
        var path = Path.Combine(dir, "bench-history.jsonl");
        try
        {
            await BenchHistoryWriter.AppendAsync(path, MakeRun("a"), CancellationToken.None);
            await BenchHistoryWriter.AppendAsync(path, MakeRun("b"), CancellationToken.None);

            var lines = await File.ReadAllLinesAsync(path);
            lines.Should().HaveCount(2);
            lines[0].Should().Contain("\"label\":\"a\"");
            lines[1].Should().Contain("\"label\":\"b\"");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreatesParentDirectoryWhenMissing()
    {
        var dir = Directory.CreateTempSubdirectory("nutanix-bench-history-").FullName;
        var path = Path.Combine(dir, "nested", "tree", "bench-history.jsonl");
        try
        {
            await BenchHistoryWriter.AppendAsync(path, MakeRun("a"), CancellationToken.None);
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static BenchRun MakeRun(string label) => new()
    {
        Timestamp = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero),
        Label = label,
        Queries = 100,
        Top1 = 0.82,
        Top5 = 0.94,
        Top10 = 0.97,
        P50Ms = 1450,
        P95Ms = 2300,
        Embedder = "bge-m3",
        Reranker = "bge-reranker-v2-m3",
        RrfK = 60,
        RerankTopN = 30,
        CandidatePoolSize = 50,
        Limit = 10,
    };
}
