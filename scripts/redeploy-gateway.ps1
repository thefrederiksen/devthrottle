# Rebuild the Gateway tray app and swap it into the installed per-user location, then
# smoke-check GET /cockpit AND assert the RUNNING Gateway reports the exact commit we published.
# Run from any checkout: paths derive from this script's location.
# No elevation: the Gateway is a per-user tray app under %LOCALAPPDATA% (docs/plans/gateway-tray-app.md).
#
# Issue #290: the old version published from the shared working tree / a shared stage dir, so a
# concurrent branch switch or a second redeploy could clobber what got published and ship a STALE
# build while GET /cockpit still returned 200. This version:
#   1. Publishes from an ISOLATED `git worktree` pinned to the resolved HEAD commit, into a per-run
#      stage dir (GUID) - a concurrent edit or redeploy cannot change what THIS run publishes.
#   2. After swap + relaunch, reads the RUNNING Gateway's reported build identity (GET /healthz ->
#      version, which is AppVersion.Full and carries "+<sha>") and asserts its SHA matches the
#      commit we just published. On mismatch it exits non-zero naming expected vs actual.
#
# -DefineOnly loads the functions below for hermetic testing (scripts\test\) WITHOUT running a
# deploy - the same pattern deploy-cockpit.ps1 uses.

[CmdletBinding()]
param(
    [switch]$DefineOnly
)

# ---------------------------------------------------------------------------
# Testable units (pure where possible; no live-fleet side effects on import).
# ---------------------------------------------------------------------------

# Run a git command and return [pscustomobject]{ ExitCode; Output }. git writes progress lines
# ("Preparing worktree...") to stderr; under Windows PowerShell 5.1 a merged native stderr becomes a
# NativeCommandError that terminates when the CALLER set $ErrorActionPreference='Stop'. We force
# Continue locally and key success off $LASTEXITCODE only, so an informational stderr line is never
# mistaken for failure. (CLAUDE.md / PowerShell tool note on native stderr.)
function Invoke-GitCapture {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$GitArgs)

    $eapPrev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = (& git @GitArgs 2>&1 | Out-String)
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out.Trim() }
    }
    finally {
        $ErrorActionPreference = $eapPrev
    }
}

# Extract the short (7-char) commit SHA from a Gateway version string. AppVersion.Full looks like
# "0.6.3+1cc1abd9c2..." (SourceLink appends the full SHA after '+'); the running Gateway reports it
# verbatim on GET /healthz. Returns "" when the string carries no '+<sha>' suffix.
function Get-ShaFromVersionString {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Version)

    $plus = $Version.IndexOf('+')
    if ($plus -lt 0) { return '' }
    $sha = $Version.Substring($plus + 1)
    if ($sha.Length -gt 7) { return $sha.Substring(0, 7) }
    return $sha
}

# Resolve the commit a deploy will publish: the HEAD of the checkout at $RepoRoot. We pin the
# isolated worktree to THIS sha so a branch switch in the main checkout after this point cannot
# change what gets published. Throws if the path is not a git checkout (fail loud - no fallback).
function Get-DeployCommitSha {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepoRoot)

    $r = Invoke-GitCapture -GitArgs @('-C', $RepoRoot, 'rev-parse', 'HEAD')
    if ($r.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($r.Output)) {
        throw "Get-DeployCommitSha: '$RepoRoot' is not a git checkout (git rev-parse HEAD failed: $($r.Output))"
    }
    return $r.Output.Trim()
}

# Two commit SHAs identify the same build when one is a prefix of the other (the running Gateway
# reports a short 7-char SHA; the published commit is the full 40-char SHA). Case-insensitive.
function Test-ShaMatch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Expected,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Actual
    )
    if ([string]::IsNullOrWhiteSpace($Expected) -or [string]::IsNullOrWhiteSpace($Actual)) { return $false }
    $e = $Expected.Trim().ToLowerInvariant()
    $a = $Actual.Trim().ToLowerInvariant()
    return $e.StartsWith($a) -or $a.StartsWith($e)
}

# Publish the Gateway tray app from a throwaway git worktree pinned to $CommitSha, into $StageDir.
# Isolation is the point (issue #290): the worktree is a separate checkout of the SAME repo at the
# exact commit, so a concurrent branch switch or edit in the main checkout cannot change the source
# that gets published. The worktree is always removed afterward (even on failure). Returns the path
# to the published devthrottle-gateway.exe; throws if it did not appear.
function New-IsolatedGatewayPublish {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$CommitSha,
        [Parameter(Mandatory)][string]$StageDir
    )

    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("cc-gwdeploy-wt-" + [Guid]::NewGuid().ToString('N'))
    Write-Host "[redeploy-gateway] isolating publish: worktree=$work commit=$CommitSha"

    # `git worktree add --detach <path> <sha>` checks the repo out at the exact commit in a separate
    # directory. --detach so we never move a branch ref. Fail loud if it does not take.
    $add = Invoke-GitCapture -GitArgs @('-C', $RepoRoot, 'worktree', 'add', '--detach', $work, $CommitSha)
    if ($add.ExitCode -ne 0) {
        throw "New-IsolatedGatewayPublish: git worktree add failed: $($add.Output)"
    }

    try {
        if (Test-Path $StageDir) { Remove-Item -Recurse -Force $StageDir }
        New-Item -ItemType Directory -Force $StageDir | Out-Null

        # -p:SelfContained=false (the PROPERTY, not the --self-contained switch): from a CLEAN worktree
        # the switch alone trips NETSDK1150 ("a non self-contained executable cannot be referenced by a
        # self-contained executable") because PublishSingleFile + a RID infers self-contained for the
        # referenced CcDirector.Gateway. Setting the property flows it to every project in the graph.
        # (The old shared-tree publish only avoided this by reusing cached build state - exactly the
        # silent fragility issue #290 is closing.)
        $proj = Join-Path $work 'src\CcDirector.GatewayApp\CcDirector.GatewayApp.csproj'
        # Pipe the publish log to the host (Out-Host) so it does NOT land in this function's success
        # stream - the function returns ONLY the exe path, never the build chatter.
        & dotnet publish $proj `
            -c Release -r win-x64 -p:SelfContained=false `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $StageDir --nologo | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "New-IsolatedGatewayPublish: dotnet publish failed (exit $LASTEXITCODE)"
        }

        $exe = Join-Path $StageDir 'devthrottle-gateway.exe'
        if (-not (Test-Path $exe)) {
            throw "New-IsolatedGatewayPublish: published exe not found at $exe"
        }
        return $exe
    }
    finally {
        # Always remove the throwaway worktree so we never leak checkouts under TEMP.
        Remove-GitWorktree -RepoRoot $RepoRoot -WorktreePath $work
    }
}

# Remove a throwaway git worktree, tolerating the brief file locks the dotnet build server holds on
# the just-published output (those cause "Permission denied" if we delete immediately). Retries a
# few times with a short delay, then prunes the admin entry so a lingering directory never leaves a
# dangling worktree registration behind. Best-effort: a leaked TEMP dir is logged, never fatal.
function Remove-GitWorktree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$WorktreePath
    )
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        $rm = Invoke-GitCapture -GitArgs @('-C', $RepoRoot, 'worktree', 'remove', '--force', $WorktreePath)
        if ($rm.ExitCode -eq 0) { return }
        Start-Sleep -Milliseconds 750
    }
    Write-Warning "[redeploy-gateway] worktree remove did not complete; pruning admin entry for $WorktreePath"
    if (Test-Path $WorktreePath) { Remove-Item -Recurse -Force $WorktreePath -ErrorAction SilentlyContinue }
    Invoke-GitCapture -GitArgs @('-C', $RepoRoot, 'worktree', 'prune') | Out-Null
}

# Read the RUNNING Gateway's reported build identity from GET /healthz and assert its commit SHA
# matches $ExpectedCommitSha (the commit we just published). This is what turns "GET /cockpit -> 200"
# (proves A gateway is up) into "the gateway is running THIS build" (proves WHICH build). Polls
# because the relaunched Gateway needs a moment to bind. Throws with expected-vs-actual on mismatch
# or when no SHA is reported; returns the running version string on success.
function Assert-RunningGatewaySha {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ExpectedCommitSha,
        [string]$HealthzUrl = 'http://127.0.0.1:7878/healthz',
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = ''
    do {
        try {
            $health = Invoke-RestMethod -Uri $HealthzUrl -TimeoutSec 5
            $runningVersion = [string]$health.version
            if (-not [string]::IsNullOrWhiteSpace($runningVersion)) {
                $runningSha = Get-ShaFromVersionString -Version $runningVersion
                if ([string]::IsNullOrWhiteSpace($runningSha)) {
                    $msg = "Running Gateway reported version '$runningVersion' with NO commit SHA " +
                           "(expected +<sha>); cannot prove which build is live."
                    throw $msg
                }
                if (-not (Test-ShaMatch -Expected $ExpectedCommitSha -Actual $runningSha)) {
                    $msg = "STALE BUILD: published commit $ExpectedCommitSha but the RUNNING Gateway " +
                           "reports $runningSha (version '$runningVersion'). The deploy did not take - " +
                           "refusing to declare success."
                    throw $msg
                }
                Write-Host "[redeploy-gateway] build-identity OK: running Gateway reports $runningVersion (matches published $ExpectedCommitSha)"
                return $runningVersion
            }
            $lastError = "healthz answered but reported an empty version"
        }
        catch {
            # A "STALE BUILD" / "NO commit SHA" assertion is a real failure - rethrow it immediately
            # instead of treating it as "not up yet".
            if ($_.Exception.Message -match 'STALE BUILD|NO commit SHA') { throw }
            $lastError = $_.Exception.Message
        }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)

    throw "Assert-RunningGatewaySha: gave up after ${TimeoutSeconds}s waiting for $HealthzUrl to report a version (last: $lastError)"
}

if ($DefineOnly) { return }

# ---------------------------------------------------------------------------
# The deploy itself.
# ---------------------------------------------------------------------------

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$gwDir = "$env:LOCALAPPDATA\cc-director\gateway"
# Per-run stage dir (GUID): two redeploys cannot corrupt each other's staged output (issue #290).
$stage = Join-Path $repoRoot ("local_builds\_gw-deploy-" + [Guid]::NewGuid().ToString('N'))

# Pin to the commit at HEAD now, then publish from an isolated worktree at that commit - a branch
# switch or edit in this checkout after this line cannot change what we ship.
$commitSha = Get-DeployCommitSha -RepoRoot $repoRoot
Write-Host "[redeploy-gateway] publishing Gateway at commit $commitSha"

try {
    $stagedExe = New-IsolatedGatewayPublish -RepoRoot $repoRoot -CommitSha $commitSha -StageDir $stage

    # Ask the running tray app to exit gracefully, then wait for the exe to unlock.
    try { Invoke-WebRequest 'http://127.0.0.1:7878/shutdown' -Method POST -UseBasicParsing -TimeoutSec 5 | Out-Null } catch {}
    for ($i = 0; $i -lt 20; $i++) {
        if (-not (Get-Process -Name devthrottle-gateway -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$gwDir*" })) { break }
        Start-Sleep -Milliseconds 500
    }

    New-Item -ItemType Directory -Force $gwDir | Out-Null
    Copy-Item $stagedExe $gwDir -Force
    Start-Process -FilePath "$gwDir\devthrottle-gateway.exe" -ArgumentList '--managed' -WorkingDirectory $gwDir

    Start-Sleep 5

    # Smoke check: a Gateway answers /cockpit (existing gate, preserved).
    Invoke-RestMethod 'http://127.0.0.1:7878/cockpit'

    # Build-identity gate (issue #290): prove the RUNNING Gateway is the commit we just published,
    # not a stale one. Fails loud (non-zero exit via $ErrorActionPreference=Stop) on mismatch.
    Assert-RunningGatewaySha -ExpectedCommitSha $commitSha | Out-Null

    Write-Host "[redeploy-gateway] DONE: Gateway redeployed and verified at commit $commitSha"
}
finally {
    # Clean up this run's stage dir; never touch sibling runs' dirs (per-run GUID).
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue }
}
