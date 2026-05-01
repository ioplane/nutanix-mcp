using FluentAssertions;
using NutanixMcp.Core.Bench;
using Xunit;

namespace NutanixMcp.Core.Tests;

public class BenchScoringTest
{
    [Fact]
    public void HitAtRank1WhenFirstResultIsExpected()
    {
        int rank = BenchScoring.FirstHitRank([42L, 100L, 200L], [42L]);
        rank.Should().Be(1);
        BenchScoring.IsHit(rank, 1).Should().BeTrue();
        BenchScoring.IsHit(rank, 10).Should().BeTrue();
    }

    [Fact]
    public void RankIsOneBased()
    {
        int rank = BenchScoring.FirstHitRank([100L, 42L, 200L], [42L]);
        rank.Should().Be(2);
        BenchScoring.IsHit(rank, 1).Should().BeFalse();
        BenchScoring.IsHit(rank, 5).Should().BeTrue();
    }

    [Fact]
    public void MissReturnsZero()
    {
        int rank = BenchScoring.FirstHitRank([1L, 2L, 3L], [42L]);
        rank.Should().Be(0);
        BenchScoring.IsHit(rank, 10).Should().BeFalse();
    }

    [Fact]
    public void UnionOfExpectedHitsCountsAsHit()
    {
        int rank = BenchScoring.FirstHitRank([99L, 42L, 7L], [13L, 7L, 42L]);
        // 42 first matches (at rank 2). Even though 7 is also acceptable,
        // earlier-matching expected ids are preferred.
        rank.Should().Be(2);
    }

    [Fact]
    public void EmptyExpectedReturnsZero() => BenchScoring.FirstHitRank([1L, 2L], []).Should().Be(0);

    [Fact]
    public void EmptyResultsReturnsZero() => BenchScoring.FirstHitRank([], [42L]).Should().Be(0);

    [Fact]
    public void IsHitRejectsZeroOrNegativeCutoff()
    {
        var act = () => BenchScoring.IsHit(1, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
