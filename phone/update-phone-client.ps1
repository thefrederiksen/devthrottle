# Build CC Director Client and deploy+launch it on your physical Android phone over
# wireless adb. Run this whenever you want the latest build on the phone:
#
#   powershell -ExecutionPolicy Bypass -File phone\update-phone-client.ps1
#   (or in a Claude Code prompt:  ! powershell -File phone\update-phone-client.ps1 )
#
# This is the CC Director Client (Sessions + FIFO voice app), NOT the CC Recorder.
# It auto-selects the physical device (ignores the emulator). If the phone isn't
# connected, it tells you how to reconnect wireless debugging.

$ErrorActionPreference = "Stop"

$sdk  = "$env:LOCALAPPDATA\Android\Sdk"
$adb  = "$sdk\platform-tools\adb.exe"
$proj = Join-Path $PSScriptRoot "CcDirectorClient\CcDirectorClient.csproj"

$env:ANDROID_HOME     = $sdk
$env:ANDROID_SDK_ROOT = $sdk
$env:JAVA_HOME        = "C:\Program Files\Eclipse Adoptium\jdk-21.0.10.7-hotspot"

# Devices look like:  <ip>:<port>   device  product:... model:<model>
# Pick a real phone: an ip:port serial that is NOT the emulator and NOT the
# mDNS (_adb-tls-connect) duplicate.
$lines = & $adb devices | Select-String "\sdevice$"
$phone = $null
foreach ($l in $lines) {
    $serial = ($l.ToString() -split "\s+")[0]
    if ($serial -like "emulator-*") { continue }   # skip the dev emulator
    # First non-emulator device wins: a wireless IP:port serial or an
    # mDNS (_adb-tls-connect) serial both identify the physical phone.
    $phone = $serial; break
}

if (-not $phone) {
    Write-Host "No physical phone connected over adb." -ForegroundColor Yellow
    Write-Host "On the phone: Developer options -> Wireless debugging -> note the IP:port," -ForegroundColor Yellow
    Write-Host "then run:  adb connect <ip:port>   (re-pair if it asks)." -ForegroundColor Yellow
    & $adb devices -l
    exit 1
}

Write-Host "Deploying CC Director Client to phone: $phone" -ForegroundColor Cyan
# -t:Run handles Fast Deployment (pushes assemblies), so it's incremental/fast.
dotnet build $proj -f net10.0-android -t:Run -p:AdbTarget="-s $phone"
Write-Host "Done. Latest CC Director Client build is on the phone." -ForegroundColor Green
