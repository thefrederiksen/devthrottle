<#
.SYNOPSIS
  Verify a Gateway-role install of CC Director end to end.

.DESCRIPTION
  Run this AFTER the setup wizard's Gateway install (or
  `cc-director-setup-cli install --role gateway`). It checks that the Gateway
  tray app (devthrottle-gateway.exe) is installed at the canonical per-user
  location, registered to start at logon (HKCU Run key), RUNNING, and that the
  Gateway (7878) and the supervised Cockpit (7470) actually answer.

  Read-only and non-destructive; never needs administrator rights (the whole
  Gateway lifecycle is per-user - docs/plans/gateway-tray-app.md).
  Exit code 0 = every check passed, 1 = one or more failed.

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

Write-Host "=== CC Director Gateway verification (tray app) ==="
Write-Host ("gateway=:{0}  cockpit=:{1}  timeout={2}s" -f $GatewayPort, $CockpitPort, $TimeoutSeconds)
Write-Host ""

# 1. Installed at the canonical per-user path (master spec: docs/install/INSTALLATION.md).
$root  = Join-Path $env:LOCALAPPDATA 'cc-director'
$gwExe = Join-Path $root 'gateway\devthrottle-gateway.exe'
$ckExe = Join-Path $root 'cockpit\devthrottle-cockpit.exe'
Add-Result "Gateway exe installed" (Test-Path $gwExe) $gwExe
Add-Result "Cockpit exe installed" (Test-Path $ckExe) $ckExe

# 2. Autostart Run key (proxy for 'comes back at next logon'; an actual logoff/logon is the
# ultimate test). The tray app registers itself on startup.
$runVal = (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'CcDirectorGateway' -ErrorAction SilentlyContinue).CcDirectorGateway
Add-Result "Autostart Run key" ($null -ne $runVal) ("CcDirectorGateway=" + $runVal)

# 3. Tray app process running from the installed location.
$proc = Get-Process -Name 'devthrottle-gateway' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -ieq $gwExe } | Select-Object -First 1
Add-Result "Tray app running" ($null -ne $proc) ($(if ($proc) { "pid=$($proc.Id)" } else { "no process from $gwExe" }))

# 4. Gateway health endpoint
$gwUrl = "http://127.0.0.1:$GatewayPort/healthz"
Add-Result "Gateway /healthz" (Wait-Http $gwUrl $TimeoutSeconds) $gwUrl

# 5. Cockpit endpoint
$ckUrl = "http://127.0.0.1:$CockpitPort/"
Add-Result "Cockpit responding" (Wait-Http $ckUrl $TimeoutSeconds) $ckUrl

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
    Write-Host "RESULT: PASS - Gateway tray app installed and serving Gateway + Cockpit." -ForegroundColor Green
    if ($tnet) {
        # ONE URL: the Cockpit is served through the Gateway front door (fallback proxy).
        Write-Host ("Open the Cockpit: https://{0}/" -f $tnet)
    } else {
        Write-Host "Open the Cockpit at https://<tailnet-host>/ (Tailscale not detected to build the URL)."
    }
    Write-Host "Log off and back on once, then re-run this script to confirm the tray app comes back automatically."
    exit 0
}

Write-Host ("RESULT: FAIL - {0} check(s) failed:" -f $failed.Count) -ForegroundColor Red
foreach ($f in $failed) { Write-Host ("  - {0}: {1}" -f $f.Check, $f.Detail) }
$logDir = Join-Path $root 'logs'
Write-Host ("Gateway logs: {0}" -f $logDir)
exit 1
