using System.Globalization;

namespace Hasami;

/// <summary>How the retention curve's half-life is determined.</summary>
public abstract record RetentionPolicy
{
    private RetentionPolicy() { }

    /// <summary>
    /// An absolute half-life in seconds: retention density halves every
    /// <paramref name="Seconds"/> of age, regardless of the history span.
    /// </summary>
    public sealed record AbsoluteHalfLife(double Seconds) : RetentionPolicy;

    /// <summary>
    /// A half-life expressed as a fraction of the history span (<c>span / Value</c>),
    /// giving a scale-free retained shape.
    /// </summary>
    public sealed record HalfLivesAcrossSpan(double Value) : RetentionPolicy;

    /// <summary>The default when neither CLI option is given: <c>span / 4</c>.</summary>
    public const double DefaultHalfLivesAcrossSpan = 4.0;

    /// <summary>
    /// Resolves the two mutually-exclusive command-line half-life options into a single
    /// policy. Shared by both CLIs so they accept the same inputs and report the same errors.
    /// </summary>
    /// <param name="halfLife">The raw <c>--half-life</c> duration string, if given.</param>
    /// <param name="halfLives">The <c>--half-lives</c> value, if given.</param>
    /// <exception cref="RetentionPolicyException">Both options given, invalid duration, or non-positive half-lives.</exception>
    public static RetentionPolicy Resolve(string? halfLife, double? halfLives)
    {
        if (halfLife is not null && halfLives is not null)
            throw new RetentionPolicyException(RetentionPolicyError.ConflictingHalfLifeOptions);
        if (halfLife is not null)
        {
            var seconds = DurationParser.Seconds(halfLife)
                ?? throw new RetentionPolicyException(RetentionPolicyError.InvalidDuration, halfLife);
            return new AbsoluteHalfLife(seconds);
        }
        var k = halfLives ?? DefaultHalfLivesAcrossSpan;
        if (!(k > 0 && double.IsFinite(k)))
            throw new RetentionPolicyException(RetentionPolicyError.NonPositiveHalfLives);
        return new HalfLivesAcrossSpan(k);
    }

    /// <summary>
    /// Like <see cref="Resolve(string?, double?)"/>, but takes <c>--half-lives</c> as raw
    /// text and parses it culture-invariantly, so the CLIs behave the same in every locale.
    /// </summary>
    public static RetentionPolicy Resolve(string? halfLife, string? halfLives)
    {
        double? k = null;
        if (halfLives is not null)
        {
            if (!double.TryParse(halfLives.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                throw new RetentionPolicyException(RetentionPolicyError.InvalidHalfLives, halfLives);
            k = parsed;
        }
        return Resolve(halfLife, k);
    }
}

/// <summary>The reasons <see cref="RetentionPolicy.Resolve(string?, double?)"/> can reject its inputs.</summary>
public enum RetentionPolicyError
{
    ConflictingHalfLifeOptions,
    InvalidDuration,
    InvalidHalfLives,
    NonPositiveHalfLives,
}

/// <summary>A rejected half-life option, with a ready-to-display message.</summary>
public sealed class RetentionPolicyException(RetentionPolicyError error, string? value = null)
    : Exception(FormatMessage(error, value))
{
    public RetentionPolicyError Error { get; } = error;
    public string? Value { get; } = value;

    private static string FormatMessage(RetentionPolicyError error, string? value) => error switch
    {
        RetentionPolicyError.ConflictingHalfLifeOptions => "Cannot specify both --half-life and --half-lives",
        RetentionPolicyError.InvalidDuration =>
            $"Invalid --half-life value '{value}'. Use e.g. 30d, 12h, 90m, 2w, or a number of seconds.",
        RetentionPolicyError.InvalidHalfLives => $"Invalid --half-lives value '{value}'. Expected a positive number.",
        RetentionPolicyError.NonPositiveHalfLives => "--half-lives must be positive and finite",
        _ => error.ToString(),
    };
}
