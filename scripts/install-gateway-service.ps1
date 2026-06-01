# Deploy the CC Director Gateway as the Windows service `cc-gateway-service` (via NSSM), and have
# that always-on service SUPERVISE the Cockpit UI. Idempotent. MUST run elevated.
#
# End state: ONE service, `cc-gateway-service`, that:
#   - is the always-on, headless, fleet-wide discovery/aggregation Gateway (survives logout/RDP,
#     starts at boot, holds NO session PTYs - stopping it loses no sessions), and
#   - launches and keeps the Cockpit web app running (CC_COCKPIT_MANAGED=1). The Cockpit stays its
#     OWN process, so it restarts without bouncing the Gateway, but is never left dead.
#
# Dev mode is the opposite default: a Gateway run by hand (dotnet run, no CC_COCKPIT_MANAGED) does
# NOT launch a Cockpit, so the developer controls it (dotnet run) with hot reload.
#
# LocalSystem + CC_DIRECTOR_ROOT: a service runs as LocalSystem, whose %LOCALAPPDATA% is the system
# profile, not the interactive user's. CcStorage honors CC_DIRECTOR_ROOT, so we point the service at
# this user's cc-director dir - no per-user account/password. OPENAI_API_KEY is read from the user's
# env at install time (the Gateway requires it) and injected into the service; never written to the repo.
#
# NOTE: ErrorActionPreference stays 'Continue'. nssm/sc write to stderr on idempotent no-ops; under
# -Stop in Windows PowerShell 5.1 that stderr becomes a TERMINATING NativeCommandError. We check
# $LASTEXITCODE / verify HTTP explicitly so a real failure still stops us loudly.

$ErrorActionPreference = 'Continue'
$nssm    = "$env:LOCALAPPDATA\Microsoft\WinGet\Links\nssm.exe"
$svc     = "cc-gateway-service"
$repo    = Split-Path -Parent $PSScriptRoot            # repo root (this script lives in scripts/)
# Canonical layout (master spec: docs/install/INSTALLATION.md): machine-wide service
# binaries under %ProgramFiles%\CC Director, machine-wide service data under
# %ProgramData%\cc-director. The retired C:\cc-tools root must not be used.
$pfRoot  = "$env:ProgramFiles\CC Director"
$pdRoot  = "$env:ProgramData\cc-director"
$gwDir   = "$pfRoot\gateway"
$gwExe   = "$gwDir\cc-director-gateway.exe"
$ckDir   = "$pfRoot\cockpit"
$ckExe   = "$ckDir\cc-director-cockpit.exe"
$root    = "$env:LOCALAPPDATA\cc-director"   # the primary user's per-user root (vault stays per-user)
$logDir  = "$pdRoot\logs"
$trayDir = "$env:LOCALAPPDATA\cc-director\gateway-tray"

$openaiKey = [Environment]::GetEnvironmentVariable('OPENAI_API_KEY','User')
if ([string]::IsNullOrWhiteSpace($openaiKey)) {
    Write-Output "FAILED: OPENAI_API_KEY is not set in your User environment; the Gateway needs it to start."
    exit 1
}

function Wait-Url($url, $tries) {
    for ($i = 0; $i -lt $tries; $i++) {
        try { if ((Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { return $true } } catch { }
        Start-Sleep -Seconds 1
    }
    return $false
}

Write-Output "=== stopping $svc (if running) so its exe can be republished ==="
& $nssm stop $svc | Out-Null
Start-Sleep -Seconds 2

Write-Output "=== publishing gateway + cockpit (current build, incl. the Cockpit supervisor) ==="
New-Item -ItemType Directory -Force $logDir | Out-Null
& dotnet publish "$repo\src\CcDirector.Gateway\CcDirector.Gateway.csproj" -c Release -o $gwDir --nologo
if ($LASTEXITCODE -ne 0) { Write-Output "FAILED: gateway publish exit $LASTEXITCODE"; exit 1 }
& dotnet publish "$repo\src\CcDirector.Cockpit\CcDirector.Cockpit.csproj" -c Release -o $ckDir --nologo
if ($LASTEXITCODE -ne 0) { Write-Output "FAILED: cockpit publish exit $LASTEXITCODE"; exit 1 }

Write-Output "=== removing the stale 'cc_director' service (so only $svc remains) ==="
& sc.exe stop cc_director   | Out-Null
& sc.exe delete cc_director | Out-Null

Write-Output "=== installing/configuring service $svc ==="
& $nssm remove $svc confirm | Out-Null
& $nssm install $svc $gwExe "--port 7878" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Output "FAILED: nssm install exit $LASTEXITCODE"; exit 1 }
& $nssm set $svc AppDirectory $gwDir | Out-Null
# Gateway needs CC_DIRECTOR_ROOT + OPENAI_API_KEY; the Cockpit supervisor needs CC_COCKPIT_MANAGED=1
# and the Cockpit exe path.
& $nssm set $svc AppEnvironmentExtra "CC_DIRECTOR_ROOT=$root" "OPENAI_API_KEY=$openaiKey" "CC_COCKPIT_MANAGED=1" "CC_COCKPIT_EXE=$ckExe" | Out-Null
& $nssm set $svc Start SERVICE_AUTO_START | Out-Null
& $nssm set $svc AppStdout "$logDir\stdout.log" | Out-Null
& $nssm set $svc AppStderr "$logDir\stderr.log" | Out-Null
& $nssm set $svc AppStopMethodConsole 5000 | Out-Null
& $nssm set $svc DisplayName "CC Gateway Service" | Out-Null
& $nssm set $svc Description "CC Director Gateway (always-on, headless) + Cockpit supervisor." | Out-Null

Write-Output "=== retiring tray + handing the Cockpit to the supervisor ==="
Get-Process cc-director-gateway-tray -ErrorAction SilentlyContinue | Stop-Process -Force
# Stop any hand-run Cockpit so the service's supervisor is the single owner of port 7470.
Get-Process cc-director-cockpit -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
if (Test-Path $trayDir) { Remove-Item -Recurse -Force $trayDir -ErrorAction SilentlyContinue }

Write-Output "=== starting service ==="
& $nssm start $svc | Out-Null
Start-Sleep -Seconds 5
Write-Output "service status: $(& $nssm status $svc)"

if (-not (Wait-Url "http://127.0.0.1:7878/healthz" 15)) {
    Write-Output "FAILED: gateway not answering on 7878. Check $logDir\stderr.log"
    exit 1
}
Write-Output "gateway OK on 7878"

if (-not (Wait-Url "http://127.0.0.1:7470/" 25)) {
    Write-Output "FAILED: supervised Cockpit not answering on 7470. Check $logDir\stderr.log"
    exit 1
}
Write-Output "=== DONE: '$svc' up on 7878 and supervising the Cockpit on 7470 ==="
