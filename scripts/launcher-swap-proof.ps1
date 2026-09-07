<#
.SYNOPSIS
    The end-to-end proof of the Director installing the launcher's staged update (issue #2719,
    Phase 0), on an ISOLATED storage root, with REAL launcher binaries and a REAL running launcher.

.DESCRIPTION
    Phase 0's unit tests drive the whole pass through fakes for the process list, the stop and the
    start. That is right for the decisions and says nothing about the three production delegates that
    touch the machine: DefaultStopProcess, DefaultStartLauncher (including the CC_DIRECTOR_ROOT it
    hands the child) and the witness reading a genuinely new process. Until this script ran, none of
    those had ever executed against a real launcher - and the first run found a real defect.

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
    The isolated storage root. It must be provably disposable - see Assert-Disposable below, which
    refuses anything this script cannot prove it owns.

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
$sep = [System.IO.Path]::DirectorySeparatorChar

function Say([string]$text) { Write-Host "[launcher-swap-proof] $text" }

# ---------------------------------------------------------------------------
# 0. THE DELETION BOUNDARY, AND IT IS AN ALLOW-LIST.
#
#    This script recursively removes $Root and stops processes, so what it may
#    act on has to be POSITIVELY PROVEN disposable - never merely "not on a list
#    of things I thought of".
#
#    The first version compared uncanonicalized strings against one default path
#    and deleted anything that was not it. That is a deny-list and it failed open
#    for every shape nobody listed: "<real root>\." passes a string compare and
#    resolves to the real root; so does an ancestor such as %LOCALAPPDATA%, a
#    child such as "<real root>\config", and any installation not at the default
#    path. Every one of them would have been deleted before the driver's own
#    refusal ever ran.
#
#    What is admitted now: a CANONICAL path, inside the scratch parent, carrying
#    this script's marker in its leaf name, which either does not exist yet or
#    carries the sentinel file this script writes. Anything that cannot be
#    classified survives - that is the boundary working, not the boundary leaking.
# ---------------------------------------------------------------------------
$RigMarker    = "cc-director-launcher-proof"
$SentinelName = ".launcher-swap-proof-owns-this-directory"

$Root          = [System.IO.Path]::GetFullPath($Root).TrimEnd($sep)
$scratchParent = [System.IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd($sep)
$realRoot      = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "cc-director")).TrimEnd($sep)

function Assert-Disposable([string]$path) {
    if ($path -ieq $scratchParent) {
        throw "REFUSING: $path is the scratch PARENT itself, not a directory this script owns."
    }
    if (-not $path.StartsWith($scratchParent + $sep, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "REFUSING: $path is not inside $scratchParent, so this script does not own it."
    }
    # The real root, anything inside it, and any ancestor of it are all off limits.
    if ($path -ieq $realRoot -or
        $path.StartsWith($realRoot + $sep, [System.StringComparison]::OrdinalIgnoreCase) -or
        $realRoot.StartsWith($path + $sep, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "REFUSING: $path is this machine's REAL cc-director root, is inside it, or is an ancestor of it."
    }
    if ((Split-Path -Leaf $path) -notlike "*$RigMarker*") {
        throw "REFUSING: $path does not carry this script's marker '$RigMarker' in its leaf name."
    }
    # A directory that already exists must PROVE it is one of ours before it is deleted. One with no
    # sentinel belongs to somebody else, whatever its name says.
    if ((Test-Path $path) -and -not (Test-Path (Join-Path $path $SentinelName))) {
        throw "REFUSING: $path exists but carries no $SentinelName, so this script did not create it."
    }
}

Assert-Disposable $Root

$launcherDir  = Join-Path $Root "launcher"
$stagedDir    = Join-Path $Root "state\staged"
$setupDir     = Join-Path $Root "config\setup"
$sentinel     = Join-Path $Root $SentinelName

# Every artifact of THIS run is named for this run. Two runs side by side previously shared one
# output file and one verdict file in %TEMP%, so a failing run could read another run's PASS.
$runId        = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$runDir       = Join-Path $env:TEMP "launcher-swap-proof-$runId"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$proofOut     = Join-Path $runDir "out.txt"
$proofErr     = Join-Path $runDir "err.txt"
$proofVerdict = Join-Path $runDir "verdict.txt"

# ---------------------------------------------------------------------------
# 1. Two real launcher builds, differing only in the version they stamp. The
#    staged one has to be genuinely NEWER or FindStagedUpdate correctly declines.
#
#    THE BUILDS ARE KEYED TO THE SOURCE TREE. Reuse used to be decided by the
#    version string alone, so a run could PASS against binaries published from an
#    older tree - a proof of code that is no longer the code. The build directory
#    now carries the tree's own identity, so a changed tree cannot reuse the
#    previous tree's launchers.
# ---------------------------------------------------------------------------
Push-Location $repo
try {
    $treeId = (& git rev-parse --short HEAD).Trim()
    if ((& git status --porcelain).Length -gt 0) { $treeId = "$treeId-dirty-$runId" }
}
finally { Pop-Location }

$buildRoot = Join-Path $env:TEMP "launcher-swap-proof-builds\$treeId"
$oldBuild  = Join-Path $buildRoot "old\cc-launcher.exe"
$newBuild  = Join-Path $buildRoot "new\cc-launcher.exe"

function Publish-Launcher([string]$version, [string]$outDir) {
    $exe = Join-Path $outDir "cc-launcher.exe"
    if (Test-Path $exe) {
        $stamped = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
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

# THE DRIVER IS BUILT FROM THIS TREE, EVERY RUN. It used to be invoked with --no-build against a
# project deliberately outside the solution, which nothing here ever built: on a clean checkout the
# rig could not start at all, and after one build it could PASS using a stale assembly while the
# current source carried a regression. A proof that can run against code which is not the code under
# test is not a proof.
Say "building the proof driver from this tree"
& dotnet build (Join-Path $repo "scripts\launcher-swap-proof\LauncherSwapProof.csproj") --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "building the proof driver failed" }

# ---------------------------------------------------------------------------
# 2. Lay out the isolated install: the old build installed, the new one staged,
#    and a manifest that says what is installed.
# ---------------------------------------------------------------------------
function Stop-RigLaunchers([string]$dir) {
    # Only processes running from the RIG's launcher directory, matched on that directory PLUS A
    # SEPARATOR. A bare prefix also matches a sibling such as "<dir>-old", which is somebody else's
    # process - the same rule, and the same reason, as InstalledLauncherProcesses.Ours in the product.
    $prefix = $dir.TrimEnd($sep) + $sep
    Get-Process -Name "cc-launcher" -ErrorAction SilentlyContinue | ForEach-Object {
        $path = $null
        try { $path = $_.MainModule.FileName } catch { }
        if ($path -and $path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            Say "stopping leftover rig launcher pid $($_.Id)"
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

Assert-Disposable $Root
Stop-RigLaunchers $launcherDir
if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
New-Item -ItemType Directory -Force -Path $launcherDir, $stagedDir, $setupDir | Out-Null
# The sentinel goes down FIRST, so a run interrupted at any later point leaves a directory the NEXT
# run can prove is its own rather than one it must refuse.
Set-Content -Path $sentinel -Value "created by scripts/launcher-swap-proof.ps1" -Encoding utf8

Copy-Item $oldBuild (Join-Path $launcherDir "cc-launcher.exe") -Force
Copy-Item $newBuild (Join-Path $stagedDir  "cc-launcher.exe") -Force
@{ "cc-launcher" = $OldVersion } | ConvertTo-Json | Set-Content -Path (Join-Path $setupDir "installed.json") -Encoding utf8

# THE RIG'S WORLD MUST NOT REACH THE INTERNET. A launcher serving this root runs its OWN periodic
# auto-update, and it does not care that it is in a test rig: on 2026-09-07 the rig's 2.0.4 launcher
# reached GitHub, downloaded the real 2.0.6 release, overwrote the 2.0.99 build this script had
# staged, and installed itself over the rig's own installed binary. The proof then reported "nothing
# is staged" - a FAIL that said nothing whatever about the code under test, and which could as
# easily have been a PASS measured against binaries this script never put there.
#
# A rig that races the real release feed is not a controlled experiment. Switched off in the config
# the launcher reads, and again through the environment variable, because the launcher started by
# the SWAP inherits its environment from the driver rather than from this script.
@{ autoUpdate = @{ enabled = $false } } | ConvertTo-Json |
    Set-Content -Path (Join-Path $Root "config\config.json") -Encoding utf8

Say "isolated root laid out at $Root (installed $OldVersion, staged $NewVersion); auto-update OFF"

# ---------------------------------------------------------------------------
# 3. Start the rig's launcher, pointed at the isolated root.
#
#    CC_DIRECTOR_ROOT is set here for the same reason the product sets it: it
#    NAMES the world the launcher is to serve. The product's own start passes
#    the install's shared root; this script passes the rig's. (It used to REMOVE
#    the variable and rely on the process default - that is the defect this rig
#    found, written up in BuildLauncherStartInfo.)
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
$psi.EnvironmentVariables["CC_AUTOUPDATE"] = "0"
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
#
#    THE DRIVER'S OUTPUT IS REDIRECTED TO FILES, AND THAT IS NOT TIDINESS. The
#    swap starts a launcher that is MEANT to stay running, and a child started
#    without redirection inherits the console handles - so a pipeline reading
#    this script's output waits on the launcher rather than on the driver, and
#    the script hangs after printing PASS. Observed 2026-09-06: the first
#    successful run hung for ten minutes with the proof already passed.
# ---------------------------------------------------------------------------
# Inherited by the driver, and so by the launcher the SWAP starts.
$env:CC_AUTOUPDATE = "0"
try {
    $driver = Start-Process -FilePath "dotnet" -PassThru -NoNewWindow `
        -RedirectStandardOutput $proofOut -RedirectStandardError $proofErr `
        -ArgumentList @(
            "run", "--project",
            (Join-Path $repo "scripts\launcher-swap-proof\LauncherSwapProof.csproj"),
            "--no-build", "--", $Root, $NewVersion, $proofVerdict)

    # POLL, DO NOT WAIT ON THE STREAMS. -Wait (and WaitForExit) on a process with redirected output
    # waits for those streams to CLOSE, and the launcher the swap started inherited them. HasExited
    # asks about the process and nothing else.
    while (-not $driver.HasExited) { Start-Sleep -Milliseconds 500 }
    if (Test-Path $proofOut) { Get-Content $proofOut | ForEach-Object { Write-Host $_ } }
    if (Test-Path $proofErr) { Get-Content $proofErr | ForEach-Object { Write-Host $_ } }

    # THE VERDICT IS THE FILE THE DRIVER WROTE, not the exit code. Windows PowerShell's
    # Start-Process -PassThru without -Wait does not retain the process handle, so ExitCode reads back
    # EMPTY - and this script duly reported a FAILED proof on a run that had passed. -Wait is not
    # available either, for the stream reason above. The file is named for THIS run, so a verdict left
    # by a concurrent run cannot be read as this one's.
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
        # Re-asserted rather than assumed: the checks at the top ran before the run and this deletes
        # after it. Nothing here is cheaper than being sure.
        Assert-Disposable $Root
        if (Test-Path $Root) { Remove-Item -Recurse -Force $Root -ErrorAction SilentlyContinue }
        Say "torn down"
    } else {
        Say "left in place at $Root (-KeepRoot); artifacts in $runDir"
    }
}

if ($proofExit -ne 0) { Write-Error "THE PROOF FAILED (exit $proofExit) - see $proofOut"; exit $proofExit }
Say "PROOF PASSED"
