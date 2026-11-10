#Requires -Version 5.1
<#
.SYNOPSIS
    Where the test gate keeps its records. Dot-source this file; it defines functions and runs nothing.

.DESCRIPTION
    WHERE THE RECORDS LIVE. Per user, per machine, in a place the process environment cannot move: the
    per-user local application data folder on Windows. A record whose path followed TEMP would be one that
    two shells with different TEMP values never share - and scripts\test-local.ps1 points TEMP into its
    own run folder for every test process.

    THERE IS NO GATE LOCK. Until 17 October 2026 this file was scripts\gate-lock.ps1 and also held the
    release-gate lock, which refused a second -Parked or -Gateway run on the machine with exit 6. The
    owner removed it: every test process keeps its files under its own temporary root and every host binds
    a port the operating system assigns, so gate runs in separate worktrees share nothing and may run at
    the same time, limited only by the machine's memory and disk. The record locations below are
    unchanged, so records written before the lock went are still found.

    THE HOME IS THE PER-USER APPLICATION DATA DIRECTORY ON EVERY PLATFORM. Until issue #4806 this
    function answered "/" on anything that was not Windows, so the records landed at the filesystem
    ROOT - /test-gate/gated, and the runs log at /test-gate/runs.jsonl - which no ordinary user can
    write. A gate that got past the build on a Mac then failed at the very end while recording that it
    had passed. The Windows answer is unchanged, so records written before this are still found.
#>

function Get-GateHomeDirectory {
    # LocalApplicationData is defined on every platform .NET runs on, and it is per user everywhere:
    # %LOCALAPPDATA% on Windows (what this has always returned there, so nothing moves), and
    # ~/Library/Application Support on macOS - the same place the product keeps its own state.
    $localAppData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData, [Environment+SpecialFolderOption]::DoNotVerify)
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        throw "Cannot locate the per-user local application data directory, so the gate records have no environment-independent home."
    }
    return Join-Path $localAppData "cc-director"
}

<#
.SYNOPSIS
    Where the gate records a green, unfiltered -Parked run: one JSON file per run, named by the commit it
    certified. scripts\assert-gated.ps1 reads it; nothing else writes it. The run folder in TEMP is
    deleted after a green run, so this record is the one that outlives it.
#>
function Get-GateRecordDirectory {
    return Join-Path (Join-Path (Get-GateHomeDirectory) "test-gate") "gated"
}

<#
.SYNOPSIS
    Where scripts\test-local.ps1 -ClearFlaky records that every flaky test a candidate's release gate named
    has been deleted or fixed on main. One JSON file per clearance, named by the CANDIDATE it clears.
    scripts\assert-gated.ps1 reads it beside the gate record. Kept apart from the gate records because a
    clearance is not a run of the gate, and must never be mistaken for one.
#>
function Get-GateClearanceDirectory {
    return Join-Path (Split-Path -Parent (Get-GateRecordDirectory)) "cleared"
}

<#
.SYNOPSIS
    The session this process belongs to, for the run record. Every fleet-launched session carries
    CC_SESSION_ID; a run started outside the fleet has no session to name and says so.
#>
function Get-GateSessionName {
    if ([string]::IsNullOrWhiteSpace($env:CC_SESSION_ID)) { return "(no session - identify it by the directory)" }
    return "cc-director session $env:CC_SESSION_ID"
}
