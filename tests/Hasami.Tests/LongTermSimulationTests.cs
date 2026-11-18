using System.Globalization;
using Xunit.Abstractions;

namespace Hasami.Tests;

/// <summary>
/// Long-term behavior simulations used as regression guards: a change to the retention
/// algorithm that degrades the converged distribution trips the assertions here.
/// </summary>
/// <remarks>
/// Scenario: a backup is taken every day for 52 weeks; once a week <c>sukashi --retain 50</c>
/// runs over everything accumulated so far. This drives the iterated pruning path rather
/// than a single one-shot prune. The two tests differ only in the half-life policy.
/// Set <c>HASAMI_SIM_REPORT=1</c> to dump machine-readable result blocks.
/// </remarks>
public class LongTermSimulationTests(ITestOutputHelper output)
{
    private const int Day = 86_400;
    private const int Weeks = 52;
    private const int Keep = 50;

    private sealed record WeekSample(int Week, int Count, double RmsPct);

    private sealed record SimulationResult(double[] FinalAges, double[] OneShotAges, List<WeekSample> Trace);

    /// <summary>Ages (days from the newest backup) of every backup, ascending.</summary>
    private static double[] AgesInDays(BackupTree tree)
    {
        if (tree.MostRecent is not { } newest) return [];
        return tree.Backups.Reverse().Select(b => (double)(newest.Value - b.Value) / Day).ToArray();
    }

    /// <summary>RMS deviation in warp space from a perfectly uniform-in-u set, as % of the u-range.</summary>
    private static double RmsPctInWarpSpace(double[] ages, double halfLifeDays)
    {
        if (ages.Length <= 1 || ages[^1] <= 0) return 0;
        double U(double a) => 1.0 - double.Exp2(-a / halfLifeDays);
        double uMax = U(ages[^1]);
        if (uMax <= 0) return 0;
        int n = ages.Length;
        double sse = 0;
        for (int i = 0; i < n; i++)
        {
            double ideal = uMax * i / (n - 1);
            sse += (U(ages[i]) - ideal) * (U(ages[i]) - ideal);
        }
        return Math.Sqrt(sse / n) / uMax * 100;
    }

    private static SimulationResult RunScenario(
        Func<BackupTree, IReadOnlyList<TimeCode>> prune, Func<double[], double> halfLifeDays)
    {
        var tree = new BackupTree();
        int lastDay = 0;
        var trace = new List<WeekSample>();

        for (int week = 0; week < Weeks; week++)
        {
            for (int d = 0; d < 7; d++)
            {
                lastDay = week * 7 + d;
                tree = tree.Adding(new TimeCode((long)lastDay * Day));
            }
            tree = new BackupTree(prune(tree));      // weekly `sukashi --retain 50 ...`
            var ages = AgesInDays(tree);
            trace.Add(new WeekSample(week + 1, tree.Count, RmsPctInWarpSpace(ages, halfLifeDays(ages))));
        }

        // One-shot baseline: prune every daily backup ever taken in a single pass.
        var oneShot = new BackupTree(Enumerable.Range(0, lastDay + 1).Select(d => new TimeCode((long)d * Day)));
        return new SimulationResult(AgesInDays(tree), AgesInDays(new BackupTree(prune(oneShot))), trace);
    }

    private void Report(string label, SimulationResult r, double halfLifeDays)
    {
        if (Environment.GetEnvironmentVariable("HASAMI_SIM_REPORT") is null) return;
        string F(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);
        void Emit(string tag, double[] ages)
        {
            output.WriteLine($"=== {tag} BEGIN ===");
            output.WriteLine(string.Join(",", ages.Select(a => F(a, "F4"))));
            output.WriteLine($"=== {tag} END ===");
        }
        output.WriteLine($"########## {label} ##########");
        Emit("KEPT_AGES", r.FinalAges);
        Emit("ONESHOT_AGES", r.OneShotAges);
        output.WriteLine("=== WEEKLY_TRACE BEGIN ===");
        foreach (var s in r.Trace) output.WriteLine($"week={s.Week} count={s.Count} rms_pct={F(s.RmsPct, "F3")}");
        output.WriteLine("=== WEEKLY_TRACE END ===");
        output.WriteLine("=== STATS BEGIN ===");
        output.WriteLine($"count={r.FinalAges.Length} span_days={F(r.FinalAges.LastOrDefault(), "F1")} " +
                         $"H_days={F(halfLifeDays, "F2")} final_rms_pct={F(r.Trace[^1].RmsPct, "F3")}");
        output.WriteLine("=== STATS END ===");
    }

    private static double[] Gaps(double[] ages) => ages.Zip(ages.Skip(1), (a, b) => b - a).ToArray();

    [Fact]
    public void SimulateWeeklyRelativeHalfLives()
    {
        const double halfLives = 4.0;
        var r = RunScenario(t => t.RetainedBackupsAcrossSpan(halfLives, Keep), ages => ages.LastOrDefault() / halfLives);
        Report("RELATIVE --half-lives 4", r, r.FinalAges.LastOrDefault() / halfLives);

        var ages = r.FinalAges;
        var span = ages[^1];

        // Structure: a full keep-set spanning the whole year, endpoints pinned.
        Assert.Equal(Keep, ages.Length);
        Assert.Equal(0, ages[0]);
        Assert.Equal(363, span);

        // Exponential shape: gaps near "now" are far tighter than gaps in the tail.
        var g = Gaps(ages);
        Assert.True(g[0] <= 7, $"first gap {g[0]}");
        Assert.True(g[^1] >= 4 * g[0], $"last gap {g[^1]} vs first {g[0]}");

        // Convergence + steady state.
        var final = r.Trace[^1];
        Assert.True(final.RmsPct < 3.0, $"final rms {final.RmsPct}");
        Assert.All(r.Trace.Where(s => s.Week >= 20), s => Assert.True(s.RmsPct < 5.0, $"week {s.Week} rms {s.RmsPct}"));
        var fillingPhaseWorst = r.Trace.Where(s => s.Week <= 7).Max(s => s.RmsPct);
        Assert.True(final.RmsPct < fillingPhaseWorst / 4);

        // Iteration is no worse than a one-shot prune (no hysteresis penalty).
        Assert.True(final.RmsPct <= RmsPctInWarpSpace(r.OneShotAges, span / halfLives) + 0.5);
    }

    [Fact]
    public void SimulateWeeklyAbsoluteHalfLife()
    {
        const double halfLifeDays = 30.0;
        var r = RunScenario(t => t.RetainedBackups(halfLifeDays * Day, Keep), _ => halfLifeDays);
        Report("ABSOLUTE --half-life 30d", r, halfLifeDays);

        var ages = r.FinalAges;
        Assert.Equal(Keep, ages.Length);
        Assert.Equal(0, ages[0]);
        Assert.Equal(363, ages[^1]);

        // ~12 half-lives over the span: heavily front-loaded, big tail gap before the pinned oldest.
        Assert.True(ages.Count(a => a <= 90) >= 30);
        var g = Gaps(ages);
        Assert.True(g[^1] >= 4 * g[0]);

        var final = r.Trace[^1];
        Assert.True(final.RmsPct < 5.0, $"final rms {final.RmsPct}");
        Assert.All(r.Trace.Where(s => s.Week >= 25), s => Assert.True(s.RmsPct < 8.0, $"week {s.Week} rms {s.RmsPct}"));
        Assert.True(final.RmsPct <= RmsPctInWarpSpace(r.OneShotAges, halfLifeDays) + 1.0);
    }
}
