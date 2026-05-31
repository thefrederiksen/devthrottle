# Install the CC Director Gateway as the Windows service `cc-gateway-service` (via NSSM) and
# fully retire the Avalonia tray host. Idempotent. MUST run elevated.
#
# End state: ONE thing - the service `cc-gateway-service`. No tray app, no fallback.
#   - The Gateway is the always-on, headless, fleet-wide discovery/aggregation layer. As a
#     service it survives logout/RDP and starts at boot. It holds NO session PTYs, so stopping
#     the tray loses no sessions (Directors re-register within ~15s).
#   - LocalSystem + CC_DIRECTOR_ROOT: a service runs as LocalSystem, whose %LOCALAPPDATA% is the
#     system profile, not the interactive user's. CcStorage honors CC_DIRECTOR_ROOT, so we point
#     the service at this user's cc-director dir explicitly - no per-user account / password needed.
#
# NOTE: ErrorActionPreference stays 'Continue'. nssm/sc write to stderr on idempotent no-ops
# (e.g. stopping a service that does not exist yet); under -Stop in Windows PowerShell 5.1 that
# stderr is wrapped into a TERMINATING NativeCommandError. We check $LASTEXITCODE / verify
# /healthz explicitly instead, so a real failure still stops us loudly.

$ErrorActionPreference = 'Continue'
# Resolve paths from the running (elevated) user's environment - no hard-coded user profile.
$nssm    = "$env:LOCALAPPDATA\Microsoft\WinGet\Links\nssm.exe"
$svc     = "cc-gateway-service"
$exe     = "C:\cc-tools\cc-director-gateway\cc-director-gateway.exe"
$dir     = "C:\cc-tools\cc-director-gateway"
$root    = "$env:LOCALAPPDATA\cc-director"
$logDir  = "$dir\logs"
$trayDir = "$env:LOCALAPPDATA\cc-director\gateway-tray"

# The Gateway hard-requires OPENAI_API_KEY at startup. As a LocalSystem service it does NOT
# inherit your user environment, so we read the key from YOUR user env here (this script runs
# in your elevated session, not as LocalSystem) and inject it into the service environment.
# The secret is read at install time and never written into the repo.
$openaiKey = [Environment]::GetEnvironmentVariable('OPENAI_API_KEY','User')
if ([string]::IsNullOrWhiteSpace($openaiKey)) {
    Write-Output "FAILED: OPENAI_API_KEY is not set in your User environment; the Gateway needs it to start."
    exit 1
}

Write-Output "=== removing the stale 'cc_director' service (so only $svc remains) ==="
& sc.exe stop cc_director   | Out-Null
& sc.exe delete cc_director | Out-Null

Write-Output "=== installing service $svc ==="
New-Item -ItemType Directory -Force $logDir | Out-Null
& $nssm stop $svc           | Out-Null
& $nssm remove $svc confirm | Out-Null
& $nssm install $svc $exe "--port 7878" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Output "FAILED: nssm install exit $LASTEXITCODE"; exit 1 }
& $nssm set $svc AppDirectory $dir | Out-Null
& $nssm set $svc AppEnvironmentExtra "CC_DIRECTOR_ROOT=$root" "OPENAI_API_KEY=$openaiKey" | Out-Null
& $nssm set $svc Start SERVICE_AUTO_START | Out-Null
& $nssm set $svc AppStdout "$logDir\stdout.log" | Out-Null
& $nssm set $svc AppStderr "$logDir\stderr.log" | Out-Null
& $nssm set $svc AppStopMethodConsole 5000 | Out-Null
& $nssm set $svc DisplayName "CC Gateway Service" | Out-Null
& $nssm set $svc Description "CC Director fleet-wide discovery/aggregation Gateway (always-on, headless)." | Out-Null

Write-Output "=== retiring the tray host (stop process + delete app) ==="
Get-Process cc-director-gateway-tray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
if (Test-Path $trayDir) {
    Remove-Item -Recurse -Force $trayDir -ErrorAction SilentlyContinue
    if (Test-Path $trayDir) { Write-Output "WARN: could not fully delete $trayDir (a file may still be locked)" }
    else { Write-Output "deleted tray app: $trayDir" }
}

Write-Output "=== starting service ==="
& $nssm start $svc | Out-Null
Start-Sleep -Seconds 5
Write-Output "service status: $(& $nssm status $svc)"

# Verify the gateway answers on 7878. Fail loudly if not - no fallback.
$ok = $false
for ($i = 0; $i -lt 12; $i++) {
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:7878/healthz" -UseBasicParsing -TimeoutSec 3
        if ($r.StatusCode -eq 200) { Write-Output "HEALTHZ OK: $($r.Content)"; $ok = $true; break }
    } catch { Start-Sleep -Seconds 1 }
}
if (-not $ok) {
    Write-Output "HEALTHZ FAILED - $svc is not answering on 7878. Check $logDir\stderr.log"
    exit 1
}
Write-Output "=== DONE: only '$svc' remains; tray removed; gateway up on 7878 ==="
