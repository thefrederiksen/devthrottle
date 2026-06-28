# Proof test for issue #809: redeploy-gateway.ps1 must copy the Gateway PAYLOAD (the exe AND its
# wwwroot tree) into the install dir, not just the exe - so the mobile app lands beside the exe and
# GET /m/ serves with no manual copy. The single-file exe carries no loose content, so a copy of only
# the exe drops wwwroot/m and /m 404s.
#
# Loads the REAL Copy-GatewayPayload function from scripts\redeploy-gateway.ps1 via -DefineOnly (no
# live deploy - this NEVER touches the running Gateway on 7878 or the production install), then:
#   A. A staged publish (exe + wwwroot\m\index.html) copies into a fresh install dir, and
#      wwwroot\m\index.html ends up BESIDE the exe.
#   B. A re-copy replaces a stale wwwroot file (no stale hashed asset survives).
#   C. A stage dir with NO wwwroot\m fails loud (a Release publish must stage the mobile app).
#
# ASCII only. Run: powershell -ExecutionPolicy Bypass -File scripts\test\test-redeploy-gateway-mobile.ps1
# Exit 0 = PASS, non-zero = FAIL.

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$script = Join-Path $repoRoot 'scripts\redeploy-gateway.ps1'
if (-not (Test-Path $script)) { Write-Host "FAIL: redeploy script not found at $script"; exit 1 }

# Load the functions WITHOUT running a deploy.
. $script -DefineOnly
if (-not (Get-Command 'Copy-GatewayPayload' -ErrorAction SilentlyContinue)) {
    Write-Host "FAIL: -DefineOnly did not define Copy-GatewayPayload"; exit 1
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

# Build a fake publish stage dir: devthrottle-gateway.exe + wwwroot\m\index.html + a hashed asset.
function New-FakeStage {
    param([string]$Root)
    New-Item -ItemType Directory -Force (Join-Path $Root 'wwwroot\m\assets') | Out-Null
    Set-Content -Path (Join-Path $Root 'devthrottle-gateway.exe') -Value 'exe' -Encoding ascii
    Set-Content -Path (Join-Path $Root 'wwwroot\m\index.html') -Value '<html>__GATEWAY_TOKEN__</html>' -Encoding ascii
    Set-Content -Path (Join-Path $Root 'wwwroot\m\assets\index-abc123.js') -Value 'console.log(1)' -Encoding ascii
}

$work = Join-Path $env:TEMP ("cc-809-redeploy-" + [Guid]::NewGuid().ToString('N'))
try {
    # --- A. payload copy lands wwwroot\m beside the exe ---------------------------------------------
    Write-Host "=== A. Copy-GatewayPayload lands exe + wwwroot\m ==="
    $stage = Join-Path $work 'stage'
    $gw    = Join-Path $work 'install'
    New-FakeStage -Root $stage
    Copy-GatewayPayload -StageDir $stage -GatewayDir $gw
    Assert-True (Test-Path (Join-Path $gw 'devthrottle-gateway.exe')) "the exe landed in the install dir"
    Assert-True (Test-Path (Join-Path $gw 'wwwroot\m\index.html')) "wwwroot\m\index.html landed BESIDE the exe"
    Assert-True (Test-Path (Join-Path $gw 'wwwroot\m\assets\index-abc123.js')) "the hashed asset landed too"

    # --- B. re-copy removes a stale wwwroot file ---------------------------------------------------
    Write-Host "=== B. re-copy removes stale wwwroot files ==="
    Set-Content -Path (Join-Path $gw 'wwwroot\m\stale-old.js') -Value 'stale' -Encoding ascii
    Copy-GatewayPayload -StageDir $stage -GatewayDir $gw
    Assert-True (-not (Test-Path (Join-Path $gw 'wwwroot\m\stale-old.js'))) "a stale wwwroot file did NOT survive the re-copy"
    Assert-True (Test-Path (Join-Path $gw 'wwwroot\m\index.html')) "wwwroot\m\index.html is still present after the re-copy"

    # --- C. a stage with no mobile app fails loud --------------------------------------------------
    Write-Host "=== C. missing wwwroot\m fails loud ==="
    $badStage = Join-Path $work 'bad-stage'
    New-Item -ItemType Directory -Force $badStage | Out-Null
    Set-Content -Path (Join-Path $badStage 'devthrottle-gateway.exe') -Value 'exe' -Encoding ascii
    Assert-Throws { Copy-GatewayPayload -StageDir $badStage -GatewayDir (Join-Path $work 'bad-install') } `
        'mobile app missing' "Copy-GatewayPayload FAILS LOUD when the publish output has no wwwroot\m"
}
finally {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host "RESULT: PASS - redeploy copies the exe AND wwwroot\m so /m serves with no manual copy (issue #809)."
    exit 0
} else {
    Write-Host ("RESULT: FAIL - {0} assertion(s) failed:" -f $failures.Count)
    foreach ($f in $failures) { Write-Host "  - $f" }
    exit 1
}
