using System.Collections.Immutable;

namespace Hasami;

/// <summary>
/// An immutable, sorted collection of <see cref="TimeCode"/>s representing backups,
/// in ascending order (oldest to newest). All "mutating" methods return new instances.
/// </summary>
public sealed class BackupTree : IEquatable<BackupTree>
{
    private readonly ImmutableSortedSet<TimeCode> _timeCodes;

    public BackupTree(IEnumerable<TimeCode> timeCodes) => _timeCodes = timeCodes.ToImmutableSortedSet();

    public BackupTree(params TimeCode[] timeCodes) : this((IEnumerable<TimeCode>)timeCodes) { }

    private BackupTree(ImmutableSortedSet<TimeCode> set) => _timeCodes = set;

    /// <summary>The backups in ascending order (oldest first).</summary>
    public IReadOnlyList<TimeCode> Backups => _timeCodes;

    public int Count => _timeCodes.Count;

    /// <summary>The most recent backup, or null if the tree is empty.</summary>
    public TimeCode? MostRecent => _timeCodes.Count > 0 ? _timeCodes.Max : null;

    /// <summary>The oldest backup, or null if the tree is empty.</summary>
    public TimeCode? Oldest => _timeCodes.Count > 0 ? _timeCodes.Min : null;

    public BackupTree Adding(TimeCode backup) => new(_timeCodes.Add(backup));
    public BackupTree Union(BackupTree other) => new(_timeCodes.Union(other._timeCodes));
    public BackupTree Intersection(BackupTree other) => new(_timeCodes.Intersect(other._timeCodes));
    public BackupTree Subtracting(BackupTree other) => new(_timeCodes.Except(other._timeCodes));
    public BackupTree SymmetricDifference(BackupTree other) => new(_timeCodes.SymmetricExcept(other._timeCodes));
    public bool IsSubsetOf(BackupTree other) => _timeCodes.IsSubsetOf(other._timeCodes);
    public bool IsStrictSubsetOf(BackupTree other) => _timeCodes.IsProperSubsetOf(other._timeCodes);

    public bool Equals(BackupTree? other) => other is not null && _timeCodes.SequenceEqual(other._timeCodes);
    public override bool Equals(object? obj) => Equals(obj as BackupTree);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var tc in _timeCodes) hash.Add(tc);
        return hash.ToHashCode();
    }

    // ---------------------------------------------------------------- Pruning

    /// <summary>
    /// Selects which backups to retain so the retained set follows an exponential-decay
    /// retention curve: the density of retained backups halves for every
    /// <paramref name="halfLife"/> of additional age.
    /// </summary>
    /// <remarks>
    /// Each backup's age (measured from the newest backup in the tree) is warped into
    /// the CDF coordinate <c>u(age) = 1 - 2^(-age / halfLife)</c>, in which the target
    /// retention density is uniform. The most redundant backup — the one whose removal
    /// merges the smallest <c>u</c>-gap — is then removed greedily until only
    /// <paramref name="keepCount"/> remain. The newest and oldest backups are always
    /// retained (when <paramref name="keepCount"/> &gt;= 2). No wall-clock time is
    /// consulted, so re-running on an unchanged set is a no-op.
    /// </remarks>
    /// <param name="halfLife">Age over which retention density halves, in timestamp units (seconds). Must be &gt; 0.</param>
    /// <param name="keepCount">Maximum number of backups to retain (&gt;= 0).</param>
    /// <returns>The backups to retain, newest-first.</returns>
    public IReadOnlyList<TimeCode> RetainedBackups(double halfLife, int keepCount)
    {
        if (!(halfLife > 0 && double.IsFinite(halfLife)))
            throw new ArgumentOutOfRangeException(nameof(halfLife), "Half-life must be positive and finite");
        ArgumentOutOfRangeException.ThrowIfNegative(keepCount);
        return Thinned(halfLife, keepCount);
    }

    /// <summary>
    /// Selects which backups to retain using a half-life expressed as a fraction of the
    /// tree's history span: <c>halfLife = span / halfLivesAcrossSpan</c>. This makes the
    /// retained shape scale-free.
    /// </summary>
    /// <returns>The backups to retain, newest-first.</returns>
    public IReadOnlyList<TimeCode> RetainedBackupsAcrossSpan(double halfLivesAcrossSpan, int keepCount)
    {
        if (!(halfLivesAcrossSpan > 0 && double.IsFinite(halfLivesAcrossSpan)))
            throw new ArgumentOutOfRangeException(nameof(halfLivesAcrossSpan), "Half-lives across span must be positive and finite");
        ArgumentOutOfRangeException.ThrowIfNegative(keepCount);

        if (_timeCodes.Count == 0) return [];
        long span = _timeCodes.Max.Value - _timeCodes.Min.Value;
        if (span <= 0)
        {
            // All timestamps identical (degenerate); the count guard in Thinned
            // returns everything, so the half-life value is irrelevant.
            return Thinned(1, keepCount);
        }
        // An extreme (but finite) divisor can round span/k to 0 or overflow it to
        // infinity. Fall back to the gentlest representable decay rather than crash.
        double halfLife = span / halfLivesAcrossSpan;
        double safeHalfLife = halfLife > 0 && double.IsFinite(halfLife) ? halfLife : double.MaxValue;
        return Thinned(safeHalfLife, keepCount);
    }

    /// <summary>Selects which backups to retain under the given policy.</summary>
    /// <returns>The backups to retain, newest-first.</returns>
    public IReadOnlyList<TimeCode> RetainedBackups(RetentionPolicy policy, int keepCount) => policy switch
    {
        RetentionPolicy.AbsoluteHalfLife a => RetainedBackups(a.Seconds, keepCount),
        RetentionPolicy.HalfLivesAcrossSpan r => RetainedBackupsAcrossSpan(r.Value, keepCount),
        _ => throw new ArgumentException($"Unknown policy {policy}", nameof(policy)),
    };

    /// <summary>Core CDF-warp greedy thinner.</summary>
    private List<TimeCode> Thinned(double halfLife, int keepCount)
    {
        var points = _timeCodes;                 // ascending: oldest -> newest
        int n = points.Count;

        if (keepCount <= 0 || n == 0) return [];
        if (n <= keepCount) return points.Reverse().ToList();   // keep all, newest-first
        if (keepCount == 1) return [points[n - 1]];             // newest only

        // Warp each age (relative to the newest) into u = 1 - 2^(-age / H). u is
        // non-increasing along `points`; the newest has u = 0.
        long newest = points[n - 1].Value;
        var u = new double[n];
        for (int i = 0; i < n; i++)
            u[i] = 1.0 - double.Exp2(-(double)(newest - points[i].Value) / halfLife);

        // Survivors form a doubly-linked sequence over 0..n-1. The endpoints
        // (0 = oldest, n-1 = newest) are pinned; only interior points are removal
        // candidates. Removing interior point i costs |u[next] - u[prev]|.
        var prev = new int[n];
        var next = new int[n];
        var alive = new bool[n];
        var version = new int[n];   // for lazy heap invalidation
        for (int i = 0; i < n; i++) { prev[i] = i - 1; next[i] = i + 1; alive[i] = true; }
        int survivors = n;

        double Cost(int i) => Math.Abs(u[next[i]] - u[prev[i]]);

        // Min-heap ordered by (cost, index): ties are broken by index so the result
        // is deterministic and invariant to input order.
        var heap = new PriorityQueue<(int Index, int Version), (double Cost, int Index)>(n);
        void Push(int i) => heap.Enqueue((i, version[i]), (Cost(i), i));

        for (int i = 1; i < n - 1; i++) Push(i);

        while (survivors > keepCount && heap.TryDequeue(out var top, out _))
        {
            int i = top.Index;
            if (!alive[i] || top.Version != version[i]) continue;   // stale entry

            alive[i] = false;
            survivors--;
            int p = prev[i], q = next[i];
            next[p] = q;
            prev[q] = p;
            // Re-cost the interior neighbors; endpoints are pinned and never pushed.
            if (p > 0 && p < n - 1) { version[p]++; Push(p); }
            if (q > 0 && q < n - 1) { version[q]++; Push(q); }
        }

        var kept = new List<TimeCode>(survivors);
        for (int i = n - 1; i >= 0; i--)
            if (alive[i]) kept.Add(points[i]);   // newest-first
        return kept;
    }
}
