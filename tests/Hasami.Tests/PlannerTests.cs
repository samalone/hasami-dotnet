namespace Hasami.Tests;

public class PlannerParseTests
{
    [Fact]
    public void ParsesSimpleLine() =>
        Assert.Equal([new PlanItem(1713225600, "snapshots/2026-04-15/")],
                     Planner.Parse(["1713225600\tsnapshots/2026-04-15/"]));

    [Fact]
    public void ParsesMultipleLines()
    {
        var items = Planner.Parse(["1713225600\ta", "1713312000\tb", "1713398400\tc"]);
        Assert.Equal(["a", "b", "c"], items.Select(i => i.Key));
        Assert.Equal([1713225600L, 1713312000L, 1713398400L], items.Select(i => i.TimeCode.Value));
    }

    [Fact]
    public void SkipsEmptyLines() =>
        Assert.Equal(["a", "b"], Planner.Parse(["", "100\ta", "", "200\tb", ""]).Select(i => i.Key));

    [Fact]
    public void PreservesTabsInsideKey() =>
        Assert.Equal([new PlanItem(100, "path/with\ttab")], Planner.Parse(["100\tpath/with\ttab"]));

    [Fact]
    public void StripsTrailingNewline() => Assert.Equal([new PlanItem(100, "a")], Planner.Parse(["100\ta\n"]));

    [Fact]
    public void StripsTrailingCRLF() => Assert.Equal([new PlanItem(100, "a")], Planner.Parse(["100\ta\r\n"]));

    [Fact]
    public void AcceptsNegativeTimestamps() => Assert.Equal([new PlanItem(-5, "a")], Planner.Parse(["-5\ta"]));

    [Fact]
    public void RejectsMissingTab() =>
        Assert.Throws<PlanParseException>(() => Planner.Parse(["not-a-valid-line"]));

    [Fact]
    public void ReportsLineNumberForMalformedInput() =>
        Assert.Equal(3, Assert.Throws<PlanParseException>(() => Planner.Parse(["100\ta", "200\tb", "garbage"])).LineNumber);

    [Fact]
    public void RejectsNonIntegerTimestamp() =>
        Assert.Contains("invalid timestamp", Assert.Throws<PlanParseException>(() => Planner.Parse(["notanumber\tkey"])).Reason);

    [Fact]
    public void RejectsEmptyKey() => Assert.Throws<PlanParseException>(() => Planner.Parse(["100\t"]));
}

public class PlannerPlanTests
{
    private static readonly RetentionPolicy Relative4 = new RetentionPolicy.HalfLivesAcrossSpan(4);

    private static List<PlanItem> Keyed(int from, int to) =>
        Enumerable.Range(from, to - from + 1).Select(i => new PlanItem(i, $"key-{i}")).ToList();

    [Fact]
    public void EmptyInputProducesEmptyResult() =>
        Assert.Equal(new PlanResult([], []), Planner.Plan([], Relative4, retain: 10));

    [Fact]
    public void EveryInputKeyAppearsInExactlyOneList()
    {
        var items = Keyed(1, 50);
        var result = Planner.Plan(items, Relative4, retain: 10);
        var keep = result.Keep.ToHashSet();
        var prune = result.Prune.ToHashSet();
        Assert.False(keep.Overlaps(prune));
        Assert.Equal(items.Count, keep.Union(prune).Count());
    }

    [Fact]
    public void KeepCountMatchesRetain()
    {
        var result = Planner.Plan(Keyed(1, 180), Relative4, retain: 20);
        Assert.Equal(20, result.Keep.Count);
        Assert.Equal(160, result.Prune.Count);
    }

    [Fact]
    public void OutputRespectsMode()
    {
        var result = Planner.Plan([new(10, "a"), new(20, "b"), new(30, "c")], Relative4, retain: 10);
        Assert.Same(result.Keep, result.Output(PlanMode.Keep));
        Assert.Same(result.Prune, result.Output(PlanMode.Prune));
    }

    [Fact]
    public void OutputPreservesInputOrder()
    {
        var result = Planner.Plan([new(50, "mid"), new(10, "old"), new(90, "new")], Relative4, retain: 3);
        Assert.Equal(["mid", "old", "new"], result.Keep);
        Assert.Empty(result.Prune);
    }

    [Fact]
    public void NewestItemAlwaysRetained() =>
        Assert.Equal(["key-100"], Planner.Plan(Keyed(1, 100), Relative4, retain: 1).Keep);

    [Fact]
    public void ExactTimestampTieResolvesByInputOrder()
    {
        var result = Planner.Plan([new(100, "first"), new(100, "second")], Relative4, retain: 10);
        Assert.Equal(["first"], result.Keep);
        Assert.Equal(["second"], result.Prune);
    }

    [Fact]
    public void RetentionSetIsInvariantToInputOrder()
    {
        long[] timestamps = [1000, 1001, 1002, 2000, 3500, 7000];
        var ascending = timestamps.Order().Select(t => new PlanItem(t, $"k-{t}")).ToList();
        var descending = timestamps.OrderDescending().Select(t => new PlanItem(t, $"k-{t}")).ToList();
        var r1 = Planner.Plan(ascending, Relative4, retain: 3);
        var r2 = Planner.Plan(descending, Relative4, retain: 3);
        Assert.Equal(r1.Keep.ToHashSet(), r2.Keep.ToHashSet());
        Assert.Contains("k-7000", r1.Keep);
        Assert.Contains("k-7000", r2.Keep);
    }

    [Fact]
    public void AbsoluteHalfLifePolicyIsWired()
    {
        var items = Keyed(1, 180);
        var absolute = Planner.Plan(items, new RetentionPolicy.AbsoluteHalfLife(179.0 / 4.0), retain: 20);
        var relative = Planner.Plan(items, Relative4, retain: 20);
        Assert.Equal(relative.Keep.ToHashSet(), absolute.Keep.ToHashSet());
        Assert.Equal(20, absolute.Keep.Count);
    }

    [Fact]
    public void KeepListMatchesCoreAlgorithm()
    {
        var result = Planner.Plan(Keyed(1, 180), Relative4, retain: 20);
        var expected = BackupTreeTests.CharacterizationAges180Keep20.Select(age => $"key-{180 - age}");
        Assert.Equal(expected.ToHashSet(), result.Keep.ToHashSet());
        Assert.Equal(20, result.Keep.Count);
    }
}
