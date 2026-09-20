#Requires -Version 5.1
<#
.SYNOPSIS
    Count, from a smart shutdown's own record, how many seats ended WITHOUT a handover document.

.DESCRIPTION
    The one number the mission asks to be compared between runs is how many seats ended without
    handing over. Counting it by eye off a table is how two reports came to give two different
    answers for the same record, so this counts it off the record itself and prints the row it
    counted each seat from - a reader can disagree with a row, which is the point.

    A seat "handed over" when the record carries a handoverPath for it: that is the document the
    Director wrote and named, and it is the same fact the drain state 'drained' is derived from.
    A seat covered by its lead's document has no path of its own and is counted separately, because
    it is neither of the two things - it did not write one, and nothing of it was lost.

.PARAMETER WorkspaceId
    The record to count. May be given more than once; each is counted on its own.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GatewayUrl,
    [string]$Token,
    [string]$TokenFile,
    [Parameter(Mandatory = $true)][string[]]$WorkspaceId
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot "gateway-common.ps1")

$gateway = New-GatewaySession -GatewayUrl $GatewayUrl -Token $Token -TokenFile $TokenFile

foreach ($id in $WorkspaceId) {
    $doc = Invoke-GatewayApi -Gateway $gateway -Method GET -Path "/gateway/workspaces/$id"
    Write-Output ""
    Write-Output "RECORD $id"
    Write-Output ("  shut down at : {0}" -f $doc.completedAtUtc)
    Write-Output ("  kind         : {0}" -f $doc.shutdownKind)
    Write-Output ("  reason       : {0}" -f $doc.drivenByNote)
    Write-Output ("  seats        : {0}" -f @($doc.seats).Count)
    Write-Output ""
    Write-Output ("  {0,-62} {1,-10} {2,-8} {3}" -f "seat", "drainState", "document", "agent")

    $wrote = 0; $covered = 0; $without = 0
    foreach ($s in $doc.seats) {
        $hasDoc = -not [string]::IsNullOrWhiteSpace($s.handoverPath)
        $isCovered = ($s.drainState -eq 'covered')
        # COVERED IS ASKED FIRST, and it has to be. A seat covered by its lead carries the LEAD'S
        # handoverPath on the record, so asking "has it a path" first counts somebody else's document
        # as its own and overstates how many seats wrote one.
        if ($isCovered) { $covered++ } elseif ($hasDoc) { $wrote++ } else { $without++ }
        $docCell = if ($isCovered) { "covered" } elseif ($hasDoc) { "yes" } else { "none" }
        $name = $s.name
        if ($name.Length -gt 60) { $name = $name.Substring(0, 60) }
        Write-Output ("  {0,-62} {1,-10} {2,-8} {3}" -f $name, $s.drainState, $docCell, $s.agent)
    }

    Write-Output ""
    Write-Output ("  wrote a handover document        : {0}" -f $wrote)
    Write-Output ("  covered by a lead's document     : {0}" -f $covered)
    Write-Output ("  ENDED WITHOUT A HANDOVER         : {0} of {1}" -f $without, @($doc.seats).Count)
}
