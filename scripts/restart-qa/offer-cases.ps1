#Requires -Version 5.1
<#
.SYNOPSIS
    Make the three reasons a restart record stops being offered when the Director starts, on records
    that really happened, and leave every one of them still in the restart history.

.DESCRIPTION
    Four rules decide whether a record interrupts the owner at start-up: there is something left to
    act on, he has not already used it, he has not cleared it, and it is not older than seven days.
    Two of them cannot be reached by waiting - a record eight days old, and a record the owner has
    cleared - so this reaches them deliberately:

      -Age      : move a record's shutdown time eight days back, so the seven day rule bites.
                  The original time is printed, and -Restore puts it back.
      -Clear    : write the mark the "Don't ask again" button writes.
      -Use      : write the mark that bringing ONE seat back writes.

    NOTHING IS DELETED, and that is half of what is being shown: the owner ruled on 20 September 2026
    that a record is never removed, so after each treatment the record must still be in the history,
    still carrying its whole offer, and saying why it stopped appearing by itself.

    A CAPTURED RECORD CANNOT BE FORGED, which is why this works on records that exist rather than
    planting new ones: the Gateway refuses a PUT that CREATES a captured workspace, and restores a
    stored record's machine, Director, start time and clearing over any write. What an update may
    change is the shutdown time, which is the one field the age rule reads.

.PARAMETER Age
    The record to move eight days back.

.PARAMETER Restore
    Put a record's shutdown time back to the value given with -RestoreTo. Used after -Age.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GatewayUrl,
    [string]$Token,
    [string]$TokenFile,
    [Parameter(Mandatory = $true)][string]$DirectorId,
    [string]$Age,
    [int]$AgeDays = 8,
    [string]$Clear,
    [string]$Use,
    [string]$Restore,
    [string]$RestoreTo
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot "gateway-common.ps1")

$gateway = New-GatewaySession -GatewayUrl $GatewayUrl -Token $Token -TokenFile $TokenFile
# Write-Output, not Write-Host: Write-Host bypasses the pipeline in Windows PowerShell 5.1, so a
# caller piping this into Tee-Object gets an empty transcript - which is how one run of this script
# left no evidence file at all.
function Say([string]$text) { Write-Output "[offer-cases] $text" }

function Set-ShutdownTime([string]$id, [string]$stampUtc) {
    $doc = Invoke-GatewayApi -Gateway $gateway -Method GET -Path "/gateway/workspaces/$id"
    $was = $doc.completedAtUtc
    $doc.completedAtUtc = $stampUtc
    $saved = Invoke-GatewayApi -Gateway $gateway -Method PUT -Path "/gateway/workspaces/$id" -Body $doc
    Say "$id : shutdown time was $was, is now $($saved.completedAtUtc)"
    return $was
}

if ($Age) {
    $doc = Invoke-GatewayApi -Gateway $gateway -Method GET -Path "/gateway/workspaces/$Age"
    $stamp = ([DateTime]::Parse($doc.completedAtUtc)).ToUniversalTime().AddDays(-$AgeDays)
    Say "AGE $Age by $AgeDays day(s) - so the seven day rule stops it being offered"
    $was = Set-ShutdownTime -id $Age -stampUtc $stamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
    Say "  to put it back: -Restore $Age -RestoreTo $was"
}

if ($Clear) {
    Say "CLEAR $Clear - the mark the 'Don't ask again' button writes"
    $null = Invoke-GatewayApi -Gateway $gateway -Method POST -Path "/gateway/workspaces/$Clear/restore/marks" `
        -Body @{ directorId = $DirectorId; kind = 'cleared' }
    Say "  written"
}

if ($Use) {
    $doc = Invoke-GatewayApi -Gateway $gateway -Method GET -Path "/gateway/workspaces/$Use"
    # The seat is chosen by NAME in the output, so a reader can see which one was marked and check the
    # record for it afterwards. The first is taken; any one ends the start-up offer for the whole record,
    # which is the owner's own rule and is the thing being shown.
    $seat = @($doc.seats)[0]
    Say "USE $Use - the mark that bringing ONE seat back writes, on seat:"
    Say "  $($seat.sessionId)  $($seat.name)"
    $null = Invoke-GatewayApi -Gateway $gateway -Method POST -Path "/gateway/workspaces/$Use/restore/marks" `
        -Body @{ directorId = $DirectorId; kind = 'reopened'; seatSessionId = $seat.sessionId; reopenedSessionId = [guid]::NewGuid().ToString() }
    Say "  written"
}

if ($Restore) {
    if (-not $RestoreTo) { throw "-Restore needs -RestoreTo <the original shutdown time>." }
    Say "RESTORE $Restore to $RestoreTo"
    $null = Set-ShutdownTime -id $Restore -stampUtc $RestoreTo
}
