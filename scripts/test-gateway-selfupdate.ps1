<#
.SYNOPSIS
  Live test of the Gateway self-update mechanism against a THROWAWAY tray instance.

.DESCRIPTION
  Exercises the process-based self-update end to end (POST /shutdown -> swap -> relaunch ->
  /healthz -> auto-rollback) on a disposable Gateway instance on an alternate port. It NEVER
  touches the live Gateway (7878) or Cockpit (7470): the throwaway runs with
  CC_GATEWAY_NO_TAILSCALE=1 (no serve-mapping writes), --no-autostart (no Run-key write),
  and never --managed (no Cockpit supervision). No elevation needed.

  Two checks:
   1. Happy path: stage a good build, expect it to swap in and stay healthy (outcome Updated, exit 0).
   2. Rollback path: simulate a bad build by pointing the helper's health probe at a DEAD port, so the
      new build "fails" health and the helper rolls back to the previous build (outcome RolledBack,
      exit 1) - the throwaway instance is healthy again afterward.

  Builds the Gateway tray app from current source so the exe has the --apply-update mode.
  Cleans up the throwaway processes and temp files at the end.

.PARAMETER Port
  Alternate port the throwaway instance binds (default 7899 - NOT the live 7878/7470).

.PARAMETER Root
  ISOLATED install root for this test run (issue #176). The self-update helper records the new
  version into <Root>\config\setup\installed.json and any rollback pin into update-pins.json. By
  pointing CC_DIRECTOR_ROOT at a throwaway temp dir, this test NEVER writes the FAKE 9.9.x versions
  into the PRODUCTION %LOCALAPPDATA%\cc-director setup state. Defaults to a fresh temp dir under the
  test work dir. Do NOT point this at %LOCALAPPDATA%\cc-director.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-gateway-selfupdate.ps1
#>
[CmdletBinding()]
param(
    [int]$Port = 7899,
    [int]$DeadPort = 7897,
    [string]$Root
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path $env:TEMP ("cc-gwsu-" + [Guid]::NewGuid().ToString('N'))
$installed = Join-Path $work 'installed\cc-director-gateway.exe'
$staged    = Join-Path $work 'staged\cc-director-gateway.exe'
$runArgs   = "--port $Port --no-autostart"

# --- Isolation (issue #176): the self-update helper writes installed.json / update-pins.json into
# CC_DIRECTOR_ROOT\config\setup. Point that at a THROWAWAY root so the FAKE 9.9.x versions this test
# stages never land in the production %LOCALAPPDATA%\cc-director setup state (a silent half-install).
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Join-Path $work 'root' }
$prodRoot = Join-Path $env:LOCALAPPDATA 'cc-director'
if ([IO.Path]::GetFullPath($Root).TrimEnd('\') -ieq [IO.Path]::GetFullPath($prodRoot).TrimEnd('\')) {
    Write-Output "FAILED: -Root must not be the production install root ($prodRoot). Refusing to pollute production setup state."
    exit 2
}
New-Item -ItemType Directory -Force $Root | Out-Null
$env:CC_DIRECTOR_ROOT = $Root
Write-Output "[OK] isolated install root: $Root (production $prodRoot is untouched)"

# Snapshot the production setup files so we can PROVE they are byte-identical before vs after the run.
$prodSetup = Join-Path $prodRoot 'config\setup'
$prodInstalled = Join-Path $prodSetup 'installed.json'
$prodPins      = Join-Path $prodSetup 'update-pins.json'
function Get-FileHashOrNull([string]$p) {
    if (Test-Path $p) { return (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash }
    return '(absent)'
}
$prodInstalledHashBefore = Get-FileHashOrNull $prodInstalled
$prodPinsHashBefore      = Get-FileHashOrNull $prodPins
Write-Output "[hash-before] installed.json=$prodInstalledHashBefore  update-pins.json=$prodPinsHashBefore"

# The throwaway must never touch the live Tailscale serve mappings; children inherit this.
$env:CC_GATEWAY_NO_TAILSCALE = '1'

function Stop-Throwaway {
    # Only processes running from OUR temp dir - never the live Gateway.
    Get-Process -Name cc-director-gateway -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like "$work*" } |
        ForEach-Object { try { $_.Kill() } catch {} }
}

function Cleanup {
    try { Invoke-WebRequest "http://127.0.0.1:$Port/shutdown" -Method POST -UseBasicParsing -TimeoutSec 3 | Out-Null } catch {}
    Start-Sleep -Seconds 2
    Stop-Throwaway
    Start-Sleep -Seconds 1
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}

function Healthy([int]$p, [int]$tries = 15) {
    for ($i = 0; $i -lt $tries; $i++) {
        try { if ((Invoke-WebRequest "http://127.0.0.1:$p/healthz" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { return $true } } catch {}
        Start-Sleep -Seconds 1
    }
    return $false
}

Write-Output "=== building Gateway tray app from current source ==="
$pub = Join-Path $work 'publish'
& dotnet publish "$repo\src\CcDirector.GatewayApp\CcDirector.GatewayApp.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $pub --nologo | Out-Null
$built = Join-Path $pub 'cc-director-gateway.exe'
if (-not (Test-Path $built)) { Write-Output "FAILED: gateway build not found at $built"; Cleanup; exit 1 }

New-Item -ItemType Directory -Force (Split-Path $installed) | Out-Null
New-Item -ItemType Directory -Force (Split-Path $staged)    | Out-Null
Copy-Item $built $installed -Force
Copy-Item $built $staged -Force

try {
    Write-Output "=== starting throwaway Gateway on port $Port ==="
    Start-Process -FilePath $installed -ArgumentList $runArgs -WorkingDirectory (Split-Path $installed)
    if (-not (Healthy $Port)) { Write-Output "FAILED: throwaway Gateway did not come up on $Port"; Cleanup; exit 1 }
    Write-Output "[OK] throwaway Gateway healthy on $Port"

    # --- Happy path: good staged build, real health port -> Updated ---
    # NOT `& ... | Out-Null` (children inherit the stdout pipe -> hangs until the relaunched
    # gateway exits) and NOT Start-Process -Wait (PS 5.1 waits for the whole process TREE,
    # which includes the relaunched gateway). .WaitForExit() waits for the helper alone.
    Write-Output "=== TEST 1 (happy): apply staged build, health on $Port ==="
    $hp = Start-Process -FilePath $staged -ArgumentList "--apply-update --target `"$installed`" --port $Port --new-version 9.9.9 --args `"$runArgs`"" -PassThru
    $hp.WaitForExit()
    $rc1 = $hp.ExitCode
    $h1 = Healthy $Port
    Write-Output ("  exit={0}  healthy={1}  (expect exit 0, healthy true)" -f $rc1, $h1)

    # --- Rollback path: health probe points at a DEAD port -> new build 'unhealthy' -> RolledBack ---
    # NOTE: --port steers BOTH the shutdown POST and the health probe, so the stop request must
    # still reach the real instance; we pass the real port for relaunch args but the dead port to
    # the helper's probe by shutting the instance down ourselves first.
    Write-Output "=== TEST 2 (rollback): apply staged build, health on DEAD port $DeadPort ==="
    try { Invoke-WebRequest "http://127.0.0.1:$Port/shutdown" -Method POST -UseBasicParsing -TimeoutSec 3 | Out-Null } catch {}
    Start-Sleep -Seconds 2
    $hp2 = Start-Process -FilePath $staged -ArgumentList "--apply-update --target `"$installed`" --port $DeadPort --new-version 9.9.8 --args `"$runArgs`"" -PassThru
    $hp2.WaitForExit()
    $rc2 = $hp2.ExitCode
    $h2 = Healthy $Port   # after rollback the previous build relaunches with the REAL port args
    Write-Output ("  exit={0}  healthy-after-rollback={1}  (expect exit 1, healthy true)" -f $rc2, $h2)

    # --- Isolation proof (issue #176): the production setup files must be byte-identical to before. ---
    $prodInstalledHashAfter = Get-FileHashOrNull $prodInstalled
    $prodPinsHashAfter      = Get-FileHashOrNull $prodPins
    Write-Output ""
    Write-Output "[hash-after ] installed.json=$prodInstalledHashAfter  update-pins.json=$prodPinsHashAfter"
    $prodUntouched = ($prodInstalledHashAfter -eq $prodInstalledHashBefore) -and ($prodPinsHashAfter -eq $prodPinsHashBefore)
    if ($prodUntouched) {
        Write-Output "[OK] production setup state byte-identical (installed.json + update-pins.json unchanged)."
    } else {
        Write-Output "[X] PRODUCTION SETUP STATE CHANGED - this test polluted $prodSetup (issue #176 regression)."
    }

    Write-Output ""
    if ($rc1 -eq 0 -and $h1 -and $rc2 -ne 0 -and $h2 -and $prodUntouched) {
        Write-Output "RESULT: PASS - self-update applies a healthy build, auto-rolls-back an unhealthy one, and leaves production setup state untouched."
    } else {
        Write-Output "RESULT: FAIL - see the values above and $($env:CC_DIRECTOR_ROOT)\logs."
    }
}
finally {
    Write-Output "=== cleanup ==="
    Cleanup
    Write-Output "done"
}
