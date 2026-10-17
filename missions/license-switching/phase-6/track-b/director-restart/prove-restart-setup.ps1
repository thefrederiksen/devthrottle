<#
.SYNOPSIS
    Proves the owner's Linux restart setup has every setting director-restart.md relies on.

.DESCRIPTION
    Reads devthrottle-director.service and checks each setting the restart behaviour depends on. The unit cannot
    be run here; the check stops a later edit from quietly dropping a setting that keeps a deliberate quit quit,
    keeps the agent sessions alive, or keeps the unit from starting a second Director.

    With -Wsl it also runs the unit's start condition, the exact command line from the file, inside WSL against a
    fake process named cc-director in a throwaway folder - so the "skip when a Director already runs" claim is
    observed, not only read. Nothing is registered, enabled or started as a service.

    Prints PASS or FAIL per check and exits 1 if any check failed.
#>
param([switch]$Wsl)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$unitPath = Join-Path $here 'devthrottle-director.service'
$failures = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "PASS $name" }
    else { Write-Output "FAIL $name - $detail"; $script:failures++ }
}

$unit = @{}
foreach ($line in Get-Content $unitPath) {
    if ($line -match '^\s*([A-Za-z]+)=(.*)$') { $unit[$Matches[1]] = $Matches[2].Trim() }
}

Check 'Restarted only on failure, so exit 0 and SIGTERM stay stopped (Restart=on-failure)' ($unit['Restart'] -eq 'on-failure') "Restart=$($unit['Restart'])"
Check 'A SIGTERM reported as 143 counts as a stop on purpose' (($unit['SuccessExitStatus'] -split '\s+') -contains '143') "SuccessExitStatus=$($unit['SuccessExitStatus'])"
Check 'Sessions survive the Director exiting (KillMode=process)' ($unit['KillMode'] -eq 'process') "KillMode=$($unit['KillMode'])"
Check 'A crash loop gives up (StartLimitBurst set)' ([int]$unit['StartLimitBurst'] -ge 1) 'StartLimitBurst missing'
Check 'Started with the desktop (WantedBy=graphical-session.target)' ($unit['WantedBy'] -eq 'graphical-session.target') "WantedBy=$($unit['WantedBy'])"
Check 'No second Director: the start is skipped while one runs (ExecCondition with pgrep)' (
    $unit['ExecCondition'] -match "pgrep -u %U -x cc-director" -and $unit['ExecCondition'] -match "'!\s") "ExecCondition=$($unit['ExecCondition'])"
$pathValue = if ($unit['Environment'] -match '^PATH=(.*)$') { $Matches[1] } else { '' }
Check 'The agents can be found: the search path names the user install folders' (
    ($pathValue -split ':') -contains '%h/.local/bin' -and ($pathValue -split ':') -contains '%h/.claude/local') "Environment=$($unit['Environment'])"

if ($Wsl) {
    # The command systemd would run, with %U replaced by the user number the same way systemd does.
    $condition = $unit['ExecCondition'] -replace '^/bin/sh -c ', ''
    $condition = $condition.Trim("'")
    $script = @"
set -u
uid=`$(id -u)
cond=`$(printf '%s' "$condition" | sed "s/%U/`$uid/")
d=`$(mktemp -d)
sh -c "`$cond"; echo "none=`$?"
cp /bin/sleep "`$d/cc-director"; "`$d/cc-director" 30 & pid=`$!
sleep 0.5
sh -c "`$cond"; echo "running=`$?"
kill `$pid; wait `$pid 2>/dev/null
rm -rf "`$d"
"@
    # Without a byte-order mark: PowerShell 5.1 otherwise prefixes one, and sh reads it as part of the first word.
    $OutputEncoding = New-Object System.Text.UTF8Encoding $false
    $out = ($script -replace "`r", '') | wsl.exe -e sh -s
    $none = ($out | Select-String '^none=(\d+)').Matches.Groups[1].Value
    $running = ($out | Select-String '^running=(\d+)').Matches.Groups[1].Value
    Check 'WSL: with no Director running the condition lets the start go ahead (exit 0)' ($none -eq '0') "exit=$none"
    Check 'WSL: with a Director running the condition skips the start (exit 1)' ($running -eq '1') "exit=$running"
}

if ($failures -gt 0) { Write-Output "$failures check(s) FAILED"; exit 1 }
Write-Output 'All checks passed.'
exit 0
