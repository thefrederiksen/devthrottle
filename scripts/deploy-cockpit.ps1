# Deploy the staged Cockpit build - NO ADMIN required (after grant-service-control.ps1 has run
# once to give your user start/stop rights on cc-gateway-service).
#
# Stops the Gateway service, swaps the Cockpit files, starts the service (which relaunches the
# new Cockpit on 7470). The agent runs this for every future deploy; you don't run anything.

$ErrorActionPreference = 'Stop'
$svc    = 'cc-gateway-service'
$stage  = 'D:\ReposFred\cc-director\local_builds\cockpit-publish'
$target = 'C:\cc-tools\cc-director-cockpit'

if (-not (Test-Path "$stage\cc-director-cockpit.dll")) { Write-Host "ERROR: cockpit build not staged at $stage." ; exit 1 }

Write-Host "Stopping $svc (Directors + sessions are separate and survive)..."
Stop-Service $svc -Force
for ($i = 0; $i -lt 20; $i++) {
  if (-not (Get-Process -Name cc-director-gateway,cc-director-cockpit -ErrorAction SilentlyContinue)) { break }
  Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 1

Write-Host "Swapping in the new Cockpit build..."
# Copy ALL app assemblies (cc-director-cockpit.dll AND its dependencies, e.g.
# CcDirector.Gateway.Contracts.dll). Cherry-picking only the cockpit dll leaves a stale
# Contracts.dll behind; the new cockpit dll is compiled against the new Contracts, so the
# Blazor circuit throws a type/method mismatch on render and the page comes up blank.
# This is a framework-dependent publish, so the publish root holds only the app's own
# managed assemblies - copying all *.dll / *.pdb is correct and future-proofs new deps.
foreach ($dll in Get-ChildItem "$stage\*.dll","$stage\*.pdb") {
  Copy-Item $dll.FullName "$target\$($dll.Name)" -Force
}
foreach ($f in @('cc-director-cockpit.deps.json','cc-director-cockpit.runtimeconfig.json','cc-director-cockpit.staticwebassets.endpoints.json')) {
  if (Test-Path "$stage\$f") { Copy-Item "$stage\$f" "$target\$f" -Force }
}
Copy-Item "$stage\wwwroot\app.css" "$target\wwwroot\app.css" -Force
Copy-Item "$stage\wwwroot\js\*"    "$target\wwwroot\js\" -Force -Recurse

Write-Host "Starting $svc..."
Start-Service $svc

$gwUp = $false; $ckUp = $false
for ($i = 0; $i -lt 25; $i++) {
  Start-Sleep -Seconds 1
  if (-not $gwUp) { try { if ((Invoke-WebRequest 'http://127.0.0.1:7878/healthz' -UseBasicParsing -TimeoutSec 4).StatusCode -eq 200) { $gwUp = $true } } catch {} }
  if (-not $ckUp) { try { if ((Invoke-WebRequest 'http://localhost:7470/' -UseBasicParsing -TimeoutSec 4).StatusCode -eq 200) { $ckUp = $true } } catch {} }
  if ($gwUp -and $ckUp) { break }
}
Write-Host ("Gateway up: {0}    Cockpit up: {1}" -f $gwUp, $ckUp)
$dll = Get-Item "$target\cc-director-cockpit.dll"
Write-Host ("Deployed Cockpit DLL: {0}" -f $dll.LastWriteTime)
