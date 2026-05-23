# Build CC Recorder and deploy+launch it on your physical Android phone over
# wireless adb. Run this whenever you want the latest build on the phone:
#
#   powershell -ExecutionPolicy Bypass -File phone\update-phone.ps1
#   (or in a Claude Code prompt:  ! powershell -File phone\update-phone.ps1 )
#
# It auto-selects the physical device (ignores the emulator). If the phone
# isn't connected, it tells you how to reconnect wireless debugging.

$ErrorActionPreference = "Stop"

$sdk  = "$env:LOCALAPPDATA\Android\Sdk"
$adb  = "$sdk\platform-tools\adb.exe"
$proj = Join-Path $PSScriptRoot "CcRecorder\CcRecorder.csproj"

$env:ANDROID_HOME     = $sdk
$env:ANDROID_SDK_ROOT = $sdk
$env:JAVA_HOME        = "C:\Program Files\Eclipse Adoptium\jdk-21.0.10.7-hotspot"

# Devices look like:  100.86.144.11:32997   device  product:... model:SM_F721W
# Pick a real phone: an ip:port serial that is NOT the emulator and NOT the
# mDNS (_adb-tls-connect) duplicate.
$lines = & $adb devices | Select-String "\sdevice$"
$phone = $null
foreach ($l in $lines) {
    $serial = ($l.ToString() -split "\s+")[0]
    if ($serial -like "emulator-*") { continue }
    if ($serial -like "*_adb-tls-connect*") { continue }
    if ($serial -match "^\d{1,3}(\.\d{1,3}){3}:\d+$") { $phone = $serial; break }
}

if (-not $phone) {
    Write-Host "No physical phone connected over adb." -ForegroundColor Yellow
    Write-Host "On the phone: Developer options -> Wireless debugging -> note the IP:port," -ForegroundColor Yellow
    Write-Host "then run:  adb connect <ip:port>   (re-pair if it asks)." -ForegroundColor Yellow
    & $adb devices -l
    exit 1
}

Write-Host "Deploying to phone: $phone" -ForegroundColor Cyan
# -t:Run handles Fast Deployment (pushes assemblies), so it's incremental/fast.
dotnet build $proj -f net10.0-android -t:Run -p:AdbTarget="-s $phone"
Write-Host "Done. Latest build is on the phone." -ForegroundColor Green
