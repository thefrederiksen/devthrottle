# Rebuild the Gateway and swap it into the installed Windows service, then smoke-check
# GET /cockpit. Run from any checkout: paths derive from this script's location.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$gwDir = "$env:ProgramFiles\CC Director\gateway"
$stage = Join-Path $repoRoot "local_builds\_gw-deploy"

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
dotnet publish (Join-Path $repoRoot "src\CcDirector.Gateway\CcDirector.Gateway.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $stage --nologo

Stop-Service cc-gateway-service -Force
Start-Sleep 3
Get-ChildItem $gwDir -Force | Remove-Item -Recurse -Force
Copy-Item "$stage\*" $gwDir -Recurse -Force
Start-Service cc-gateway-service

Start-Sleep 5
Invoke-RestMethod 'http://127.0.0.1:7878/cockpit'
