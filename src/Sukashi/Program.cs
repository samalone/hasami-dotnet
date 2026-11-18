using System.CommandLine;
using System.Globalization;
using System.Runtime.Versioning;
using Hasami;
using Hasami.Cli;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string Discussion = """
        Sukashi means "thinning" or "letting light through" in Japanese gardening. This tool prunes files
        and directories using the Hasami sukashi algorithm, moving unwanted items to the Windows Recycle
        Bin for safety.

        The algorithm retains more recent backups and progressively fewer older ones, fitting the retained
        set to an exponential-decay curve so that gaps between retained backups grow with age. It works with
        backups taken on irregular schedules and is deterministic.

        Use --half-life to set an absolute half-life (retention density halves every <duration> of age, e.g.
        --half-life 30d). Use --half-lives to set a half-life relative to the history span (--half-lives 4
        means span/4), which keeps the same shape at any scale. The two are mutually exclusive; the default
        is --half-lives 4.

        By default, processes all non-hidden files and directories. Use --files-only or --directories-only
        to restrict the type of items processed. Use --include-hidden to process hidden items (those with the
        Hidden or System attribute, or whose name starts with '.').

        Use --dry-run to see what would happen without actually moving anything to the Recycle Bin.
        Use --force-delete to delete items permanently instead (required on network shares and removable
        drives, which have no Recycle Bin).
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        var directoryArg = new Argument<string>("backup-directory") { Description = "The directory containing items to prune" };
        var retain = CliSupport.RetainOption();
        var halfLife = CliSupport.HalfLifeOption();
        var halfLives = CliSupport.HalfLivesOption();
        var verbose = new Option<bool>("--verbose", "-v") { Description = "Show verbose output with algorithm details" };
        var sortByDate = new Option<bool>("--sort-by-date") { Description = "Sort output by creation date instead of name" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would be done without actually moving anything to the Recycle Bin" };
        var includeHidden = new Option<bool>("--include-hidden") { Description = "Include hidden items (Hidden/System attribute, or name starting with '.')" };
        var filesOnly = new Option<bool>("--files-only") { Description = "Process only files (exclude directories)" };
        var directoriesOnly = new Option<bool>("--directories-only") { Description = "Process only directories (exclude files)" };
        var forceDelete = new Option<bool>("--force-delete") { Description = "Delete permanently instead of moving to the Recycle Bin (needed on network shares)" };

        var root = new RootCommand(
            $"Prune backup files and directories using the Hasami {CliSupport.SukashiName} algorithm - " +
            "letting important items shine through.\n\n" + Discussion)
        {
            directoryArg, retain, halfLife, halfLives, verbose, sortByDate, dryRun,
            includeHidden, filesOnly, directoriesOnly, forceDelete,
        };

        root.SetAction(parse => new Sukashi(
            Directory: parse.GetValue(directoryArg)!,
            Retain: parse.GetValue(retain),
            HalfLife: parse.GetValue(halfLife),
            HalfLives: parse.GetValue(halfLives),
            Verbose: parse.GetValue(verbose),
            SortByDate: parse.GetValue(sortByDate),
            DryRun: parse.GetValue(dryRun),
            IncludeHidden: parse.GetValue(includeHidden),
            FilesOnly: parse.GetValue(filesOnly),
            DirectoriesOnly: parse.GetValue(directoriesOnly),
            ForceDelete: parse.GetValue(forceDelete)).Run());

        return CliSupport.Run(root, args);
    }
}

[SupportedOSPlatform("windows")]
internal sealed record Sukashi(
    string Directory,
    int Retain,
    string? HalfLife,
    string? HalfLives,
    bool Verbose,
    bool SortByDate,
    bool DryRun,
    bool IncludeHidden,
    bool FilesOnly,
    bool DirectoriesOnly,
    bool ForceDelete)
{
    private sealed record Item(string Name, string FullPath, bool IsDirectory, DateTime Created, TimeCode TimeCode);

    private string ItemType => FilesOnly ? "files" : DirectoriesOnly ? "directories" : "items";

    public int Run()
    {
        if (FilesOnly && DirectoriesOnly)
            throw new UsageException("Cannot specify both --files-only and --directories-only");
        CliSupport.ValidateRetain(Retain);

        // Shared with sukashi-plan so both CLIs accept the same inputs and errors.
        var policy = RetentionPolicy.Resolve(HalfLife, HalfLives);

        if (!System.IO.Directory.Exists(Directory))
            throw new UsageException($"Directory '{Directory}' does not exist");

        var items = GetBackupItems();
        if (items.Count == 0)
        {
            Console.WriteLine($"No backup {ItemType} found in '{Directory}'");
            return ExitCodes.Success;
        }

        if (Verbose)
        {
            Console.WriteLine($"Found {items.Count} backup {ItemType}:");
            foreach (var item in items) Console.WriteLine($"  {Describe(item)}");
            Console.WriteLine();
        }

        var tree = new BackupTree(items.Select(i => i.TimeCode));
        var retainedSet = tree.RetainedBackups(policy, Retain).ToHashSet();

        if (Verbose)
        {
            var policyDescription = policy switch
            {
                RetentionPolicy.AbsoluteHalfLife a => $"half-life {FormatDuration(a.Seconds)}",
                RetentionPolicy.HalfLivesAcrossSpan r => $"{r.Value.ToString(CultureInfo.InvariantCulture)} half-lives across span",
                _ => policy.ToString(),
            };
            Console.WriteLine($"Pruning algorithm ({policyDescription}, retain {Retain}):");
            Console.WriteLine();
        }

        // Items whose creation times fall in the same second share a TimeCode and are
        // retained or removed together, as in the original tool.
        var retainedItems = items.Where(i => retainedSet.Contains(i.TimeCode)).ToList();
        var deletedItems = items.Where(i => !retainedSet.Contains(i.TimeCode)).ToList();

        Comparison<Item> order = SortByDate
            ? (a, b) => a.Created.CompareTo(b.Created)
            : (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        retainedItems.Sort(order);
        deletedItems.Sort(order);

        // Check before printing the plan, so a real run on a volume without a Recycle Bin
        // fails without touching anything.
        string? recycleProblem = ForceDelete || deletedItems.Count == 0 ? null : RecycleBin.UnavailableReason(Directory);
        if (recycleProblem is not null && !DryRun)
            throw new UsageException($"Cannot move items to the Recycle Bin: {recycleProblem}. Use --force-delete to delete them permanently.");

        Console.WriteLine($"{(DryRun ? "WOULD RETAIN" : "RETAINING")} ({retainedItems.Count} {ItemType}):");
        foreach (var item in retainedItems) Console.WriteLine($"  {Describe(item)}");

        var deleteAction = ForceDelete ? "DELETE" : "MOVE TO RECYCLE BIN";
        Console.WriteLine();
        Console.WriteLine($"{(DryRun ? $"WOULD {deleteAction}" : deleteAction)} ({deletedItems.Count} {ItemType}):");
        foreach (var item in deletedItems) Console.WriteLine($"  {Describe(item)}");

        var pastTense = ForceDelete ? "deleted" : "moved to the Recycle Bin";
        if (!DryRun && deletedItems.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Proceeding with {CliSupport.SukashiName} - " +
                              $"{(ForceDelete ? "deleting" : "moving to the Recycle Bin")} {deletedItems.Count} {ItemType}...");

            int successCount = 0, failureCount = 0;
            foreach (var item in deletedItems)
            {
                try
                {
                    if (ForceDelete) DeletePermanently(item);
                    else RecycleBin.Recycle(item.FullPath);
                    successCount++;
                    if (Verbose) Console.WriteLine($"  {(ForceDelete ? "Deleted" : "Moved to the Recycle Bin")}: {item.Name}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine($"  Failed to {(ForceDelete ? "delete" : "move to the Recycle Bin")} {item.Name}: {e.Message}");
                    failureCount++;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Sukashi completed: {successCount} {pastTense}, {failureCount} failed");
            if (failureCount > 0) Console.WriteLine("Note: Failed items remain in the backup directory");
        }
        else if (DryRun)
        {
            Console.WriteLine();
            Console.WriteLine($"Dry run completed - no {ItemType} were {pastTense}");
            if (recycleProblem is not null)
                Console.WriteLine($"Warning: a real run would fail: {recycleProblem}. Use --force-delete to delete permanently.");
        }

        Console.WriteLine();
        Console.WriteLine($"Summary: {retainedItems.Count} retained, {deletedItems.Count} {(DryRun ? $"would be {pastTense}" : pastTense)}");
        return ExitCodes.Success;
    }

    private List<Item> GetBackupItems()
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,          // hidden-item filtering is done below
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
        };
        var items = new List<Item>();
        foreach (var info in new DirectoryInfo(Directory).EnumerateFileSystemInfos("*", options))
        {
            bool hidden = info.Name.StartsWith('.')
                          || (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
            if (hidden && !IncludeHidden) continue;

            bool isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
            if (FilesOnly && isDirectory) continue;
            if (DirectoriesOnly && !isDirectory) continue;

            DateTime created;
            try
            {
                created = info.CreationTimeUtc;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (Verbose) Console.WriteLine($"Warning: Could not get creation date for '{info.Name}'");
                continue;
            }
            items.Add(new Item(info.Name, info.FullName, isDirectory, created, TimeCode.FromDateTime(created)));
        }
        return items;
    }

    /// <summary>
    /// Deletes an item permanently. Read-only attributes (common in copied backups) are
    /// cleared on demand, since .NET refuses to delete read-only files. Directory links
    /// (junctions, symlinks) are removed without touching their targets.
    /// </summary>
    private static void DeletePermanently(Item item)
    {
        if (!item.IsDirectory)
        {
            File.SetAttributes(item.FullPath, File.GetAttributes(item.FullPath) & ~FileAttributes.ReadOnly);
            File.Delete(item.FullPath);
            return;
        }
        bool isLink = (File.GetAttributes(item.FullPath) & FileAttributes.ReparsePoint) != 0;
        try
        {
            System.IO.Directory.Delete(item.FullPath, recursive: !isLink);
        }
        catch (UnauthorizedAccessException) when (!isLink)
        {
            ClearReadOnly(new DirectoryInfo(item.FullPath));
            System.IO.Directory.Delete(item.FullPath, recursive: true);
        }
    }

    private static void ClearReadOnly(DirectoryInfo dir)
    {
        // Enumeration does not descend into reparse points, so link targets are untouched.
        var all = new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true };
        foreach (var info in dir.EnumerateFileSystemInfos("*", all).Prepend(dir))
            if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                info.Attributes &= ~FileAttributes.ReadOnly;
    }

    private static string Describe(Item item) =>
        $"{item.Name} (created: {item.Created.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)})";

    private static string FormatDuration(double seconds)
    {
        (string Name, double Size)[] units = [("w", 604800), ("d", 86400), ("h", 3600), ("m", 60), ("s", 1)];
        foreach (var (name, size) in units)
        {
            if (seconds < size) continue;
            var value = seconds / size;
            return value == Math.Round(value) && value < long.MaxValue
                ? $"{(long)value}{name}"
                : $"{value.ToString("G6", CultureInfo.InvariantCulture)}{name}";
        }
        return $"{seconds.ToString("G6", CultureInfo.InvariantCulture)}s";
    }
}
