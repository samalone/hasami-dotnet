# Hasami (.NET / Windows port)

C# port of the Swift project [samalone/Hasami](https://github.com/samalone/Hasami), a
backup pruning utility. The upstream Swift repo is the reference for algorithm behavior;
keep the two in agreement.

## Layout

- `src/Hasami` — library (net10.0, cross-platform). `TimeCode`, `BackupTree` (immutable,
  over `ImmutableSortedSet`; the pruning algorithm lives in `Thinned`), `RetentionPolicy`
  (+ `Resolve`, shared by both CLIs), `DurationParser`, and `Planner` (the sukashi-plan logic).
- `src/Sukashi` — `sukashi.exe` (net10.0-windows). Filesystem pruning. `RecycleBin.cs`
  P/Invokes `SHFileOperationW`.
- `src/SukashiPlan` — `sukashi-plan.exe` (net10.0). stdin/stdout planner.
- `src/Shared/Cli.cs` — linked into both executables: exit codes, the parse/error wrapper,
  and the shared `--retain` / `--half-life` / `--half-lives` options.
- `tests/Hasami.Tests` — xUnit. Ported from the Swift tests, including the golden
  characterization test (`CharacterizationEvenlySpaced`) and the long-term simulations.

## Build & test

```powershell
dotnet build
dotnet test
.\scripts\install.ps1 [-Prefix <dir>] [-AddToPath]
```

Warnings are errors (`Directory.Build.props`). The version is set there too.

## Design notes

- Ages are measured from the newest timestamp, never the wall clock.
- The heap orders by `(cost, index)`, which matches Swift's tie-break. Changing the
  ordering, or replacing `double.Exp2`, can change results; the golden test catches that.
- CLI number parsing is culture-invariant on purpose (`RetentionPolicy.Resolve(string?, string?)`).
- `sukashi` refuses to recycle anything on a drive that isn't a fixed local drive, because
  Windows would delete it permanently without asking. Don't weaken that check.
- Uses System.CommandLine 2.0.x. Parse errors are mapped to exit code 64 in `CliSupport.Run`.
