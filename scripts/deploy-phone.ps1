#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and installs the CC Director Client (MAUI Android) app onto a phone.

.DESCRIPTION
    Connects to an Android device over WiFi adb (when -Phone is given), then builds
    and installs the CcDirectorClient app to it and launches it. Use this to push the
    voice/Wingman/Terminal client to your phone.

    The phone must be on the same network (e.g. the tailnet) and have wireless
    debugging enabled. The app's default gateway is the tailnet host, so on a
    tailnet-connected phone the session roster loads for real.

.PARAMETER Phone
    adb target for a WiFi device, in the form <ip>:<port> (e.g. from "Wireless
    debugging" on the phone). If omitted, the script uses whatever single device is
    already attached (USB or a prior WiFi connection).

.PARAMETER Configuration
    Build configuration: Debug (default) or Release. Debug is auto-signed and is the
    simplest path for personal use. Release needs a signing keystore configured in
    the project.

.PARAMETER NoLaunch
    Install only; do not launch the app afterwards.

.EXAMPLE
    .\scripts\deploy-phone.ps1 -Phone 100.0.0.5:5555
    .\scripts\deploy-phone.ps1                       # device already attached
    .\scripts\deploy-phone.ps1 -Configuration Release
#>
param(
    [string]$Phone = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

$package = "com.ccdirector.client"
$framework = "net10.0-android"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "phone\CcDirectorClient\CcDirectorClient.csproj"

# --- locate adb -------------------------------------------------------------
$sdk = $env:ANDROID_HOME
if ([string]::IsNullOrWhiteSpace($sdk)) { $sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$adb = Join-Path $sdk "platform-tools\adb.exe"
if (-not (Test-Path $adb)) {
    throw "adb not found at '$adb'. Install the Android SDK platform-tools, or set ANDROID_HOME to your SDK path."
}
if (-not (Test-Path $projectPath)) {
    throw "Project not found at '$projectPath'."
}

# --- connect (WiFi) ---------------------------------------------------------
if (-not [string]::IsNullOrWhiteSpace($Phone)) {
    Write-Host "Connecting to $Phone ..."
    $connect = & $adb connect $Phone
    Write-Host $connect
    if ($connect -notmatch "connected") {
        throw "adb could not connect to '$Phone'. Check that wireless debugging is on and the ip:port is current (it changes when toggled)."
    }
}

# --- resolve the target device ----------------------------------------------
$deviceLines = & $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match "\tdevice$" }
if ($deviceLines.Count -eq 0) {
    throw "No attached device. Connect the phone over USB, or pass -Phone <ip>:<port> for wireless debugging."
}
if ([string]::IsNullOrWhiteSpace($Phone)) {
    if ($deviceLines.Count -gt 1) {
        throw "More than one device is attached. Pass -Phone <ip>:<port> (or a USB serial) to pick one."
    }
    $serial = ($deviceLines[0] -split "\t")[0]
} else {
    $serial = $Phone
}
Write-Host "Target device: $serial"

# --- build + install --------------------------------------------------------
Write-Host "Building + installing ($Configuration) ..."
& dotnet build $projectPath -c $Configuration -f $framework -t:Install -p:AdbTarget="-s $serial" --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Build/install failed (dotnet exit code $LASTEXITCODE)."
}

# --- launch -----------------------------------------------------------------
if (-not $NoLaunch) {
    Write-Host "Launching $package ..."
    # adb monkey writes progress to stderr; under -ErrorActionPreference Stop that
    # surfaces as a terminating error even on success, so relax it for this one call.
    $eap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    & $adb -s $serial shell monkey -p $package -c android.intent.category.LAUNCHER 1 2>&1 | Out-Null
    $ErrorActionPreference = $eap
}

Write-Host ""
Write-Host "Done. CcDirectorClient ($Configuration) installed on $serial."
if ($NoLaunch) { Write-Host "Not launched (-NoLaunch). Open it from the phone's app drawer." }
