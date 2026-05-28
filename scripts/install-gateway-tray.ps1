<#
.SYNOPSIS
  Publish and install the CC Director Gateway tray app to a stable per-user location.

.DESCRIPTION
  The tray app must launch from a fixed path so the HKCU\...\Run autostart entry it
  registers stays valid across rebuilds. This script publishes a framework-dependent
  Release build to:

      %LOCALAPPDATA%\cc-director\gateway-tray\

  The app self-registers the autostart Run key (pointing at this installed exe) the
  first time it runs. Pass -Launch to start it immediately after install so autostart
  becomes active right away.

.PARAMETER Launch
  Launch the installed exe after installing (this is what activates login-autostart,
  because the app writes the Run key on startup).

.PARAMETER Port
  Port to pass to the launched instance (only used with -Launch). Defaults to the
  gateway's built-in default.

.EXAMPLE
  powershell -NoProfile -File scripts\install-gateway-tray.ps1 -Launch
#>
[CmdletBinding()]
param(
    [switch]$Launch,
    [int]$Port = 0
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repoRoot "src\CcDirector.GatewayApp\CcDirector.GatewayApp.csproj"
$installDir = Join-Path $env:LOCALAPPDATA "cc-director\gateway-tray"
$exeName = "cc-director-gateway-tray.exe"

if (-not (Test-Path $proj)) { throw "Project not found: $proj" }

Write-Host "[install-gateway-tray] Publishing Release build..."
Write-Host "[install-gateway-tray]   project    : $proj"
Write-Host "[install-gateway-tray]   install dir: $installDir"

# If a tray instance is running from the install dir it will lock the exe. Detect and
# stop ONLY a process whose path is inside our install dir (never the user's Directors).
$running = Get-Process -Name ($exeName -replace '\.exe$','') -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) }
foreach ($p in $running) {
    Write-Host "[install-gateway-tray] Stopping existing installed instance PID $($p.Id) to free the exe"
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 1
}

dotnet publish $proj -c Release -o $installDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exePath = Join-Path $installDir $exeName
if (-not (Test-Path $exePath)) { throw "Expected exe not found after publish: $exePath" }

Write-Host ""
Write-Host "[install-gateway-tray] Installed: $exePath"

if ($Launch) {
    # Do NOT use $args here: it is an automatic PowerShell variable, and
    # Start-Process -ArgumentList rejects an empty/null collection, which made -Launch
    # fail whenever no -Port was given. Build an explicit list and only pass it when set.
    $launchArgs = @()
    if ($Port -gt 0) { $launchArgs += @("--port", "$Port") }
    Write-Host "[install-gateway-tray] Launching (this registers the autostart Run key)..."
    $proc = if ($launchArgs.Count -gt 0) {
        Start-Process -FilePath $exePath -ArgumentList $launchArgs -PassThru
    } else {
        Start-Process -FilePath $exePath -PassThru
    }
    Start-Sleep -Seconds 4
    if (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) {
        Write-Host "[install-gateway-tray] Running, PID $($proc.Id). Autostart on login is now active."
    } else {
        Write-Host "[install-gateway-tray] WARNING: process exited immediately - check the gateway log."
    }
} else {
    Write-Host ""
    Write-Host "[install-gateway-tray] To activate login-autostart, launch it once:"
    Write-Host "    & `"$exePath`""
}
