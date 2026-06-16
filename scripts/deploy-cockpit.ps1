# Deploy the staged Cockpit build (DEV deploy: a user-driven file copy into the live install dir).
#
# Asks the Gateway tray app to exit (POST /shutdown - it supervises the Cockpit, so the Cockpit
# goes down with it), swaps the Cockpit files, and relaunches the tray app (which relaunches the
# new Cockpit on 7470).
#
# Canonical location (master spec: docs/install/INSTALLATION.md): the Cockpit binaries live under
# %LOCALAPPDATA%\cc-director\cockpit and the Gateway tray app under %LOCALAPPDATA%\cc-director\gateway.
# Everything is per-user: NO elevation, NO Windows service (docs/plans/gateway-tray-app.md).
# The PRODUCTION no-admin update path is the tray app updating its own + the Cockpit's binaries -
# not this script.
#
# -DefineOnly: dot-source this script to load Sync-CockpitWwwroot (and the path vars) WITHOUT
# running the deploy. The issue #232 proof test uses this so it can exercise the real mirror
# function against temp dirs - one source of truth, no copied logic - and never touches the live
# fleet.
param([switch] $DefineOnly)

$ErrorActionPreference = 'Stop'
$stage      = 'D:\ReposFred\cc-director\local_builds\cockpit-publish'
$root       = "$env:LOCALAPPDATA\cc-director"
$target     = "$root\cockpit"
$gatewayExe = "$root\gateway\devthrottle-gateway.exe"

# Mirror the whole published wwwroot into the live install (issue #232).
#
# The old script cherry-picked wwwroot\app.css + wwwroot\js\* and left everything else stale.
# That silently dropped wwwroot\devthrottle-cockpit.styles.css - the bundle Blazor compiles every
# component's SCOPED *.razor.css into (App.razor links it) - plus its .br/.gz siblings, wwwroot\lib
# (xterm.css) and wwwroot\pages. Result: any deploy that changed a .razor.css shipped with the
# markup updated but its styling stale (hit for real during the #212 W3 deploy). Mirroring the
# entire wwwroot is the standing "cockpit = whole-folder swap" guidance and is future-proof: new
# static assets are picked up automatically, no copy list to maintain.
#
# robocopy /MIR makes the target wwwroot byte-identical to the staged wwwroot (copies new/changed,
# prunes removed). It is deterministic - no fallback. Robocopy exit codes 0-7 are SUCCESS (8+ are
# failures); we translate that explicitly so $LASTEXITCODE does not trip $ErrorActionPreference.
function Sync-CockpitWwwroot {
  param(
    [Parameter(Mandatory)] [string] $StageWwwroot,
    [Parameter(Mandatory)] [string] $TargetWwwroot
  )
  if (-not (Test-Path $StageWwwroot)) { throw "Staged wwwroot not found: $StageWwwroot" }
  New-Item -ItemType Directory -Force $TargetWwwroot | Out-Null
  robocopy $StageWwwroot $TargetWwwroot /MIR /NJH /NJS /NP /NFL /NDL | Out-Null
  $rc = $LASTEXITCODE
  if ($rc -ge 8) { throw "robocopy mirror of wwwroot FAILED with exit code $rc ($StageWwwroot -> $TargetWwwroot)" }
  return $rc
}

if ($DefineOnly) { return }

if (-not (Test-Path "$stage\devthrottle-cockpit.dll")) { Write-Host "ERROR: cockpit build not staged at $stage." ; exit 1 }
if (-not (Test-Path $gatewayExe)) { Write-Host "ERROR: Gateway tray app not installed at $gatewayExe." ; exit 1 }

Write-Host "Asking the Gateway tray app to exit (Directors + sessions are separate and survive)..."
try { Invoke-WebRequest 'http://127.0.0.1:7878/shutdown' -Method POST -UseBasicParsing -TimeoutSec 5 | Out-Null } catch {}
for ($i = 0; $i -lt 20; $i++) {
  if (-not (Get-Process -Name devthrottle-gateway,devthrottle-cockpit -ErrorAction SilentlyContinue)) { break }
  Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 1

Write-Host "Swapping in the new Cockpit build..."
# Copy ALL app assemblies (devthrottle-cockpit.dll AND its dependencies, e.g.
# CcDirector.Gateway.Contracts.dll). Cherry-picking only the cockpit dll leaves a stale
# Contracts.dll behind; the new cockpit dll is compiled against the new Contracts, so the
# Blazor circuit throws a type/method mismatch on render and the page comes up blank.
# This is a framework-dependent publish, so the publish root holds only the app's own
# managed assemblies - copying all *.dll / *.pdb is correct and future-proofs new deps.
foreach ($dll in Get-ChildItem "$stage\*.dll","$stage\*.pdb") {
  Copy-Item $dll.FullName "$target\$($dll.Name)" -Force
}
foreach ($f in @('devthrottle-cockpit.deps.json','devthrottle-cockpit.runtimeconfig.json','devthrottle-cockpit.staticwebassets.endpoints.json')) {
  if (Test-Path "$stage\$f") { Copy-Item "$stage\$f" "$target\$f" -Force }
}
# Mirror the ENTIRE published wwwroot (scoped *.styles.css bundle + .br/.gz, app.css, js, lib,
# pages, _framework) so a .razor.css change deploys fresh instead of stale (issue #232).
Sync-CockpitWwwroot -StageWwwroot "$stage\wwwroot" -TargetWwwroot "$target\wwwroot"

Write-Host "Relaunching the Gateway tray app..."
Start-Process -FilePath $gatewayExe -ArgumentList '--managed' -WorkingDirectory "$root\gateway"

$gwUp = $false; $ckUp = $false
for ($i = 0; $i -lt 25; $i++) {
  Start-Sleep -Seconds 1
  if (-not $gwUp) { try { if ((Invoke-WebRequest 'http://127.0.0.1:7878/healthz' -UseBasicParsing -TimeoutSec 4).StatusCode -eq 200) { $gwUp = $true } } catch {} }
  if (-not $ckUp) { try { if ((Invoke-WebRequest 'http://localhost:7470/' -UseBasicParsing -TimeoutSec 4).StatusCode -eq 200) { $ckUp = $true } } catch {} }
  if ($gwUp -and $ckUp) { break }
}
Write-Host ("Gateway up: {0}    Cockpit up: {1}" -f $gwUp, $ckUp)
$dll = Get-Item "$target\devthrottle-cockpit.dll"
Write-Host ("Deployed Cockpit DLL: {0}" -f $dll.LastWriteTime)
