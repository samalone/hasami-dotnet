namespace Hasami.Tests;

public class BackupTreeTests
{
    private static BackupTree Range(int from, int to) =>
        new(Enumerable.Range(from, to - from + 1).Select(i => new TimeCode(i)));

    private static TimeCode[] Tc(params long[] values) => values.Select(v => new TimeCode(v)).ToArray();

    [Fact]
    public void Creation()
    {
        var tree = new BackupTree(3, 1, 2);
        Assert.Equal(Tc(1, 2, 3), tree.Backups);
        Assert.Equal(3, tree.Count);
        Assert.Equal(new TimeCode(1), tree.Oldest);
        Assert.Equal(new TimeCode(3), tree.MostRecent);
    }

    [Fact]
    public void SetOperations()
    {
        var tree1 = new BackupTree(1, 2, 3);
        var tree2 = new BackupTree(2, 3, 4);
        Assert.Equal(Tc(1, 2, 3, 4), tree1.Union(tree2).Backups);
        Assert.Equal(Tc(2, 3), tree1.Intersection(tree2).Backups);
        Assert.Equal(Tc(1), tree1.Subtracting(tree2).Backups);
        Assert.Equal(Tc(1, 4), tree1.SymmetricDifference(tree2).Backups);
    }

    [Fact]
    public void SetPredicates()
    {
        var tree1 = new BackupTree(1, 2, 3);
        var tree3 = new BackupTree(1, 2);
        var tree4 = new BackupTree(1, 2, 3, 4);

        Assert.True(tree1.IsSubsetOf(tree1));
        Assert.True(tree3.IsSubsetOf(tree1));
        Assert.True(tree1.IsSubsetOf(tree4));
        Assert.False(tree1.IsSubsetOf(tree3));

        Assert.False(tree1.IsStrictSubsetOf(tree1));
        Assert.True(tree3.IsStrictSubsetOf(tree1));
    }

    [Fact]
    public void Adding()
    {
        var tree = new BackupTree(1, 2, 3);
        var newTree = tree.Adding(4);
        Assert.Equal(Tc(1, 2, 3, 4), newTree.Backups);
        Assert.Equal(Tc(1, 2, 3), tree.Backups); // original unchanged
    }

    [Fact]
    public void Equality()
    {
        Assert.Equal(new BackupTree(1, 2, 3), new BackupTree(3, 2, 1));
        Assert.NotEqual(new BackupTree(1, 2), new BackupTree(1, 2, 3));
    }

    // ------------------------------------------------------------ Pruning

    [Fact]
    public void EmptyTree()
    {
        var tree = new BackupTree();
        Assert.Empty(tree.RetainedBackups(halfLife: 10, keepCount: 10));
        Assert.Empty(tree.RetainedBackupsAcrossSpan(4, keepCount: 10));
    }

    [Fact]
    public void KeepCountZero()
    {
        var tree = new BackupTree(1, 2, 3);
        Assert.Empty(tree.RetainedBackups(halfLife: 10, keepCount: 0));
        Assert.Empty(tree.RetainedBackupsAcrossSpan(4, keepCount: 0));
    }

    [Fact]
    public void FewerBackupsThanKeepCountReturnsAllNewestFirst()
    {
        var tree = new BackupTree(90, 95, 100);
        Assert.Equal(Tc(100, 95, 90), tree.RetainedBackupsAcrossSpan(4, keepCount: 10));
    }

    [Fact]
    public void KeepCountEqualToCountReturnsAll()
    {
        var result = Range(1, 20).RetainedBackupsAcrossSpan(4, keepCount: 20);
        Assert.Equal(20, result.Count);
        Assert.Equal(Range(1, 20).Backups.ToHashSet(), result.ToHashSet());
    }

    [Fact]
    public void KeepCountOneReturnsNewest()
    {
        var tree = Range(900, 1000);
        Assert.Equal(Tc(1000), tree.RetainedBackups(halfLife: 10, keepCount: 1));
        Assert.Equal(Tc(1000), tree.RetainedBackupsAcrossSpan(4, keepCount: 1));
    }

    [Fact]
    public void NewestAndOldestPinned()
    {
        var result = Range(1, 180).RetainedBackupsAcrossSpan(4, keepCount: 20);
        Assert.Equal(new TimeCode(180), result[0]);
        Assert.Equal(new TimeCode(1), result[^1]);
    }

    [Fact]
    public void Determinism()
    {
        var tree = Range(1, 180);
        Assert.Equal(tree.RetainedBackupsAcrossSpan(4, 20), tree.RetainedBackupsAcrossSpan(4, 20));
    }

    [Fact]
    public void RetainedCountMatchesKeepCount()
    {
        var tree = Range(1, 180);
        Assert.Equal(20, tree.RetainedBackups(halfLife: 30, keepCount: 20).Count);
        Assert.Equal(20, tree.RetainedBackupsAcrossSpan(4, keepCount: 20).Count);
    }

    [Fact]
    public void ResultSortedNewestFirst()
    {
        var result = Range(1, 180).RetainedBackupsAcrossSpan(4, keepCount: 20);
        for (int i = 0; i < result.Count - 1; i++)
            Assert.True(result[i].Value > result[i + 1].Value);
    }

    [Fact]
    public void RecentBackupsAreDenserThanOld()
    {
        var result = Range(1, 400).RetainedBackupsAcrossSpan(4, keepCount: 20);
        var ages = result.Select(r => result[0].Value - r.Value).ToArray();
        var newestGap = ages[1] - ages[0];
        var oldestGap = ages[^1] - ages[^2];
        Assert.True(newestGap <= oldestGap);
    }

    [Fact]
    public void ExtremeDivisorDoesNotCrash()
    {
        // A tiny --half-lives makes span/k overflow to infinity; the relative method
        // must clamp gracefully rather than trip the half-life validation.
        var result = Range(1, 50).RetainedBackupsAcrossSpan(1e-310, keepCount: 20);
        Assert.Equal(20, result.Count);
        Assert.Equal(new TimeCode(50), result[0]);
        Assert.Equal(new TimeCode(1), result[^1]);
    }

    [Fact]
    public void InvalidArgumentsThrow()
    {
        var tree = Range(1, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.RetainedBackups(halfLife: 0, keepCount: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.RetainedBackups(halfLife: double.PositiveInfinity, keepCount: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.RetainedBackups(halfLife: 10, keepCount: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.RetainedBackupsAcrossSpan(double.NaN, keepCount: 5));
    }

    [Fact]
    public void AbsoluteEqualsRelativeWhenHalfLifeMatchesSpan()
    {
        var tree = Range(1, 400);
        const double span = 400.0 - 1.0, k = 4.0;
        Assert.Equal(tree.RetainedBackups(halfLife: span / k, keepCount: 15), tree.RetainedBackupsAcrossSpan(k, keepCount: 15));
    }

    [Fact]
    public void PolicyDispatch()
    {
        var tree = Range(1, 180);
        Assert.Equal(tree.RetainedBackups(halfLife: 30, keepCount: 20),
                     tree.RetainedBackups(new RetentionPolicy.AbsoluteHalfLife(30), keepCount: 20));
        Assert.Equal(tree.RetainedBackupsAcrossSpan(4, keepCount: 20),
                     tree.RetainedBackups(new RetentionPolicy.HalfLivesAcrossSpan(4), keepCount: 20));
    }

    [Fact]
    public void RetentionInvariantToInputOrder()
    {
        // Regression for issue #5: the retention set depends only on the input set.
        long[] timestamps = [1000, 1001, 1002, 2000, 3500, 7000];
        var ascending = new BackupTree(timestamps.Order().Select(t => new TimeCode(t)));
        var descending = new BackupTree(timestamps.OrderDescending().Select(t => new TimeCode(t)));
        var r1 = ascending.RetainedBackupsAcrossSpan(4, keepCount: 3);
        var r2 = descending.RetainedBackupsAcrossSpan(4, keepCount: 3);
        Assert.Equal(r1, r2);
        Assert.Contains(new TimeCode(7000), r1);
    }

    /// <summary>
    /// Golden test: 180 evenly-spaced backups (ts 1...180), keep 20, half-life = span/4.
    /// Must match the Swift implementation's output exactly.
    /// Gaps grow with age (3, 4×8, 8×6, 16×2, 32×2), fitting the exponential-decay curve.
    /// </summary>
    [Fact]
    public void CharacterizationEvenlySpaced()
    {
        var result = Range(1, 180).RetainedBackupsAcrossSpan(4, keepCount: 20);
        Assert.Equal(CharacterizationAges180Keep20.Select(age => new TimeCode(180 - age)), result);
    }

    internal static readonly int[] CharacterizationAges180Keep20 =
        [0, 3, 7, 11, 15, 19, 23, 27, 31, 35, 43, 51, 59, 67, 75, 83, 99, 115, 147, 179];
}
