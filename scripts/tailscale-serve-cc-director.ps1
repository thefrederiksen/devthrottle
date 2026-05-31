<#
.SYNOPSIS
  Configure Tailscale Serve so every running cc-director service is reachable
  from the tailnet over HTTPS.

.DESCRIPTION
  Detects running cc-director processes (Gateway + each Director slot), finds
  the Control API port each one is listening on, and emits (or applies) the
  matching `tailscale serve` mappings.

  Convention:
    cc-director-gateway        -> https=443 (front door, no port in phone URL)
    cc-director<N>             -> https=<same-port> (Director's own Control API port)

  Tailscale provides one wildcard cert for the node's MagicDNS name that is
  valid on any port, so no per-port cert work is needed.

  This script is idempotent. Re-running it just re-emits the same mappings.

.PARAMETER Apply
  Actually run the `tailscale serve` commands. Without this, only prints them.

.PARAMETER Reset
  Run `tailscale serve reset` before configuring, removing ALL existing serve
  mappings (including non-cc-director ones, if any). Use when starting clean.

.EXAMPLE
  .\tailscale-serve-cc-director.ps1
  Dry run. Prints the commands it would execute.

.EXAMPLE
  .\tailscale-serve-cc-director.ps1 -Apply
  Apply the mappings for whatever Directors are currently running.

.EXAMPLE
  .\tailscale-serve-cc-director.ps1 -Reset -Apply
  Wipe existing serve config, then re-apply for currently running Directors.
#>
[CmdletBinding()]
param(
  [switch]$Apply,
  [switch]$Reset
)

$ErrorActionPreference = 'Stop'

$tailscale = "C:\Program Files\Tailscale\tailscale.exe"
if (-not (Test-Path $tailscale)) {
  throw "tailscale.exe not found at $tailscale. Install Tailscale or update this script's path."
}

# Gateway uses 7878; Directors use the Control API range 7879..7898 (see
# src\CcDirector.ControlApi\PortAllocator.cs).
$controlApiMin = 7878
$controlApiMax = 7898

function Get-CcDirectorListeners {
  $procs = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -like 'cc-director-*'
  }
  if (-not $procs) { return @() }

  $listening = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue
  $result = @()
  foreach ($p in $procs) {
    $ports = $listening |
      Where-Object { $_.OwningProcess -eq $p.Id -and $_.LocalPort -ge $controlApiMin -and $_.LocalPort -le $controlApiMax } |
      Select-Object -ExpandProperty LocalPort -Unique |
      Sort-Object
    foreach ($port in $ports) {
      $result += [PSCustomObject]@{
        ProcessName = $p.ProcessName
        ProcessId   = $p.Id
        Port        = $port
        IsGateway   = ($p.ProcessName -eq 'cc-director-gateway')
      }
    }
  }
  return $result
}

if ($Reset) {
  Write-Host "Resetting all Tailscale Serve mappings..."
  & $tailscale serve reset
  if ($LASTEXITCODE -ne 0) {
    throw "tailscale serve reset failed with exit code $LASTEXITCODE."
  }
}

$listeners = Get-CcDirectorListeners
if (-not $listeners -or $listeners.Count -eq 0) {
  Write-Host "No cc-director processes are currently listening on Control API ports ($controlApiMin..$controlApiMax)."
  Write-Host "Start the Gateway and at least one Director, then re-run."
  return
}

Write-Host "Detected cc-director services:"
foreach ($l in $listeners) {
  Write-Host ("  PID {0,-6}  {1,-26}  port {2}" -f $l.ProcessId, $l.ProcessName, $l.Port)
}
Write-Host ""

$mappings = foreach ($l in $listeners) {
  $httpsPort = if ($l.IsGateway) { 443 } else { $l.Port }
  [PSCustomObject]@{
    HttpsPort   = $httpsPort
    BackendPort = $l.Port
    ProcessName = $l.ProcessName
  }
}

Write-Host "Tailscale Serve mappings:"
foreach ($m in $mappings) {
  Write-Host ("  --https={0,-5} -> http://localhost:{1}  ({2})" -f $m.HttpsPort, $m.BackendPort, $m.ProcessName)
}

if (-not $Apply) {
  Write-Host ""
  Write-Host "(Dry run. Re-run with -Apply to execute.)"
  return
}

Write-Host ""
Write-Host "Applying..."
foreach ($m in $mappings) {
  & $tailscale serve --bg "--https=$($m.HttpsPort)" "http://localhost:$($m.BackendPort)"
  if ($LASTEXITCODE -ne 0) {
    throw "tailscale serve failed for --https=$($m.HttpsPort) (exit $LASTEXITCODE). Other mappings may have been applied."
  }
}

Write-Host ""
Write-Host "---Serve status---"
& $tailscale serve status
