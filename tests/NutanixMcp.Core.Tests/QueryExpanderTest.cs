using FluentAssertions;
using NutanixMcp.Core.Retrieval;
using Xunit;

namespace NutanixMcp.Core.Tests;

public class QueryExpanderTest
{
    [Fact]
    public void AnnotatesAhvOnFirstOccurrence()
    {
        var ex = QueryExpander.Expand("AHV cluster networking");
        ex.Original.Should().Be("AHV cluster networking");
        ex.Expanded.Should().Be("AHV (Acropolis Hypervisor) cluster networking");
    }

    [Fact]
    public void AnnotatesEachAcronymAtMostOnce()
    {
        var ex = QueryExpander.Expand("AHV on AHV cluster");
        // Second occurrence of AHV should not be re-annotated.
        ex.Expanded.Should().Be("AHV (Acropolis Hypervisor) on AHV cluster");
    }

    [Fact]
    public void IsCaseInsensitiveAndPreservesOriginalCasing()
    {
        var ex = QueryExpander.Expand("Configure ahv switch on aos");
        ex.Expanded.Should().Be("Configure ahv (Acropolis Hypervisor) switch on aos (Acropolis Operating System)");
    }

    [Fact]
    public void LeavesUnknownTokensUntouched()
    {
        var ex = QueryExpander.Expand("How to configure VLAN tagging");
        ex.Expanded.Should().Be("How to configure VLAN tagging");
    }

    [Fact]
    public void NullQueryThrows()
    {
        var act = () => QueryExpander.Expand(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void HandlesEmptyQuery()
    {
        var ex = QueryExpander.Expand("");
        ex.Expanded.Should().Be("");
    }
}
