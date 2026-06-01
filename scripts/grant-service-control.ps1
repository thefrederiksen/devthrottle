# ONE-TIME elevated: grant your user (soren) start/stop rights on the Gateway service so the
# agent can deploy the Cockpit (Stop-Service -> swap -> Start-Service) WITHOUT admin afterward.
# It only ADDS one ACE (start/stop/query for your SID); it removes nothing. Idempotent.
#
# Usage (elevated):  powershell -ExecutionPolicy Bypass -File D:\ReposFred\cc-director\scripts\grant-service-control.ps1

$ErrorActionPreference = 'Stop'
$svc = 'cc-gateway-service'

$id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object System.Security.Principal.WindowsPrincipal($id)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Write-Host "ERROR: run this from an ADMIN PowerShell (Run as administrator)." -ForegroundColor Red
  exit 1
}

# Grant to whoever runs this. The current elevated token carries the same user SID as the
# normal token, so deriving it here keeps the script portable and free of any machine-specific
# identifier (no hard-coded SID).
$sid = $id.User.Value
$ace = "(A;;CCLCSWRPWPDTLOCRRC;;;$sid)"                    # query/start/stop/interrogate/read

function Get-Sddl {
  $lines = sc.exe sdshow $svc
  $picked = $lines | Where-Object { $_ -match 'D:' }
  return ($picked -join '').Trim()
}

$sddl = Get-Sddl
if (-not $sddl) { Write-Host "ERROR: could not read the service SDDL." -ForegroundColor Red; exit 1 }
Write-Host "Current SDDL: $sddl"

if ($sddl.Contains($sid)) {
  Write-Host "Your user already has an ACE on $svc - nothing to do." -ForegroundColor Green
} else {
  if ($sddl -match 'S:') { $new = $sddl -replace '(S:)', ($ace + '$1') }
  else                   { $new = $sddl + $ace }
  Write-Host "New SDDL:     $new"
  sc.exe sdset $svc $new | Out-Null
  $after = Get-Sddl
  Write-Host "Granted start/stop on $svc to your user." -ForegroundColor Green
  Write-Host "Verify SDDL:  $after"
}

Write-Host ""
Write-Host "Done. The agent can now deploy the Cockpit without admin." -ForegroundColor Green
