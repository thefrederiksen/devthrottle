<#
.SYNOPSIS
  Verify a Gateway-role install of CC Director end to end.

.DESCRIPTION
  Run this AFTER the setup wizard's Gateway install (or
  `cc-director-setup-cli install --role gateway`). It checks that the
  cc-gateway-service Windows service is installed, set to auto-start (so it
  survives a reboot), and RUNNING, and that the Gateway (7878) and the
  supervised Cockpit (7470) actually answer.

  Read-only and non-destructive. Does NOT require administrator rights (it will
  try Start-Service if the service is stopped, which is a no-op if you lack the
  right). Exit code 0 = every check passed, 1 = one or more failed.

.PARAMETER GatewayPort
  Gateway health port (default 7878).

.PARAMETER CockpitPort
  Cockpit port (default 7470).

.PARAMETER TimeoutSeconds
  How long to wait for each endpoint to answer before giving up (default 60).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-gateway.ps1
#>
[CmdletBinding()]
param(
    [int]$GatewayPort = 7878,
    [int]$CockpitPort = 7470,
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Continue'
$svc = 'cc-gateway-service'
$results = [System.Collections.Generic.List[object]]::new()

function Add-Result([string]$name, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Check = $name; Pass = $ok; Detail = $detail })
    $tag = if ($ok) { '[PASS]' } else { '[FAIL]' }
    Write-Host ("{0} {1} - {2}" -f $tag, $name, $detail)
}

function Wait-Http([string]$url, [int]$timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    do {
        try {
            $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5
            if ($resp.StatusCode -eq 200) { return $true }
        } catch {
            # not up yet; keep waiting
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    return $false
}

Write-Host "=== CC Director Gateway verification ==="
Write-Host ("service={0}  gateway=:{1}  cockpit=:{2}  timeout={3}s" -f $svc, $GatewayPort, $CockpitPort, $TimeoutSeconds)
Write-Host ""

# 1. Service installed
$service = Get-Service -Name $svc -ErrorAction SilentlyContinue
if ($null -eq $service) {
    Add-Result "Service installed" $false "$svc not found - run the Gateway install first"
} else {
    Add-Result "Service installed" $true "$svc found"

    # 2. Auto-start (proxy for 'survives a reboot'; an actual reboot is the ultimate test)
    $startMode = (Get-CimInstance -ClassName Win32_Service -Filter "Name='$svc'" -ErrorAction SilentlyContinue).StartMode
    Add-Result "Auto-start on boot" ($startMode -eq 'Auto') "StartMode=$startMode"

    # 2b. Native service, not the old NSSM wrapper. This is a real check: a Gateway still
    # running through NSSM means the native install/migration did NOT take effect, even though
    # the endpoints below may answer (the old service is still serving). Do not give false confidence.
    $binPath = (Get-CimInstance -ClassName Win32_Service -Filter "Name='$svc'" -ErrorAction SilentlyContinue).PathName
    $isNssm = $binPath -and ($binPath -match 'nssm')
    Add-Result "Native service (not NSSM)" (-not $isNssm) ("binPath=" + $binPath)

    # 3. Running (try to start it once if stopped, then re-check)
    if ($service.Status -ne 'Running') {
        Write-Host ("[INFO] service Status={0}; attempting Start-Service..." -f $service.Status)
        try { Start-Service -Name $svc -ErrorAction Stop } catch { Write-Host ("[INFO] Start-Service failed: {0}" -f $_.Exception.Message) }
        Start-Sleep -Seconds 3
        $service.Refresh()
    }
    Add-Result "Service running" ($service.Status -eq 'Running') ("Status={0}" -f $service.Status)
}

# 4. Gateway health endpoint
$gwUrl = "http://127.0.0.1:$GatewayPort/healthz"
Add-Result "Gateway /healthz" (Wait-Http $gwUrl $TimeoutSeconds) $gwUrl

# 5. Cockpit endpoint
$ckUrl = "http://127.0.0.1:$CockpitPort/"
Add-Result "Cockpit responding" (Wait-Http $ckUrl $TimeoutSeconds) $ckUrl

# Informational only: where the new installer's canonical binaries live (master spec:
# docs/install/INSTALLATION.md). A Gateway serving correctly from another path is still a
# working Gateway, so this does NOT affect the verdict - it just tells you whether THIS install
# is at the canonical location the new installer uses.
$pf = [Environment]::GetFolderPath('ProgramFiles')
$gwExe = Join-Path $pf 'CC Director\gateway\cc-director-gateway.exe'
$ckExe = Join-Path $pf 'CC Director\cockpit\cc-director-cockpit.exe'
$gwAt = if (Test-Path $gwExe) { 'present' } else { 'NOT at canonical path' }
$ckAt = if (Test-Path $ckExe) { 'present' } else { 'NOT at canonical path' }
Write-Host ("[INFO] Gateway exe ({0}): {1}" -f $gwAt, $gwExe)
Write-Host ("[INFO] Cockpit exe ({0}): {1}" -f $ckAt, $ckExe)

# Resolve the Tailscale host so we show a URL that works from the phone / anywhere on the tailnet -
# never localhost (localhost is useless off this machine).
function Get-TailnetHost {
    $ts = "C:\Program Files\Tailscale\tailscale.exe"
    if (Test-Path $ts) {
        try {
            $dns = (& $ts status --json 2>$null | ConvertFrom-Json).Self.DNSName
            if ($dns) { return $dns.TrimEnd('.') }
        } catch { }
        try { $ip = (& $ts ip -4 2>$null); if ($ip) { return ($ip | Select-Object -First 1).Trim() } } catch { }
    }
    return $null
}

Write-Host ""
$failed = @($results | Where-Object { -not $_.Pass })
if ($failed.Count -eq 0) {
    $tnet = Get-TailnetHost
    Write-Host "RESULT: PASS - Gateway service installed and serving Gateway + Cockpit." -ForegroundColor Green
    if ($tnet) {
        Write-Host ("Open the Cockpit: http://{0}:{1}" -f $tnet, $CockpitPort)
        Write-Host ("Gateway:          http://{0}:{1}" -f $tnet, $GatewayPort)
    } else {
        Write-Host ("Open the Cockpit on the tailnet host on port {0} (Tailscale not detected to build the URL)." -f $CockpitPort)
    }
    Write-Host "Reboot once and re-run this script to confirm the service comes back automatically."
    exit 0
}

Write-Host ("RESULT: FAIL - {0} check(s) failed:" -f $failed.Count) -ForegroundColor Red
foreach ($f in $failed) { Write-Host ("  - {0}: {1}" -f $f.Check, $f.Detail) }
$logDir = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'cc-director\logs'
Write-Host ("Service logs: {0}" -f $logDir)
exit 1
