#Requires -Version 5.1
<#
.SYNOPSIS
    Per-session isolation harness for agent test Directors (issue #299).

.DESCRIPTION
    Gives each implementation/QA session its own test slot, build output, and
    Control API port so two sessions can run concurrently on one machine without
    colliding. Three subcommands:

      allocate  - Find and RESERVE the lowest free slot N >= 6 for a worktree.
                  "Free" means: no running process whose image is cc-director<N>.exe
                  AND no registered scheduled task cc-director<N>-launch. The
                  reservation IS the task registration (Register-ScheduledTask
                  WITHOUT -Force): if two sessions race for the same N, exactly one
                  registration succeeds and the loser moves to N+1. Emits a
                  machine-readable session manifest (JSON) into the worktree's
                  local_builds directory.

      launch    - Start the session's test Director via ITS OWN scheduled task
                  (clean svchost parentage, CLAUDE.md rule 0b - never from the
                  agent's process tree). Resolves the new PID by exact image path,
                  then reads the Director's own log for the self-allocated Control
                  API port ("Kestrel listening on ..."). Updates the manifest with
                  pid/port/logFile. The whole start->port-discovered window runs
                  under a machine-wide named mutex: the Director's PortAllocator
                  probes for a free port BEFORE Kestrel binds it, so two Directors
                  starting in the same instant can pick the same port (proven
                  live: simultaneous starts both allocated 7892 and one failed
                  with "address already in use"). Serializing launches means each
                  Director has BOUND its port before the next one probes. If the
                  port never appears in the log, the just-launched process is
                  stopped (exact image path confirmed first) so a failed launch
                  never leaks an orphan Director.

      teardown  - Stop ONLY the session's own Director (image path must match the
                  manifest exe exactly - refuses anything else), unregister the
                  session's task, optionally remove the worktree (-RemoveWorktree,
                  for scratch worktrees), and delete the manifest.

    Slots 1-5 and the main build are NEVER touched: slot 5 stays the legacy/manual
    default and may be in use by a human-driven session; the allocator starts at 6.
    This script never binds a port itself - the Director self-allocates its Control
    API port at startup (PortAllocator), and the launch mutex guarantees the
    allocation-to-bind window of one Director never overlaps another's, so two
    sessions cannot collide on a port.

.PARAMETER Command
    allocate | launch | teardown

.PARAMETER Worktree
    (allocate) Absolute path to the session's git worktree. The slot exe is
    expected at <Worktree>\local_builds\cc-director<N>.exe after the session runs
    scripts\local-build-avalonia.ps1 -Slot <N> from the worktree root.

.PARAMETER Manifest
    (launch, teardown) Path to the session manifest JSON emitted by allocate.

.PARAMETER MinSlot
    (allocate) Lowest slot number to consider. Defaults to 6 and may not be lower.

.PARAMETER RemoveWorktree
    (teardown) Also remove the session's git worktree (git worktree remove --force).
    Use for scratch worktrees only - a primary worktree that QA still needs must be
    left in place.

.EXAMPLE
    # Full session lifecycle (run from anywhere; paths are explicit):
    powershell -NoProfile -File scripts\agent-session-isolation.ps1 allocate -Worktree D:\ReposFred\wt-issue-123
    #   -> SLOT=6, MANIFEST=D:\ReposFred\wt-issue-123\local_builds\agent-session-slot6.json
    powershell -NoProfile -File D:\ReposFred\wt-issue-123\scripts\local-build-avalonia.ps1 -Slot 6
    powershell -NoProfile -File scripts\agent-session-isolation.ps1 launch -Manifest D:\ReposFred\wt-issue-123\local_builds\agent-session-slot6.json
    #   -> PID=12345, PORT=7881; probe http://127.0.0.1:7881/healthz
    powershell -NoProfile -File scripts\agent-session-isolation.ps1 teardown -Manifest D:\ReposFred\wt-issue-123\local_builds\agent-session-slot6.json
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("allocate", "launch", "teardown")]
    [string]$Command,

    [string]$Worktree = "",
    [string]$Manifest = "",
    [int]$MinSlot = 6,
    [int]$MaxSlot = 20,
    [switch]$RemoveWorktree
)

$ErrorActionPreference = "Stop"

# The hard floor. Slots 1-5 and the main build belong to the human (CLAUDE.md rule
# 0b; slot 5 is the legacy/manual default and may be in use). Nothing in this script
# may ever allocate, launch, or tear down below this.
$script:SlotFloor = 6

function Fail([string]$Message) {
    Write-Host "[agent-session-isolation] ERROR: $Message"
    exit 1
}

function Get-SlotExeName([int]$Slot) { return "cc-director$Slot.exe" }
function Get-SlotTaskName([int]$Slot) { return "cc-director$Slot-launch" }
function Get-SlotProcessName([int]$Slot) { return "cc-director$Slot" }

function Test-SlotProcessRunning([int]$Slot) {
    $procs = @(Get-Process -Name (Get-SlotProcessName $Slot) -ErrorAction SilentlyContinue)
    return ($procs.Count -gt 0)
}

function Test-SlotTaskRegistered([int]$Slot) {
    $task = Get-ScheduledTask -TaskName (Get-SlotTaskName $Slot) -ErrorAction SilentlyContinue
    return ($null -ne $task)
}

function Read-Manifest([string]$Path) {
    if (-not $Path) { Fail "-Manifest is required for this command." }
    if (-not (Test-Path $Path)) { Fail "Manifest not found: $Path" }
    $m = Get-Content $Path -Raw | ConvertFrom-Json
    if ($m.slot -lt $script:SlotFloor) {
        Fail "Manifest slot $($m.slot) is below the floor ($script:SlotFloor). Slots 1-5 / main build are off-limits; refusing."
    }
    return $m
}

function Write-Manifest($Obj, [string]$Path) {
    $json = $Obj | ConvertTo-Json -Depth 4
    $json | Out-File -FilePath $Path -Encoding ascii
}

function New-SlotTaskRegistration([int]$Slot, [string]$ExePath, [string]$WorkingDir, [bool]$Force) {
    # WorkingDirectory MUST be set or Avalonia first-run resource resolution can
    # fail with exit -1 (see CLAUDE.md rule 0b setup notes).
    $action = New-ScheduledTaskAction -Execute $ExePath -WorkingDirectory $WorkingDir
    # On-demand only: trigger parked far in the future.
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5)
    if ($Force) {
        Register-ScheduledTask -TaskName (Get-SlotTaskName $Slot) -Action $action -Trigger $trigger -Force | Out-Null
    } else {
        # NO -Force: registration fails if the task already exists. That failure is
        # the allocation race arbiter - the loser gets an error and tries the next slot.
        Register-ScheduledTask -TaskName (Get-SlotTaskName $Slot) -Action $action -Trigger $trigger | Out-Null
    }
}

# ---------------------------------------------------------------- allocate ----
function Invoke-Allocate {
    if (-not $Worktree) { Fail "-Worktree is required for allocate." }
    if ($MinSlot -lt $script:SlotFloor) { Fail "-MinSlot $MinSlot is below the floor ($script:SlotFloor). Slots 1-5 / main build are off-limits." }
    $wt = (Resolve-Path $Worktree -ErrorAction SilentlyContinue)
    if ($null -eq $wt) { Fail "Worktree path does not exist: $Worktree" }
    $wt = $wt.Path
    if (-not (Test-Path (Join-Path $wt "scripts\local-build-avalonia.ps1"))) {
        Fail "Worktree $wt does not look like a cc-director checkout (scripts\local-build-avalonia.ps1 missing)."
    }

    $localBuilds = Join-Path $wt "local_builds"
    if (-not (Test-Path $localBuilds)) {
        New-Item -ItemType Directory -Path $localBuilds | Out-Null
    }

    $allocated = 0
    for ($n = $MinSlot; $n -le $MaxSlot; $n++) {
        if (Test-SlotProcessRunning $n) {
            Write-Host "[allocate] slot $n busy: process $(Get-SlotProcessName $n) is running"
            continue
        }
        if (Test-SlotTaskRegistered $n) {
            Write-Host "[allocate] slot $n busy: scheduled task $(Get-SlotTaskName $n) is registered"
            continue
        }
        # Reserve by registering the per-slot task WITHOUT -Force. If another
        # session registered it between our probe and now, this throws and we
        # move on - exactly one session can hold a slot's task.
        $exePath = Join-Path $localBuilds (Get-SlotExeName $n)
        try {
            New-SlotTaskRegistration -Slot $n -ExePath $exePath -WorkingDir $localBuilds -Force $false
            $allocated = $n
            break
        } catch {
            Write-Host "[allocate] slot $n lost in registration race ($($_.Exception.Message)); trying next"
        }
    }

    if ($allocated -eq 0) { Fail "No free slot found in [$MinSlot..$MaxSlot]." }

    $manifestPath = Join-Path $localBuilds "agent-session-slot$allocated.json"
    $manifestObj = [pscustomobject]@{
        slot        = $allocated
        worktree    = $wt
        exePath     = (Join-Path $localBuilds (Get-SlotExeName $allocated))
        taskName    = (Get-SlotTaskName $allocated)
        pid         = 0
        port        = 0
        logFile     = ""
        allocatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        launchedAt  = ""
    }
    Write-Manifest $manifestObj $manifestPath

    Write-Host "[allocate] SLOT=$allocated"
    Write-Host "[allocate] TASK=$(Get-SlotTaskName $allocated)"
    Write-Host "[allocate] MANIFEST=$manifestPath"
    Write-Host "[allocate] Next: build the slot exe from the worktree root:"
    Write-Host "[allocate]   powershell -NoProfile -File `"$wt\scripts\local-build-avalonia.ps1`" -Slot $allocated"
}

# ------------------------------------------------------------------ launch ----
function Stop-OwnLaunchedDirector([int]$DirectorPid, [string]$ExePath) {
    # Stop the Director THIS launch started, after re-confirming the image path is
    # exactly the session's exe - so a failed launch never leaks an orphan process.
    $p = Get-Process -Id $DirectorPid -ErrorAction SilentlyContinue
    if ($null -eq $p) { return }
    if (-not ($p.Path -and ($p.Path -ieq $ExePath))) {
        Write-Host "[launch] NOT stopping PID $DirectorPid - image $($p.Path) is not this session's exe"
        return
    }
    Write-Host "[launch] stopping failed-launch PID $DirectorPid ($($p.Path))"
    Stop-Process -Id $DirectorPid -Force -Confirm:$false
}

function Invoke-Launch {
    $m = Read-Manifest $Manifest

    if (-not (Test-Path $m.exePath)) {
        Fail "Slot exe not built yet: $($m.exePath). Run scripts\local-build-avalonia.ps1 -Slot $($m.slot) from the worktree root first."
    }

    # A process already running this slot name means either a leftover from a
    # previous launch of this session or a foreign collision. Either way we do not
    # pile a second instance on top - the session must tear down first.
    $existing = @(Get-Process -Name (Get-SlotProcessName $m.slot) -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        Fail "A $(Get-SlotProcessName $m.slot) process is already running (PID $($existing[0].Id), path $($existing[0].Path)). Run teardown first; never stack launches."
    }

    # Re-register with -Force: allocate reserved the task before the exe existed;
    # this re-asserts the exact exe path + WorkingDirectory now that it does.
    $localBuilds = Split-Path -Parent $m.exePath
    New-SlotTaskRegistration -Slot $m.slot -ExePath $m.exePath -WorkingDir $localBuilds -Force $true

    # MACHINE-WIDE LAUNCH MUTEX. The Director's PortAllocator probes for a free
    # port and only LATER binds it (Kestrel). Two Directors starting in the same
    # instant can therefore allocate the SAME port - proven live: two simultaneous
    # launches both allocated 7892 and the loser's Control API died with "address
    # already in use". Holding this mutex from task start until the port is read
    # from the Director's log (i.e. Kestrel has bound it) guarantees the next
    # session's Director sees the port as busy and picks another.
    $mutex = New-Object System.Threading.Mutex($false, "Global\cc-director-agent-session-launch")
    $acquired = $false
    try {
        try {
            $acquired = $mutex.WaitOne([TimeSpan]::FromSeconds(300))
        } catch [System.Threading.AbandonedMutexException] {
            # A previous holder died while holding the mutex; we now own it.
            $acquired = $true
        }
        if (-not $acquired) {
            Fail "Could not acquire the machine-wide launch mutex within 300s (another session's launch is stuck)."
        }

        Write-Host "[launch] starting scheduled task $($m.taskName)"
        Start-ScheduledTask -TaskName $m.taskName

        # Resolve the PID: the process whose image path is EXACTLY our exe.
        $deadlinePid = (Get-Date).AddSeconds(60)
        $directorPid = 0
        while ((Get-Date) -lt $deadlinePid) {
            $procs = @(Get-Process -Name (Get-SlotProcessName $m.slot) -ErrorAction SilentlyContinue)
            foreach ($p in $procs) {
                if ($p.Path -and ($p.Path -ieq $m.exePath)) { $directorPid = $p.Id; break }
            }
            if ($directorPid -ne 0) { break }
            Start-Sleep -Milliseconds 500
        }
        if ($directorPid -eq 0) {
            Fail "Director did not appear within 60s (expected image $($m.exePath)). Check Task Scheduler history for $($m.taskName)."
        }
        Write-Host "[launch] PID=$directorPid"

        # Discover the self-allocated Control API port from the Director's own log:
        # %LOCALAPPDATA%\cc-director\logs\director\director-<date>-<PID>.log contains
        # "[ControlApiHost] Kestrel listening on http://<host>:<port>".
        $logDir = Join-Path $env:LOCALAPPDATA "cc-director\logs\director"
        $deadlinePort = (Get-Date).AddSeconds(120)
        $port = 0
        $logFile = ""
        while ((Get-Date) -lt $deadlinePort) {
            $candidates = @(Get-ChildItem -Path $logDir -Filter "director-*-$directorPid.log" -ErrorAction SilentlyContinue)
            foreach ($f in $candidates) {
                $hit = Select-String -Path $f.FullName -Pattern "Kestrel listening on http://[^:]+:(\d+)" -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($hit) {
                    $port = [int]$hit.Matches[0].Groups[1].Value
                    $logFile = $f.FullName
                    break
                }
            }
            if ($port -ne 0) { break }
            # The Director may have exited (bad build, startup crash) - fail fast.
            $alive = Get-Process -Id $directorPid -ErrorAction SilentlyContinue
            if ($null -eq $alive) {
                Fail "Director PID $directorPid exited before reporting its Control API port. Check the newest log in $logDir."
            }
            Start-Sleep -Milliseconds 500
        }
        if ($port -eq 0) {
            # Never leak an orphan: the Director is up but unreachable (no Control
            # API). Stop it (exact-path confirmed) before failing.
            Stop-OwnLaunchedDirector -DirectorPid $directorPid -ExePath $m.exePath
            Fail "Control API port not found in Director log within 120s (PID $directorPid, dir $logDir). The just-launched process has been stopped."
        }
    } finally {
        if ($acquired) { $mutex.ReleaseMutex() | Out-Null }
        $mutex.Dispose()
    }

    $m.pid = $directorPid
    $m.port = $port
    $m.logFile = $logFile
    $m.launchedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    Write-Manifest $m $Manifest

    Write-Host "[launch] PORT=$port"
    Write-Host "[launch] LOG=$logFile"
    Write-Host "[launch] Control API: http://127.0.0.1:$port (probe GET /healthz)"
    Write-Host "[launch] MANIFEST=$Manifest (updated)"
}

# ---------------------------------------------------------------- teardown ----
function Invoke-Teardown {
    $m = Read-Manifest $Manifest

    # Stop ONLY processes whose image path is EXACTLY the session's exe. The
    # manifest PID is checked first; we also sweep same-name processes in case the
    # PID was recycled - but ONLY exact-path matches are ever stopped.
    $targets = @()
    $procs = @(Get-Process -Name (Get-SlotProcessName $m.slot) -ErrorAction SilentlyContinue)
    foreach ($p in $procs) {
        if ($p.Path -and ($p.Path -ieq $m.exePath)) {
            $targets += $p
        } else {
            Write-Host "[teardown] leaving PID $($p.Id) alone: image $($p.Path) is NOT this session's exe"
        }
    }
    if ($targets.Count -eq 0) {
        Write-Host "[teardown] no running process for $($m.exePath) (already stopped)"
    }
    foreach ($p in $targets) {
        Write-Host "[teardown] stopping PID $($p.Id) ($($p.Path))"
        Stop-Process -Id $p.Id -Force -Confirm:$false
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $deadline) {
            $alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
            if ($null -eq $alive) { break }
            Start-Sleep -Milliseconds 250
        }
        $alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
        if ($alive) { Fail "PID $($p.Id) did not exit within 15s." }
        Write-Host "[teardown] PID $($p.Id) exited"
    }

    if (Test-SlotTaskRegistered $m.slot) {
        Unregister-ScheduledTask -TaskName $m.taskName -Confirm:$false
        Write-Host "[teardown] unregistered scheduled task $($m.taskName)"
    } else {
        Write-Host "[teardown] scheduled task $($m.taskName) not registered (already gone)"
    }

    if ($RemoveWorktree) {
        # Resolve the main repo through the worktree's git metadata, step out of the
        # worktree (you cannot remove the directory you are standing in), and remove.
        $commonDir = (& git -C $m.worktree rev-parse --path-format=absolute --git-common-dir)
        if ($LASTEXITCODE -ne 0 -or -not $commonDir) {
            Fail "Could not resolve the main repo for worktree $($m.worktree); not removing it."
        }
        $mainRepo = Split-Path -Parent $commonDir
        Set-Location $env:TEMP
        & git -C $mainRepo worktree remove --force $m.worktree
        if ($LASTEXITCODE -ne 0) { Fail "git worktree remove failed for $($m.worktree)." }
        Write-Host "[teardown] removed worktree $($m.worktree)"
    } else {
        # Manifest lives inside the worktree; only delete it when the worktree stays.
        Remove-Item -Path $Manifest -Force -Confirm:$false
        Write-Host "[teardown] deleted manifest $Manifest"
    }

    Write-Host "[teardown] done (slot $($m.slot) is free again)"
}

switch ($Command) {
    "allocate" { Invoke-Allocate }
    "launch"   { Invoke-Launch }
    "teardown" { Invoke-Teardown }
}
