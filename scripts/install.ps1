<#
.SYNOPSIS
    Build, test, and install the Hasami command-line tools (sukashi, sukashi-plan).

.DESCRIPTION
    Runs the full test suite in Release; only if it passes does it publish both tools as
    single-file executables into -Prefix (default: %LOCALAPPDATA%\Programs\Hasami).
    The executables are framework-dependent, so they need the .NET 10 runtime installed.

.PARAMETER Prefix
    Installation directory.

.PARAMETER AddToPath
    Add the installation directory to the user's PATH if it is not already there.
#>
[CmdletBinding()]
param(
    [string]$Prefix = (Join-Path $env:LOCALAPPDATA 'Programs\Hasami'),
    [switch]$AddToPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $rid = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'Arm64' { 'win-arm64' }
        'X64'   { 'win-x64' }
        default { throw "Unsupported architecture: $_ (64-bit Windows is required)" }
    }

    Write-Host '==> dotnet test -c Release'
    dotnet test -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; nothing was installed.' }

    New-Item -ItemType Directory -Force -Path $Prefix | Out-Null
    foreach ($project in 'src\Sukashi', 'src\SukashiPlan') {
        Write-Host "==> publishing $project ($rid) to $Prefix"
        dotnet publish $project -c Release -r $rid --self-contained false `
            -p:PublishSingleFile=true -p:DebugType=none -o $Prefix
        if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
    }

    if ($AddToPath) {
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        $entries = @($userPath -split ';' | Where-Object { $_ })
        if ($entries -notcontains $Prefix) {
            [Environment]::SetEnvironmentVariable('Path', (($entries + $Prefix) -join ';'), 'User')
            Write-Host "Added $Prefix to the user PATH (open a new terminal to pick it up)."
        }
    }

    Write-Host "Installed sukashi.exe and sukashi-plan.exe -> $Prefix"
}
finally {
    Pop-Location
}
