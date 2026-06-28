# Issue #809 end-to-end proof harness (ISOLATED - never touches the user's installed Gateway on 7878
# or %LOCALAPPDATA%; CC_DIRECTOR_ROOT is redirected to a temp dir and every Gateway runs on a FREE
# loopback port).
#
# Proves the DELIVERY gap and its fix using the REAL serving + packaging code:
#   1. AC1  - a Release publish of the Gateway stages wwwroot/m beside the host, and the release zip
#             (devthrottle-gateway-mobile-win-x64.zip, built exactly as release.yml does) carries it.
#   2. BUG  - an exe-only layout (no wwwroot, the OLD broken delivery) -> GET /m/ returns 404 with the
#             "Mobile app not built into this Gateway" body (the "no wwwroot/m" condition).
#   3. FIX  - delivering the side-car zip into wwwroot/m beside the host -> GET /m/ returns 200 and the
#             served shell is the React app (the #806 index with the token-injection placeholder
#             replaced). Leaves this FIXED Gateway running for the roster screenshot; writes its
#             port/pid to running.json. Run with -Teardown <port> <pid> to stop it.
#
# ASCII only. Exit 0 = PASS.

[CmdletBinding()]
param(
    [switch]$Teardown,
    [int]$Port,
    [int]$ProcId
)

$ErrorActionPreference = 'Stop'
$proofDir = $PSScriptRoot
$repoRoot = Split-Path (Split-Path (Split-Path (Split-Path $proofDir -Parent) -Parent) -Parent) -Parent

if ($Teardown) {
    try { Invoke-WebRequest "http://127.0.0.1:$Port/shutdown" -Method POST -UseBasicParsing -TimeoutSec 3 | Out-Null } catch {}
    Start-Sleep -Seconds 1
    $p = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
    if ($p) { Stop-Process -Id $ProcId -Force -ErrorAction SilentlyContinue; Write-Host "stopped pid $ProcId" }
    exit 0
}

function Get-FreePort {
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop(); return $port
}

# Launch the dev console Gateway host (shares the EXACT GatewayHost + MobileApp serving code with the
# shipped tray exe) from $fromDir on $port, isolated. Returns the process. NEVER binds 7878.
function Start-IsolatedGateway {
    param([string]$FromDir, [int]$Port, [string]$Root)
    $dll = Join-Path $FromDir 'CcDirector.Gateway.dll'
    if (-not (Test-Path $dll)) { throw "Start-IsolatedGateway: host dll not found at $dll" }
    $env:CC_DIRECTOR_ROOT = $Root            # isolate logs + state away from the user's machine
    $env:CC_GATEWAY_NO_TAILSCALE = '1'       # no tailscale serve writes
    $env:CC_TURNBRIEFS = '0'                 # no brain/turn-brief spawn
    return Start-Process -FilePath 'dotnet' -ArgumentList "`"$dll`" --port $Port" -PassThru -WindowStyle Hidden
}

function Wait-Healthz {
    param([int]$Port, [int]$TimeoutSec = 40)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        try { if ((Invoke-WebRequest "http://127.0.0.1:$Port/healthz" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { return $true } } catch {}
        Start-Sleep -Milliseconds 700
    } while ((Get-Date) -lt $deadline)
    return $false
}

$work = Join-Path $env:TEMP ("cc-809-proof-" + [Guid]::NewGuid().ToString('N'))
$pkg  = Join-Path $work 'pkg'         # Release publish of the Gateway host (carries wwwroot/m)
New-Item -ItemType Directory -Force $work | Out-Null

Write-Host "=== issue #809 end-to-end proof (isolated; user Gateway on 7878 untouched) ==="
Write-Host "repoRoot=$repoRoot"
Write-Host "work=$work"
Write-Host ""

# --- Build: Release publish of the Gateway host (triggers the release-gated mobile build) -----------
Write-Host "=== 0. Release-publish the Gateway host (runs the mobile npm build) ==="
& dotnet publish (Join-Path $repoRoot 'src\CcDirector.Gateway\CcDirector.Gateway.csproj') -c Release -o $pkg --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# --- AC1: wwwroot/m staged beside the host + the release zip carries it ------------------------------
Write-Host ""
Write-Host "=== AC1. Release publish stages wwwroot/m; the release zip carries it ==="
$mDir = Join-Path $pkg 'wwwroot\m'
$index = Join-Path $mDir 'index.html'
if (-not (Test-Path $index)) { throw "AC1 FAIL: $index not staged by the Release publish" }
Write-Host "wwwroot/m staged beside the host:"
Get-ChildItem -Recurse -File $mDir | ForEach-Object { "  " + $_.FullName.Substring($mDir.Length).TrimStart('\','/') }

# Build the release asset EXACTLY as release.yml does (zip the CONTENTS of wwwroot/m).
$zip = Join-Path $work 'devthrottle-gateway-mobile-win-x64.zip'
Compress-Archive -Path (Join-Path $mDir '*') -DestinationPath $zip -Force
Write-Host ""
Write-Host "release asset $(Split-Path $zip -Leaf) entries:"
[System.IO.Compression.ZipFile]::OpenRead($zip).Entries | ForEach-Object { "  " + $_.FullName }
$zipHasIndex = ([System.IO.Compression.ZipFile]::OpenRead($zip).Entries | Where-Object { $_.FullName -eq 'index.html' }).Count -ge 1
if (-not $zipHasIndex) { throw "AC1 FAIL: release zip does not carry index.html at its root" }
Write-Host "AC1 PASS: release asset carries wwwroot/m (index.html + assets)."

# --- BUG: exe-only layout (no wwwroot) -> /m 404 ----------------------------------------------------
Write-Host ""
Write-Host "=== BUG. exe-only delivery (no wwwroot) -> GET /m/ 404 (the #809 symptom) ==="
$broken = Join-Path $work 'broken'
New-Item -ItemType Directory -Force $broken | Out-Null
Copy-Item (Join-Path $pkg '*') $broken -Recurse -Force
Remove-Item -Recurse -Force (Join-Path $broken 'wwwroot')   # simulate the OLD exe-only delivery
$p1 = Get-FreePort
$proc1 = Start-IsolatedGateway -FromDir $broken -Port $p1 -Root (Join-Path $work 'root-broken')
try {
    if (-not (Wait-Healthz -Port $p1)) { throw "broken gateway did not answer /healthz on $p1" }
    $code = 0; $body = ''
    try { $r = Invoke-WebRequest "http://127.0.0.1:$p1/m/" -UseBasicParsing -TimeoutSec 8; $code = $r.StatusCode; $body = $r.Content }
    catch { $code = [int]$_.Exception.Response.StatusCode; $body = (New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd() }
    Write-Host "GET /m/ -> $code : $body"
    if ($code -ne 404) { throw "BUG repro FAIL: expected 404 from the exe-only layout, got $code" }
    Write-Host "BUG PASS: exe-only delivery 404s at /m (no wwwroot/m beside the host)."
}
finally {
    try { Invoke-WebRequest "http://127.0.0.1:$p1/shutdown" -Method POST -UseBasicParsing -TimeoutSec 3 | Out-Null } catch {}
    Start-Sleep -Seconds 1
    if (-not $proc1.HasExited) { Stop-Process -Id $proc1.Id -Force -ErrorAction SilentlyContinue }
}

# --- FIX: deliver the side-car zip into wwwroot/m -> /m 200 -----------------------------------------
Write-Host ""
Write-Host "=== FIX. deliver the side-car zip into wwwroot/m -> GET /m/ 200 ==="
$fixed = Join-Path $work 'fixed'
New-Item -ItemType Directory -Force $fixed | Out-Null
Copy-Item (Join-Path $pkg '*') $fixed -Recurse -Force
Remove-Item -Recurse -Force (Join-Path $fixed 'wwwroot')          # start from the broken exe-only layout
$fixedM = Join-Path $fixed 'wwwroot\m'
New-Item -ItemType Directory -Force $fixedM | Out-Null
Expand-Archive -Path $zip -DestinationPath $fixedM -Force         # the engine's extract-into-wwwroot/m
if (-not (Test-Path (Join-Path $fixedM 'index.html'))) { throw "FIX FAIL: delivery did not land wwwroot/m/index.html" }

$rootFixed = Join-Path $work 'root-fixed'
$p2 = Get-FreePort
$proc2 = Start-IsolatedGateway -FromDir $fixed -Port $p2 -Root $rootFixed
if (-not (Wait-Healthz -Port $p2)) { throw "fixed gateway did not answer /healthz on $p2" }
$r2 = Invoke-WebRequest "http://127.0.0.1:$p2/m/" -UseBasicParsing -TimeoutSec 8
Write-Host "GET /m/ -> $($r2.StatusCode)"
if ($r2.StatusCode -ne 200) { throw "FIX FAIL: expected 200, got $($r2.StatusCode)" }
$html = $r2.Content
$htmlOut = Join-Path $proofDir 'fix-m-served.html'
Set-Content -Path $htmlOut -Value $html -Encoding utf8
# The served shell must be the React app and must NOT carry the raw token placeholder (it is injected).
$looksLikeApp = ($html -match '<div id="root"') -or ($html -match 'type="module"') -or ($html -match '/m/assets/')
if (-not $looksLikeApp) { throw "FIX FAIL: /m did not serve the React app shell" }
if ($html -match '__GATEWAY_TOKEN__') { throw "FIX FAIL: token placeholder was not injected" }
Write-Host "FIX PASS: /m serves the React app shell (token injected). HTML saved: $htmlOut"

# Confirm the host logged 'serving /m ... (exists=True)' and NO 'no wwwroot/m' for the fixed run.
$gwLogDir = Join-Path $rootFixed 'logs\director'
$logLine = ''; $noWwwroot = $false; $servingLine = ''
if (Test-Path $gwLogDir) {
    $log = Get-ChildItem $gwLogDir -Filter 'director-*.log' | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) {
        $txt = Get-Content $log.FullName -Raw
        $noWwwroot = $txt -match 'not built into this host'
        $m = [regex]::Match($txt, '\[MobileApp\] serving /m from .*'); if ($m.Success) { $servingLine = $m.Value }
    }
}
Write-Host "gateway log: serving line = '$servingLine'; 'no wwwroot/m' present = $noWwwroot"
if ($noWwwroot) { throw "FIX FAIL: the fixed run still logged the 'no wwwroot/m' message" }

# Leave the fixed gateway running for the roster screenshot.
$running = @{ port = $p2; pid = $proc2.Id; root = $rootFixed; work = $work; url = "http://127.0.0.1:$p2/m/" }
$running | ConvertTo-Json | Set-Content -Path (Join-Path $proofDir 'running.json') -Encoding ascii
Write-Host ""
Write-Host "RESULT: PASS - AC1 (zip carries wwwroot/m), BUG (exe-only 404), FIX (delivered zip -> /m 200, app shell, token injected)."
Write-Host "FIXED gateway LEFT RUNNING for the screenshot: port=$p2 pid=$($proc2.Id) url=http://127.0.0.1:$p2/m/"
Write-Host "Teardown: powershell -File `"$($MyInvocation.MyCommand.Path)`" -Teardown -Port $p2 -ProcId $($proc2.Id)"
exit 0
