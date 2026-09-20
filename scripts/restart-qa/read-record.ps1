#Requires -Version 5.1
<#
.SYNOPSIS
    Read what a smart shutdown left on the Gateway, and write it out so a person can disagree with it.

.DESCRIPTION
    The record of a smart shutdown is a workspace on the Gateway: the machine, the Director, why it
    happened, and one seat per session with its agent, repository, mission, role, owner, conversation
    id, handover path, drain state and restore decision. That record - not a script printing PASS -
    is the evidence for most of the failure cases in mission document section 7.

    This prints a table a reader can check at a glance and saves the whole document as JSON beside
    it, so nothing is lost to the summary.

    It knows no rig: a Gateway address, a credential, and optionally which machine and Director to
    keep.

.PARAMETER Machine
    Keep only records taken on this machine. Omit to keep every record.

.PARAMETER DirectorId
    Keep only records of this Director. Omit to keep every Director's.

.PARAMETER Newest
    How many records to read in full, newest first. The default is 1.

.PARAMETER SaveTo
    Where to write the full documents, as JSON.

.PARAMETER TableTo
    Where to write the readable table. It is also printed.

.EXAMPLE
    .\read-record.ps1 -GatewayUrl http://127.0.0.1:7911 -TokenFile ... -Machine SOREN_NORTH `
        -SaveTo .\case-1-record.json -TableTo .\case-1-record.txt
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GatewayUrl,
    [string]$Token,
    [string]$TokenFile,
    [string]$Machine,
    [string]$DirectorId,
    [int]$Newest = 1,
    [string]$SaveTo,
    [string]$TableTo
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot "gateway-common.ps1")

$gateway = New-GatewaySession -GatewayUrl $GatewayUrl -Token $Token -TokenFile $TokenFile

$summaries = Get-ApiList (Invoke-GatewayApi -Gateway $gateway -Method GET -Path "/gateway/workspaces") "workspaces"


if ($Machine) { $summaries = @($summaries | Where-Object { $_.machine -ieq $Machine }) }

# The Director is NOT filtered here. The listing route answers with summaries, and a summary carries
# no directorId - so matching one against it threw every record away and printed "NO RECORD MATCHED",
# which reads exactly like a run that wrote no record. It is applied to the full document below,
# where the field is really there.
$directorFilteredAtDocument = [bool]$DirectorId

# Newest first. The summary carries the capture time; a record with none sorts last rather than being
# dropped, because a record that exists is a fact whatever its header says.
$summaries = @($summaries | Sort-Object -Property @{ Expression = { $_.createdUtc }; Descending = $true })

$lines = New-Object System.Collections.Generic.List[string]
function Line([string]$s) { $lines.Add($s); Write-Host $s }

Line "records on $($gateway.Url): $($summaries.Count) on machine '$Machine'"
if ($directorFilteredAtDocument) { Line "director '$DirectorId' is matched on each full record, not on the listing, which carries no Director" }
if ($summaries.Count -eq 0) {
    Line "NO RECORD MATCHED. That is the answer, not an error - say so where it is used."
}

$documents = @()
$kept = 0
foreach ($s in $summaries) {
    if ($kept -ge $Newest) { break }
    $doc = Invoke-GatewayApi -Gateway $gateway -Method GET -Path "/gateway/workspaces/$($s.id)"
    if ($DirectorId -and ($doc.directorId -ine $DirectorId)) {
        Line "skipped $($s.id): it belongs to Director $($doc.directorId)"
        continue
    }
    $documents += $doc
    $kept++

    Line ""
    Line "=============================================================================="
    Line "record        : $($doc.id)"
    Line "name          : $($doc.name)"
    Line "machine       : $($doc.machine)   director: $($doc.directorName) ($($doc.directorId))"
    Line "origin        : $($doc.origin)    shutdownKind: $($doc.shutdownKind)"
    Line "reason        : $($doc.reason)"
    Line "created       : $($doc.createdUtc)   started: $($doc.startedAtUtc)"
    Line "completed     : $($doc.completedAtUtc)   cancelled: $($doc.cancelledAtUtc)   updated: $($doc.updatedUtc)"
    Line "versions      : before $($doc.directorVersionBefore)   after $($doc.directorVersionAfter)"
    Line "restoreAfterRestart: $($doc.restoreAfterRestart)"
    Line "seats         : $(@($doc.seats).Count)"
    Line ""
    Line ("{0,-38} {1,-10} {2,-18} {3,-9} {4}" -f "seat", "agent", "drainState", "restore", "handover / conversation")
    Line ("{0,-38} {1,-10} {2,-18} {3,-9} {4}" -f ("-" * 38), ("-" * 10), ("-" * 18), ("-" * 9), ("-" * 30))
    foreach ($seat in $doc.seats) {
        $name = if ($seat.name) { $seat.name } else { "(no name)" }
        if ($name.Length -gt 38) { $name = $name.Substring(0, 38) }
        $restore = if ($seat.restore) { $seat.restore.decision } else { "(none)" }
        $where = ""
        if ($seat.handoverPath)     { $where = "handover: $(Split-Path -Leaf $seat.handoverPath)" }
        if ($seat.claudeSessionId)  { $where = ($where + "  conversation: $($seat.claudeSessionId)").Trim() }
        $drain = if ($seat.drainState) { $seat.drainState } else { "(none)" }
        Line ("{0,-38} {1,-10} {2,-18} {3,-9} {4}" -f $name, $seat.agent, $drain, $restore, $where)
    }

    # The sweep. A handover is written by an agent, and an agent will put a password in one when it
    # is told to - so the Director sweeps what it collected before the record is kept. The sweep
    # PROVES ITSELF FIRST against known patterns, and that proof is printed here beside the findings:
    # a run reporting "0 findings" with 0 of 10 patterns proved is an instrument that was not working,
    # not a handover that was clean, and the two must never read the same.
    Line ""
    if ($doc.integrity) {
        $g = $doc.integrity
        # THE FIELD NAMES HERE ARE THE WIRE'S, AND THEY WERE WRONG UNTIL 20 SEPTEMBER 2026. This block
        # read filesSwept, patternName, filePath, lineNumber and sweptAtUtc; the record carries
        # documentsSwept, pattern, file, line and checkedAtUtc. The count printed BLANK, which reads as
        # nothing swept, and the first run that actually found a planted credential THREW here -
        # Split-Path on a null - so the one case the sweep exists for was the one case this could not
        # print. readyToRestart is printed too: it is the sweep's verdict, and a reader needs it.
        Line "the secret sweep:"
        Line ("  documents swept    : {0}" -f $g.documentsSwept)
        Line ("  patterns proved    : {0} of {1}" -f $g.sweepPatternsProved, $g.sweepPatternsTotal)
        $proofFailures = @($g.sweepProofFailures)
        if ($proofFailures.Count -gt 0) {
            Line ("  PROOF FAILURES     : {0} - the instrument did not pass its own test, so a clean result here means nothing" -f $proofFailures.Count)
            foreach ($pf in $proofFailures) { Line ("    - {0}" -f $pf) }
        }
        $findings = @($g.secretFindings)
        Line ("  findings           : {0}" -f $findings.Count)
        foreach ($f in $findings) {
            Line ("    - {0} in {1} (line {2}): {3}" -f $f.pattern, (Split-Path -Leaf $f.file), $f.line, $f.redactedExcerpt)
        }
        Line ("  ready to restart   : {0}" -f $g.readyToRestart)
        if ($g.notReadyReason) { Line ("  not ready because  : {0}" -f $g.notReadyReason) }
        if ($g.checkedAtUtc) { Line ("  swept at           : {0}" -f $g.checkedAtUtc) }
    } else {
        Line "the secret sweep: THIS RECORD CARRIES NO INTEGRITY SECTION AT ALL. That is not a clean sweep; it is no sweep to read."
    }

    # The parts a reader has to be able to check WITHOUT reading the JSON: who reported to whom, and
    # every reason a seat carries.
    Line ""
    if ($doc.ownerQuestions -and @($doc.ownerQuestions).Count -gt 0) {
        Line "owner questions gathered by the run:"
        foreach ($q in $doc.ownerQuestions) { Line ("  from {0}: {1}" -f $q.fromName, $q.question) }
        Line ""
    }
    foreach ($seat in $doc.seats) {
        $bits = @()
        if ($seat.reportsTo)   { $bits += "reports to $($seat.reportsTo)" }
        if ($seat.mission)     { $bits += "mission '$($seat.mission.name)'" }
        if ($seat.role)        { $bits += "role '$($seat.role)'" }
        if ($seat.restore -and $seat.restore.why) { $bits += "restore why: $($seat.restore.why)" }
        if ($seat.blockedReason) { $bits += "blocked: $($seat.blockedReason)" }
        if ($seat.coveredBy)     { $bits += "covered by $($seat.coveredBy)" }
        if ($seat.coveredNote)   { $bits += "covered note: $($seat.coveredNote)" }
        if ($seat.closedAtUtc)   { $bits += "closed $($seat.closedAtUtc)" }
        if ($seat.restoredSessionId) { $bits += "restored as $($seat.restoredSessionId)" }
        if ($bits.Count -gt 0) { Line ("  {0}: {1}" -f $seat.sessionId, ($bits -join "; ")) }
    }
}

if ($SaveTo) {
    $dir = Split-Path -Parent $SaveTo
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $documents | ConvertTo-Json -Depth 12 | Set-Content -Path $SaveTo -Encoding ascii
    Write-Host "[record] full document(s) saved to $SaveTo"
}
if ($TableTo) {
    $dir = Split-Path -Parent $TableTo
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [System.IO.File]::WriteAllLines($TableTo, $lines)
    Write-Host "[record] table saved to $TableTo"
}
