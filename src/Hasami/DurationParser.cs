using System.Globalization;

namespace Hasami;

/// <summary>
/// Parses human-friendly duration strings into seconds, for use as an absolute
/// half-life on the command line.
/// </summary>
public static class DurationParser
{
    private static readonly Dictionary<char, double> Multipliers = new()
    {
        ['s'] = 1,        // seconds
        ['m'] = 60,       // minutes
        ['h'] = 3600,     // hours
        ['d'] = 86400,    // days
        ['w'] = 604800,   // weeks
    };

    /// <summary>
    /// Parses a duration such as <c>30d</c>, <c>12h</c>, <c>90m</c>, <c>2w</c> or
    /// <c>45s</c> into seconds. A bare number is seconds; fractions (<c>1.5d</c>) are
    /// allowed. Parsing is culture-invariant ('.' is the decimal separator).
    /// </summary>
    /// <returns>The duration in seconds, or null if it is not a valid positive, finite duration.</returns>
    public static double? Seconds(string text)
    {
        var s = text.Trim().ToLowerInvariant();
        if (s.Length == 0) return null;

        if (Multipliers.TryGetValue(s[^1], out var multiplier))
        {
            if (!TryParsePositiveFinite(s[..^1], out var value)) return null;
            var result = value * multiplier;
            return double.IsFinite(result) ? result : null;
        }

        // No recognized suffix: the whole string is a number of seconds.
        return TryParsePositiveFinite(s, out var seconds) ? seconds : null;
    }

    /// <summary>
    /// Parses a culture-invariant floating-point number that must be positive and finite.
    /// Rejects "inf", "nan" and values that overflow to infinity.
    /// </summary>
    internal static bool TryParsePositiveFinite(string s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && value > 0 && double.IsFinite(value);
}
