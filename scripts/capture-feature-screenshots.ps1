#Requires -Version 5.1
<#
.SYNOPSIS
    Capture feature screenshots of the cc-director (DevThrottle) desktop app for
    the public feature documentation.

.DESCRIPTION
    This is the capture half of the /document-features pipeline. It:

      1. Builds a SLOT build of the app (default slot 5) so it never collides
         with the user's daily-driver Director or slots 1-4.
      2. Launches that build via the "cc-director-launch" Windows scheduled task
         (NOT from this process tree) per the CLAUDE.md launch rule, so the app
         and any child processes get clean stdio.
      3. Discovers the new Director's Control API port from its instances file.
      4. Creates a few throwaway demo git repositories and opens dummy sessions
         against them so the UI looks realistic. Dummy sessions default to a plain
         shell (Agent=RawCli, pwsh) so no Claude subscription is spent and no Claude
         auth is required. Pass -Agent ClaudeCode for richer wingman/voice content.
      5. Drives each documented screen with cc-click and saves a PNG into
         docs/public/features/assets/.
      6. Tears everything down: deletes the dummy sessions, removes the temp repos,
         and stops ONLY the slot build it started. It never touches another slot.

    The script is parameterized by docs/features/feature-inventory.yaml only for the
    screenshot file names; the actual navigation steps live in the $Shots table below
    (UI automation cannot be inferred from the inventory). Add a screen by adding a
    row to $Shots.

.PARAMETER Slot
    Which dev/test slot to build and launch. Default 5 (never 1-4, never main).

.PARAMETER Agent
    Agent kind for the dummy sessions. "RawCli" (default, plain shell - cheap, no auth)
    or "ClaudeCode" (real Claude session - richer screens, spends subscription).

.PARAMETER SkipBuild
    Reuse the existing local_builds\cc-director<Slot>.exe instead of rebuilding.

.PARAMETER KeepRunning
    Do not tear down at the end (leave the Director and dummy sessions up for
    manual inspection). Teardown is still safe to run later by hand.

.EXAMPLE
    .\scripts\capture-feature-screenshots.ps1
    .\scripts\capture-feature-screenshots.ps1 -SkipBuild
    .\scripts\capture-feature-screenshots.ps1 -Agent ClaudeCode
#>
param(
    [string]$Slot = "5",
    [ValidateSet("RawCli", "ClaudeCode")]
    [string]$Agent = "RawCli",
    [string]$ShellCommand = "powershell",
    [switch]$SkipBuild,
    [switch]$KeepRunning
)

$ErrorActionPreference = "Stop"

# ----------------------------------------------------------------------------
# Logging - plain ASCII, timestamped. No Unicode anywhere (repo rule).
# ----------------------------------------------------------------------------
function Write-Step([string]$msg) { Write-Host "[capture] $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "[capture] OK   $msg" -ForegroundColor Green }
function Write-Warn2([string]$msg){ Write-Host "[capture] WARN $msg" -ForegroundColor Yellow }
function Fail([string]$msg) {
    # No fallbacks: stop loudly with a clear, actionable message (repo rule).
    Write-Host "[capture] FAIL $msg" -ForegroundColor Red
    throw $msg
}

# ----------------------------------------------------------------------------
# Paths
# ----------------------------------------------------------------------------
$repoRoot   = Split-Path -Parent $PSScriptRoot
$assetsDir  = Join-Path $repoRoot "docs\public\features\assets"
$inventory  = Join-Path $repoRoot "docs\features\feature-inventory.yaml"
$buildScript= Join-Path $repoRoot "scripts\local-build-avalonia.ps1"
$slotExe    = Join-Path $repoRoot "local_builds\cc-director$Slot.exe"
$localAppData = $env:LOCALAPPDATA
$instancesDir = Join-Path $localAppData "cc-director\config\director\instances"
$taskName   = "cc-director-launch"
$tempRoot   = Join-Path $env:TEMP "devthrottle-doc-demo"

if (-not (Test-Path $inventory)) { Fail "Feature inventory not found at $inventory" }
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null

# ----------------------------------------------------------------------------
# Tool resolution - fail loudly if a required tool is missing (no silent skip).
# ----------------------------------------------------------------------------
function Resolve-Tool([string]$name, [string]$hint) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if (-not $cmd) { Fail "$name not found on PATH. $hint" }
    return $cmd.Source
}

# cc-click may not be installed on PATH (the cc-* toolset installs to
# %LOCALAPPDATA%\cc-director\bin), so also look in that bin and in this repo's
# build output. It is a project in this repo (tools/cc-click), so if it is
# missing everywhere we fail loudly with the one command that builds it.
function Resolve-CcClick() {
    $onPath = Get-Command "cc-click" -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $candidates = @(
        (Join-Path $localAppData "cc-director\bin\cc-click.exe"),
        (Join-Path $repoRoot "tools\cc-click\src\CcClick\bin\Release\net10.0-windows\cc-click.exe"),
        (Join-Path $repoRoot "tools\cc-click\src\CcClick\bin\Debug\net10.0-windows\cc-click.exe")
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    Fail "cc-click not found on PATH, in %LOCALAPPDATA%\cc-director\bin, or in the repo build output. Build it once with: dotnet build tools/cc-click/src/CcClick/CcClick.csproj -c Release"
}

$ccClick = Resolve-CcClick
$gitExe  = Resolve-Tool "git" "Install Git for Windows."
Write-Ok "cc-click: $ccClick"
Write-Ok "git:      $gitExe"

# ----------------------------------------------------------------------------
# cc-click helpers. cc-click returns JSON; an "error" property means the action
# did not happen. We surface that to the caller rather than hiding it.
# ----------------------------------------------------------------------------
function Invoke-CcClick([string[]]$ccArgs) {
    # cc-click writes its result JSON to stdout and error JSON to stderr (exit 1).
    # Under $ErrorActionPreference='Stop', letting a native command's stderr surface
    # makes PowerShell 5.1 raise a TERMINATING NativeCommandError. So localize the
    # preference and merge streams, then separate stdout (results) from stderr
    # (errors) ourselves - a cc-click "not found" must be a normal return, not a crash.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $raw = & $ccClick @ccArgs 2>&1 }
    finally { $ErrorActionPreference = $prev }

    $stdout = $raw | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }
    $text = ($stdout | Out-String).Trim()
    if (-not $text) {
        $errLines = $raw | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { $_.ToString() }
        $text = (($errLines) -join "`n").Trim()
    }
    if (-not $text) { return $null }
    try { return ($text | ConvertFrom-Json) } catch { return @{ raw = $text } }
}

# IMPORTANT: several "CC Director" windows can be open at once (the user's live
# Directors). We therefore target OUR slot build by its process id, never by the
# shared title, so we never screenshot or click another Director. Dialogs have
# unique titles ("New Session", "CC Director Settings") and are targeted by title.

# Confirm the slot Director actually has a top-level window for our pid. Uses the
# OS main-window handle (reliable and immune to cc-click parsing) rather than the
# window list.
function Test-PidWindow([int]$ProcId) {
    $p = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
    return ($p -and $p.MainWindowHandle -ne [System.IntPtr]::Zero)
}

function Save-PidShot([int]$ProcId, [string]$outFile) {
    $out = Join-Path $assetsDir $outFile
    Invoke-CcClick @("screenshot", "--pid", "$ProcId", "--output", $out) | Out-Null
    if (Test-Path $out) {
        Write-Ok "captured $outFile ($([math]::Round((Get-Item $out).Length/1KB)) KB)"
        return $true
    }
    Write-Warn2 "screenshot did not produce $outFile"
    return $false
}

function Save-TitleShot([string]$winTitle, [string]$outFile) {
    $out = Join-Path $assetsDir $outFile
    Invoke-CcClick @("screenshot", "--window", $winTitle, "--output", $out) | Out-Null
    if (Test-Path $out) {
        Write-Ok "captured $outFile ($([math]::Round((Get-Item $out).Length/1KB)) KB)"
        return $true
    }
    Write-Warn2 "screenshot did not produce $outFile (window '$winTitle' not found?)"
    return $false
}

# Click a control by visible text/name on OUR slot window (by pid).
function Click-PidByName([int]$ProcId, [string]$name) {
    $res = Invoke-CcClick @("click", "--pid", "$ProcId", "--name", $name)
    if ($res -and $res.error) { Write-Warn2 "click '$name' -> $($res.error)"; return $false }
    Start-Sleep -Milliseconds 700
    return $true
}

# Dismiss an open dialog (targeted by its unique title) via a common close button.
function Dismiss-DialogByTitle([string]$winTitle) {
    foreach ($label in @("Cancel", "Close", "Done", "OK")) {
        $res = Invoke-CcClick @("click", "--window", $winTitle, "--name", $label)
        if ($res -and -not $res.error) { Start-Sleep -Milliseconds 400; return $true }
    }
    Write-Warn2 "could not find a Cancel/Close/Done/OK button to dismiss '$winTitle'"
    return $false
}

# Stray OS dialogs (e.g. "Get an app to open this link") can pop on top of the
# Director window and ruin a capture. Close any top-level window NOT owned by our
# slot process whose title matches a known nuisance pattern. Uses the handle from
# cc-click list-windows + a WM_CLOSE message (no clicking, so it cannot misfire on
# the user's real windows - the title patterns are OS shells, not app windows).
Add-Type -Namespace Win32 -Name Native -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern System.IntPtr SendMessage(System.IntPtr hWnd, uint Msg, System.IntPtr wParam, System.IntPtr lParam);
"@
function Close-StrayDialogs([int]$OurProcId) {
    $patterns = @("Get an app", "open this", "How do you want to open", "Did you mean to switch")
    $res = Invoke-CcClick @("list-windows")
    if (-not $res) { return }
    $windows = if ($res.windows) { $res.windows } else { $res }
    foreach ($w in $windows) {
        if ($w.processId -eq $OurProcId) { continue }
        $t = "$($w.title)"
        if ([string]::IsNullOrWhiteSpace($t)) { continue }
        foreach ($p in $patterns) {
            if ($t -like "*$p*") {
                [Win32.Native]::SendMessage([System.IntPtr][int64]$w.handle, 0x0010, [System.IntPtr]::Zero, [System.IntPtr]::Zero) | Out-Null
                Write-Warn2 "closed stray OS dialog: $t"
                break
            }
        }
    }
}

# ----------------------------------------------------------------------------
# Control API helpers
# ----------------------------------------------------------------------------
function Invoke-Api([string]$method, [string]$base, [string]$path, $body) {
    $uri = "$base$path"
    if ($body) {
        $json = $body | ConvertTo-Json -Depth 6
        return Invoke-RestMethod -Method $method -Uri $uri -Body $json -ContentType "application/json" -TimeoutSec 20
    }
    return Invoke-RestMethod -Method $method -Uri $uri -TimeoutSec 20
}

# ============================================================================
# STEP 1: Build the slot
# ============================================================================
if ($SkipBuild) {
    if (-not (Test-Path $slotExe)) { Fail "-SkipBuild was set but $slotExe does not exist. Run without -SkipBuild first." }
    Write-Ok "Reusing existing build: $slotExe"
} else {
    Write-Step "Building slot $Slot (this can take a few minutes)..."
    & powershell -NoProfile -ExecutionPolicy Bypass -File $buildScript -Slot $Slot
    if ($LASTEXITCODE -ne 0) { Fail "Slot build failed (exit $LASTEXITCODE)." }
    if (-not (Test-Path $slotExe)) { Fail "Build reported success but $slotExe is missing." }
    Write-Ok "Built $slotExe"
}

# ============================================================================
# STEP 2: Register + launch via the cc-director-launch scheduled task
# (per CLAUDE.md: session-creating runs must launch outside this process tree)
# ============================================================================
Write-Step "Pointing scheduled task '$taskName' at the slot build and launching it..."
$wd = Join-Path $repoRoot "local_builds"
$action  = New-ScheduledTaskAction -Execute $slotExe -WorkingDirectory $wd
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Force | Out-Null

# Remember slot PIDs already running so we only ever act on the one WE start.
function Get-SlotPids() {
    Get-Process -Name "cc-director$Slot" -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $slotExe } | Select-Object -ExpandProperty Id
}
$before = @(Get-SlotPids)
Start-ScheduledTask -TaskName $taskName

Write-Step "Waiting for the slot Director process to appear..."
$ourPid = $null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 1
    $now = @(Get-SlotPids)
    $new = $now | Where-Object { $before -notcontains $_ }
    if ($new) { $ourPid = $new | Select-Object -First 1; break }
}
if (-not $ourPid) { Fail "Slot Director did not start within 60s (looked for $slotExe)." }
Write-Ok "Slot Director PID = $ourPid"

# ============================================================================
# STEP 3: Discover the Control API base URL from the instances file
# ============================================================================
Write-Step "Discovering Control API endpoint from $instancesDir ..."
$apiBase = $null
for ($i = 0; $i -lt 150; $i++) {
    Start-Sleep -Seconds 1
    if (-not (Test-Path $instancesDir)) { continue }
    $files = Get-ChildItem -Path $instancesDir -Filter "*.json" -ErrorAction SilentlyContinue
    foreach ($f in $files) {
        try { $dto = Get-Content $f.FullName -Raw | ConvertFrom-Json } catch { continue }
        if ($dto.Pid -eq $ourPid -or $dto.pid -eq $ourPid) {
            $ep = if ($dto.ControlEndpoint) { $dto.ControlEndpoint } else { $dto.controlEndpoint }
            if ($ep) { $apiBase = $ep.TrimEnd('/'); break }
        }
    }
    if ($apiBase) { break }
    if (($i + 1) % 15 -eq 0) { Write-Step "  still waiting for the instance file (PID $ourPid)... ${i}s" }
}
if (-not $apiBase) { Fail "Could not find the Control API endpoint for PID $ourPid in the instances files after 150s." }
Write-Ok "Control API: $apiBase"

Write-Step "Waiting for the Control API to answer..."
$ready = $false
for ($i = 0; $i -lt 90; $i++) {
    try { Invoke-Api GET $apiBase "/sessions" $null | Out-Null; $ready = $true; break }
    catch { Start-Sleep -Seconds 1 }
}
if (-not $ready) { Fail "Control API at $apiBase never became ready." }
Write-Ok "Control API is ready."

# ============================================================================
# STEP 4: Create throwaway demo repos + dummy sessions
# ============================================================================
Write-Step "Creating demo repositories under $tempRoot ..."
if (Test-Path $tempRoot) { Remove-Item -Recurse -Force $tempRoot }
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

$demos = @(
    @{ Name = "demo-web";  Type = "Developer"; Wingman = $false },
    @{ Name = "demo-api";  Type = "QA";        Wingman = $false },
    @{ Name = "demo-docs"; Type = "Product";   Wingman = $true  }
)

function New-DemoRepo([string]$path, [string]$name) {
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    Push-Location $path
    try {
        & $gitExe init -q
        & $gitExe config user.email "docbot@devthrottle.local"
        & $gitExe config user.name  "DevThrottle Doc Bot"
        Set-Content -Path (Join-Path $path "README.md") -Value "# $name`n`nDemo repository for DevThrottle feature screenshots." -Encoding ascii
        Set-Content -Path (Join-Path $path "main.py")   -Value "def main():`n    print('hello from $name')" -Encoding ascii
        & $gitExe add -A
        & $gitExe commit -q -m "Initial commit"
        # Leave one uncommitted change so the Source Control tab has content to show.
        Add-Content -Path (Join-Path $path "main.py") -Value "`n# work in progress" -Encoding ascii
        Set-Content -Path (Join-Path $path "notes.txt") -Value "scratch notes" -Encoding ascii
    } finally { Pop-Location }
}

$createdSessions = @()
foreach ($d in $demos) {
    $repoPath = Join-Path $tempRoot $d.Name
    New-DemoRepo $repoPath $d.Name
    $body = @{
        repoPath       = $repoPath
        agent          = $Agent
        type           = $d.Type
        wingmanEnabled = $d.Wingman
    }
    if ($Agent -eq "RawCli") { $body.command = $ShellCommand }
    try {
        $resp = Invoke-Api POST $apiBase "/sessions" $body
        $sid  = if ($resp.id) { $resp.id } elseif ($resp.sessionId) { $resp.sessionId } else { $null }
        if ($sid) { $createdSessions += $sid }
        Write-Ok "session '$($d.Name)' created (id=$sid, type=$($d.Type))"
    } catch {
        Write-Warn2 "could not create session for $($d.Name): $($_.Exception.Message)"
    }
}
if ($createdSessions.Count -eq 0) { Fail "No dummy sessions were created; nothing realistic to screenshot." }

Write-Step "Letting the sessions settle..."
Start-Sleep -Seconds 8

# ============================================================================
# STEP 5: Resolve the window and capture each shot
# ============================================================================
Write-Step "Confirming the slot Director window (PID $ourPid) is visible..."
$haveWin = $false
for ($i = 0; $i -lt 60; $i++) {
    Close-StrayDialogs $ourPid   # a blocking OS dialog can delay the main window
    if (Test-PidWindow $ourPid) { $haveWin = $true; break }
    Start-Sleep -Seconds 1
}
if (-not $haveWin) { Fail "No visible window for slot Director PID $ourPid on this desktop after 60s." }
Write-Ok "Slot-5 window for PID $ourPid is visible."
Close-StrayDialogs $ourPid

# Data-driven shot table. Each shot has an output PNG and an action that navigates
# to the screen first. Navigation always clicks OUR slot window (by pid). A shot
# with a "Dialog" title is screenshotted by that unique title and then dismissed;
# the rest are main-window states captured by pid. Extend by adding rows.
# Dialog shots are placed last-ish so a stuck dialog cannot block the tab shots.
#
# NOTE on dialogs: a toolbar button that opens a modal ShowDialog window only
# renders that window when the app can be brought to the foreground. Run from an
# interactive desktop session, the dialog shots succeed; run from a background /
# non-interactive context they are reported as missing (the main-window and tab
# shots still work, because PrintWindow captures the window's own pixels and the
# tabs switch content in place without needing the foreground).
$Shots = @(
    @{ Id = "main-window";        Out = "main-window.png";        Action = { } },
    @{ Id = "terminal-tab";       Out = "terminal-tab.png";       Action = { Click-PidByName $ourPid "Terminal" | Out-Null } },
    @{ Id = "source-control-tab"; Out = "source-control-tab.png"; Action = { Click-PidByName $ourPid "Source Control" | Out-Null } },
    @{ Id = "new-session-dialog"; Out = "new-session-dialog.png"; Action = { Click-PidByName $ourPid "New Session" | Out-Null }; Dialog = "New Session" },
    @{ Id = "settings-dialog";    Out = "settings-dialog.png";    Action = { Click-PidByName $ourPid "Settings" | Out-Null };    Dialog = "CC Director Settings" },
    @{ Id = "tools-view";         Out = "tools-view.png";         Action = { Click-PidByName $ourPid "Terminal" | Out-Null; Click-PidByName $ourPid "Tools" | Out-Null } }
)

$captured = @()
$failed = @()
foreach ($shot in $Shots) {
    Write-Step "Shot: $($shot.Id)"
    Close-StrayDialogs $ourPid
    try { & $shot.Action } catch { Write-Warn2 "navigation for $($shot.Id) threw: $($_.Exception.Message)" }
    Start-Sleep -Milliseconds 600
    if ($shot.Dialog) {
        Close-StrayDialogs $ourPid   # do NOT close our own dialog (different title); only OS nuisances
        if (Save-TitleShot $shot.Dialog $shot.Out) { $captured += $shot.Out } else { $failed += $shot.Out }
        Dismiss-DialogByTitle $shot.Dialog | Out-Null
        Start-Sleep -Milliseconds 500
    } else {
        Close-StrayDialogs $ourPid   # clear any OS dialog that popped back before capture
        if (Save-PidShot $ourPid $shot.Out) { $captured += $shot.Out } else { $failed += $shot.Out }
    }
}

Write-Host ""
Write-Ok "Captured $($captured.Count)/$($Shots.Count) shots into $assetsDir"
if ($failed.Count -gt 0) { Write-Warn2 "Missing: $($failed -join ', ')" }

# Note which inventory screenshots are NOT covered by this harness yet, so the
# gap is visible rather than silently implied as complete.
$invText = Get-Content $inventory -Raw
$wantShots = [regex]::Matches($invText, 'screenshot:\s*([A-Za-z0-9._-]+\.png)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
$haveShots = $Shots | ForEach-Object { $_.Out }
$notAuto = $wantShots | Where-Object { $haveShots -notcontains $_ }
if ($notAuto) {
    Write-Warn2 "Inventory screens not auto-captured (capture manually or extend `$Shots): $($notAuto -join ', ')"
}

# ============================================================================
# STEP 6: Teardown (ONLY our slot - never another Director)
# ============================================================================
if ($KeepRunning) {
    Write-Warn2 "-KeepRunning set: leaving Director PID $ourPid and the demo repos in place."
    Write-Host "[capture] To clean up later: stop PID $ourPid (verify path = $slotExe) and delete $tempRoot"
    return
}

Write-Step "Tearing down dummy sessions..."
foreach ($sid in $createdSessions) {
    try { Invoke-Api DELETE $apiBase "/sessions/$sid" $null | Out-Null; Write-Ok "killed session $sid" }
    catch { Write-Warn2 "could not delete session ${sid}: $($_.Exception.Message)" }
}

Write-Step "Stopping the slot Director (PID $ourPid)..."
$proc = Get-Process -Id $ourPid -ErrorAction SilentlyContinue
if ($proc -and $proc.Path -eq $slotExe) {
    Stop-Process -Id $ourPid -Force
    Write-Ok "stopped PID $ourPid"
} else {
    Write-Warn2 "PID $ourPid is not the slot exe we started (path mismatch); leaving it alone."
}

Write-Step "Removing demo repositories..."
if (Test-Path $tempRoot) { Remove-Item -Recurse -Force $tempRoot }
Write-Ok "Done. Screenshots are in $assetsDir"
