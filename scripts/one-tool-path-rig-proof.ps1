#Requires -Version 5.1
<#
.SYNOPSIS
    Proves, on a real Director start, that the path ends up holding exactly ONE DevThrottle tools
    folder - the machine's master - and that a Director serving a throwaway root does not touch the
    real saved path.

.DESCRIPTION
    Builds a throwaway root that looks like the computer this mission was written for: a master tools
    folder at the root, a copy inside the default Director's folder, a copy inside a named Director's
    folder, the nested copy the leak produced, a copy that has already been deleted and is still on the
    path, and a bystander folder inside a Director that is nothing to do with the tools.

    It then starts a slot Director on that root through a scheduled task - never from the agent's own
    process tree (CLAUDE.md rule 0b) - with a crafted process path carrying all six, reads the line the
    Director itself writes at start, and stops it with its named signal (CLAUDE.md rule 0b).

    HOW THE SIGNAL NAME IS FOUND, AND WHY NOT FROM THE INSTANCE REGISTRATION. The signal is named for
    the Director's own identifier. This script reads that identifier out of the Director's OWN LOG, from
    the line it writes the moment the listener comes up: "Lifecycle signals listening for directorId=".
    An earlier version of this script looked for the identifier in the instance registration file
    instead and never found one in time: that file is written inside ControlApiHost.StartAsync, on a
    background task that starts well after the listener, so every run of this proof ended in the
    force-kill branch below while the script's own description said it had signalled. The log line is
    written by the same process, names the identifier, and exists exactly when the listener does.

    FORCE-KILL IS STILL THE LAST RESORT AND IT STILL EXISTS. If the identifier never appears, or nothing
    is listening for it, or the signalled shutdown does not exit within its grace period, the script
    force-kills the process it started - guarded to the slot 5 or higher executable at the path it
    launched. A force-killed Director gets no chance to clean up and leaves an interrupted crash journal
    entry behind (issue #960); on a throwaway rig root that journal is discarded with the root, which is
    why it is tolerable here and is not tolerable against a real Director. Every run prints which of the
    two happened, and the report at the end repeats it.

    WHAT IT PROVES
      1. After one Director start the running path holds exactly one DevThrottle tools folder, and it
         is the master.
      2. Every copy the master replaces came off, including the one whose folder no longer exists.
      3. The bystander folder inside a Director's own folder is still there.
      4. The real user's SAVED path is byte-for-byte what it was before the run.

    WHAT IT DOES NOT PROVE
      The saved-path rewrite itself. The saved path is one per-user registry value on this machine and
      a rig must never write it - which is the product rule proved by point 4, not a gap in the rig.
      The rewrite that WOULD be written is covered by FleetToolPathRepairTests.

    NOTHING IN THIS SCRIPT EVER TOUCHES %LOCALAPPDATA%\cc-director. It refuses to run if the rig root
    it was given resolves inside the real one.

.PARAMETER Exe
    The slot Director to start. Must be slot 5 or higher; slots 1 to 4 and the installed app belong to
    the owner and are never touched.

.PARAMETER RigRoot
    Where to build the throwaway root. Defaults to a fresh directory under the repository's
    local_builds folder.

.EXAMPLE
    powershell -NoProfile -File scripts\one-tool-path-rig-proof.ps1
#>
param(
    [string]$Exe = "",
    [string]$RigRoot = "",
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repoRoot "scripts\local-build\cc-director5.exe" }

$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$What, [bool]$Ok, [string]$Detail) {
    if ($Ok) { Write-Host "  PASS  $What" }
    else { Write-Host "  FAIL  $What -- $Detail"; $failures.Add("$What -- $Detail") }
}

# ---- Refuse anything that could reach the owner's own Director or root ----

if (-not (Test-Path $Exe)) {
    Write-Host "[rig] ERROR: no slot Director at $Exe. Build one first:"
    Write-Host "      powershell -NoProfile -File scripts\local-build-avalonia.ps1 -Slot 5 -OutputDir `"$repoRoot\scripts\local-build`""
    exit 1
}
$exeName = [System.IO.Path]::GetFileNameWithoutExtension($Exe)
if ($exeName -notmatch '^cc-director(\d+)$' -or [int]$Matches[1] -lt 5) {
    Write-Host "[rig] ERROR: $exeName is not a test slot. Slots 1 to 4 and the installed app are the owner's; use slot 5 or higher."
    exit 1
}
$slot = [int]$Matches[1]

$realRoot = Join-Path $env:LOCALAPPDATA 'cc-director'
if (-not $RigRoot) { $RigRoot = Join-Path $repoRoot ("local_builds\one-tool-path-rig-" + [guid]::NewGuid().ToString("N").Substring(0, 8)) }
$RigRoot = [System.IO.Path]::GetFullPath($RigRoot)
if ($RigRoot.TrimEnd('\') -ieq $realRoot.TrimEnd('\') -or $RigRoot.StartsWith($realRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "[rig] ERROR: refusing to build a rig inside the real root ($realRoot)."
    exit 1
}

Write-Host "[rig] slot=$slot exe=$Exe"
Write-Host "[rig] rig root=$RigRoot"

# ---- The saved path, read exactly as the product reads it, before anything runs ----

function Read-SavedPathRaw {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
    try { return [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    finally { $key.Dispose() }
}
$savedPathBefore = Read-SavedPathRaw
Write-Host "[rig] saved user path captured ($($savedPathBefore.Length) characters). It must not change."

# ---- Build the rig: the machine that prompted this mission, in miniature ----

$directorHome = Join-Path $RigRoot 'instances\default'
$master       = Join-Path $RigRoot 'bin'
$ownCopy      = Join-Path $directorHome 'bin'
$namedCopy    = Join-Path $RigRoot 'instances\slot-7\bin'
$nestedCopy   = Join-Path $directorHome 'instances\default\bin'
$deletedCopy  = Join-Path $RigRoot 'instances\gone\bin'
$bystander    = Join-Path $directorHome 'scripts'

foreach ($d in @($master, $ownCopy, $namedCopy, $nestedCopy, $bystander)) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}
foreach ($d in @($master, $ownCopy, $namedCopy, $nestedCopy)) {
    Set-Content -Path (Join-Path $d 'cc-devthrottle.cmd') -Value "@echo off`r`necho $d" -Encoding ascii
}
Set-Content -Path (Join-Path $bystander 'notes.txt') -Value 'a file inside a Director folder that is nothing to do with the tools' -Encoding ascii
# The copy that is already gone: on the path, and not on disk. This is the state the deletion in the
# next phase leaves behind, so it is the one that must not be assumed away.
Write-Host "[rig] built: master, own copy, named copy, nested copy, bystander; $deletedCopy is on the path and NOT on disk"

$craftedPath = @(
    $ownCopy,
    $namedCopy,
    $nestedCopy,
    $deletedCopy,
    $bystander,
    "$env:SystemRoot\system32",
    $env:SystemRoot,
    $master
) -join ';'

# ---- Launch through Task Scheduler, never from this process tree (CLAUDE.md rule 0b) ----

$wrapper = Join-Path $RigRoot 'start-rig-director.cmd'
$wrapperLines = @(
    '@echo off',
    "set ""CC_DIRECTOR_ROOT=$RigRoot""",
    "set ""PATH=$craftedPath""",
    "start """" ""$Exe"""
)
Set-Content -Path $wrapper -Value ($wrapperLines -join "`r`n") -Encoding ascii

$taskName = "cc-director$slot-one-tool-path-rig"
$action = New-ScheduledTaskAction -Execute $wrapper -WorkingDirectory (Split-Path -Parent $Exe)
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Force | Out-Null

$before = @(Get-Process -Name "cc-director$slot" -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
Start-ScheduledTask -TaskName $taskName
Write-Host "[rig] scheduled task $taskName started; waiting for the Director"

$directorPid = 0
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    $now = @(Get-Process -Name "cc-director$slot" -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ($_.Path -ieq (Resolve-Path $Exe).Path) })
    $fresh = @($now | Where-Object { $before -notcontains $_.Id })
    if ($fresh.Count -gt 0) { $directorPid = $fresh[0].Id; break }
    Start-Sleep -Milliseconds 300
}

$logLine = ''
$logFile = ''
if ($directorPid -gt 0) {
    Write-Host "[rig] Director running as PID $directorPid; waiting for the line it writes at start"
    # Searched from the rig root rather than from a guessed folder: WHERE the Director keeps its own
    # data is its business, and a proof that hard-codes it reports "the Director said nothing" when the
    # only thing that happened is that this script looked in the wrong place. It did exactly that once.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $candidate = Get-ChildItem $RigRoot -Recurse -Filter "director-*-$directorPid.log" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($candidate) {
            $logFile = $candidate.FullName
            $hit = Select-String -Path $logFile -Pattern 'Tool path at start:' -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($hit) { $logLine = $hit.Line; break }
        }
        Start-Sleep -Milliseconds 300
    }
}

# The identifier the shutdown signal is named for, read from the Director's own log. It is written by
# StartLifecycleSignals, which runs AFTER the tool path line above, so this waits again rather than
# assuming the line has already landed.
$directorId = ''
if ($logFile) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $idHit = Select-String -Path $logFile -Pattern 'Lifecycle signals listening for directorId=([0-9a-fA-F-]+)' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($idHit) { $directorId = $idHit.Matches[0].Groups[1].Value; break }
        Start-Sleep -Milliseconds 300
    }
    if ($directorId) { Write-Host "[rig] the Director says it is listening as directorId=$directorId" }
    else { Write-Host "[rig] the Director never said it was listening for a shutdown signal" }
}

# ---- Stop it the way a Director is stopped: its own named signal ----

function Stop-RigDirector([int]$TargetPid, [string]$TargetDirectorId) {
    if ($TargetPid -le 0) { return 'there was no Director to stop' }
    $exited = $false
    $how = ''
    if ($TargetDirectorId) {
        $signal = "Local\cc-director-shutdown-$($TargetDirectorId.ToLowerInvariant())"
        Write-Host "[rig] signalling $signal (PID $TargetPid)"
        try {
            $evt = [System.Threading.EventWaitHandle]::OpenExisting($signal)
            $evt.Set() | Out-Null
            $evt.Dispose()
            $d = (Get-Date).AddSeconds(60)
            while ((Get-Date) -lt $d) {
                if ($null -eq (Get-Process -Id $TargetPid -ErrorAction SilentlyContinue)) { $exited = $true; break }
                Start-Sleep -Milliseconds 250
            }
            if ($exited) { $how = 'stopped by its named signal' }
            else { $how = 'signalled, but it did not exit within 60 seconds' }
        } catch {
            # OpenExisting throwing means nothing is listening for that name - the one case where a
            # Director genuinely cannot be asked to stop (CLAUDE.md rule 0b).
            $how = "nothing is listening for $signal"
            Write-Host "[rig] $how"
        }
    } else {
        $how = 'the Director never reported a directorId, so there was no name to signal'
        Write-Host "[rig] $how"
    }
    if (-not $exited) {
        $alive = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
        if ($alive -and $alive.Path -and ($alive.Path -ieq (Resolve-Path $Exe).Path)) {
            Write-Host "[rig] LAST RESORT: force killing PID $TargetPid - $how. A force-killed Director"
            Write-Host "      leaves an interrupted crash journal entry behind; it goes with this rig root."
            Stop-Process -Id $TargetPid -Force -Confirm:$false
            $how = "FORCE KILLED - $how"
        } else {
            $how = "$how, and the process was already gone"
        }
    }
    return $how
}

$stopOutcome = Stop-RigDirector $directorPid $directorId
Write-Host "[rig] how the Director was stopped: $stopOutcome"
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue

# ---- The verdict ----

Write-Host ""
Write-Host "[rig] the line the Director wrote at start:"
Write-Host "      $logLine"
Write-Host ""

Check "the Director started" ($directorPid -gt 0) "no cc-director$slot process appeared within $TimeoutSeconds seconds"
Check "the Director reported its tool path at start" ($logLine -ne '') "no 'Tool path at start:' line in $logFile"

if ($logLine -ne '') {
    Check "the running path holds exactly ONE DevThrottle tools entry" `
        ($logLine -match 'it now holds 1 DevThrottle tools entry') `
        "the line does not say one entry"
    Check "the one entry is the master" `
        ($logLine -match [regex]::Escape("entry: $master")) `
        "the surviving entry is not $master"
    foreach ($copy in @(@{n='the default Director copy';p=$ownCopy}, @{n='the named Director copy';p=$namedCopy},
                        @{n='the nested copy the leak produced';p=$nestedCopy}, @{n='the copy already deleted from disk';p=$deletedCopy})) {
        Check "$($copy.n) came off the path" `
            ($logLine -match [regex]::Escape($copy.p)) `
            "the line does not name $($copy.p) as removed"
    }
    Check "the saved path was NOT written, because this root is not the machine's install" `
        ($logLine -match 'The saved path was not touched') `
        "the line does not say the saved path was left alone"
}

Check "the bystander folder inside the Director is untouched" `
    (Test-Path (Join-Path $bystander 'notes.txt')) `
    "$bystander\notes.txt is gone"

$savedPathAfter = Read-SavedPathRaw
Check "the real user's saved path is byte-for-byte unchanged" `
    ($savedPathAfter -ceq $savedPathBefore) `
    "the saved user path CHANGED during this run"

# Checked, not asserted in a docstring. The eleven checks above are all read from the log BEFORE the
# stop, so a forced stop does not make any of them untrue - but a script that says it signals and
# quietly forces is a claim the next reader will believe. This is the claim, answerable.
Check "the Director was stopped by its named signal, not force-killed" `
    ($stopOutcome -eq 'stopped by its named signal') `
    "it was stopped another way: $stopOutcome"

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "[rig] PASS - every check above held. Rig kept at $RigRoot"
    exit 0
}
Write-Host "[rig] FAIL - $($failures.Count) check(s) did not hold. Rig kept at $RigRoot"
exit 1
