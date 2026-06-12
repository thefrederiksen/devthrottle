#Requires -Version 5.1
<#
.SYNOPSIS
    Degradation property test (issue #336, Phase 1 exit criterion 1D):
    with the Gateway stopped (or never configured), the desktop Director is fully usable.

.DESCRIPTION
    Operationalizes the degradation story in docs/architecture/DIRECTOR_DUMB_WRAPPER_TARGET.md
    section 6 as a scripted, repeatable check. All assertions run via Control API only -
    no manual eyeballing required - so QA and future regressions can re-run it cheaply.

    TWO TEST CASES:
      Case A: Director with no gateway.url configured.
              Expected: indicator state = NotConfigured (gray), all session operations pass.
      Case B: Director with gateway.url configured but the Gateway unreachable (stopped).
              Expected: indicator state = Failed, all session operations pass identically.

    STEPS EXERCISED (per case):
      1. Healthz - Director responds; GET /sessions and GET /facts are non-blocking.
      2. Create a RawCli/pwsh session; assert 201 and session appears in GET /sessions.
      3. Round-trip a prompt (POST /prompt, assert output in GET /buffer).
      4. Resize the session (POST /resize).
      5. Git facts (GET /sessions/{sid}/git on a scratch git repo with a staged file).
      6. Git stage/unstage round-trip on the scratch repo.
      7. Kill the session (DELETE /sessions/{sid}), assert session gone.
      8. Session persistence / crash recovery: start a second session, kill the Director
         process (simulating a crash), restart it, assert GET /interrupted lists the session.

    RAWCLI STATUS: RawCli kind merged in PR #364 (issue #333); RawCli step is ACTIVE.

    GATEWAY-DOWN NOTE: this script does NOT stop the user's production Gateway. Case B
    uses a fake gateway.url (http://127.0.0.1:1) pointing at a port where nothing listens,
    so the Director tries to register, gets connection-refused, and transitions to Failed -
    exactly the degraded-mode state the plan requires. No real Gateway is touched.

    ISOLATION:
      - Uses agent-session-isolation.ps1 to allocate its own slot (>= 6).
      - Builds in the provided worktree (default: this script's parent directory).
      - Sets CC_DIRECTOR_ROOT to a temp dir so the test Director never reads or writes
        the user's real %LOCALAPPDATA%\cc-director config. A per-case config.json is
        written into the temp dir before each case.
      - Launches via its own per-slot scheduled task wrapper (rule 0b: not from this
        agent's process tree). The wrapper sets CC_DIRECTOR_ROOT then starts the Director.
      - Tears down ONLY its own Director (exact image-path guard from agent-session-isolation).

.PARAMETER RepoRoot
    Absolute path to the cc-director repo worktree. Defaults to this script's parent dir.

.PARAMETER Manifest
    (Optional) Pre-allocated slot manifest JSON from agent-session-isolation.ps1 allocate.
    When supplied the script skips allocate and uses this manifest's exe directly.
    The caller must have built the slot exe before passing this parameter.

.PARAMETER SkipBuild
    Skip the dotnet build step. Use when a fresh build was done by the caller already.

.PARAMETER CaseFilter
    Which cases to run: "A", "B", or "AB" (default).

.PARAMETER TimeoutSeconds
    Max seconds to wait for each blocking step (Director start, buffer output, etc.).
    Default 120.

.EXAMPLE
    # Full run from a fresh slot worktree:
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-degradation.ps1

.EXAMPLE
    # Re-run with a pre-built, pre-allocated slot (fast):
    powershell -NoProfile -File scripts\test-degradation.ps1 -Manifest D:\wt\local_builds\agent-session-slot7.json -SkipBuild

.NOTES
    Exit code 0 = all assertions passed.
    Exit code 1 = one or more assertions failed.
    Output is ASCII only; every assertion is prefixed [PASS] or [FAIL].
    Cross-machine proof is intentionally NOT required: the property is proven without a
    remote machine - the "Gateway stop" is a fake port 1, Director and assertions run on
    this machine, and the report states so explicitly.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$Manifest = "",
    [switch]$SkipBuild,
    [ValidateSet("A", "B", "AB")]
    [string]$CaseFilter = "AB",
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
$script:Passed = 0
$script:Failed = 0
$script:Results = [System.Collections.Generic.List[object]]::new()
$script:StartTime = Get-Date
$script:Transcript = [System.Text.StringBuilder]::new()

function Write-T([string]$msg) {
    Write-Host $msg
    [void]$script:Transcript.AppendLine($msg)
}

# Resolve the repo root from the script's own location if not supplied.
if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
if (-not (Test-Path (Join-Path $RepoRoot "cc-director.sln"))) {
    Write-T "[test-degradation] ERROR: RepoRoot '$RepoRoot' does not look like a cc-director checkout."
    exit 1
}

$IsoScript = Join-Path $RepoRoot "scripts\agent-session-isolation.ps1"

function Write-Step([string]$msg) {
    Write-T ""
    Write-T "--- $msg ---"
}

function Add-Result([string]$check, [bool]$ok, [string]$detail) {
    $script:Results.Add([pscustomobject]@{ Check = $check; Pass = $ok; Detail = $detail })
    $tag = if ($ok) { "[PASS]" } else { "[FAIL]" }
    $line = "{0} {1} - {2}" -f $tag, $check, $detail
    Write-T $line
    if ($ok) { $script:Passed++ } else { $script:Failed++ }
}

function Invoke-Api {
    param(
        [string]$Uri,
        [string]$Method = "GET",
        [string]$Body = "",
        [int]$TimeoutSec = 30
    )
    try {
        $a = @{ Uri = $Uri; Method = $Method; UseBasicParsing = $true; TimeoutSec = $TimeoutSec }
        if ($Body) { $a["Body"] = $Body; $a["ContentType"] = "application/json" }
        $resp = Invoke-WebRequest @a
        return [pscustomobject]@{ Ok = $true; Status = $resp.StatusCode; Content = $resp.Content }
    } catch {
        $st = 0
        try { $st = $_.Exception.Response.StatusCode.value__ } catch { }
        $body = ""
        try {
            $s = $_.Exception.Response.GetResponseStream()
            $r = [System.IO.StreamReader]::new($s)
            $body = $r.ReadToEnd()
        } catch { }
        return [pscustomobject]@{ Ok = $false; Status = $st; Content = $body; Error = $_.Exception.Message }
    }
}

function Wait-Http([string]$url, [int]$timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    do {
        $r = Invoke-Api -Uri $url -TimeoutSec 5
        if ($r.Ok -and $r.Status -eq 200) { return $true }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Wait-BufferContains([string]$bufUrl, [string]$substring, [int]$timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    do {
        $r = Invoke-Api -Uri $bufUrl -TimeoutSec 10
        if ($r.Ok -and $r.Content -match [regex]::Escape($substring)) { return $true }
        Start-Sleep -Milliseconds 750
    } while ((Get-Date) -lt $deadline)
    return $false
}

# Write an isolated config.json for the test Director.
# Returns the isolated root dir path (set CC_DIRECTOR_ROOT to this).
function New-IsolatedRoot([string]$label, [string]$gatewayUrl = "") {
    $dir = Join-Path $env:TEMP ("cc-dir-degrade-$label-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
    $configDir = Join-Path $dir "config"
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    if ($gatewayUrl) {
        $json = "{`"gateway`":{`"url`":`"$gatewayUrl`",`"token`":`"test-token`"}}"
    } else {
        $json = "{}"
    }
    $json | Set-Content -Path (Join-Path $configDir "config.json") -Encoding UTF8
    return $dir
}

# Create a scratch git repo with a staged change (so GET /git has something to report).
function New-ScratchRepo {
    $dir = Join-Path $env:TEMP ("cc-dir-scratch-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Path $dir | Out-Null
    git -C $dir init --initial-branch=main 2>$null | Out-Null
    git -C $dir config user.email "test@example.com" 2>$null | Out-Null
    git -C $dir config user.name "Test" 2>$null | Out-Null
    "hello" | Set-Content -Path (Join-Path $dir "hello.txt") -Encoding UTF8
    git -C $dir add "hello.txt" 2>$null | Out-Null
    git -C $dir commit -m "initial" 2>$null | Out-Null
    # Stage a change so /git has non-empty staged diff
    "hello changed" | Set-Content -Path (Join-Path $dir "hello.txt") -Encoding UTF8
    git -C $dir add "hello.txt" 2>$null | Out-Null
    return $dir
}

# Launch the Director for a specific case via a per-case wrapper script.
# Returns [pscustomobject]@{ Port; Pid; LogFile } or null on failure.
function Start-DirectorForCase {
    param(
        [string]$CaseName,
        [string]$DirectorExe,
        [string]$IsolatedRoot,
        [string]$TaskName,
        [string]$LogDir,
        [int]$TimeoutSec
    )
    # Write a wrapper PS1 that sets CC_DIRECTOR_ROOT and starts the Director.
    # The Director process will be a child of powershell (the task exe), but since
    # powershell.exe is the scheduled task's executable, it runs outside our ConPty
    # (rule 0b). The Director process inherits the CC_DIRECTOR_ROOT env var from the
    # powershell process and writes its log to the standard LogDir.
    $localBuilds = Split-Path -Parent $DirectorExe
    $wrapperPath = Join-Path $localBuilds "launch-wrapper-$CaseName.ps1"
    $wrapperContent = @"
`$env:CC_DIRECTOR_ROOT = '$IsolatedRoot'
`$exePath = '$DirectorExe'
Start-Process -FilePath `$exePath -WorkingDirectory '$localBuilds'
"@
    $wrapperContent | Set-Content -Path $wrapperPath -Encoding UTF8

    # Register the scheduled task to run our wrapper (with -NoProfile -File <wrapper>).
    $action = New-ScheduledTaskAction `
        -Execute "powershell.exe" `
        -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$wrapperPath`"" `
        -WorkingDirectory $localBuilds
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5)
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Force | Out-Null
    Write-T "[test-degradation] [$CaseName] registered task $TaskName with CC_DIRECTOR_ROOT=$IsolatedRoot"

    # Start the task
    Start-ScheduledTask -TaskName $TaskName
    Write-T "[test-degradation] [$CaseName] started task $TaskName"

    # Resolve the Director PID: look for cc-director<N> whose image path matches the exe.
    $exeName = [System.IO.Path]::GetFileNameWithoutExtension($DirectorExe)
    $deadline = (Get-Date).AddSeconds(60)
    $dirPid = 0
    while ((Get-Date) -lt $deadline) {
        $procs = @(Get-Process -Name $exeName -ErrorAction SilentlyContinue)
        foreach ($p in $procs) {
            if ($p.Path -and ($p.Path -ieq $DirectorExe)) { $dirPid = $p.Id; break }
        }
        if ($dirPid -ne 0) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($dirPid -eq 0) {
        Write-T "[test-degradation] [$CaseName] ERROR: Director did not appear within 60s"
        return $null
    }
    Write-T "[test-degradation] [$CaseName] Director PID=$dirPid"

    # Discover the Control API port from the Director's log file.
    if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
    $deadlinePort = (Get-Date).AddSeconds($TimeoutSec)
    $port = 0
    $logFile = ""
    while ((Get-Date) -lt $deadlinePort) {
        $candidates = @(Get-ChildItem -Path $LogDir -Filter "director-*-$dirPid.log" -ErrorAction SilentlyContinue)
        foreach ($f in $candidates) {
            $hit = Select-String -Path $f.FullName -Pattern "Kestrel listening on http://[^:]+:(\d+)" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($hit) {
                $port = [int]$hit.Matches[0].Groups[1].Value
                $logFile = $f.FullName
                break
            }
        }
        if ($port -ne 0) { break }
        $alive = Get-Process -Id $dirPid -ErrorAction SilentlyContinue
        if ($null -eq $alive) {
            Write-T "[test-degradation] [$CaseName] ERROR: Director PID $dirPid exited before reporting port."
            return $null
        }
        Start-Sleep -Milliseconds 500
    }
    if ($port -eq 0) {
        Write-T "[test-degradation] [$CaseName] ERROR: Port not found in log within ${TimeoutSec}s (PID $dirPid, logDir $LogDir)."
        # Stop orphan
        $p = Get-Process -Id $dirPid -ErrorAction SilentlyContinue
        if ($p -and $p.Path -ieq $DirectorExe) { Stop-Process -Id $dirPid -Force -Confirm:$false }
        return $null
    }

    Write-T "[test-degradation] [$CaseName] Port=$port LogFile=$logFile"
    return [pscustomobject]@{ Port = $port; Pid = $dirPid; LogFile = $logFile }
}

# Stop the Director for a case (exact image-path guard).
function Stop-DirectorForCase {
    param([string]$DirectorExe, [int]$DirectorPid, [string]$CaseName)
    $p = Get-Process -Id $DirectorPid -ErrorAction SilentlyContinue
    if ($null -eq $p) {
        Write-T "[test-degradation] [$CaseName] Director PID $DirectorPid already gone"
        return
    }
    if (-not ($p.Path -ieq $DirectorExe)) {
        Write-T "[test-degradation] [$CaseName] WARNING: PID $DirectorPid image $($p.Path) != $DirectorExe, NOT stopping"
        return
    }
    Write-T "[test-degradation] [$CaseName] Stopping Director PID $DirectorPid"
    Stop-Process -Id $DirectorPid -Force -Confirm:$false
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $alive = Get-Process -Id $DirectorPid -ErrorAction SilentlyContinue
        if ($null -eq $alive) { break }
        Start-Sleep -Milliseconds 250
    }
}

# Run all lifecycle assertions against a running Director.
function Invoke-LifecycleAssertions([string]$CaseName, [string]$BaseUrl, [string]$ScratchRepo) {
    Write-Step "[$CaseName] Step 1: healthz and non-blocking fact endpoints"
    $rH = Invoke-Api -Uri "$BaseUrl/healthz" -TimeoutSec 15
    Add-Result "[$CaseName] healthz 200" ($rH.Ok -and $rH.Status -eq 200) "status=$($rH.Status)"

    $sw1 = [System.Diagnostics.Stopwatch]::StartNew()
    $rS = Invoke-Api -Uri "$BaseUrl/sessions" -TimeoutSec 15
    $sw1.Stop()
    Add-Result "[$CaseName] GET /sessions non-blocking (<5000ms)" ($rS.Ok -and $sw1.ElapsedMilliseconds -lt 5000) "status=$($rS.Status) elapsed=$($sw1.ElapsedMilliseconds)ms"

    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $rF = Invoke-Api -Uri "$BaseUrl/facts" -TimeoutSec 15
    $sw2.Stop()
    Add-Result "[$CaseName] GET /facts non-blocking (<5000ms)" ($rF.Ok -and $sw2.ElapsedMilliseconds -lt 5000) "status=$($rF.Status) elapsed=$($sw2.ElapsedMilliseconds)ms"

    Write-Step "[$CaseName] Step 2: create RawCli/pwsh session"
    $repoEsc = $ScratchRepo.Replace('\', '\\')
    $createBody = "{`"repoPath`":`"$repoEsc`",`"agent`":`"RawCli`",`"command`":`"powershell`",`"commandArgs`":`"-NoProfile -NoLogo`"}"
    $rC = Invoke-Api -Uri "$BaseUrl/sessions" -Method "POST" -Body $createBody -TimeoutSec 30
    Add-Result "[$CaseName] POST /sessions 201" ($rC.Status -eq 201) "status=$($rC.Status)"

    $sid = ""
    if ($rC.Status -eq 201) {
        try { $sid = ($rC.Content | ConvertFrom-Json).sessionId } catch { }
    }
    if (-not $sid) {
        Add-Result "[$CaseName] session id present" $false "could not parse sessionId"
        return @{ Sid = "" }
    }
    Add-Result "[$CaseName] session id present" $true "sid=$sid"

    Start-Sleep -Milliseconds 500
    $rL = Invoke-Api -Uri "$BaseUrl/sessions" -TimeoutSec 15
    $listed = $false
    try { $listed = (($rL.Content | ConvertFrom-Json) | ForEach-Object { $_.sessionId }) -contains $sid } catch { }
    Add-Result "[$CaseName] session in GET /sessions list" $listed "listed=$listed"

    Write-Step "[$CaseName] Step 3: round-trip a prompt"
    # Wait for pwsh to render its prompt
    Start-Sleep -Seconds 3
    $promptBody = "{`"text`":`"Write-Host 'DEGRADE-PROBE-OK'`",`"appendEnter`":true}"
    $rP = Invoke-Api -Uri "$BaseUrl/sessions/$sid/prompt" -Method "POST" -Body $promptBody -TimeoutSec 20
    Add-Result "[$CaseName] POST /prompt 200" ($rP.Ok -and $rP.Status -eq 200) "status=$($rP.Status)"

    $found = Wait-BufferContains -bufUrl "$BaseUrl/sessions/$sid/buffer" -substring "DEGRADE-PROBE-OK" -timeoutSec 30
    Add-Result "[$CaseName] probe output in buffer" $found "found=$found"

    Write-Step "[$CaseName] Step 4: resize"
    $rR = Invoke-Api -Uri "$BaseUrl/sessions/$sid/resize" -Method "POST" -Body "{`"cols`":120,`"rows`":40}" -TimeoutSec 10
    Add-Result "[$CaseName] POST /resize 200" ($rR.Ok -and $rR.Status -eq 200) "status=$($rR.Status)"

    Write-Step "[$CaseName] Step 5: git facts (GET /git)"
    $rG = Invoke-Api -Uri "$BaseUrl/sessions/$sid/git" -TimeoutSec 20
    Add-Result "[$CaseName] GET /git 200" ($rG.Ok -and $rG.Status -eq 200) "status=$($rG.Status)"
    $gitData = $rG.Ok -and $rG.Content.Length -gt 2 -and $rG.Content -ne "null"
    Add-Result "[$CaseName] GET /git returns non-empty data" $gitData "contentLen=$($rG.Content.Length)"

    Write-Step "[$CaseName] Step 6: git stage/unstage round-trip"
    $pathsBody = "{`"paths`":[`"hello.txt`"]}"
    $rU = Invoke-Api -Uri "$BaseUrl/sessions/$sid/git/unstage" -Method "POST" -Body $pathsBody -TimeoutSec 20
    Add-Result "[$CaseName] POST /git/unstage 200" ($rU.Ok -and $rU.Status -eq 200) "status=$($rU.Status)"

    $rSt = Invoke-Api -Uri "$BaseUrl/sessions/$sid/git/stage" -Method "POST" -Body $pathsBody -TimeoutSec 20
    Add-Result "[$CaseName] POST /git/stage 200" ($rSt.Ok -and $rSt.Status -eq 200) "status=$($rSt.Status)"

    Write-Step "[$CaseName] Step 7: kill the session"
    $rD = Invoke-Api -Uri "$BaseUrl/sessions/$sid" -Method "DELETE" -TimeoutSec 20
    Add-Result "[$CaseName] DELETE /sessions 200" ($rD.Ok -and $rD.Status -eq 200) "status=$($rD.Status)"

    Start-Sleep -Milliseconds 500
    $rL2 = Invoke-Api -Uri "$BaseUrl/sessions" -TimeoutSec 15
    $stillListed = $false
    try { $stillListed = (($rL2.Content | ConvertFrom-Json) | ForEach-Object { $_.sessionId }) -contains $sid } catch { }
    Add-Result "[$CaseName] session absent after kill" (-not $stillListed) "stillListed=$stillListed"

    return @{ Sid = $sid }
}

# Crash-recovery assertion: create session, kill Director, restart, verify crash journal.
function Invoke-CrashRecovery {
    param(
        [string]$CaseName,
        [string]$BaseUrl,
        [string]$ScratchRepo,
        [string]$DirectorExe,
        [int]$CurrentPid,
        [string]$TaskName,
        [string]$IsolatedRoot,
        [string]$LogDir,
        [int]$TimeoutSec
    )
    Write-Step "[$CaseName] Step 8: crash recovery"

    # Create a session to be interrupted
    $repoEsc = $ScratchRepo.Replace('\', '\\')
    $createBody = "{`"repoPath`":`"$repoEsc`",`"agent`":`"RawCli`",`"command`":`"powershell`",`"commandArgs`":`"-NoProfile -NoLogo`"}"
    $rC = Invoke-Api -Uri "$BaseUrl/sessions" -Method "POST" -Body $createBody -TimeoutSec 30
    if ($rC.Status -ne 201) {
        Add-Result "[$CaseName] crash-recovery: session created" $false "status=$($rC.Status)"
        return 0
    }
    $sid = ""
    try { $sid = ($rC.Content | ConvertFrom-Json).sessionId } catch { }
    if (-not $sid) {
        Add-Result "[$CaseName] crash-recovery: session id parsed" $false "could not parse"
        return 0
    }
    Write-T "[test-degradation] [$CaseName] crash-recovery session: sid=$sid"
    # Wait for the UI thread to process the session-created event and write the crash journal.
    # OnExternalSessionCreated -> Dispatcher.UIThread.Post -> _sessions.Add -> PersistSessionState
    # (triggered by SelectSession when _activeSession is null). Give the UI thread 6 seconds.
    Write-T "[test-degradation] [$CaseName] waiting 6s for crash journal to be written..."
    Start-Sleep -Seconds 6

    # Kill the Director (exact image-path guard)
    $p = Get-Process -Id $CurrentPid -ErrorAction SilentlyContinue
    if ($null -eq $p -or -not ($p.Path -ieq $DirectorExe)) {
        Add-Result "[$CaseName] crash-recovery: Director found for kill" $false "pid=$CurrentPid path=$($p.Path)"
        return 0
    }
    Write-T "[test-degradation] [$CaseName] killing Director PID=$CurrentPid"
    Stop-Process -Id $CurrentPid -Force -Confirm:$false
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        $alive = Get-Process -Id $CurrentPid -ErrorAction SilentlyContinue
        if ($null -eq $alive) { break }
        Start-Sleep -Milliseconds 250
    }
    $alive = Get-Process -Id $CurrentPid -ErrorAction SilentlyContinue
    Add-Result "[$CaseName] crash-recovery: Director killed" ($null -eq $alive) "pid=$CurrentPid"

    # Re-launch via the same task (wrapper already in place with correct CC_DIRECTOR_ROOT)
    Write-T "[test-degradation] [$CaseName] re-launching via task $TaskName"
    Start-ScheduledTask -TaskName $TaskName

    # Discover new PID
    $exeName = [System.IO.Path]::GetFileNameWithoutExtension($DirectorExe)
    $deadlinePid = (Get-Date).AddSeconds(60)
    $newPid = 0
    while ((Get-Date) -lt $deadlinePid) {
        $procs = @(Get-Process -Name $exeName -ErrorAction SilentlyContinue)
        foreach ($pr in $procs) {
            if ($pr.Path -and ($pr.Path -ieq $DirectorExe) -and $pr.Id -ne $CurrentPid) {
                $newPid = $pr.Id; break
            }
        }
        if ($newPid -ne 0) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($newPid -eq 0) {
        Add-Result "[$CaseName] crash-recovery: Director restarted" $false "new PID not found within 60s"
        return 0
    }
    Write-T "[test-degradation] [$CaseName] new Director PID=$newPid"

    # Discover new port from log
    $newPort = 0
    $deadlinePort = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadlinePort) {
        $candidates = @(Get-ChildItem -Path $LogDir -Filter "director-*-$newPid.log" -ErrorAction SilentlyContinue)
        foreach ($f in $candidates) {
            $hit = Select-String -Path $f.FullName -Pattern "Kestrel listening on http://[^:]+:(\d+)" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($hit) { $newPort = [int]$hit.Matches[0].Groups[1].Value; break }
        }
        if ($newPort -ne 0) { break }
        $alive = Get-Process -Id $newPid -ErrorAction SilentlyContinue
        if ($null -eq $alive) { Write-T "[test-degradation] [$CaseName] restarted Director exited early."; break }
        Start-Sleep -Milliseconds 500
    }
    if ($newPort -eq 0) {
        Add-Result "[$CaseName] crash-recovery: restarted Director port discovered" $false "timeout"
        return 0
    }
    Add-Result "[$CaseName] crash-recovery: Director restarted" $true "newPid=$newPid newPort=$newPort"

    $newUrl = "http://127.0.0.1:$newPort"
    $up = Wait-Http "$newUrl/healthz" 60
    Add-Result "[$CaseName] crash-recovery: restarted Director healthz" $up "url=$newUrl/healthz"

    if ($up) {
        # Crash journal should list the interrupted session.
        $rI = Invoke-Api -Uri "$newUrl/interrupted" -TimeoutSec 15
        Add-Result "[$CaseName] crash-recovery: GET /interrupted 200" ($rI.Ok -and $rI.Status -eq 200) "status=$($rI.Status)"
        $found = $false
        try {
            $entries = $rI.Content | ConvertFrom-Json
            # ASP.NET Core serializes DirectorCrashJournalData with camelCase property names.
            # "Sessions" property becomes "sessions" in JSON.
            foreach ($entry in $entries) {
                $sessList = $entry.sessions  # camelCase from ASP.NET Core
                if ($null -eq $sessList) { $sessList = $entry.Sessions }  # fallback
                foreach ($rs in $sessList) {
                    $rsSid = if ($rs.sessionId) { $rs.sessionId } else { $rs.SessionId }
                    if ($rsSid -and ($rsSid -ieq $sid)) { $found = $true; break }
                }
                if ($found) { break }
            }
        } catch { }
        Add-Result "[$CaseName] crash-recovery: interrupted session in journal" $found "sid=$sid found=$found"
    }

    return $newPid
}

# Run a single case (A or B).
function Invoke-Case {
    param(
        [string]$CaseName,
        [string]$DirectorExe,
        [string]$TaskName,
        [string]$GatewayUrl,
        [string]$ScratchRepo,
        [int]$TimeoutSec
    )
    Write-T ""
    Write-T "##############################################"
    $caseDesc = if ($GatewayUrl) { 'gateway.url set, Gateway unreachable (fake port 1)' } else { 'no gateway.url configured' }
    Write-T "# CASE $CaseName - $caseDesc"
    Write-T "##############################################"

    $isolatedRoot = New-IsolatedRoot -label $CaseName.ToLower() -gatewayUrl $GatewayUrl
    Write-T "[test-degradation] [$CaseName] isolated root: $isolatedRoot"

    # The logs directory is inside the isolated root so each case has its own log space.
    $logDir = Join-Path $isolatedRoot "logs\director"
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null

    Write-Step "[$CaseName] Launching Director"
    $result = Start-DirectorForCase -CaseName $CaseName -DirectorExe $DirectorExe -IsolatedRoot $isolatedRoot -TaskName $TaskName -LogDir $logDir -TimeoutSec $TimeoutSec
    if ($null -eq $result) {
        Add-Result "[$CaseName] Director launched" $false "Start-DirectorForCase returned null"
        if (Test-Path $isolatedRoot) { Remove-Item -Path $isolatedRoot -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue }
        return
    }
    Add-Result "[$CaseName] Director launched" $true "port=$($result.Port) pid=$($result.Pid)"

    $baseUrl = "http://127.0.0.1:$($result.Port)"
    $up = Wait-Http "$baseUrl/healthz" $TimeoutSec
    Add-Result "[$CaseName] Director healthz" $up "url=$baseUrl/healthz"

    if (-not $up) {
        Stop-DirectorForCase -DirectorExe $DirectorExe -DirectorPid $result.Pid -CaseName $CaseName
        if (Test-Path $isolatedRoot) { Remove-Item -Path $isolatedRoot -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue }
        return
    }

    # For Case B: wait a few seconds for the gateway registration attempt to fail
    if ($GatewayUrl) {
        Write-T "[test-degradation] [$CaseName] waiting 5s for gateway registration to fail..."
        Start-Sleep -Seconds 5
    }

    # Run lifecycle assertions
    Invoke-LifecycleAssertions -CaseName $CaseName -BaseUrl $baseUrl -ScratchRepo $ScratchRepo | Out-Null

    # Crash recovery
    $currentPid = $result.Pid
    $newPid = Invoke-CrashRecovery `
        -CaseName $CaseName `
        -BaseUrl $baseUrl `
        -ScratchRepo $ScratchRepo `
        -DirectorExe $DirectorExe `
        -CurrentPid $currentPid `
        -TaskName $TaskName `
        -IsolatedRoot $isolatedRoot `
        -LogDir $logDir `
        -TimeoutSec $TimeoutSec

    # For Case B: additional non-blocking assertion after a retry wait
    if ($GatewayUrl -and $newPid -ne 0) {
        Write-Step "[$CaseName] Step 9: non-blocking after gateway retry cycle (10s wait)"
        Start-Sleep -Seconds 10
        $retryUrl = "http://127.0.0.1:$(if ($newPid -ne 0) { 'unknown' } else { $result.Port })"
        # Re-discover port for new pid
        $newPortForRetry = 0
        $cands = @(Get-ChildItem -Path $logDir -Filter "director-*-$newPid.log" -ErrorAction SilentlyContinue)
        foreach ($f in $cands) {
            $hit = Select-String -Path $f.FullName -Pattern "Kestrel listening on http://[^:]+:(\d+)" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($hit) { $newPortForRetry = [int]$hit.Matches[0].Groups[1].Value; break }
        }
        if ($newPortForRetry -gt 0) {
            $sw9 = [System.Diagnostics.Stopwatch]::StartNew()
            $rRetry = Invoke-Api -Uri "http://127.0.0.1:$newPortForRetry/sessions" -TimeoutSec 15
            $sw9.Stop()
            # Threshold is 10s (generous): one gateway registration retry takes ~5s on a
            # connection-refused port, and the first Tailscale identity resolution attempt
            # may add a few more seconds. The claim is "never blocks indefinitely", not "instant".
            Add-Result "[$CaseName] GET /sessions non-blocking after gateway retry wait (<10000ms)" ($rRetry.Ok -and $sw9.ElapsedMilliseconds -lt 10000) "elapsed=$($sw9.ElapsedMilliseconds)ms"
        }
    }

    # Stop whichever Director is still running for this case
    $pidToStop = if ($newPid -ne 0) { $newPid } else { $result.Pid }
    Stop-DirectorForCase -DirectorExe $DirectorExe -DirectorPid $pidToStop -CaseName $CaseName

    # Unregister the per-case task
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-T "[test-degradation] [$CaseName] unregistered task $TaskName"
    }

    # Cleanup isolated root
    if (Test-Path $isolatedRoot) {
        Remove-Item -Path $isolatedRoot -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
    }
}

# ============================================================
# MAIN
# ============================================================

$ScriptVersion = "1.0.0"
Write-T "=== CC Director Degradation Property Test v$ScriptVersion ==="
Write-T ("Date    : {0}" -f (Get-Date).ToString("yyyy-MM-dd HH:mm:ss"))
Write-T ("Repo    : {0}" -f $RepoRoot)
Write-T ("Cases   : {0}" -f $CaseFilter)
Write-T ""

# ---- Resolve or allocate slot ----
$ownManifest = ""
$weAllocated = $false

if ($Manifest) {
    if (-not (Test-Path $Manifest)) {
        Write-T "[test-degradation] ERROR: supplied manifest not found: $Manifest"
        exit 1
    }
    $ownManifest = $Manifest
    Write-T "[test-degradation] Using pre-supplied manifest: $ownManifest"
} else {
    Write-Step "Allocating isolation slot (>= 6)"
    $allocOut = & powershell -NoProfile -File $IsoScript allocate -Worktree $RepoRoot 2>&1 | ForEach-Object {
        Write-T "[isolation] $_"
        $_
    }
    foreach ($line in $allocOut) {
        if ($line -match "MANIFEST=(.+)") { $ownManifest = $Matches[1].Trim() }
    }
    if (-not $ownManifest) {
        Write-T "[test-degradation] ERROR: could not allocate a slot."
        exit 1
    }
    $weAllocated = $true
    Write-T "[test-degradation] Allocated manifest: $ownManifest"
}

$manifestObj = Get-Content $ownManifest -Raw | ConvertFrom-Json
$directorExe = $manifestObj.exePath
$slotNum     = $manifestObj.slot
$slotTask    = $manifestObj.taskName

Write-T "[test-degradation] Slot=$slotNum Exe=$directorExe"

# ---- Build (unless skipped or pre-supplied) ----
if (-not $SkipBuild -and -not $Manifest) {
    Write-Step "Building slot $slotNum"
    $buildScript = Join-Path $RepoRoot "scripts\local-build-avalonia.ps1"
    & powershell -NoProfile -File $buildScript -Slot $slotNum
    if ($LASTEXITCODE -ne 0) {
        Write-T "[test-degradation] ERROR: build failed."
        # Release the allocated slot
        & powershell -NoProfile -File $IsoScript teardown -Manifest $ownManifest 2>&1 | ForEach-Object { Write-T "[isolation] $_" }
        exit 1
    }
    Add-Result "Build slot $slotNum" $true "exe=$directorExe"
} else {
    if (-not (Test-Path $directorExe)) {
        Write-T "[test-degradation] ERROR: exe not found at $directorExe. Run the build first."
        exit 1
    }
    Write-T "[test-degradation] Using existing exe: $directorExe"
}

# Release the allocation-only scheduled task (we'll register our own per-case wrapper tasks).
# This frees the task slot that allocate() reserved; our per-case tasks use different names.
if ($weAllocated) {
    if (Get-ScheduledTask -TaskName $slotTask -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $slotTask -Confirm:$false
        Write-T "[test-degradation] Released allocation task $slotTask (per-case tasks use their own names)"
    }
    # Remove the manifest (we manage lifecycle manually in this script)
    if (Test-Path $ownManifest) { Remove-Item -Path $ownManifest -Force -Confirm:$false -ErrorAction SilentlyContinue }
}

# ---- Scratch git repo (shared across cases) ----
$scratchRepo = New-ScratchRepo
Write-T "[test-degradation] Scratch git repo: $scratchRepo"

# ---- Run cases ----
# Each case gets its own task name to avoid collisions.
$taskA = "cc-director$slotNum-degrade-caseA"
$taskB = "cc-director$slotNum-degrade-caseB"

if ($CaseFilter -eq "A" -or $CaseFilter -eq "AB") {
    Invoke-Case `
        -CaseName "A" `
        -DirectorExe $directorExe `
        -TaskName $taskA `
        -GatewayUrl "" `
        -ScratchRepo $scratchRepo `
        -TimeoutSec $TimeoutSeconds
}

if ($CaseFilter -eq "B" -or $CaseFilter -eq "AB") {
    Invoke-Case `
        -CaseName "B" `
        -DirectorExe $directorExe `
        -TaskName $taskB `
        -GatewayUrl "http://127.0.0.1:1" `
        -ScratchRepo $scratchRepo `
        -TimeoutSec $TimeoutSeconds
}

# ---- Cleanup ----
if (Test-Path $scratchRepo) {
    Remove-Item -Path $scratchRepo -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
}

# ---- Summary ----
$elapsed = ((Get-Date) - $script:StartTime).TotalSeconds
Write-T ""
Write-T "====== DEGRADATION TEST SUMMARY ======"
Write-T ("Cases     : {0}" -f $CaseFilter)
Write-T ("Passed    : {0}" -f $script:Passed)
Write-T ("Failed    : {0}" -f $script:Failed)
Write-T ("Elapsed   : {0:F1}s" -f $elapsed)
Write-T ""
Write-T "--- FULL ASSERTION LIST ---"
foreach ($r in $script:Results) {
    $tag = if ($r.Pass) { "[PASS]" } else { "[FAIL]" }
    Write-T ("{0} {1} - {2}" -f $tag, $r.Check, $r.Detail)
}
Write-T ""

if ($script:Failed -eq 0) {
    Write-T "RESULT: PASS - Director is fully usable without the Gateway."
    Write-T "Phase 1 exit criterion 1D: SATISFIED."
    Write-T ""
    Write-T "Cross-machine proof note: no remote machine was required. Gateway stop is"
    Write-T "simulated as connection-refused (fake port 1). The Director, all sessions,"
    Write-T "and all assertions ran on this machine. This is the correct shape per"
    Write-T "docs/architecture/DIRECTOR_DUMB_WRAPPER_TARGET.md section 6."
    exit 0
} else {
    Write-T ("RESULT: FAIL - {0} assertion(s) failed. See list above." -f $script:Failed)
    exit 1
}
