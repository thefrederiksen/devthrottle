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
    Director itself writes at start, and stops it with its named signal (CLAUDE.md rule 0).

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

# ---- Stop it the way a Director is stopped: its own named signal ----

function Stop-RigDirector([int]$TargetPid) {
    if ($TargetPid -le 0) { return }
    $directorId = ''
    $regFiles = @(Get-ChildItem $RigRoot -Recurse -Directory -Filter 'instances' -ErrorAction SilentlyContinue |
        Where-Object { $_.Parent.Name -ieq 'director' } |
        ForEach-Object { Get-ChildItem $_.FullName -Filter *.json -ErrorAction SilentlyContinue })
    foreach ($f in $regFiles) {
        try {
            $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
            if ($j.Pid -eq $TargetPid -and $j.DirectorId) { $directorId = [string]$j.DirectorId; break }
        } catch {}
    }
    $exited = $false
    if ($directorId) {
        $signal = "Local\cc-director-shutdown-$($directorId.ToLowerInvariant())"
        Write-Host "[rig] signalling $signal (PID $TargetPid)"
        try {
            $evt = [System.Threading.EventWaitHandle]::OpenExisting($signal)
            $evt.Set() | Out-Null
            $evt.Dispose()
            $d = (Get-Date).AddSeconds(30)
            while ((Get-Date) -lt $d) {
                if ($null -eq (Get-Process -Id $TargetPid -ErrorAction SilentlyContinue)) { $exited = $true; break }
                Start-Sleep -Milliseconds 250
            }
        } catch {
            Write-Host "[rig] nothing is listening for $signal"
        }
    } else {
        Write-Host "[rig] no instance registration names PID $TargetPid"
    }
    if (-not $exited) {
        $alive = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
        if ($alive -and $alive.Path -and ($alive.Path -ieq (Resolve-Path $Exe).Path)) {
            Write-Host "[rig] LAST RESORT: the signalled shutdown did not exit in time - force killing PID $TargetPid"
            Stop-Process -Id $TargetPid -Force -Confirm:$false
        }
    }
}

Stop-RigDirector $directorPid
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

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "[rig] PASS - every check above held. Rig kept at $RigRoot"
    exit 0
}
Write-Host "[rig] FAIL - $($failures.Count) check(s) did not hold. Rig kept at $RigRoot"
exit 1
