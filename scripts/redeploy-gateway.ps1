# Rebuild the Gateway tray app and swap it into the installed per-user location, then
# smoke-check GET /cockpit. Run from any checkout: paths derive from this script's location.
# No elevation: the Gateway is a per-user tray app under %LOCALAPPDATA% (docs/plans/gateway-tray-app.md).
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$gwDir = "$env:LOCALAPPDATA\cc-director\gateway"
$stage = Join-Path $repoRoot "local_builds\_gw-deploy"

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
dotnet publish (Join-Path $repoRoot "src\CcDirector.GatewayApp\CcDirector.GatewayApp.csproj") `
    -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $stage --nologo

# Ask the running tray app to exit gracefully, then wait for the exe to unlock.
try { Invoke-WebRequest 'http://127.0.0.1:7878/shutdown' -Method POST -UseBasicParsing -TimeoutSec 5 | Out-Null } catch {}
for ($i = 0; $i -lt 20; $i++) {
  if (-not (Get-Process -Name cc-director-gateway -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$gwDir*" })) { break }
  Start-Sleep -Milliseconds 500
}

New-Item -ItemType Directory -Force $gwDir | Out-Null
Copy-Item "$stage\cc-director-gateway.exe" $gwDir -Force
Start-Process -FilePath "$gwDir\cc-director-gateway.exe" -ArgumentList '--managed' -WorkingDirectory $gwDir

Start-Sleep 5
Invoke-RestMethod 'http://127.0.0.1:7878/cockpit'
