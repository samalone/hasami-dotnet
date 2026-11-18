using System.Globalization;

namespace Hasami;

/// <summary>
/// A point in time as an integer value, typically seconds since the Unix epoch.
/// </summary>
public readonly record struct TimeCode(long Value) : IComparable<TimeCode>
{
    public static implicit operator TimeCode(long value) => new(value);

    /// <summary>Creates a TimeCode from a date, truncating to whole seconds.</summary>
    public static TimeCode FromDateTimeOffset(DateTimeOffset date) => new(date.ToUnixTimeSeconds());

    /// <summary>Creates a TimeCode from a date, truncating to whole seconds.</summary>
    public static TimeCode FromDateTime(DateTime date) => FromDateTimeOffset(new DateTimeOffset(date.ToUniversalTime()));

    /// <summary>Parses an ISO 8601 date string such as <c>2024-03-20T12:00:00Z</c>.</summary>
    /// <exception cref="FormatException">The string is not a valid ISO 8601 date.</exception>
    public static TimeCode Parse(string iso8601) =>
        FromDateTimeOffset(DateTimeOffset.Parse(iso8601, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    public int CompareTo(TimeCode other) => Value.CompareTo(other.Value);

    public static bool operator <(TimeCode lhs, TimeCode rhs) => lhs.Value < rhs.Value;
    public static bool operator >(TimeCode lhs, TimeCode rhs) => lhs.Value > rhs.Value;
    public static bool operator <=(TimeCode lhs, TimeCode rhs) => lhs.Value <= rhs.Value;
    public static bool operator >=(TimeCode lhs, TimeCode rhs) => lhs.Value >= rhs.Value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
