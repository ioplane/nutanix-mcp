using FluentAssertions;
using NutanixMcp.Core.Settings;
using Xunit;

namespace NutanixMcp.Core.Tests;

public class NutanixMcpSettingsValidateTest
{
    [Fact]
    public void Defaults_AreValid()
    {
        var act = () => new NutanixMcpSettings().Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void EmptyConnectionString_Throws()
    {
        var s = new NutanixMcpSettings { ConnectionString = "  " };
        s.Invoking(x => x.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionString*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void OutOfRangeHttpPort_Throws(int port)
    {
        var s = new NutanixMcpSettings { HttpPort = port };
        s.Invoking(x => x.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*HttpPort*");
    }

    [Fact]
    public void NonPositiveIngestBatchSize_Throws()
    {
        var s = new NutanixMcpSettings { IngestBatchSize = 0 };
        s.Invoking(x => x.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*IngestBatchSize*");
    }

    [Fact]
    public void ChunkThresholdsOutOfOrder_Throws()
    {
        // target > max violates min <= target <= max.
        var s = new NutanixMcpSettings { ChunkTargetTokens = 2000, ChunkMaxTokens = 1200 };
        s.Invoking(x => x.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*ChunkMinTokens <= ChunkTargetTokens <= ChunkMaxTokens*");
    }
}
