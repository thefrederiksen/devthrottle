#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the DevThrottle local development sign-in stand-in and launches a Director pointed at it,
    so the first-run login flow works end to end with no backend.

.DESCRIPTION
    Sets the shared signing secret and the sign-in URL PROCESS-SCOPED (not at User/Machine level),
    starts tools\devthrottle-dev-signin on loopback, then launches a slot Director that inherits
    those variables. Because the variables are only in this script's process and its children, your
    other (daily-driver) Directors are unaffected.

    Click "Sign in" in the launched Director, pick a provider in the browser, and you land in the app.

.PARAMETER Slot
    Which dev slot Director to launch (cc-director<Slot>.exe in local_builds). Defaults to 5.

.PARAMETER Build
    Build the slot Director first (scripts\local-build-avalonia.ps1 -Slot <Slot>).

.PARAMETER Secret
    The shared HMAC-SHA256 signing secret. Defaults to the tool's built-in dev secret.

.PARAMETER Port
    Port the sign-in tool listens on. Defaults to 8765.

.EXAMPLE
    scripts\run-dev-signin.ps1 -Build
#>
param(
    [int]$Slot = 5,
    [switch]$Build,
    [string]$Secret = "devthrottle-local-dev-secret",
    [int]$Port = 8765
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$signinProject = Join-Path $repoRoot "tools\devthrottle-dev-signin\DevThrottleDevSignin.csproj"
$slotExe = Join-Path $repoRoot "local_builds\cc-director$Slot.exe"

if ($Build) {
    Write-Host "Building slot $Slot ..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "local-build-avalonia.ps1") -Slot $Slot
}

if (-not (Test-Path $slotExe)) {
    Write-Error "Slot Director not found at $slotExe. Run with -Build, or build it first: scripts\local-build-avalonia.ps1 -Slot $Slot"
    exit 1
}

# Process-scoped env: this PowerShell process and the children it starts inherit these; nothing
# persists to User/Machine, so other Directors on this machine are untouched.
$env:DEVTHROTTLE_JWT_SIGNING_SECRET = $Secret
$env:DEVTHROTTLE_SIGNIN_URL         = "http://127.0.0.1:$Port/signin"
$env:DEVTHROTTLE_DEV_SIGNIN_PORT    = "$Port"

Write-Host "Starting dev sign-in tool on http://127.0.0.1:$Port/signin ..." -ForegroundColor Cyan
$signin = Start-Process -FilePath "dotnet" `
    -ArgumentList @("run", "--project", "`"$signinProject`"") `
    -PassThru -WindowStyle Minimized

Start-Sleep -Seconds 3   # give the listener a moment to bind before the Director may open the browser

Write-Host "Launching Director: $slotExe" -ForegroundColor Cyan
Start-Process -FilePath $slotExe -WorkingDirectory (Split-Path -Parent $slotExe)

Write-Host ""
Write-Host "Ready. In the Director, click 'Sign in', then pick a provider in the browser." -ForegroundColor Green
Write-Host "Dev sign-in tool PID: $($signin.Id)  (close that window to stop it)" -ForegroundColor Gray
