using System.CommandLine;
using System.Text;

namespace Hasami.Cli;

/// <summary>sysexits(3)-style exit codes, matching the original Swift tools.</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 64;
    public const int DataError = 65;
}

/// <summary>An invalid combination of command-line arguments; exits with <see cref="ExitCodes.Usage"/>.</summary>
internal sealed class UsageException(string message) : Exception(message);

internal static class CliSupport
{
    /// <summary>
    /// "sukashi (透かし)" when the console can display Japanese, otherwise plain "sukashi".
    /// A legacy console code page (e.g. 437) would render the kanji as "???".
    /// </summary>
    public static string SukashiName =>
        Console.OutputEncoding.CodePage == Encoding.UTF8.CodePage ? "sukashi (透かし)" : "sukashi";

    /// <summary>
    /// Parses <paramref name="args"/>, reports parse errors as usage errors (exit 64),
    /// and otherwise runs the command's action. Usage errors thrown by the action are
    /// reported the same way.
    /// </summary>
    public static int Run(RootCommand root, string[] args)
    {
        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
                Console.Error.WriteLine($"Error: {error.Message}");
            Console.Error.WriteLine($"Run '{root.Name} --help' for usage.");
            return ExitCodes.Usage;
        }
        try
        {
            return parseResult.Invoke(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
        }
        catch (Exception e) when (e is UsageException or RetentionPolicyException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            Console.Error.WriteLine($"Run '{root.Name} --help' for usage.");
            return ExitCodes.Usage;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return ExitCodes.Failure;
        }
    }

    public static Option<int> RetainOption() => new("--retain", "-r")
    {
        Description = "Number of items to retain",
        DefaultValueFactory = _ => 10,
        HelpName = "number",
    };

    public static Option<string?> HalfLifeOption() => new("--half-life")
    {
        Description = "Absolute half-life: retention density halves every <duration> of age. Accepts s/m/h/d/w " +
                      "suffixes (e.g. 30d); a bare number is seconds. Mutually exclusive with --half-lives.",
        HelpName = "duration",
    };

    public static Option<string?> HalfLivesOption() => new("--half-lives")
    {
        Description = "Relative half-life: how many half-lives span the full history (e.g. 4 means span/4). " +
                      "Scale-free. Mutually exclusive with --half-life. Default: 4.",
        HelpName = "number",
    };

    public static void ValidateRetain(int retain)
    {
        if (retain < 0) throw new UsageException("--retain must be non-negative");
    }
}
