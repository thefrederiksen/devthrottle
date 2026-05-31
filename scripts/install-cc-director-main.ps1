#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and installs the MAIN CC Director build (the daily-driver, no slot number).

.DESCRIPTION
    The local_builds directory holds five builds:
        cc-director.exe    - MAIN (this script): the daily-driver, gets a Start Menu
                             entry and can be pinned to the taskbar.
        cc-director1.exe   - development slot 1
        cc-director2.exe   - development slot 2
        cc-director3.exe   - development slot 3
        cc-director4.exe   - development slot 4

    This script:
      1. Builds the main exe via scripts/local-build-avalonia.ps1 (no -Slot), which
         produces local_builds/cc-director.exe.
      2. Creates a Start Menu shortcut "CC Director" pointing at that exe, with its
         WorkingDirectory set to local_builds (Avalonia's first-run resource
         resolution needs a real working directory or it can exit -1).
      3. Tells you the one click needed to pin it to the taskbar (Windows 11 has no
         supported API to pin programmatically, so we do not fake it).

.PARAMETER SelfContained
    Build the main exe as self-contained (no .NET runtime needed on the machine).

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER SkipBuild
    Skip the build step and only (re)create the Start Menu shortcut for an exe that
    already exists at local_builds/cc-director.exe.

.PARAMETER Launch
    Launch the main exe after install.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-cc-director-main.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-cc-director-main.ps1 -Launch
#>
param(
    [switch]$SelfContained,
    [string]$Configuration = "Release",
    [switch]$SkipBuild,
    [switch]$Launch
)

$ErrorActionPreference = "Stop"

$repoRoot   = Split-Path -Parent $PSScriptRoot
$buildScript = Join-Path $repoRoot "scripts\local-build-avalonia.ps1"
$localBuilds = Join-Path $repoRoot "local_builds"
$exePath     = Join-Path $localBuilds "cc-director.exe"

# ----- Step 1: build the main exe (no slot -> cc-director.exe) -----
if (-not $SkipBuild) {
    Write-Host "[install-cc-director-main] Building main CC Director (cc-director.exe)..." -ForegroundColor Cyan
    if (-not (Test-Path $buildScript)) { throw "Build script not found: $buildScript" }

    # A running main exe locks the file; the build's copy step would fail. Detect a
    # process running from THIS exact path and stop early with a clear message. Never
    # touch the user's other Directors (slots or different paths).
    $running = Get-Process -Name "cc-director" -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ($_.Path -ieq $exePath) }
    if ($running) {
        $pids = ($running | ForEach-Object { $_.Id }) -join ", "
        throw "cc-director.exe (the main build) is running (PID $pids). Close it before rebuilding the main build, then re-run this script."
    }

    $buildArgs = @{ Configuration = $Configuration }
    if ($SelfContained) { $buildArgs["SelfContained"] = $true }
    & $buildScript @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Main build failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path $exePath)) {
    throw "Main exe not found at $exePath. Build it first (run without -SkipBuild)."
}

# ----- Step 2: Start Menu shortcut -----
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcutPath = Join-Path $startMenuDir "CC Director.lnk"

Write-Host "[install-cc-director-main] Creating Start Menu shortcut..." -ForegroundColor Cyan
Write-Host "[install-cc-director-main]   target  : $exePath"
Write-Host "[install-cc-director-main]   shortcut: $shortcutPath"

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcutPath)
$lnk.TargetPath       = $exePath
$lnk.WorkingDirectory = $localBuilds
$lnk.Description       = "CC Director"
$lnk.IconLocation      = "$exePath,0"
$lnk.Save()
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null

if (-not (Test-Path $shortcutPath)) { throw "Failed to create Start Menu shortcut at $shortcutPath" }

# ----- Step 3: taskbar -----
# Windows 11 removed the supported "Pin to taskbar" shell verb. There is no reliable,
# documented API to pin programmatically, so we do not fake it (faking would silently
# do nothing on most machines). The Start Menu entry above is the reliable artifact;
# pinning to the taskbar is one manual click.
Write-Host ""
Write-Host "[install-cc-director-main] Installed. 'CC Director' is now in the Start Menu." -ForegroundColor Green
Write-Host ""
Write-Host "  To pin it to the taskbar (one click, Windows 11 has no API for this):" -ForegroundColor Yellow
Write-Host "    1. Press Start and type: CC Director"
Write-Host "    2. Right-click 'CC Director' -> Pin to taskbar"
Write-Host ""

if ($Launch) {
    Write-Host "[install-cc-director-main] Launching main build..." -ForegroundColor Cyan
    Start-Process -FilePath $exePath -WorkingDirectory $localBuilds
}
