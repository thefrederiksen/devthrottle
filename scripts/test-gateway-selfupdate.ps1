<#
.SYNOPSIS
  Live test of the Gateway self-update mechanism against a THROWAWAY service.

.DESCRIPTION
  Exercises GatewaySelfUpdate end to end (stop -> swap -> start -> /healthz -> auto-rollback) on a
  disposable service named (by default) cc-gw-selfupdate-test on an alternate port. It NEVER touches
  the live cc-gateway-service or its ports. Run as Administrator.

  Two checks:
   1. Happy path: stage a good build, expect it to swap in and stay healthy (outcome Updated, exit 0).
   2. Rollback path: simulate a bad build by pointing the helper's health probe at a DEAD port, so the
      new build "fails" health and the helper rolls back to the previous build (outcome RolledBack,
      exit 1) - the test service is healthy again afterward and the bad version is pinned.

  Builds the Gateway from current source (self-contained) so the exe has the --apply-service-update
  mode. Cleans up the throwaway service and temp files at the end.

.PARAMETER Port
  Alternate port the throwaway service binds (default 7899 - NOT the live 7878/7470).

.PARAMETER Service
  Throwaway service name (default cc-gw-selfupdate-test).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-gateway-selfupdate.ps1
#>
[CmdletBinding()]
param(
    [int]$Port = 7899,
    [int]$DeadPort = 7898,
    [string]$Service = 'cc-gw-selfupdate-test'
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path $env:TEMP ("cc-gwsu-" + [Guid]::NewGuid().ToString('N'))
$installed = Join-Path $work 'installed\cc-director-gateway.exe'
$staged    = Join-Path $work 'staged\cc-director-gateway.exe'

function Cleanup {
    & sc.exe stop $Service   2>$null | Out-Null
    Start-Sleep -Seconds 2
    & sc.exe delete $Service 2>$null | Out-Null
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}

function Healthy([int]$p, [int]$tries = 15) {
    for ($i = 0; $i -lt $tries; $i++) {
        try { if ((Invoke-WebRequest "http://127.0.0.1:$p/healthz" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { return $true } } catch {}
        Start-Sleep -Seconds 1
    }
    return $false
}

Write-Output "=== building Gateway from current source (self-contained) ==="
$pub = Join-Path $work 'publish'
& dotnet publish "$repo\src\CcDirector.Gateway\CcDirector.Gateway.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $pub --nologo | Out-Null
$built = Join-Path $pub 'cc-director-gateway.exe'
if (-not (Test-Path $built)) { Write-Output "FAILED: gateway build not found at $built"; Cleanup; exit 1 }

New-Item -ItemType Directory -Force (Split-Path $installed) | Out-Null
New-Item -ItemType Directory -Force (Split-Path $staged)    | Out-Null
Copy-Item $built $installed -Force
Copy-Item $built $staged -Force

try {
    Write-Output "=== registering throwaway service '$Service' on port $Port ==="
    & sc.exe create $Service binPath= "`"$installed`" --port $Port" start= demand obj= LocalSystem | Out-Null
    & sc.exe start $Service | Out-Null
    if (-not (Healthy $Port)) { Write-Output "FAILED: throwaway service did not come up on $Port"; Cleanup; exit 1 }
    Write-Output "[OK] throwaway service healthy on $Port"

    # --- Happy path: good staged build, real health port -> Updated ---
    Write-Output "=== TEST 1 (happy): apply staged build, health on $Port ==="
    & $staged --apply-service-update --service $Service --target $installed --port $Port --new-version 9.9.9
    $rc1 = $LASTEXITCODE
    $h1 = Healthy $Port
    Write-Output ("  exit={0}  healthy={1}  (expect exit 0, healthy true)" -f $rc1, $h1)

    # --- Rollback path: health probe points at a DEAD port -> new build 'unhealthy' -> RolledBack ---
    Write-Output "=== TEST 2 (rollback): apply staged build, health on DEAD port $DeadPort ==="
    & $staged --apply-service-update --service $Service --target $installed --port $DeadPort --new-version 9.9.8
    $rc2 = $LASTEXITCODE
    $h2 = Healthy $Port   # after rollback the service runs the previous build on the real port again
    Write-Output ("  exit={0}  healthy-after-rollback={1}  (expect exit 1, healthy true)" -f $rc2, $h2)

    Write-Output ""
    if ($rc1 -eq 0 -and $h1 -and $rc2 -ne 0 -and $h2) {
        Write-Output "RESULT: PASS - self-update applies a healthy build and auto-rolls-back an unhealthy one."
    } else {
        Write-Output "RESULT: FAIL - see the values above and %ProgramData%\cc-director\logs."
    }
}
finally {
    Write-Output "=== cleanup ==="
    Cleanup
    Write-Output "done"
}
