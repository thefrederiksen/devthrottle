<#
.SYNOPSIS
    The end-to-end proof of the Director installing the launcher's staged update (issue #2719,
    Phase 0), on an ISOLATED storage root, with REAL launcher binaries and a REAL running launcher.

.DESCRIPTION
    Phase 0's unit tests drive the whole pass through fakes for the process list, the stop and the
    start. That is right for the decisions and says nothing about the three production delegates that
    touch the machine: DefaultStopProcess, DefaultStartLauncher (including the CC_DIRECTOR_ROOT scrub
    and the --managed argument), and the witness reading a genuinely new process. Until this script
    ran, none of those had ever executed against a real launcher.

    WHY AN ISOLATED ROOT AND NOT THIS MACHINE'S LAUNCHER. On Windows the installed launcher is the
    parent of the Director, and on this account that Director carries the live fleet. Swapping it
    would put the one unproven invariant - a launcher swap must not orphan its Director - directly
    underneath every running session. The owner's standing instruction is to ask before anything stops
    or replaces the running launcher, and that question is unanswered.

    So the rig builds its own world. A launcher started with CC_DIRECTOR_ROOT pointing elsewhere
    serves THAT root completely: it registers under it, derives its own root key from it, and its
    instance guard refuses to act on a Director that is not its own (established 2026-09-06, recorded
    on issue #2719). The rig asserts that separation rather than assuming it - the driver refuses to
    run against the real root, and checks afterwards that the machine's real launcher did not move.

    WHAT THIS DOES NOT PROVE. The rig's launcher supervises no Director, so a launcher that is a LIVE
    DIRECTOR'S PARENT has still never been swapped. The no-orphan invariant is asserted only through
    a fake, in LauncherUpdateOwnerTests. Say so wherever this run is quoted.

.PARAMETER Root
    The isolated storage root. Must NOT be this machine's real cc-director root; the driver refuses.

.PARAMETER OldVersion
    The version stamped into the build that starts out installed.

.PARAMETER NewVersion
    The version stamped into the build that is staged, and which must end up running and commandable.

.PARAMETER KeepRoot
    Leave the isolated root and its launcher in place after the run, for inspection.

.EXAMPLE
    .\scripts\launcher-swap-proof.ps1
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:LOCALAPPDATA "cc-director-launcher-proof"),
    [string]$OldVersion = "2.0.4",
    [string]$NewVersion = "2.0.99",
    [switch]$KeepRoot
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

function Say([string]$text) { Write-Host "[launcher-swap-proof] $text" }

# ---------------------------------------------------------------------------
# 0. Refuse the real root here as well as in the driver. Two refusals, because
#    this script starts and stops launcher processes before the driver ever runs.
# ---------------------------------------------------------------------------
$realRoot = Join-Path $env:LOCALAPPDATA "cc-director"
if ($Root.TrimEnd('\') -ieq $realRoot.TrimEnd('\')) {
    throw "REFUSING: $Root is this machine's REAL cc-director root. This script stops launchers."
}
$launcherDir = Join-Path $Root "launcher"
$stagedDir   = Join-Path $Root "state\staged"
$setupDir    = Join-Path $Root "config\setup"

# ---------------------------------------------------------------------------
# 1. Two real launcher builds, differing only in the version they stamp. The
#    staged one has to be genuinely NEWER or FindStagedUpdate correctly declines.
# ---------------------------------------------------------------------------
$buildRoot = Join-Path $env:TEMP "launcher-swap-proof-builds"
$oldBuild  = Join-Path $buildRoot "old\cc-launcher.exe"
$newBuild  = Join-Path $buildRoot "new\cc-launcher.exe"

function Publish-Launcher([string]$version, [string]$outDir) {
    if (Test-Path (Join-Path $outDir "cc-launcher.exe")) {
        $stamped = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $outDir "cc-launcher.exe")).ProductVersion
        if ($stamped -and $stamped.StartsWith($version)) { Say "reusing $version at $outDir"; return }
    }
    Say "publishing launcher $version -> $outDir"
    & dotnet publish (Join-Path $repo "src\CcDirector.Launcher\CcDirector.Launcher.csproj") `
        -c Release -f net10.0-windows -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:Version=$version -p:InformationalVersion=$version `
        -o $outDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publishing the launcher at $version failed" }
}

Publish-Launcher $OldVersion (Split-Path -Parent $oldBuild)
Publish-Launcher $NewVersion (Split-Path -Parent $newBuild)

# ---------------------------------------------------------------------------
# 2. Lay out the isolated install: the old build installed, the new one staged,
#    and a manifest that says what is installed.
# ---------------------------------------------------------------------------
function Stop-RigLaunchers([string]$dir) {
    # Only processes running from the RIG's launcher directory. Never a name-wide
    # sweep: this machine's real launcher is also called cc-launcher.
    Get-Process -Name "cc-launcher" -ErrorAction SilentlyContinue | ForEach-Object {
        $path = $null
        try { $path = $_.MainModule.FileName } catch { }
        if ($path -and $path.StartsWith($dir, [System.StringComparison]::OrdinalIgnoreCase)) {
            Say "stopping leftover rig launcher pid $($_.Id)"
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

Stop-RigLaunchers $launcherDir
if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
New-Item -ItemType Directory -Force -Path $launcherDir, $stagedDir, $setupDir | Out-Null

Copy-Item $oldBuild (Join-Path $launcherDir "cc-launcher.exe") -Force
Copy-Item $newBuild (Join-Path $stagedDir  "cc-launcher.exe") -Force
@{ "cc-launcher" = $OldVersion } | ConvertTo-Json | Set-Content -Path (Join-Path $setupDir "installed.json") -Encoding utf8

Say "isolated root laid out at $Root (installed $OldVersion, staged $NewVersion)"

# ---------------------------------------------------------------------------
# 3. Start the rig's launcher, pointed at the isolated root.
#
#    CC_DIRECTOR_ROOT is set DELIBERATELY here - it is what makes the launcher
#    serve the rig's world instead of the machine's. That is the exact opposite
#    of what the Director's own DefaultStartLauncher does, which REMOVES the
#    variable so a launcher it starts cannot inherit a Director's instance home.
#    Both are correct: this script is the installer of the rig's world, and the
#    variable is how a world is named.
# ---------------------------------------------------------------------------
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = (Join-Path $launcherDir "cc-launcher.exe")
$psi.WorkingDirectory = $launcherDir
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
# Windows PowerShell 5.1 runs on .NET Framework, whose ProcessStartInfo has no ArgumentList - the
# single Arguments string is the only form available here.
$psi.Arguments = "--managed"
$psi.EnvironmentVariables["CC_DIRECTOR_ROOT"] = $Root
$rig = [System.Diagnostics.Process]::Start($psi)
Say "started the rig launcher: pid $($rig.Id)"

# It unpacks a single-file binary before it registers anything, so give it real time.
$registration = Join-Path $Root "config\launcher\launcher.json"
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline -and -not (Test-Path $registration)) { Start-Sleep -Milliseconds 500 }
if (-not (Test-Path $registration)) {
    Stop-RigLaunchers $launcherDir
    throw "the rig launcher never registered at $registration"
}
Say "the rig launcher registered: $(Get-Content $registration -Raw)"

# ---------------------------------------------------------------------------
# 4. The proof itself.
# ---------------------------------------------------------------------------
#    THE DRIVER'S OUTPUT IS REDIRECTED TO FILES, AND THAT IS NOT TIDINESS. The swap starts a launcher
#    that is meant to STAY RUNNING, and a child started without redirection inherits the console
#    handles - so the pipeline reading this script's output waits on the launcher, not on the driver,
#    and the script hangs after printing PASS. Observed on 2026-09-06: the first two runs terminated
#    only because the started launcher exited immediately (it was the second-instance failure the rig
#    was there to find), and the first SUCCESSFUL run hung for ten minutes with the proof already
#    passed. Handing the driver file handles instead means the launcher inherits those, and Start-Process
#    -Wait waits only on the driver.
$proofOut = Join-Path $env:TEMP "launcher-swap-proof.out.txt"
$proofErr = Join-Path $env:TEMP "launcher-swap-proof.err.txt"
# Deleted BEFORE the run: a verdict left by a previous run must never be read as this run's result.
$proofVerdict = Join-Path $env:TEMP "launcher-swap-proof.verdict.txt"
if (Test-Path $proofVerdict) { Remove-Item -Force $proofVerdict }
try {
    $driver = Start-Process -FilePath "dotnet" -PassThru -NoNewWindow `
        -RedirectStandardOutput $proofOut -RedirectStandardError $proofErr `
        -ArgumentList @(
            "run", "--project",
            (Join-Path $repo "scripts\launcher-swap-proof\LauncherSwapProof.csproj"),
            "--no-build", "--", $Root, $NewVersion)

    # POLL, DO NOT WAIT ON THE STREAMS. -Wait (and WaitForExit) on a process with redirected output
    # waits for those streams to CLOSE, and the launcher the swap started inherited them - so the
    # script blocked on a launcher that is supposed to keep running, with the proof already passed.
    # HasExited asks about the process and nothing else.
    while (-not $driver.HasExited) { Start-Sleep -Milliseconds 500 }
    if (Test-Path $proofOut) { Get-Content $proofOut | ForEach-Object { Write-Host $_ } }
    if (Test-Path $proofErr) { Get-Content $proofErr | ForEach-Object { Write-Host $_ } }

    # Refresh before reading: a process object obtained from Start-Process -PassThru without -Wait
    # caches its state, and ExitCode came back EMPTY without this - which the check below then read as
    # "not zero" and reported as a FAILED proof on a run that had passed. An unreadable exit code is
    # undecidable, and undecidable is not a pass either, so it is raised rather than defaulted.
    # THE VERDICT IS THE FILE THE DRIVER WROTE, not the exit code. Windows PowerShell's
    # Start-Process -PassThru without -Wait does not retain the process handle, so ExitCode reads back
    # EMPTY - and this script duly reported a FAILED proof on a run that had passed. -Wait is not
    # available either: with redirected output it waits for the streams to close, and the launcher this
    # proof deliberately leaves running has inherited them.
    #
    # The check is a PRESENCE: the file must exist and must say PASS. A missing file is a broken
    # instrument, never a clean run.
    if (-not (Test-Path $proofVerdict)) {
        throw "the proof driver wrote no verdict at $proofVerdict, so the result is UNKNOWN - which is not a pass."
    }
    $verdict = (Get-Content $proofVerdict -Raw).Trim()
    $proofExit = if ($verdict -eq "PASS") { 0 } else { 1 }
    Say "verdict: $verdict"
}
finally {
    if (-not $KeepRoot) {
        Stop-RigLaunchers $launcherDir
        Start-Sleep -Seconds 2
        if (Test-Path $Root) { Remove-Item -Recurse -Force $Root -ErrorAction SilentlyContinue }
        Say "torn down"
    } else {
        Say "left in place at $Root (-KeepRoot)"
    }
}

if ($proofExit -ne 0) { Write-Error "THE PROOF FAILED (exit $proofExit)"; exit $proofExit }
Say "PROOF PASSED"
