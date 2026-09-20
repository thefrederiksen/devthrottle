#Requires -Version 5.1
<#
.SYNOPSIS
    End sessions on ONE named Director and wait until the Gateway no longer lists them.

.DESCRIPTION
    Between runs a Director has to be put back to empty, and after a run that was cancelled the
    sessions it kept are still there. This ends them and WAITS for each to be gone, because a stop
    that was accepted and a session that has left are two different facts.

    THE ONE THING IT WILL NOT DO IS GUESS WHOSE SESSIONS THESE ARE. It takes a Director id and ends
    only sessions the Gateway says belong to that Director; there is no "all sessions" verb and no
    match by name. A run pointed at the wrong Director would then end somebody's real work, so the
    Director is named every time and nothing is inferred from a port or a folder.

.PARAMETER DirectorId
    The Director whose sessions are to be ended. Required, and no default.

.PARAMETER SessionIds
    End only these. The default ends every session the Gateway lists on that Director.

.PARAMETER SeatsReport
    End the seats named in a report populate-sessions.ps1 wrote, instead of naming ids by hand.

.PARAMETER WaitSeconds
    How long to wait for the Gateway to stop listing them.

.EXAMPLE
    .\remove-sessions.ps1 -GatewayUrl http://127.0.0.1:7911 -TokenFile ... -DirectorId 3be6c633-...
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GatewayUrl,
    [string]$Token,
    [string]$TokenFile,
    [Parameter(Mandatory = $true)][string]$DirectorId,
    [string[]]$SessionIds,
    [string]$SeatsReport,
    [int]$WaitSeconds = 120
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot "gateway-common.ps1")

function Say([string]$text) { Write-Host "[remove] $text" }

$gateway = New-GatewaySession -GatewayUrl $GatewayUrl -Token $Token -TokenFile $TokenFile

function Get-OnDirector {
    # NOT returned with the comma operator. A caller wraps this in @(), and @() around an array that
    # is already wrapped gives an array holding ONE element - the inner array. That happened: seven
    # sessions became one "session" whose id was all seven joined together, every call 404ed, and the
    # script then reported the Director empty while all seven were still running.
    $live = Get-ApiList (Invoke-GatewayApi -Gateway $gateway -Method GET -Path "/sessions") "sessions"
    return @($live | Where-Object { $_.directorId -ieq $DirectorId })
}

$wanted = @()
if ($SeatsReport) {
    if (-not (Test-Path $SeatsReport)) { throw "no seats report at $SeatsReport." }
    $wanted += @((Get-Content $SeatsReport -Raw | ConvertFrom-Json).seats | ForEach-Object { $_.sessionId })
}
if ($SessionIds) { $wanted += $SessionIds }

$onDirector = @(Get-OnDirector)
$targets = @(if ($wanted.Count -gt 0) { $onDirector | Where-Object { $wanted -contains $_.sessionId } } else { $onDirector })

Say "Director $DirectorId holds $($onDirector.Count) session(s); ending $($targets.Count)"
foreach ($s in $targets) {
    try {
        Invoke-GatewayApi -Gateway $gateway -Method POST -Path "/sessions/$($s.sessionId)/stop" `
            -Body @{ reason = "restart quality assurance harness: putting the Director back to empty between runs" } -TimeoutSec 60 | Out-Null
        Say "  stop sent to $($s.sessionId) [$($s.name)]"
    } catch {
        Say "  stop REFUSED for $($s.sessionId) [$($s.name)]: $($_.Exception.Message)"
    }
    # The deletion request is asked for ONLY when the stop left the session behind. Asking anyway
    # answers "session not found", which is true and reads like a failure - and a harness whose job
    # is evidence must not print failures that are really successes.
    Start-Sleep -Milliseconds 500
    if (@(Get-OnDirector) | Where-Object { $_.sessionId -ieq $s.sessionId }) {
        try {
            Invoke-GatewayApi -Gateway $gateway -Method POST -Path "/sessions/$($s.sessionId)/request-deletion" `
                -Body @{ reason = "restart quality assurance harness: putting the Director back to empty" } -TimeoutSec 60 | Out-Null
            Say "  deletion requested for $($s.sessionId), which the stop left behind"
        } catch {
            Say "  deletion request REFUSED for $($s.sessionId): $($_.Exception.Message)"
        }
    }
}

$ids = @($targets | ForEach-Object { $_.sessionId })
if ($ids.Count -eq 0) { Say "nothing to wait for"; return }

$gone = Wait-ForCondition -What "the Gateway to stop listing $($ids.Count) session(s)" -Seconds $WaitSeconds -Test {
    $still = @(@(Get-OnDirector) | Where-Object { $ids -contains $_.sessionId })
    if ($still.Count -gt 0) { return $false }
    return $true
}

$left = @(@(Get-OnDirector) | Where-Object { $ids -contains $_.sessionId })
if ($left.Count -gt 0) {
    foreach ($s in $left) { Say "STILL LISTED: $($s.sessionId) [$($s.name)] state=$($s.activityState)" }
    Say "these were asked to stop and have not gone. Nothing was forced; say so where this is used."
} else {
    Say "the Director holds none of them any more"
}
