using System.CommandLine;
using System.Text;
using Hasami;
using Hasami.Cli;

const string description = """
    Emit a keep or prune plan for timestamped items read from stdin.

    Reads lines of <unix-timestamp><TAB><key> from stdin and writes a subset of
    keys to stdout, one per line. Composes in pipelines to prune any collection
    keyed by timestamp - cloud object storage, database rows, or anything else -
    without touching the filesystem.

    --mode keep emits the keys the Hasami algorithm would retain; --mode prune
    emits the keys it would discard. There is no default; the caller must pick
    one, so a retain list cannot accidentally be piped into a delete command.

    Use --half-life for an absolute half-life (e.g. 30d; retention density halves
    every <duration> of age) or --half-lives for a half-life relative to the
    history span (e.g. 4 means span/4, which is scale-free). The two are mutually
    exclusive; the default is --half-lives 4.

    Keys are treated as opaque strings and emitted in the order they appeared
    on stdin. Items that share a timestamp collapse to a single representative
    (first-in-input wins); the non-representative duplicates always land in
    the prune list. The planner consults no wall-clock time, so the output is
    a pure function of the input set.

    Redirected input and output are UTF-8 (a byte-order mark on input is
    honored), and output lines end in LF.
    """;

var mode = new Option<string>("--mode")
{
    Description = "Which keys to emit: 'keep' or 'prune'. Required.",
    Required = true,
    HelpName = "keep|prune",
};
mode.AcceptOnlyFromAmong("keep", "prune");
var retain = CliSupport.RetainOption();
var halfLife = CliSupport.HalfLifeOption();
var halfLives = CliSupport.HalfLivesOption();

var root = new RootCommand(description) { mode, retain, halfLife, halfLives };

root.SetAction(parse =>
{
    var planMode = parse.GetValue(mode) == "keep" ? PlanMode.Keep : PlanMode.Prune;
    var keepCount = parse.GetValue(retain);
    CliSupport.ValidateRetain(keepCount);
    // Resolved before stdin is read, so bad options are rejected without consuming input.
    var policy = RetentionPolicy.Resolve(parse.GetValue(halfLife), parse.GetValue(halfLives));

    var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    List<PlanItem> items;
    using (TextReader stdin = Console.IsInputRedirected
               ? new StreamReader(Console.OpenStandardInput(), utf8, detectEncodingFromByteOrderMarks: true)
               : Console.In)
    {
        try
        {
            items = Planner.Parse(ReadLines(stdin));
        }
        catch (PlanParseException e)
        {
            Console.Error.WriteLine($"sukashi-plan: {e.Message}");
            return ExitCodes.DataError;
        }
    }

    var result = Planner.Plan(items, policy, keepCount);

    // LF line endings so the output also works with tools like Git Bash's xargs,
    // which would otherwise keep a stray '\r' on every key.
    TextWriter stdout = Console.IsOutputRedirected
        ? new StreamWriter(Console.OpenStandardOutput(), utf8)
        : Console.Out;
    foreach (var key in result.Output(planMode))
    {
        stdout.Write(key);
        stdout.Write('\n');
    }
    stdout.Flush();
    return ExitCodes.Success;
});

return CliSupport.Run(root, args);

static IEnumerable<string> ReadLines(TextReader reader)
{
    while (reader.ReadLine() is { } line) yield return line;
}
