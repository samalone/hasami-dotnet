using System.Globalization;

namespace Hasami;

/// <summary>Which keys <c>sukashi-plan</c> should emit: the retained set or its complement.</summary>
public enum PlanMode
{
    Keep,
    Prune,
}

/// <summary>A single <c>(timestamp, key)</c> pair parsed from stdin.</summary>
public readonly record struct PlanItem(TimeCode TimeCode, string Key);

/// <summary>A stdin line that cannot be parsed as a plan item.</summary>
public sealed class PlanParseException(int lineNumber, string line, string reason)
    : Exception($"line {lineNumber}: {reason}")
{
    public int LineNumber { get; } = lineNumber;
    public string Line { get; } = line;
    public string Reason { get; } = reason;
}

/// <summary>
/// The partitioned result of a plan: every input key is in exactly one list, in the
/// order the keys appeared in the input.
/// </summary>
public sealed record PlanResult(IReadOnlyList<string> Keep, IReadOnlyList<string> Prune)
{
    public IReadOnlyList<string> Output(PlanMode mode) => mode == PlanMode.Keep ? Keep : Prune;

    public bool Equals(PlanResult? other) =>
        other is not null && Keep.SequenceEqual(other.Keep) && Prune.SequenceEqual(other.Prune);

    public override int GetHashCode() => HashCode.Combine(Keep.Count, Prune.Count);
}

/// <summary>The stdin/stdout planning logic behind <c>sukashi-plan</c>.</summary>
public static class Planner
{
    // Swift's CharacterSet.newlines, which the original trims from each line.
    private static readonly char[] Newlines = [.. new[] { 0x0A, 0x0D, 0x0B, 0x0C, 0x85, 0x2028, 0x2029 }.Select(c => (char)c)];

    /// <summary>
    /// Parses lines of <c>&lt;unix-timestamp&gt;&lt;TAB&gt;&lt;key&gt;</c>. Empty lines are
    /// skipped; any other malformed line throws <see cref="PlanParseException"/> with the
    /// 1-based line number.
    /// </summary>
    public static List<PlanItem> Parse(IEnumerable<string> lines)
    {
        var items = new List<PlanItem>();
        int lineNumber = 0;
        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = rawLine.Trim(Newlines);
            if (line.Length == 0) continue;

            int tab = line.IndexOf('\t');
            if (tab < 0)
                throw new PlanParseException(lineNumber, line, "missing tab separator between timestamp and key");

            var tsPart = line[..tab];
            var keyPart = line[(tab + 1)..];

            if (!long.TryParse(tsPart, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ts))
                throw new PlanParseException(lineNumber, line,
                    $"invalid timestamp \"{tsPart}\" (expected integer Unix seconds)");
            if (keyPart.Length == 0)
                throw new PlanParseException(lineNumber, line, "empty key after tab separator");

            items.Add(new PlanItem(new TimeCode(ts), keyPart));
        }
        return items;
    }

    /// <summary>
    /// Runs the Hasami retention algorithm over <paramref name="items"/> and partitions
    /// the keys into keep/prune lists. Items sharing a timestamp collapse to a single
    /// representative: the first one in input order wins, and the rest are always pruned.
    /// </summary>
    public static PlanResult Plan(IReadOnlyList<PlanItem> items, RetentionPolicy policy, int retain)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retain);
        if (items.Count == 0) return new PlanResult([], []);

        var repByTs = new Dictionary<TimeCode, int>();
        for (int i = 0; i < items.Count; i++)
            repByTs.TryAdd(items[i].TimeCode, i);

        var tree = new BackupTree(repByTs.Keys);
        var retained = tree.RetainedBackups(policy, retain);

        var keptIndices = new HashSet<int>(retained.Select(tc => repByTs[tc]));

        var keep = new List<string>();
        var prune = new List<string>();
        for (int i = 0; i < items.Count; i++)
            (keptIndices.Contains(i) ? keep : prune).Add(items[i].Key);
        return new PlanResult(keep, prune);
    }
}
