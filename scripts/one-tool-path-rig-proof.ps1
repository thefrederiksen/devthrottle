#Requires -Version 5.1
<#
.SYNOPSIS
    Proves, on a real Director start, that the path ends up holding exactly ONE DevThrottle tools
    folder - the machine's master - that every superseded COPY on disk is then deleted, and that
    nothing else on the machine is touched.

.DESCRIPTION
    Builds a throwaway root that looks like the computer this mission was written for: a master tools
    folder at the root, a copy inside every Director's own folder, the nested copy the leak produced, a
    copy that has already been deleted and is still on the path, retired interpreters in the root and in
    each Director, and - in every one of those places - bystanders that must come through untouched.

    It then starts a slot Director on that root through a scheduled task - never from the agent's own
    process tree (CLAUDE.md rule 0b) - with a crafted process path carrying the copies, reads the lines
    the Director itself writes at start, and stops it with its named signal (CLAUDE.md rule 0b).

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
      2. Every copy the master replaces came off the path, including the one whose folder no longer
         exists.
      3. The set of directories that DISAPPEARED is exactly the expected delete set - not a superset,
         not a subset. Printed in full on failure.
      4. Every surviving file is still there and its SHA-256 is unchanged. Every bystander, byte for
         byte.
      5. The sweep REPORTED the condition that saved each folder it kept, and the folder it saved
         because a Director is running there was actually looked at. A folder surviving proves nothing
         on its own - it survives identically when nobody looked at it.
      6. The real user's SAVED path is byte-for-byte what it was before the run.

    WHAT IT DOES NOT PROVE
      The saved-path rewrite itself. The saved path is one per-user registry value on this machine and
      a rig must never write it - which is the product rule proved by point 6, not a gap in the rig.
      The rewrite that WOULD be written is covered by FleetToolPathRepairTests.

    NOTHING IN THIS SCRIPT EVER TOUCHES %LOCALAPPDATA%\cc-director. It refuses to run if the rig root
    it was given resolves inside the real one, and every path it builds is checked against the real root
    before it is created or deleted.

.PARAMETER Exe
    The slot Director to start. Must be slot 5 or higher; slots 1 to 4 and the installed app belong to
    the owner and are never touched.

.PARAMETER RigRoot
    Where to build the throwaway root. Defaults to a fresh directory under the repository's
    local_builds folder.

.PARAMETER EmptyMaster
    Build the rig with a <rig>\bin that holds NO cc-devthrottle, and assert the opposite outcome:
    NOTHING is deleted anywhere and the Director says why. This is proof case C in the phase 3 plan -
    a rig STATE rather than a code mutation, which is why it needs no edit to the product to run.

.EXAMPLE
    powershell -NoProfile -File scripts\one-tool-path-rig-proof.ps1

.EXAMPLE
    powershell -NoProfile -File scripts\one-tool-path-rig-proof.ps1 -EmptyMaster
#>
param(
    [string]$Exe = "",
    [string]$RigRoot = "",
    [int]$TimeoutSeconds = 120,
    [switch]$EmptyMaster
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

# EVERY path this rig builds goes through here, not only the root it was handed. The root check alone
# was enough while the rig only READ; this phase DELETES, and a helper that composed a path from
# somewhere else would walk straight past a check made once at the top.
function Assert-OutsideRealRoot([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    if ($full.TrimEnd('\') -ieq $realRoot.TrimEnd('\') -or
        $full.StartsWith($realRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "[rig] ERROR: refusing to touch $full - it is inside the real root ($realRoot)."
        exit 1
    }
    return $full
}
$RigRoot = Assert-OutsideRealRoot $RigRoot

Write-Host "[rig] slot=$slot exe=$Exe"
Write-Host "[rig] rig root=$RigRoot"
if ($EmptyMaster) { Write-Host "[rig] EMPTY MASTER run: the master will hold no cc-devthrottle, and NOTHING may be deleted." }

# ---- The saved path, read exactly as the product reads it, before anything runs ----

function Read-SavedPathRaw {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
    try { return [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    finally { $key.Dispose() }
}
$savedPathBefore = Read-SavedPathRaw
Write-Host "[rig] saved user path captured ($($savedPathBefore.Length) characters). It must not change."

# ---- Build the rig: the machine that prompted this mission, in miniature ----

function New-RigDir([string]$Path) {
    $p = Assert-OutsideRealRoot $Path
    New-Item -ItemType Directory -Force -Path $p | Out-Null
    return $p
}
function New-RigFile([string]$Path, [string]$Content) {
    $p = Assert-OutsideRealRoot $Path
    New-RigDir (Split-Path -Parent $p) | Out-Null
    Set-Content -Path $p -Value $Content -Encoding ascii
    return $p
}
function New-ToolsDir([string]$Path) {
    $p = New-RigDir $Path
    New-RigFile (Join-Path $p 'cc-devthrottle.cmd') "@echo off`r`necho $p" | Out-Null
    return $p
}

$master    = Join-Path $RigRoot 'bin'
$masterPy  = Join-Path $RigRoot 'pyenv'
$masterInt = Join-Path $RigRoot 'python'

# THE MASTER. On an -EmptyMaster run the directory is there and the command is not, which is the state
# that must stop the sweep dead: a machine whose only working tools are the copies.
New-RigDir $master | Out-Null
if (-not $EmptyMaster) { New-RigFile (Join-Path $master 'cc-devthrottle.cmd') "@echo off`r`necho $master" | Out-Null }
New-RigFile (Join-Path $masterPy 'bystander.txt')  'the master interpreter environment - never a copy' | Out-Null
New-RigFile (Join-Path $masterInt 'bystander.txt') 'the master interpreter - never a copy' | Out-Null

# In the MACHINE ROOT: one retired interpreter that goes, and two near-miss names that stay. The
# near-misses are the whole point of the allow-list being a NAME rule and not a prefix rule.
$rootRetired    = Join-Path $RigRoot 'python.old-1a2b3c4d5e6f708192a3b4c5d6e7f809'
$rootNotHex     = Join-Path $RigRoot 'python.old-zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz'
$rootTooShort   = Join-Path $RigRoot 'python.old-notthirtytwo'
New-RigFile (Join-Path $rootRetired  'old.txt')  'a retired interpreter in the machine root' | Out-Null
New-RigFile (Join-Path $rootNotHex   'mine.txt') '32 letters, not 32 hexadecimal characters - must survive' | Out-Null
New-RigFile (Join-Path $rootTooShort 'mine.txt') 'not 32 characters at all - must survive' | Out-Null

# Six Director folders. slot-1 has a live Director; default is the one this Director will be.
$directors = [ordered]@{
    'default' = '00000000000000000000000000000001'
    'slot-1'  = '00000000000000000000000000000002'
    'slot-5'  = '00000000000000000000000000000003'
    'slot-7'  = '00000000000000000000000000000004'
    'named-a' = '00000000000000000000000000000005'
    'gone'    = '00000000000000000000000000000006'
}
$directorHomes = @{}
foreach ($name in $directors.Keys) {
    $dhome = New-RigDir (Join-Path $RigRoot "instances\$name")
    $directorHomes[$name] = $dhome

    New-ToolsDir (Join-Path $dhome 'bin') | Out-Null
    New-RigFile (Join-Path $dhome 'pyenv\tool.txt')  "the $name copy's interpreter environment" | Out-Null
    New-RigFile (Join-Path $dhome 'python\tool.txt') "the $name copy's interpreter" | Out-Null
    New-RigFile (Join-Path $dhome ("python.old-" + $directors[$name] + "\tool.txt")) "the $name retired interpreter" | Out-Null

    # Bystander FOLDERS with near-miss names. Every one of these is a directory a person could put
    # there, and a name rule that is really a prefix rule would take all three.
    New-RigFile (Join-Path $dhome 'bin-old\mine.txt')  "a bystander folder in $name" | Out-Null
    New-RigFile (Join-Path $dhome 'binaries\mine.txt') "a bystander folder in $name" | Out-Null
    New-RigFile (Join-Path $dhome 'pyenv2\mine.txt')   "a bystander folder in $name" | Out-Null

    # Data folders. These are the ones a false delete would be unrecoverable for.
    New-RigFile (Join-Path $dhome 'sessions\s1.json')     "{""session"":""$name""}" | Out-Null
    New-RigFile (Join-Path $dhome 'config\settings.json') "{""instance"":""$name""}" | Out-Null
    New-RigFile (Join-Path $dhome 'logs\d.log')           "a log line for $name" | Out-Null

    # A loose FILE directly inside the Director folder. The sweep never deletes a file.
    New-RigFile (Join-Path $dhome 'notes.txt') "a loose file inside $name that is nothing to do with the tools" | Out-Null
}

# The bystander the phase 2 proof already carried, kept by name so its check still means the same thing.
$bystander = Join-Path $directorHomes['default'] 'scripts'
New-RigFile (Join-Path $bystander 'notes.txt') 'a file inside a Director folder that is nothing to do with the tools' | Out-Null

# The NESTED leak: instances/default/instances/default is a Director folder BY SHAPE. Its bin goes; the
# instances folder that holds it, and the app and launcher beside it, are reported and left.
$nestedHome = New-RigDir (Join-Path $directorHomes['default'] 'instances\default')
$nestedCopy = New-ToolsDir (Join-Path $nestedHome 'bin')
New-RigFile (Join-Path $nestedHome 'notes.txt') 'a loose file inside the nested Director folder' | Out-Null
New-RigFile (Join-Path $directorHomes['default'] 'app\cc-director.exe')        'not a real executable - a bystander the sweep must report and leave' | Out-Null
New-RigFile (Join-Path $directorHomes['default'] 'launcher\cc-launcher.exe')   'not a real executable - a bystander the sweep must report and leave' | Out-Null

$ownCopy     = Join-Path $directorHomes['default'] 'bin'
$namedCopy   = Join-Path $directorHomes['slot-7'] 'bin'
$liveCopy    = Join-Path $directorHomes['slot-1'] 'bin'
# The copy that is already gone: on the path, and not on disk. This is the state the deletion leaves
# behind, so it is the one that must not be assumed away.
$deletedCopy = Join-Path $RigRoot 'instances\vanished\bin'

Write-Host "[rig] built: master, six Director folders, the nested leak, three retired interpreters in the root, and bystanders in every one"
Write-Host "[rig] $deletedCopy is on the path and NOT on disk"

# ---- What must disappear, named in full BEFORE the run ----

$expectedDeleted = New-Object System.Collections.Generic.List[string]
if (-not $EmptyMaster) {
    $expectedDeleted.Add($rootRetired)
    foreach ($name in $directors.Keys) {
        if ($name -eq 'slot-1') { continue }   # a Director is running there
        $dhome = $directorHomes[$name]
        $expectedDeleted.Add((Join-Path $dhome 'bin'))
        $expectedDeleted.Add((Join-Path $dhome 'pyenv'))
        $expectedDeleted.Add((Join-Path $dhome 'python'))
        $expectedDeleted.Add((Join-Path $dhome ("python.old-" + $directors[$name])))
    }
    $expectedDeleted.Add($nestedCopy)
}

# ---- Snapshot: every directory, every file, every hash ----

function Get-TreeSnapshot([string]$Root) {
    $dirs = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
    $files = @{}
    foreach ($item in Get-ChildItem -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue) {
        if ($item.PSIsContainer) { [void]$dirs.Add($item.FullName) }
        else {
            try { $files[$item.FullName] = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
            catch { $files[$item.FullName] = "UNREADABLE: $($_.Exception.Message)" }
        }
    }
    return @{ Dirs = $dirs; Files = $files }
}

$before = Get-TreeSnapshot $RigRoot
Write-Host "[rig] snapshot before: $($before.Dirs.Count) directories, $($before.Files.Count) files"

# ---- A REAL running Director in slot-1, owned by this script ----
#
# The guard that saves slot-1 is condition 4, and the only way to watch it work is for something to
# actually be running there. A process this script starts and stops itself, with a registration whose
# start-time window DirectorInstanceLocator really requires.

$liveProcess = Start-Process -FilePath "powershell.exe" `
    -ArgumentList '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 900' `
    -WindowStyle Hidden -PassThru
$liveDirectorId = [guid]::NewGuid().ToString()
$liveStartedAt = $liveProcess.StartTime.ToUniversalTime().ToString('o')
$registration = @{
    directorId      = $liveDirectorId
    pid             = $liveProcess.Id
    startedAt       = $liveStartedAt
    controlEndpoint = ''
    machineName     = $env:COMPUTERNAME
    user            = $env:USERNAME
    version         = '0.0.0-rig'
    schemaVersion   = 1
} | ConvertTo-Json
New-RigFile (Join-Path $directorHomes['slot-1'] "config\director\instances\$liveDirectorId.json") $registration | Out-Null
Write-Host "[rig] a live process (PID $($liveProcess.Id)) is registered as the Director of instances\slot-1"

# Written AFTER the snapshot on purpose: the registration is the rig's own scaffolding, not a bystander
# the sweep is being asked to preserve, and it is removed with the rig root.

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
New-RigFile $wrapper ($wrapperLines -join "`r`n") | Out-Null

$taskName = "cc-director$slot-one-tool-path-rig"
$action = New-ScheduledTaskAction -Execute $wrapper -WorkingDirectory (Split-Path -Parent $Exe)
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Force | Out-Null

$beforePids = @(Get-Process -Name "cc-director$slot" -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
Start-ScheduledTask -TaskName $taskName
Write-Host "[rig] scheduled task $taskName started; waiting for the Director"

$directorPid = 0
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    $now = @(Get-Process -Name "cc-director$slot" -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ($_.Path -ieq (Resolve-Path $Exe).Path) })
    $fresh = @($now | Where-Object { $beforePids -notcontains $_.Id })
    if ($fresh.Count -gt 0) { $directorPid = $fresh[0].Id; break }
    Start-Sleep -Milliseconds 300
}

$logLine = ''
$sweepSummary = ''
$sweepLines = @()
$logFile = ''
if ($directorPid -gt 0) {
    Write-Host "[rig] Director running as PID $directorPid; waiting for the lines it writes at start"
    # Searched from the rig root rather than from a guessed folder: WHERE the Director keeps its own
    # data is its business, and a proof that hard-codes it reports "the Director said nothing" when the
    # only thing that happened is that this script looked in the wrong place. It did exactly that once.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $candidate = Get-ChildItem $RigRoot -Recurse -Filter "director-*-$directorPid.log" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($candidate) {
            $logFile = $candidate.FullName
            $pathHit = Select-String -Path $logFile -Pattern 'Tool path at start:' -ErrorAction SilentlyContinue |
                Select-Object -First 1
            $sweepHit = Select-String -Path $logFile -Pattern 'Tool copies at start:' -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($pathHit) { $logLine = $pathHit.Line }
            if ($sweepHit) { $sweepSummary = $sweepHit.Line }
            if ($logLine -and $sweepSummary) { break }
        }
        Start-Sleep -Milliseconds 300
    }
}

# The identifier the shutdown signal is named for, read from the Director's own log. It is written by
# StartLifecycleSignals, which runs AFTER the lines above, so this waits again rather than assuming the
# line has already landed.
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

# The rig's own live process. It is this script's, it is a powershell.exe running Start-Sleep, and it is
# stopped by id - never by name, which would reach every other PowerShell on the machine.
if ($liveProcess -and -not $liveProcess.HasExited) {
    Stop-Process -Id $liveProcess.Id -Force -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "[rig] stopped the rig's own live process (PID $($liveProcess.Id))"
}

$after = Get-TreeSnapshot $RigRoot
Write-Host "[rig] snapshot after: $($after.Dirs.Count) directories, $($after.Files.Count) files"

# The verdict lines are read AFTER the Director has exited, so the log is fully flushed. Reading them
# during the run found them most of the time, and "most of the time" in a proof is a flake that reports
# a missing guard.
if ($logFile) {
    $sweepLines = @(Select-String -Path $logFile -Pattern '\[DirectorToolCopySweep\] (DELETE|KEEP) ' -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Line })
}
Write-Host "[rig] the sweep reported $($sweepLines.Count) verdicts"

# ---- The verdict ----

Write-Host ""
Write-Host "[rig] the lines the Director wrote at start:"
Write-Host "      $logLine"
Write-Host "      $sweepSummary"
Write-Host ""

Check "the Director started" ($directorPid -gt 0) "no cc-director$slot process appeared within $TimeoutSeconds seconds"
Check "the Director reported its tool copies at start" ($sweepSummary -ne '') "no 'Tool copies at start:' line in $logFile"

if (-not $EmptyMaster) {
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
}

# ---- 5b: the tree comparison ----

$disappeared = @($before.Dirs | Where-Object { -not $after.Dirs.Contains($_) })
$expectedSet = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
foreach ($e in $expectedDeleted) { [void]$expectedSet.Add([System.IO.Path]::GetFullPath($e)) }

$unexpectedlyGone = @($disappeared | Where-Object { -not $expectedSet.Contains($_) })
$expectedButStillThere = @($expectedDeleted | Where-Object { $after.Dirs.Contains([System.IO.Path]::GetFullPath($_)) })

Check "nothing disappeared that was not on the expected delete list" `
    ($unexpectedlyGone.Count -eq 0) `
    ("these directories were destroyed and should not have been: " + ($unexpectedlyGone -join '; '))
Check "everything on the expected delete list is gone" `
    ($expectedButStillThere.Count -eq 0) `
    ("these directories should have been removed and are still there: " + ($expectedButStillThere -join '; '))

# Every file that was NOT inside a directory the sweep was allowed to remove must still be there, with
# the same bytes. This is the check the bystanders exist for.
$changed = New-Object System.Collections.Generic.List[string]
$missing = New-Object System.Collections.Generic.List[string]
foreach ($path in $before.Files.Keys) {
    $insideDeleted = $false
    foreach ($d in $expectedSet) {
        if ($path.StartsWith($d.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { $insideDeleted = $true; break }
    }
    if ($insideDeleted) { continue }
    if (-not $after.Files.ContainsKey($path)) { $missing.Add($path); continue }
    if ($after.Files[$path] -ne $before.Files[$path]) { $changed.Add($path) }
}

Check "every surviving file is still there" ($missing.Count -eq 0) `
    ("these files were destroyed: " + ($missing -join '; '))
Check "every surviving file is byte-for-byte unchanged" ($changed.Count -eq 0) `
    ("these files changed: " + ($changed -join '; '))

# ---- 5c: the sweep must have REPORTED the condition, not merely left the folder alone ----

function Get-SweepReason([string]$Path) {
    $escaped = [regex]::Escape($Path)
    $hit = @($sweepLines | Where-Object { $_ -match "(DELETE|KEEP) $escaped - " }) | Select-Object -First 1
    if (-not $hit) { return $null }
    if ($hit -match "(?:DELETE|KEEP) $escaped - (.+)$") { return $Matches[1].Trim() }
    return $null
}

if ($EmptyMaster) {
    Check "with an empty master the Director says nothing was swept anywhere" `
        ($sweepSummary -match 'the master holds no cc-devthrottle; nothing was swept anywhere') `
        "the summary does not say the master is empty: $sweepSummary"
    Check "with an empty master the sweep classified NOTHING" `
        ($sweepLines.Count -eq 0) `
        "the sweep reported $($sweepLines.Count) verdicts on a run where it should have refused outright"
} else {
    $liveReason = Get-SweepReason $liveCopy
    Check "the sweep LOOKED AT the running Director's tools folder" `
        ($null -ne $liveReason) `
        "$liveCopy appears in no verdict line - it may have survived because nobody looked at it"
    Check "the sweep says it kept the running Director's tools because a Director is running there" `
        ($liveReason -eq 'a Director is running there') `
        "the reason it reported was: $liveReason"

    $masterReason = Get-SweepReason $master
    Check "the sweep says it kept the master because it IS the master" `
        ($null -ne $masterReason -and $masterReason -match 'master tools folder') `
        "the reason it reported for $master was: $masterReason"

    $notHexReason = Get-SweepReason $rootNotHex
    Check "the sweep says it kept the 32-letter near-miss because the NAME does not qualify" `
        ($null -ne $notHexReason -and $notHexReason -match 'hexadecimal') `
        "the reason it reported for $rootNotHex was: $notHexReason"

    $bystanderFolder = Join-Path $directorHomes['default'] 'binaries'
    $bystanderReason = Get-SweepReason $bystanderFolder
    Check "the sweep says it kept a near-miss bystander folder because the NAME does not qualify" `
        ($null -ne $bystanderReason -and $bystanderReason -match 'the name is not bin, pyenv, python') `
        "the reason it reported for $bystanderFolder was: $bystanderReason"

    $nestedInstances = Join-Path $directorHomes['default'] 'instances'
    Check "the sweep reported the nested instances folder rather than silently ignoring it" `
        ($null -ne (Get-SweepReason $nestedInstances)) `
        "$nestedInstances appears in no verdict line"

    $appDir = Join-Path $directorHomes['default'] 'app'
    $launcherDir = Join-Path $directorHomes['default'] 'launcher'
    Check "the sweep reported the app folder and left it" `
        ($null -ne (Get-SweepReason $appDir)) "$appDir appears in no verdict line"
    Check "the sweep reported the launcher folder and left it" `
        ($null -ne (Get-SweepReason $launcherDir)) "$launcherDir appears in no verdict line"

    $ownReason = Get-SweepReason $ownCopy
    Check "the sweep says it removed its OWN folder's copy because it is the Director doing the sweep" `
        ($null -ne $ownReason -and $ownReason -match 'it belongs to the Director doing the sweep') `
        "the reason it reported for $ownCopy was: $ownReason"
}

Check "the bystander folder inside the Director is untouched" `
    (Test-Path (Join-Path $bystander 'notes.txt')) `
    "$bystander\notes.txt is gone"

$savedPathAfter = Read-SavedPathRaw
Check "the real user's saved path is byte-for-byte unchanged" `
    ($savedPathAfter -ceq $savedPathBefore) `
    "the saved user path CHANGED during this run"

# Checked, not asserted in a docstring. Every check above is read from the log or from the tree, so a
# forced stop does not make any of them untrue - but a script that says it signals and quietly forces is
# a claim the next reader will believe. This is the claim, answerable.
Check "the Director was stopped by its named signal, not force-killed" `
    ($stopOutcome -eq 'stopped by its named signal') `
    "it was stopped another way: $stopOutcome"

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "[rig] PASS - every check above held. Rig kept at $RigRoot"
    exit 0
}
Write-Host "[rig] FAIL - $($failures.Count) check(s) did not hold. Rig kept at $RigRoot"
foreach ($f in $failures) { Write-Host "       $f" }
exit 1
