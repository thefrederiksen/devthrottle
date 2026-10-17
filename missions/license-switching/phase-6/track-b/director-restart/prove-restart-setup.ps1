<#
.SYNOPSIS
    Proves the owner's restart setup does what director-restart.md says, without touching a real machine.

.DESCRIPTION
    Two kinds of proof, both run in a throwaway folder under TEMP that is deleted at the end:

    1. The Windows watcher is RUN against a fake Director - a small script that exits with the codes it is
       given and counts how often it was started. No real Director, no scheduled task, no registry.
    2. The macOS launch agent and the Linux unit are READ, and every setting the restart behaviour depends
       on is checked. These files cannot be run here; the check stops a later edit from quietly dropping
       a setting that keeps a deliberate quit quit, or that keeps the agent sessions alive.

    Prints PASS or FAIL per check and exits 1 if any check failed.
#>
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$watcher = Join-Path $here 'watch-director.ps1'
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("director-restart-proof-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$failures = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Output "PASS $name" }
    else { Write-Output "FAIL $name - $detail"; $script:failures++ }
}

# The fake Director: takes the next exit code from codes.txt, writes one line to runs.txt, exits with it.
# "kill" means it force-kills itself, which is what Task Manager or an out-of-memory kill does.
$fake = Join-Path $scratch 'fake-director.ps1'
Set-Content -Path $fake -Encoding ascii -Value @'
param([string]$State)
$codesFile = Join-Path $State 'codes.txt'
$codes = @(Get-Content $codesFile)
$next = $codes[0]
Set-Content -Path $codesFile -Value ($codes | Select-Object -Skip 1) -Encoding ascii
Add-Content -Path (Join-Path $State 'runs.txt') -Value "ran $next" -Encoding ascii
if ($next -eq 'kill') { Stop-Process -Id $PID -Force }
exit ([int]$next)
'@

function Invoke-Watcher([string]$name, [string[]]$codes, [int]$maxRestarts = 5) {
    $state = Join-Path $scratch $name
    New-Item -ItemType Directory -Path $state | Out-Null
    Set-Content -Path (Join-Path $state 'codes.txt') -Value $codes -Encoding ascii
    $log = Join-Path $state 'watch.log'
    $powershell = (Get-Command powershell.exe).Source
    & $powershell -NoProfile -ExecutionPolicy Bypass -File $watcher `
        -DirectorPath $powershell `
        -DirectorArguments "-NoProfile -ExecutionPolicy Bypass -File `"$fake`" -State `"$state`"" `
        -RestartDelaySeconds 0 -MaxRestarts $maxRestarts -LogPath $log
    $watcherExit = $LASTEXITCODE
    $runs = @(Get-Content (Join-Path $state 'runs.txt')).Count
    return [pscustomobject]@{ Exit = $watcherExit; Runs = $runs; Log = (Get-Content $log -Raw) }
}

try {
    # A deliberate quit: the Director exits with 0 once. It must be started exactly once.
    $r = Invoke-Watcher 'quit' @('0')
    Check 'A deliberate quit (exit 0) is not restarted' ($r.Runs -eq 1 -and $r.Exit -eq 0) "runs=$($r.Runs) exit=$($r.Exit)"

    # A crash, then a deliberate quit: started twice, then left alone.
    $r = Invoke-Watcher 'crash-then-quit' @('3', '0')
    Check 'A crash (exit 3) is restarted, and the quit after it is not' ($r.Runs -eq 2 -and $r.Exit -eq 0) "runs=$($r.Runs) exit=$($r.Exit)"

    # A forced kill is not a deliberate quit.
    $r = Invoke-Watcher 'killed' @('kill', '0')
    Check 'A forced kill is restarted' ($r.Runs -eq 2 -and $r.Exit -eq 0) "runs=$($r.Runs) exit=$($r.Exit)"

    # A Director that keeps crashing: 1 start + 3 restarts, then the watcher gives up and says so.
    $r = Invoke-Watcher 'crash-loop' @('3', '3', '3', '3', '3', '3', '3') -maxRestarts 3
    Check 'A crash loop gives up after the restart limit' ($r.Runs -eq 4 -and $r.Exit -eq 1 -and $r.Log -match 'Giving up') "runs=$($r.Runs) exit=$($r.Exit)"

    # A wrong path starts nothing and says what to fix.
    $state = Join-Path $scratch 'missing'
    New-Item -ItemType Directory -Path $state | Out-Null
    $log = Join-Path $state 'watch.log'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $watcher -DirectorPath (Join-Path $state 'no-such.exe') -LogPath $log
    Check 'A wrong Director path starts nothing and says so' ($LASTEXITCODE -eq 2 -and (Get-Content $log -Raw) -match 'is not at') "exit=$LASTEXITCODE"

    # The macOS launch agent.
    [xml]$plist = Get-Content (Join-Path $here 'com.devthrottle.director-keepalive.plist') -Raw
    $dict = $plist.plist.dict
    function PlistValue([string]$key) {
        $keys = @($dict.ChildNodes | Where-Object { $_.NodeType -eq 'Element' })
        for ($i = 0; $i -lt $keys.Count - 1; $i++) {
            if ($keys[$i].Name -eq 'key' -and $keys[$i].InnerText -eq $key) { return $keys[$i + 1] }
        }
        return $null
    }
    $keepAlive = PlistValue 'KeepAlive'
    $successfulExit = $null
    if ($keepAlive) {
        $inner = @($keepAlive.ChildNodes | Where-Object { $_.NodeType -eq 'Element' })
        if ($inner.Count -eq 2 -and $inner[0].InnerText -eq 'SuccessfulExit') { $successfulExit = $inner[1].Name }
    }
    Check 'macOS: restarted only after an exit that was not 0 (KeepAlive SuccessfulExit false)' ($successfulExit -eq 'false') "found '$successfulExit'"
    Check 'macOS: started at sign-in (RunAtLoad true)' ((PlistValue 'RunAtLoad').Name -eq 'true') 'RunAtLoad is not true'
    Check 'macOS: sessions survive the Director exiting (AbandonProcessGroup true)' ((PlistValue 'AbandonProcessGroup').Name -eq 'true') 'AbandonProcessGroup is not true'
    Check 'macOS: at least 10 seconds between starts (ThrottleInterval)' ([int](PlistValue 'ThrottleInterval').InnerText -ge 10) 'ThrottleInterval below 10'

    # The Linux user unit.
    $unit = @{}
    foreach ($line in Get-Content (Join-Path $here 'devthrottle-director.service')) {
        if ($line -match '^\s*([A-Za-z]+)=(.*)$') { $unit[$Matches[1]] = $Matches[2].Trim() }
    }
    Check 'Linux: restarted only on failure, so exit 0 and SIGTERM stay stopped (Restart=on-failure)' ($unit['Restart'] -eq 'on-failure') "Restart=$($unit['Restart'])"
    Check 'Linux: a SIGTERM reported as 143 counts as a stop on purpose' (($unit['SuccessExitStatus'] -split '\s+') -contains '143') "SuccessExitStatus=$($unit['SuccessExitStatus'])"
    Check 'Linux: sessions survive the Director exiting (KillMode=process)' ($unit['KillMode'] -eq 'process') "KillMode=$($unit['KillMode'])"
    Check 'Linux: a crash loop gives up (StartLimitBurst set)' ([int]$unit['StartLimitBurst'] -ge 1) 'StartLimitBurst missing'
    Check 'Linux: started with the desktop (WantedBy=graphical-session.target)' ($unit['WantedBy'] -eq 'graphical-session.target') "WantedBy=$($unit['WantedBy'])"
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $scratch
}

if ($failures -gt 0) { Write-Output "$failures check(s) FAILED"; exit 1 }
Write-Output 'All checks passed.'
exit 0
