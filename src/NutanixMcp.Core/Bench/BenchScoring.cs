namespace NutanixMcp.Core.Bench;

// Pure scoring math. A `chunkIds` list is the ranked retrieval result
// (best first). `expected` is the union of acceptable hits. The function
// returns the rank (1-based) of the first acceptable chunk, or 0 when
// none of the expected ids made it into the result list.
public static class BenchScoring
{
    public static int FirstHitRank(IReadOnlyList<long> chunkIds, IReadOnlyList<long> expected)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        ArgumentNullException.ThrowIfNull(expected);

        if (expected.Count == 0)
        {
            return 0;
        }

        var lookup = new HashSet<long>(expected);
        for (int i = 0; i < chunkIds.Count; i++)
        {
            if (lookup.Contains(chunkIds[i]))
            {
                return i + 1;
            }
        }

        return 0;
    }

    public static bool IsHit(int firstHitRank, int cutoff)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cutoff, 1);
        return firstHitRank > 0 && firstHitRank <= cutoff;
    }
}
