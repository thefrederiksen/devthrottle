<#
.SYNOPSIS
    Starts the Director and starts it again if it crashes. A deliberate quit stays quit.

.DESCRIPTION
    This is the Windows piece of the owner's own restart setup (see ..\director-restart.md). The product
    installs nothing that restarts a crashed Director, and the Windows Task Scheduler cannot do it on its
    own: its "restart if the task fails" setting only fires when the task fails to START, never when the
    program it started later exits with an error. So a Task Scheduler task runs this script at sign-in,
    and this script does the watching.

    The rule is the exit code, and only the exit code:
      * 0 means the Director ended on purpose - the person quit it, it handed over to an updated build, or
        the launcher stopped it cleanly to restart it. The script stops watching and does not start it.
      * anything else means it did not end on purpose - an unhandled error, a forced kill (Task Manager,
        an out-of-memory kill) - and the script starts it again after a pause.

    A Director that keeps crashing is not started forever: after MaxRestarts restarts inside WindowMinutes
    the script gives up and writes why, so a broken build cannot spin the machine.

    What it does NOT cover: when the Director hands over to another process of its own (an update, a
    rollback, a Smart Restart through the launcher), the first process exits with 0 and the script stops
    watching. The process that took over runs without a watcher until the next sign-in.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File watch-director.ps1 -DirectorPath "C:\Users\alice\AppData\Local\cc-director\app\cc-director.exe"
#>
param(
    [Parameter(Mandatory = $true)] [string]$DirectorPath,
    [string]$DirectorArguments = '',
    [int]$RestartDelaySeconds = 10,
    [int]$MaxRestarts = 5,
    [int]$WindowMinutes = 10,
    [string]$LogPath = (Join-Path $env:LOCALAPPDATA 'cc-director\logs\director-watch.log')
)

$ErrorActionPreference = 'Stop'

function Write-WatchLog([string]$message) {
    $line = "{0} [watch-director] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message
    $folder = Split-Path -Parent $LogPath
    if ($folder -and -not (Test-Path $folder)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
    Add-Content -Path $LogPath -Value $line -Encoding ascii
}

if (-not (Test-Path -LiteralPath $DirectorPath)) {
    Write-WatchLog "The Director is not at $DirectorPath. Nothing was started. Fix the path in the scheduled task."
    exit 2
}

$restarts = New-Object System.Collections.Generic.List[datetime]
while ($true) {
    $startArgs = @{ FilePath = $DirectorPath; PassThru = $true }
    if ($DirectorArguments) { $startArgs.ArgumentList = $DirectorArguments }
    $process = Start-Process @startArgs
    Write-WatchLog "Started the Director, process $($process.Id)."
    $process.WaitForExit()
    $code = $process.ExitCode

    if ($code -eq 0) {
        Write-WatchLog "The Director ended on purpose (exit code 0). Not starting it again."
        exit 0
    }

    $now = Get-Date
    $cutoff = $now.AddMinutes(-$WindowMinutes)
    $restarts.RemoveAll({ param($t) $t -lt $cutoff }) | Out-Null
    if ($restarts.Count -ge $MaxRestarts) {
        Write-WatchLog ("The Director crashed again (exit code {0}) after {1} restarts in {2} minutes. " +
            "Giving up so a broken build does not spin the machine. Start it by hand once the cause is fixed.") -f $code, $restarts.Count, $WindowMinutes
        exit 1
    }

    $restarts.Add($now)
    Write-WatchLog "The Director did not end on purpose (exit code $code). Starting it again in $RestartDelaySeconds seconds (restart $($restarts.Count) of $MaxRestarts in $WindowMinutes minutes)."
    if ($RestartDelaySeconds -gt 0) { Start-Sleep -Seconds $RestartDelaySeconds }
}
