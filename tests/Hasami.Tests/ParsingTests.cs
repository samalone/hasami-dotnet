using System.Globalization;

namespace Hasami.Tests;

public class TimeCodeTests
{
    [Fact]
    public void Creation()
    {
        TimeCode fromLong = 42;
        Assert.Equal(42, fromLong.Value);

        var fromIso = TimeCode.Parse("2024-03-20T12:00:00Z");
        Assert.Equal(DateTimeOffset.Parse("2024-03-20T12:00:00Z", CultureInfo.InvariantCulture).ToUnixTimeSeconds(), fromIso.Value);
        Assert.Equal(1710936000, fromIso.Value);

        Assert.Equal(1000, TimeCode.FromDateTimeOffset(DateTimeOffset.FromUnixTimeSeconds(1000)).Value);
        Assert.Equal(1000, TimeCode.FromDateTime(DateTime.UnixEpoch.AddSeconds(1000.75)).Value);  // truncates
    }

    [Fact]
    public void Description()
    {
        Assert.Equal("42", new TimeCode(42).ToString());
    }

    [Fact]
    public void Ordering()
    {
        Assert.True(new TimeCode(1) < new TimeCode(2));
        Assert.True(new TimeCode(1).CompareTo(new TimeCode(2)) < 0);
    }
}

public class DurationParserTests
{
    [Theory]
    [InlineData("45s", 45.0)]
    [InlineData("90m", 5400.0)]
    [InlineData("12h", 43200.0)]
    [InlineData("30d", 2_592_000.0)]
    [InlineData("2w", 1_209_600.0)]
    [InlineData("3600", 3600.0)]          // bare number is seconds
    [InlineData("1.5d", 129_600.0)]       // fractional
    [InlineData("  7D  ", 604_800.0)]     // whitespace and case
    public void ParsesValidDurations(string input, double expected) =>
        Assert.Equal(expected, DurationParser.Seconds(input));

    [Theory]
    [InlineData("")]
    [InlineData("d")]
    [InlineData("abc")]
    [InlineData("0d")]        // not positive
    [InlineData("-5h")]       // not positive
    [InlineData("10y")]       // unsupported suffix
    [InlineData("inf")]       // non-finite
    [InlineData("infinity")]
    [InlineData("nan")]
    [InlineData("1e400")]     // overflows to +inf
    [InlineData("1e400w")]    // suffix product overflows
    public void RejectsInvalidInput(string input) => Assert.Null(DurationParser.Seconds(input));

    [Fact]
    public void IsCultureInvariant()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");   // ',' decimal separator
            Assert.Equal(129_600.0, DurationParser.Seconds("1.5d"));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}

public class RetentionPolicyTests
{
    [Fact]
    public void DefaultsToRelativeFourWhenNeitherGiven() =>
        Assert.Equal(new RetentionPolicy.HalfLivesAcrossSpan(4), RetentionPolicy.Resolve(null, (double?)null));

    [Fact]
    public void AbsoluteParsesDuration() =>
        Assert.Equal(new RetentionPolicy.AbsoluteHalfLife(2_592_000), RetentionPolicy.Resolve("30d", (double?)null));

    [Fact]
    public void RelativeUsesGivenValue()
    {
        Assert.Equal(new RetentionPolicy.HalfLivesAcrossSpan(8), RetentionPolicy.Resolve(null, 8.0));
        Assert.Equal(new RetentionPolicy.HalfLivesAcrossSpan(2.5), RetentionPolicy.Resolve(null, "2.5"));
    }

    private static void AssertError(RetentionPolicyError expected, Action action) =>
        Assert.Equal(expected, Assert.Throws<RetentionPolicyException>(action).Error);

    [Fact]
    public void RejectsConflictingOptions() =>
        AssertError(RetentionPolicyError.ConflictingHalfLifeOptions, () => RetentionPolicy.Resolve("1d", 4.0));

    [Fact]
    public void RejectsInvalidDuration()
    {
        var e = Assert.Throws<RetentionPolicyException>(() => RetentionPolicy.Resolve("nope", (double?)null));
        Assert.Equal(RetentionPolicyError.InvalidDuration, e.Error);
        Assert.Equal("nope", e.Value);
    }

    [Fact]
    public void RejectsNonPositiveHalfLives() =>
        AssertError(RetentionPolicyError.NonPositiveHalfLives, () => RetentionPolicy.Resolve(null, 0.0));

    [Fact]
    public void RejectsNonFiniteHalfLives()
    {
        AssertError(RetentionPolicyError.NonPositiveHalfLives, () => RetentionPolicy.Resolve(null, double.PositiveInfinity));
        AssertError(RetentionPolicyError.NonPositiveHalfLives, () => RetentionPolicy.Resolve(null, double.NaN));
        AssertError(RetentionPolicyError.NonPositiveHalfLives, () => RetentionPolicy.Resolve(null, "infinity"));
    }

    [Fact]
    public void RejectsUnparseableHalfLives() =>
        AssertError(RetentionPolicyError.InvalidHalfLives, () => RetentionPolicy.Resolve(null, "four"));
}
