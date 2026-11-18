# Hasami for Windows

**Intelligent filesystem pruning with Japanese precision** — a .NET / C# port of
[samalone/Hasami](https://github.com/samalone/Hasami) for Windows.

Hasami (鋏, "scissors") implements the *sukashi* (透かし, "thinning" or "letting light
through") algorithm. Like pruning a bonsai, it thins a collection of backups so the
retained set fits an exponential-decay curve: recent backups are kept densely, and the
spacing between retained backups grows with age.

Two command-line tools share the `Hasami` library:

- **`sukashi`** prunes a local directory. It reads each item's creation date and moves
  the unwanted items to the **Windows Recycle Bin** (or deletes them permanently with
  `--force-delete`).
- **`sukashi-plan`** reads `<unix-timestamp><TAB><key>` lines on stdin and writes the
  keep-list or prune-list to stdout. It never touches the filesystem, so it fits into
  pipelines that prune cloud storage, database rows, or anything else keyed by timestamp.

The algorithm is a line-for-line port: given the same timestamps and options, both tools
choose exactly the same items as the Swift originals.

## Requirements

- 64-bit Windows 10 or 11 (x64 or Arm64)
- [.NET 10](https://dotnet.microsoft.com/download) — the SDK to build, the runtime to run

`sukashi-plan` and the `Hasami` library are cross-platform; only `sukashi` is Windows-only.

## Install

```powershell
.\scripts\install.ps1
```

This runs the tests in Release and, only if they pass, publishes `sukashi.exe` and
`sukashi-plan.exe` to `~\.local\bin` (change it with `-Prefix`). If that directory isn't
on your `PATH` yet, add `-AddToPath` to add it to your user `PATH`.

To run from source instead:

```powershell
dotnet build
dotnet test
dotnet run --project src\Sukashi -- --help
```

## Usage

```powershell
# Prune a backup directory, keeping 10 items (the default)
sukashi D:\Backups

# See what would happen first
sukashi D:\Backups --dry-run --verbose

# Keep 20, with an absolute 2-week half-life
sukashi D:\Backups --retain 20 --half-life 2w

# Only consider backup directories, keep 15
sukashi D:\Backups --directories-only --retain 15

# Network share or removable drive: there is no Recycle Bin, so delete permanently
sukashi \\nas\backups --force-delete --retain 20
```

### `sukashi` options

| Option | Description | Default |
|--------|-------------|---------|
| `-r, --retain <number>` | Number of items to retain | 10 |
| `--half-life <duration>` | Absolute half-life; density halves every `<duration>` of age (`s`/`m`/`h`/`d`/`w` suffixes, e.g. `30d`; a bare number is seconds) | — |
| `--half-lives <number>` | Relative half-life: how many half-lives span the history (`span/N`); scale-free | 4 |
| `--dry-run` | Show what would happen without changing anything | off |
| `--files-only` / `--directories-only` | Only consider files, or only directories | both |
| `--include-hidden` | Include hidden items | off |
| `-v, --verbose` | Show every item found and the algorithm parameters | off |
| `--sort-by-date` | Sort output by creation date instead of name | by name |
| `--force-delete` | Delete permanently instead of using the Recycle Bin | off |

`--half-life` and `--half-lives` are mutually exclusive; with neither, the default is
`--half-lives 4`. Numbers are always written with `.` as the decimal point, whatever the
system locale.

### Differences from the macOS version

- **Recycle Bin instead of Trash.** Windows only has a Recycle Bin on fixed local drives.
  On a network share or removable drive, "recycling" would silently delete for good, so
  `sukashi` refuses to start and asks for `--force-delete` instead. (`--dry-run` warns
  about this rather than failing.) If Windows still can't recycle an item on a fixed drive
  (because the Recycle Bin is turned off for that drive, or the item is bigger than its
  maximum size), it **asks you** before deleting permanently. If you schedule unattended
  runs on such a drive, use `--force-delete` so it never stops to ask.
- **Hidden items** are those with the Hidden or System attribute, plus anything whose name
  starts with `.` (the macOS rule).
- **Read-only files** are deleted by `--force-delete` (the read-only attribute is cleared
  first). Junctions and symbolic links are removed without touching their targets.
- **Creation dates** come from NTFS. Note that copying a file in Windows gives the copy a
  new creation date. If your backup tool copies files rather than creating them, check
  `--dry-run --verbose --sort-by-date` to make sure the dates are the ones you expect.
- Dates are printed in your local time zone and regional format.

## Pipeline pruning with `sukashi-plan`

```powershell
# Prune dated S3 prefixes, keeping 30, via rclone (PowerShell)
rclone lsjson ess:ess-backups/snapshots/ --dirs-only | ConvertFrom-Json |
    ForEach-Object { "{0}`t{1}" -f ([DateTimeOffset]$_.ModTime).ToUnixTimeSeconds(), $_.Name } |
    sukashi-plan --retain 30 --mode prune |
    ForEach-Object { rclone purge "ess:ess-backups/snapshots/$_" }
```

| Option | Description | Default |
|--------|-------------|---------|
| `--mode <keep\|prune>` | Which keys to output. **Required**; there is no default, so a keep-list can't be piped into a delete command by mistake | — |
| `-r, --retain <number>` | Number of items to retain | 10 |
| `--half-life <duration>` / `--half-lives <number>` | As for `sukashi` | `--half-lives 4` |

Input is one `<unix-timestamp><TAB><key>` per line. Only the first tab separates the
timestamp from the key, empty lines are ignored, and CRLF line endings are fine. Keys come
out in input order. Items that share a timestamp collapse to the first one in the input;
the others always go in the prune list. Redirected input and output are UTF-8 (a
byte-order mark on input is honored), and output lines end in LF so the output also works
with Git Bash tools like `xargs`.

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | Success (including empty stdin for `sukashi-plan`) |
| 1 | Unexpected error (for example, the directory can't be read) |
| 64 | Usage error: invalid or conflicting options, missing directory, or no Recycle Bin without `--force-delete` |
| 65 | `sukashi-plan`: malformed input line (the line number goes to stderr) |

If some items fail to delete, `sukashi` reports each failure, leaves those items in place,
and still exits 0, as the original does.

## How it works

Each backup's age is measured from the **newest** backup (never the wall clock), then
warped through the retention curve's CDF, `u(t) = 1 − 2^(−t/H)`, into a coordinate where
the target density is uniform. The tool then repeatedly removes the most redundant backup
(the one whose neighbors are closest in `u`) until exactly `--retain` remain, always
keeping the newest and the oldest. Re-running on an unchanged set changes nothing.

180 daily backups, half-life = span/4, keep 20 (ages in days):

```
Kept:  0, 3, 7, 11, 15, 19, 23, 27, 31, 35, 43, 51, 59, 67, 75, 83, 99, 115, 147, 179
Gaps:    3, 4, 4, 4, 4, 4, 4, 4, 4, 8, 8, 8, 8, 8, 8, 16, 16, 32, 32
```

See [docs/backup-pruning-algorithm.md](docs/backup-pruning-algorithm.md) for the full
specification.

## License

Licensed under the MIT License; see [LICENSE](LICENSE).
