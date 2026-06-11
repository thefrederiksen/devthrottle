#Requires -Version 5.1
<#
.SYNOPSIS
    Proof harness for issue #299 - one isolated agent-session run.

.DESCRIPTION
    Executes the full per-session isolation lifecycle once, with timestamped
    logging, so two instances of this script started concurrently prove the
    acceptance criteria of issue #299:

      allocate (distinct slot >= 6) -> build the slot exe inside the run's own
      worktree -> launch via the run's own per-slot scheduled task -> probe the
      run's own Control API port (GET /healthz) -> hold (overlap window) ->
      teardown (process gone, task unregistered, scratch worktree removed).

    ASCII output only. Windows PowerShell 5.1.
#>
param(
    [Parameter(Mandatory = $true)] [string]$RunName,
    [Parameter(Mandatory = $true)] [string]$Worktree,
    [Parameter(Mandatory = $true)] [string]$IsolationScript,
    [Parameter(Mandatory = $true)] [string]$LogPath,
    [int]$HoldSeconds = 20,
    [switch]$RemoveWorktree
)

$ErrorActionPreference = "Stop"

function Log([string]$Message) {
    $line = "{0} [{1}] {2}" -f (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), $RunName, $Message
    Add-Content -Path $LogPath -Value $line -Encoding Ascii
    Write-Host $line
}

function LogLines($Lines, [string]$Prefix) {
    foreach ($l in @($Lines)) { Log ("{0}{1}" -f $Prefix, $l) }
}

Log "RUN START worktree=$Worktree pid_of_runner=$PID"

# ---- 1. allocate ----
Log "STEP allocate"
$allocOut = & powershell.exe -NoProfile -File $IsolationScript allocate -Worktree $Worktree
LogLines $allocOut "  "
if ($LASTEXITCODE -ne 0) { Log "FAIL allocate exit=$LASTEXITCODE"; exit 1 }
$slotLine = @($allocOut) | Where-Object { $_ -match "SLOT=(\d+)" } | Select-Object -First 1
$slot = [int]([regex]::Match($slotLine, "SLOT=(\d+)").Groups[1].Value)
$manifestLine = @($allocOut) | Where-Object { $_ -match "MANIFEST=" } | Select-Object -First 1
$manifest = ($manifestLine -split "MANIFEST=", 2)[1].Trim()
Log "ALLOCATED slot=$slot manifest=$manifest"

# ---- 2. build the slot exe inside THIS run's worktree ----
Log "STEP build (local-build-avalonia.ps1 -Slot $slot, cwd=$Worktree)"
Push-Location $Worktree
$buildOut = & powershell.exe -NoProfile -File (Join-Path $Worktree "scripts\local-build-avalonia.ps1") -Slot $slot
$buildExit = $LASTEXITCODE
Pop-Location
LogLines $buildOut "  "
if ($buildExit -ne 0) { Log "FAIL build exit=$buildExit"; exit 1 }
Log "BUILD OK slot=$slot"

# ---- 3. launch via the run's own per-slot scheduled task ----
Log "STEP launch"
$launchOut = & powershell.exe -NoProfile -File $IsolationScript launch -Manifest $manifest
LogLines $launchOut "  "
if ($LASTEXITCODE -ne 0) { Log "FAIL launch exit=$LASTEXITCODE"; exit 1 }
$pidLine = @($launchOut) | Where-Object { $_ -match "PID=(\d+)" } | Select-Object -First 1
$dirPid = [int]([regex]::Match($pidLine, "PID=(\d+)").Groups[1].Value)
$portLine = @($launchOut) | Where-Object { $_ -match "PORT=(\d+)" } | Select-Object -First 1
$port = [int]([regex]::Match($portLine, "PORT=(\d+)").Groups[1].Value)
Log "LAUNCHED pid=$dirPid port=$port"

# ---- 4. probe the run's own Control API port ----
Log "STEP probe GET http://127.0.0.1:$port/healthz"
$resp = Invoke-WebRequest -Uri "http://127.0.0.1:$port/healthz" -UseBasicParsing -TimeoutSec 15
Log "PROBE status=$($resp.StatusCode) body=$($resp.Content)"
if ($resp.StatusCode -ne 200) { Log "FAIL probe"; exit 1 }

# ---- 5. hold so the two concurrent runs overlap with both Directors alive ----
Log "STEP hold ${HoldSeconds}s (overlap window, Director alive on port $port)"
Start-Sleep -Seconds $HoldSeconds

# ---- 6. teardown ----
Log "STEP teardown (RemoveWorktree=$($RemoveWorktree.IsPresent))"
if ($RemoveWorktree) {
    $tdOut = & powershell.exe -NoProfile -File $IsolationScript teardown -Manifest $manifest -RemoveWorktree
} else {
    $tdOut = & powershell.exe -NoProfile -File $IsolationScript teardown -Manifest $manifest
}
LogLines $tdOut "  "
if ($LASTEXITCODE -ne 0) { Log "FAIL teardown exit=$LASTEXITCODE"; exit 1 }

# ---- 7. post-teardown asserts ----
$gone = Get-Process -Id $dirPid -ErrorAction SilentlyContinue
if ($null -ne $gone) { Log "FAIL process pid=$dirPid still alive after teardown"; exit 1 }
Log "ASSERT process pid=$dirPid gone"
$task = Get-ScheduledTask -TaskName "cc-director$slot-launch" -ErrorAction SilentlyContinue
if ($null -ne $task) { Log "FAIL scheduled task cc-director$slot-launch still registered"; exit 1 }
Log "ASSERT scheduled task cc-director$slot-launch unregistered"
if ($RemoveWorktree) {
    if (Test-Path $Worktree) { Log "FAIL worktree $Worktree still exists"; exit 1 }
    Log "ASSERT worktree $Worktree removed"
}

Log "RUN END slot=$slot port=$port pid=$dirPid result=SUCCESS"
exit 0
