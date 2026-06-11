# Proof test for issue #290: redeploy-gateway.ps1 must (1) publish the Gateway from a source
# ISOLATED from the shared working tree (a git worktree pinned to the resolved HEAD commit), and
# (2) after relaunch, assert the RUNNING Gateway reports the commit it just published - so a stale
# build can no longer pass while GET /cockpit returns 200.
#
# Loads the REAL functions from scripts\redeploy-gateway.ps1 via -DefineOnly (no live deploy), then:
#   A. Unit-tests the SHA parser/matcher (incl. the "no SHA reported" and "mismatch" failure inputs).
#   B. Isolation proof: New-IsolatedGatewayPublish builds a real worktree at HEAD, publishes a real
#      cc-director-gateway.exe, and removes the worktree afterward (no leaked checkout). Skippable
#      with -SkipPublish for a fast logic-only run.
#   C. Build-identity proof against a SELF-HOSTED Gateway on a FREE loopback port (the dev console
#      host, with Tailscale + turn-briefs disabled): the REAL Assert-RunningGatewaySha
#         - PASSES when expected == the running Gateway's true HEAD commit, and
#         - FAILS LOUD (throws "STALE BUILD") when expected is a deliberately wrong commit,
#      which is the exact stale-deploy bug this issue is about.
#
# ASCII only. Run: powershell -ExecutionPolicy Bypass -File scripts\test\test-redeploy-gateway-sha.ps1
# Exit 0 = PASS, non-zero = FAIL. Never touches the live Gateway (7878) or the production install.

[CmdletBinding()]
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$script = Join-Path $repoRoot 'scripts\redeploy-gateway.ps1'
if (-not (Test-Path $script)) { Write-Host "FAIL: redeploy script not found at $script"; exit 1 }

# Load the functions WITHOUT running a deploy.
. $script -DefineOnly
foreach ($fn in 'Get-ShaFromVersionString','Test-ShaMatch','Get-DeployCommitSha','New-IsolatedGatewayPublish','Assert-RunningGatewaySha') {
    if (-not (Get-Command $fn -ErrorAction SilentlyContinue)) {
        Write-Host "FAIL: -DefineOnly did not define $fn"; exit 1
    }
}

$failures = New-Object System.Collections.Generic.List[string]
function Assert-True([bool]$cond, [string]$msg) {
    if ($cond) { Write-Host "  PASS: $msg" } else { Write-Host "  FAIL: $msg"; $script:failures.Add($msg) }
}
function Assert-Throws([scriptblock]$action, [string]$matchPattern, [string]$msg) {
    try { & $action | Out-Null; Write-Host "  FAIL: $msg (no exception thrown)"; $script:failures.Add($msg) }
    catch {
        if ($_.Exception.Message -match $matchPattern) { Write-Host "  PASS: $msg" }
        else { Write-Host "  FAIL: $msg (threw but message did not match '$matchPattern': $($_.Exception.Message))"; $script:failures.Add($msg) }
    }
}

# --- A. SHA parser / matcher units -----------------------------------------
Write-Host "=== A. SHA parsing + matching ==="
Assert-True ((Get-ShaFromVersionString -Version '0.6.22+55b79c8b9c2af578e04fa5e5abd7218bf858b95e') -eq '55b79c8') "Get-ShaFromVersionString extracts the 7-char short SHA from a full informational version"
Assert-True ((Get-ShaFromVersionString -Version '0.6.22') -eq '') "Get-ShaFromVersionString returns empty when no +<sha> suffix is present"
Assert-True (Test-ShaMatch -Expected '55b79c8b9c2af578e04fa5e5abd7218bf858b95e' -Actual '55b79c8') "Test-ShaMatch: running short SHA is accepted as a prefix of the full published SHA"
Assert-True (-not (Test-ShaMatch -Expected 'deadbeefdeadbeef' -Actual '55b79c8')) "Test-ShaMatch: a different SHA does NOT match (the stale-build case)"
Assert-True (-not (Test-ShaMatch -Expected 'abc1234' -Actual '')) "Test-ShaMatch: an empty running SHA never matches"

# --- B. Isolation proof: publish from a git worktree pinned to HEAD --------
Write-Host "=== B. Isolated-worktree publish ==="
$headSha = Get-DeployCommitSha -RepoRoot $repoRoot
Assert-True ($headSha.Length -ge 7) "Get-DeployCommitSha resolved HEAD: $headSha"

if ($SkipPublish) {
    Write-Host "  SKIP: -SkipPublish set (isolated publish not exercised this run)"
} else {
    $stage = Join-Path $env:TEMP ("cc-290-stage-" + [Guid]::NewGuid().ToString('N'))
    try {
        $wtBefore = [int]@(($(Invoke-GitCapture -GitArgs @('-C', $repoRoot, 'worktree', 'list')).Output -split "`n") | Where-Object { $_.Trim() }).Count
        Write-Host "  publishing from an isolated worktree at $headSha (this takes a minute)..."
        $exe = New-IsolatedGatewayPublish -RepoRoot $repoRoot -CommitSha $headSha -StageDir $stage
        Assert-True ([bool](Test-Path $exe)) "New-IsolatedGatewayPublish produced cc-director-gateway.exe: $exe"
        $wtAfter = [int]@(($(Invoke-GitCapture -GitArgs @('-C', $repoRoot, 'worktree', 'list')).Output -split "`n") | Where-Object { $_.Trim() }).Count
        Assert-True ($wtAfter -eq $wtBefore) "the throwaway worktree was removed afterward (no leaked checkout: before=$wtBefore after=$wtAfter)"
    }
    finally {
        if (Test-Path $stage) { Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue }
    }
}

# --- C. Build-identity assertion against a self-hosted Gateway --------------
Write-Host "=== C. Post-deploy SHA assertion vs a self-hosted Gateway ==="
$gwDll = Join-Path $repoRoot 'src\CcDirector.Gateway\bin\Debug\net10.0\CcDirector.Gateway.dll'
if (-not (Test-Path $gwDll)) {
    Write-Host "  building the dev Gateway host so a real /healthz is available..."
    & dotnet build (Join-Path $repoRoot 'src\CcDirector.Gateway\CcDirector.Gateway.csproj') -c Debug --nologo | Out-Null
}
if (-not (Test-Path $gwDll)) { Write-Host "  FAIL: dev Gateway host not built at $gwDll"; $failures.Add('dev gateway host missing') }
else {
    # Free loopback port; isolate the host (no Tailscale serve writes, no brain/turn-brief spawn).
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
    $healthz = "http://127.0.0.1:$port/healthz"
    $env:CC_GATEWAY_NO_TAILSCALE = '1'
    $env:CC_TURNBRIEFS = '0'
    $proc = Start-Process -FilePath 'dotnet' -ArgumentList "`"$gwDll`" --port $port" -PassThru -WindowStyle Hidden
    try {
        # Read the running version once (also lets the host finish binding).
        $runningVersion = $null
        $deadline = (Get-Date).AddSeconds(30)
        do {
            try { $runningVersion = [string](Invoke-RestMethod -Uri $healthz -TimeoutSec 5).version } catch {}
            if (-not [string]::IsNullOrWhiteSpace($runningVersion)) { break }
            Start-Sleep -Seconds 1
        } while ((Get-Date) -lt $deadline)
        Assert-True (-not [string]::IsNullOrWhiteSpace($runningVersion)) "self-hosted Gateway answered /healthz with a version: '$runningVersion'"
        $runningSha = Get-ShaFromVersionString -Version $runningVersion
        Assert-True (-not [string]::IsNullOrWhiteSpace($runningSha)) "the running version carries a commit SHA: '$runningSha'"

        # PASS path: the REAL assertion accepts the running Gateway's true commit.
        $ok = Assert-RunningGatewaySha -ExpectedCommitSha $runningSha -HealthzUrl $healthz -TimeoutSeconds 20
        Assert-True ($ok -eq $runningVersion) "Assert-RunningGatewaySha PASSES when expected == running commit (returned '$ok')"

        # FAIL-LOUD path: a deliberately wrong (stale) commit must throw "STALE BUILD".
        Assert-Throws { Assert-RunningGatewaySha -ExpectedCommitSha 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeef' -HealthzUrl $healthz -TimeoutSeconds 20 } `
            'STALE BUILD' "Assert-RunningGatewaySha FAILS LOUD on a mismatched (stale) commit"
    }
    finally {
        try { Invoke-WebRequest "http://127.0.0.1:$port/shutdown" -Method POST -UseBasicParsing -TimeoutSec 3 | Out-Null } catch {}
        Start-Sleep -Seconds 1
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    }
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host "RESULT: PASS - redeploy publishes from an isolated worktree and the post-deploy SHA assertion passes on a match and fails loud on a stale build (issue #290)."
    exit 0
} else {
    Write-Host ("RESULT: FAIL - {0} assertion(s) failed:" -f $failures.Count)
    foreach ($f in $failures) { Write-Host "  - $f" }
    exit 1
}
