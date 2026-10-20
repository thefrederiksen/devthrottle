#Requires -Version 7.4
<#
.SYNOPSIS
    API mode mission (#4070), phase 6: prove API mode end to end on Pi, through a Gateway, with no human.

.DESCRIPTION
    The end-to-end proof of the mission's section 7, item 2, plus the failure cases of item 3 that a real run
    can show. It runs on PowerShell 7 on Linux and on Windows.

    WHAT IT STANDS UP. Everything on one throwaway storage root, so nothing on the live fleet is read, written,
    reconfigured or stopped:
      - a Gateway built from THIS tree (src/CcDirector.Gateway, the self-host console Gateway), on its own port.
        It must be this tree's Gateway: the hosted Gateway only gets API mode at the mission's single deploy, after
        this proof, and the tree must have the switch on (ConnectionModes.ApiModeOffered) or the Gateway refuses
        every API-mode create. The script checks the switch before it builds anything.
      - a slot-5 test Director built from this tree, on the same root, connected to that Gateway with the Gateway's
        own machine token. On Windows it is launched by Task Scheduler (CLAUDE.md rule 0b); on Linux directly.

    WHAT IT DRIVES. The flow, through the Gateway:
      1. Spawn Pi with --connection api and send "Reply with exactly the word PONG": a Delivered receipt and PONG.
      2. A prompt that runs a bash tool: a tool card that finished.
      3. A long prompt, then Stop: the turn ended cancelled.
      4. Take the wheel, type "Remember the code word OTTER-58" into Pi's terminal, hand back, ask for the code word
         in API mode: OTTER-58.
      5. A prompt sent while holding the wheel: queued, then delivered on hand back.
    and these failure cases:
      - Take the wheel twice: the second is refused (step 4).
      - Hand back while the terminal is mid-turn: refused with a reason, never two holders (step 4).
      - Pi's executable missing: spawn fails with a stated reason (its own step, after the flow; no model call).
    The other two failure cases are proven by unit tests, named in $UnitTestProven below and in the summary:
    Pi exiting mid-turn, and Pi refusing a prompt. A real run cannot provoke either without killing Pi from the
    outside or racing Pi's own queue, and the recorded wire fixtures replay both exactly. Pi exiting mid-turn is held to
    the Delivery Lead's ruling of 16 October 2026 in event-model.md, not to section 7's original wording: the receipt
    stays Delivered (Pi acknowledged it) and shows the failed turn beside it.

    HOW IT TALKS TO THE GATEWAY. cc-devthrottle, run from THIS tree (tools/cc-devthrottle copied under its
    installed package name), spawns, prompts and stops sessions. cc-devthrottle has no command for receipts, the
    live items, the wheel or Stop, so those go to the Gateway's own routes with the same credential:
    GET /sessions/{id}/receipts, GET /sessions/{id}/live/items, POST /sessions/{id}/wheel/take,
    POST /sessions/{id}/wheel/hand-back, POST /sessions/{id}/turn/stop, and the terminal socket /sessions/{id}/stream
    (the same socket the Cockpit's Terminal tab types through).

    WHAT IT WRITES, into -OutDir:
      summary.txt      pass or fail per step, the unit tests that prove the rest, and the spend.
      wire.jsonl       every request to the Gateway and every cc-devthrottle call, with its answer, in order.
      director-pi.log  the Director's own lines about Pi's wire (its [PiDialect] and API-mode lines).
      spend.txt        Pi's token counts for the run, from Pi's own session file, priced as spend.md prices them.
    Paths, the user's name and the token are scrubbed from all four.

    HOW IT STOPS. The Pi sessions with cc-devthrottle session stop; the test Director with its NAMED SIGNAL, never
    by killing it (a request file on Linux, the named event on Windows; CLAUDE.md rule 0b). The Gateway, which has no
    shutdown request, with the ordinary terminate signal on Linux (the host shuts down gracefully), and on Windows by
    stopping the one process this script started. If the Director does not exit after its signal, the script says so
    and leaves it running: it never force-kills.

    SPEND. Pi's configured model with tiny prompts: about 0.15 dollars a run, most of it the first request's
    system prompt. Record every real run in docs/missions/api-mode-pi-2026-10-16/spend.md; spend.txt has the row.

.PARAMETER OutDir
    Where the evidence goes. Default: docs/missions/api-mode-pi-2026-10-16/proofs/phase-6/<os>-<time>.

.PARAMETER GatewayPort
    The test Gateway's port. Must be free; never the live Gateway's.

.PARAMETER SkipBuild
    Use the Gateway and slot-5 Director already built from this tree.

.PARAMETER Python
    The Python that runs cc-devthrottle. Default: the cc-* tools' own Python on this machine.

.PARAMETER NoScreens
    Skip the screens (section 7, item 4). By default the run photographs, and dumps the accessibility tree of, the
    Cockpit and the phone app (served by the test Gateway, so the Gateway is built with both web apps) at the moments
    each screen exists: New Session with API mode, the Chat tab with a tool card, the Chat tab mid-turn, and the
    Terminal tab before and after taking the wheel. On Windows it also dumps the desktop Director's New Session dialog
    and Terminal tab through UI Automation. The desktop has no accessibility reader on Linux, so there it is skipped
    and the summary says so.

.PARAMETER Chrome
    The Chrome the screens are taken with. Default: the usual install path for the platform.

.PARAMETER PlaywrightDir
    A folder whose node_modules holds playwright-core. Default: playwright-core 1.58.2 is installed into the rig
    (it downloads no browser; it drives -Chrome).

.PARAMETER RigRoot
    The folder the run's scratch folder is made in. Default: the system temporary folder. The screens show the rig's
    work folder, and pictures cannot be scrubbed, so on Windows, where the temporary folder holds the user's name,
    pass a short folder such as C:\apiproof.

.PARAMETER KeepRig
    Keep the scratch storage root, logs and work folder after the run. By default the script deletes the one
    folder it created for this run, once the test Director and Gateway have stopped.

.EXAMPLE
    pwsh -NoProfile -File scripts/prove-api-mode-pi.ps1
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [int]$GatewayPort = 7995,
    [switch]$SkipBuild,
    [string]$Python,
    [switch]$KeepRig,
    [switch]$NoScreens,
    [string]$Chrome,
    [string]$PlaywrightDir,
    [string]$RigRoot
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$Slot = 5
$started = Get-Date
$stamp = $started.ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$osWord = if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { 'other' }
if (-not $OutDir) { $OutDir = Join-Path $repo "docs/missions/api-mode-pi-2026-10-16/proofs/phase-6/$osWord-$stamp" }
$rig = Join-Path $(if ($RigRoot) { $RigRoot } else { [System.IO.Path]::GetTempPath() }) "api-mode-pi-proof-$stamp"
$root = Join-Path $rig 'root'
$work = Join-Path $rig 'work'
$logs = Join-Path $rig 'logs'
$importRoot = Join-Path $rig 'tool-import'
$directorHome = Join-Path $rig 'home'
$gatewayUrl = "http://127.0.0.1:$GatewayPort"

# The cases a real run does not provoke, and the unit tests that prove them (section 7, item 3).
#
# Pi exiting mid-turn is held to the Delivery Lead's ruling of 16 October 2026 in event-model.md, which replaced
# section 7's wording ("the prompt is not marked delivered"): Delivered means Pi acknowledged the prompt and stays
# true; the turn ends failed with the reason, and the receipt shows that failed turn beside Delivered. Only a prompt
# Pi never acknowledged is Refused, by the Director, "Pi exited before acknowledging".
$UnitTestProven = [ordered]@{
    'Pi exits mid-turn, after acknowledging (ruling of 16 October 2026, event-model.md): the session shows failed with the reason; the receipt stays Delivered with the failed turn beside it' = @(
        'CcDirector.Core.UnitTests ApiModeSessionCreationTests.CreateSession_ApiModePiEndsWithExitCodeZeroMidTurn_SessionFailsWithTheReason',
        'CcDirector.Core.UnitTests ApiModeSessionCreationTests.CreateSession_ApiModePiEndsMidTurn_ActivityGoesFromWorkingToExitedWithNoWaitBetween',
        'CcDirector.Core.UnitTests PiDialectReplayTests.Replay_Exit_TurnFailsWithTheExitCodeAndThePromptStaysDelivered',
        'CcDirector.Gateway.UnitTests PiFixtureIngestTests.Accept_TheExitMidTurnRecording_TheReceiptShowsFailedBesideDelivered')
    'Pi exits before acknowledging (the same ruling): the receipt is Refused by the Director, "Pi exited before acknowledging", with no turn' = @(
        'CcDirector.Core.UnitTests PiDialectReplayTests.Replay_ExitBeforeAcknowledgement_RefusesThePromptAsTheDirector',
        'CcDirector.Gateway.UnitTests PiFixtureIngestTests.Accept_TheExitBeforeAcknowledgementRecording_TheReceiptIsRefusedByTheDirector')
    'Pi refuses a prompt (success:false): the receipt shows the reason' = @(
        'CcDirector.Gateway.UnitTests PiFixtureIngestTests.Accept_TheRefusalRecording_TheSecondPromptIsRefusedByPiInItsOwnWords',
        'CcDirector.Gateway.UnitTests ApiModeEventIngestTests.Accept_RefusedByTheAgent_TheReceiptCarriesPisOwnWords')
    'Also proven by unit tests, beside this run: hand back mid-turn, take the wheel twice, a missing executable' = @(
        'CcDirector.Core.UnitTests ApiModeWheelTests.HandBackWheelAsync_WhileTheTerminalIsMidTurn_IsRefusedWithTheReason',
        'CcDirector.Core.UnitTests ApiModeWheelTests.HandBackWheelAsync_TheTerminalHasNotExitedAfterItsShutdown_IsRefusedAndNoPiStartsBesideIt',
        'CcDirector.Core.UnitTests ApiModeWheelTests.TakeWheelAsync_Twice_TheSecondIsRefusedAndNothingChanges',
        'CcDirector.Core.UnitTests ApiModeSessionTests.Start_ApiModeBackendWithAMissingExecutable_FailsNamingIt')
}

$results = [System.Collections.Generic.List[object]]::new()
$wire = [System.Collections.Generic.List[string]]::new()
$token = $null
$gatewayProcess = $null
$directorPid = 0
$directorId = $null
$sessionsToStop = [System.Collections.Generic.List[string]]::new()
$screens = [System.Collections.Generic.List[object]]::new()
$screensDir = Join-Path $OutDir 'screens'
$node = $null

function Say([string]$Message) { Write-Host "[prove-api-mode-pi] $Message" }

# Paths, the user's name and the credential never reach a committed file.
function Protect-Text([string]$Text) {
    if ($null -eq $Text) { return $null }
    $t = $Text
    if ($token) { $t = $t.Replace($token, '<token>') }
    $t = $t.Replace($rig, '<rig>').Replace($rig.Replace('\', '/'), '<rig>')
    $t = $t.Replace($repo, '<repo>')
    $userHome = [Environment]::GetFolderPath('UserProfile')
    if ($userHome) { $t = $t.Replace($userHome, '~') }
    $user = [Environment]::UserName
    if ($user -and $user.Length -ge 3) { $t = [regex]::Replace($t, [regex]::Escape($user), '<user>', 'IgnoreCase') }
    return $t
}

function Add-Wire([hashtable]$Entry) {
    $Entry['at'] = (Get-Date).ToUniversalTime().ToString('o')
    $wire.Add((Protect-Text ($Entry | ConvertTo-Json -Compress -Depth 20)))
}

function Add-Result([string]$Step, [bool]$Passed, [string]$Detail) {
    $word = if ($Passed) { 'PASS' } else { 'FAIL' }
    $results.Add([pscustomobject]@{ Step = $Step; Result = $word; Detail = (Protect-Text $Detail) })
    Say "$word  $Step - $(Protect-Text $Detail)"
}

function Stop-Setup([string]$Why) {
    Say "SETUP FAILED: $Why"
    throw "setup failed: $Why"
}

# ---------------------------------------------------------------------------------------------- the Gateway --

function Invoke-Gateway([string]$Method, [string]$Path, $Body = $null) {
    $params = @{
        Uri = "$gatewayUrl$Path"; Method = $Method; SkipHttpErrorCheck = $true
        Headers = @{ Authorization = "Bearer $token" }; TimeoutSec = 60
    }
    if ($null -ne $Body) { $params['Body'] = ($Body | ConvertTo-Json -Depth 10); $params['ContentType'] = 'application/json' }
    $r = Invoke-WebRequest @params
    $json = $null
    if ($r.Content) { try { $json = $r.Content | ConvertFrom-Json -Depth 50 } catch { $json = $null } }
    Add-Wire @{ kind = 'gateway'; method = $Method; path = $Path; body = $Body; status = [int]$r.StatusCode; answer = $(if ($json) { $json } else { [string]$r.Content }) }
    return [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $json; Text = [string]$r.Content }
}

function Get-Receipts([string]$Sid) { @((Invoke-Gateway GET "/sessions/$Sid/receipts").Json.receipts) }
function Get-Items([string]$Sid) { @((Invoke-Gateway GET "/sessions/$Sid/live/items").Json.items) }
function Get-Row([string]$Sid) { @((Invoke-Gateway GET '/sessions').Json) | Where-Object { $_.sessionId -eq $Sid } | Select-Object -First 1 }

function Wait-For([scriptblock]$Condition, [int]$Seconds, [int]$EveryMs = 1000) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds $EveryMs
    }
    return $null
}

# The receipt of the prompt with exactly this text, newest first.
function Get-ReceiptFor([string]$Sid, [string]$Text) {
    Get-Receipts $Sid | Where-Object { $_.promptText -eq $Text } | Select-Object -Last 1
}

# The reply of one turn as the Gateway finally ruled it: the last "replace" of each reply item of that turn.
function Get-ReplyText([string]$Sid, [string]$TurnId) {
    $byItem = [ordered]@{}
    foreach ($item in Get-Items $Sid) {
        if ($item.kind -ne 'replyText' -or -not $item.itemId.StartsWith("reply:$TurnId-")) { continue }
        if ($item.mode -eq 'replace') { $byItem[$item.itemId] = $item.text }
        elseif (-not $byItem.Contains($item.itemId)) { $byItem[$item.itemId] = $item.text }
        else { $byItem[$item.itemId] += $item.text }
    }
    return (@($byItem.Values) -join "`n").Trim()
}

# Type into the session's real terminal through the Gateway's terminal socket, then press Enter. When
# $HandBackAtOnce is set, ask for the hand back the moment Enter is sent, while Pi is still answering.
function Send-TerminalLine([string]$Sid, [string]$Text, [switch]$HandBackAtOnce) {
    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $ws.Options.SetRequestHeader('Authorization', "Bearer $token")
    $none = [Threading.CancellationToken]::None
    $wsUrl = "ws://127.0.0.1:$GatewayPort/sessions/$Sid/stream"
    $ws.ConnectAsync([Uri]$wsUrl, $none).GetAwaiter().GetResult()
    Start-Sleep -Milliseconds 1500
    $early = $null
    try {
        foreach ($chunk in @($Text, "`r")) {
            $bytes = [Text.Encoding]::UTF8.GetBytes($chunk)
            $ws.SendAsync([ArraySegment[byte]]::new($bytes), [System.Net.WebSockets.WebSocketMessageType]::Binary, $true, $none).GetAwaiter().GetResult()
            Add-Wire @{ kind = 'terminal'; path = "/sessions/$Sid/stream"; typed = $chunk }
            Start-Sleep -Milliseconds 700
        }
        if ($HandBackAtOnce) { $early = Invoke-Gateway POST "/sessions/$Sid/wheel/hand-back" }
        Start-Sleep -Seconds 1
    } finally {
        $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'done', $none).GetAwaiter().GetResult()
        $ws.Dispose()
    }
    return $early
}

# ------------------------------------------------------------------------------------------ cc-devthrottle --

function Invoke-Tool([string[]]$ToolArgs) {
    $saved = @{}
    foreach ($name in 'PYTHONPATH', 'CC_GATEWAY_URL', 'CC_GATEWAY_SESSION_KEY', 'CC_SESSION_ID', 'CC_DIRECTOR_ID') {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    try {
        # The tool as THIS tree has it, acting with the test Gateway's machine token and as no session.
        $env:PYTHONPATH = $importRoot
        $env:CC_GATEWAY_URL = $gatewayUrl
        $env:CC_GATEWAY_SESSION_KEY = $token
        Remove-Item Env:CC_SESSION_ID -ErrorAction SilentlyContinue
        Remove-Item Env:CC_DIRECTOR_ID -ErrorAction SilentlyContinue
        $out = & $Python -c 'from cc_devthrottle.cli import tool_main; tool_main()' @ToolArgs 2>&1 | Out-String
        $code = $LASTEXITCODE
    } finally {
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
    Add-Wire @{ kind = 'cc-devthrottle'; args = $ToolArgs; exit = $code; output = $out }
    return [pscustomobject]@{ Exit = $code; Output = $out }
}

function Start-PiSession([string]$Name) {
    $r = Invoke-Tool @('session', 'spawn', $work, '--agent', 'Pi', '--connection', 'api', '--director', $directorId, '--standalone', '--name', $Name)
    $m = [regex]::Match($r.Output, 'id:\s*([0-9a-fA-F-]{36})')
    if ($r.Exit -ne 0 -or -not $m.Success) { return [pscustomobject]@{ Sid = $null; Output = $r.Output; Exit = $r.Exit } }
    $sessionsToStop.Add($m.Groups[1].Value)
    return [pscustomobject]@{ Sid = $m.Groups[1].Value; Output = $r.Output; Exit = $r.Exit }
}

function Send-Prompt([string]$Sid, [string]$Text) { Invoke-Tool @('session', 'prompt', $Sid, $Text) }

# A prompt's turn has ended when its receipt says how.
function Wait-TurnEnded([string]$Sid, [string]$Text, [int]$Seconds = 120) {
    Wait-For { $r = Get-ReceiptFor $Sid $Text; if ($r -and $r.turnOutcome) { $r } } $Seconds 1500
}

# ------------------------------------------------------------------------------------- holders of the wheel --

# How many Pi processes the test Director has under it right now. Two would be two holders of one conversation.
function Get-PiProcessCount {
    if (-not $directorPid) { return -1 }
    $all = Get-Process
    $children = @{}
    foreach ($p in $all) {
        $parent = $null
        try { $parent = $p.Parent } catch { }
        if ($parent) {
            if (-not $children.ContainsKey($parent.Id)) { $children[$parent.Id] = [System.Collections.Generic.List[object]]::new() }
            $children[$parent.Id].Add($p)
        }
    }
    $count = 0
    $queue = [System.Collections.Generic.Queue[int]]::new()
    $queue.Enqueue($directorPid)
    while ($queue.Count -gt 0) {
        $id = $queue.Dequeue()
        if (-not $children.ContainsKey($id)) { continue }
        foreach ($c in $children[$id]) {
            $line = ''
            try { $line = [string]$c.CommandLine } catch { }
            if ($c.ProcessName -eq 'pi' -or $line -match 'pi-coding-agent') { $count++ }
            $queue.Enqueue($c.Id)
        }
    }
    return $count
}

# ------------------------------------------------------------------------------------------------- setup --

function Assert-SwitchOn {
    $dto = Get-Content (Join-Path $repo 'src/CcDirector.Gateway.Contracts/SessionDto.cs') -Raw
    if ($dto -notmatch 'public static bool ApiModeOffered \{ get; \} = true;') {
        Stop-Setup 'ConnectionModes.ApiModeOffered is not on in this tree, so its Gateway refuses every API-mode create. Run from a tree with the switch on.'
    }
}

function Resolve-Python {
    if ($Python) { return $Python }
    $candidate = if ($IsWindows) { Join-Path $env:LOCALAPPDATA 'cc-director\pyenv\Scripts\python.exe' }
                 else { Join-Path $HOME '.local/share/cc-director/pyenv/bin/python3' }
    if (-not (Test-Path $candidate)) { Stop-Setup "the cc-* tools' Python is not at $candidate; pass -Python" }
    return $candidate
}

function Resolve-Pi {
    $pi = Get-Command pi -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $pi) { Stop-Setup 'pi is not on this PATH, so there is no Pi to prove API mode on' }
    return $pi.Source
}

function Build-Rig {
    if ($SkipBuild) { Say 'skipping the build: using what this tree already built'; return }
    Say 'building the Gateway from this tree'
    # With both web apps when the screens are taken, so the test Gateway serves the real Cockpit and phone app.
    $webApps = if ($NoScreens) { @() } else { @('-p:BuildCockpit=true', '-p:BuildMobile=true') }
    & dotnet build (Join-Path $repo 'src/CcDirector.Gateway') -c Debug --nologo -v q @webApps
    if ($LASTEXITCODE -ne 0) { Stop-Setup 'the Gateway did not build' }
    Say "building the slot-$Slot Director from this tree"
    if ($IsWindows) {
        & (Join-Path $repo 'scripts\local-build-avalonia.ps1') -Slot $Slot -OutputDir (Join-Path $repo 'scripts\local-build')
    } else {
        & bash (Join-Path $repo 'scripts/local-build-linux.sh') --slot $Slot --framework-dependent
    }
    if ($LASTEXITCODE -ne 0) { Stop-Setup "the slot-$Slot Director did not build" }
}

function Get-GatewayExe {
    $dir = Join-Path $repo 'src/CcDirector.Gateway/bin/Debug/net10.0'
    $exe = Join-Path $dir $(if ($IsWindows) { 'CcDirector.Gateway.exe' } else { 'CcDirector.Gateway' })
    if (-not (Test-Path $exe)) { Stop-Setup "no Gateway at $exe; run without -SkipBuild" }
    return $exe
}

function Get-DirectorExe {
    $exe = if ($IsWindows) { Join-Path $repo "scripts\local-build\cc-director$Slot.exe" }
           else { Join-Path $repo "local_builds/linux/cc-director-linux$Slot" }
    if (-not (Test-Path $exe)) { Stop-Setup "no slot-$Slot Director at $exe; run without -SkipBuild" }
    return $exe
}

function Start-TestGateway {
    # Windows retries a refused connection to localhost for about two seconds before it reports it, so a two-second
    # timeout turned a free port into a timeout; ten seconds lets the refusal arrive on every platform.
    try { $null = Invoke-WebRequest "$gatewayUrl/healthz" -TimeoutSec 10; Stop-Setup "something already answers on port $GatewayPort" } catch [System.Net.Http.HttpRequestException] { }
    $exe = Get-GatewayExe
    Say "starting the test Gateway on port $GatewayPort"
    $script:gatewayProcess = Start-Process -FilePath $exe -ArgumentList @('--port', "$GatewayPort") -WorkingDirectory (Split-Path $exe) `
        -PassThru -RedirectStandardOutput (Join-Path $logs 'gateway.out') -RedirectStandardError (Join-Path $logs 'gateway.err') `
        -Environment @{ CC_DIRECTOR_ROOT = $root; DOTNET_USE_POLLING_FILE_WATCHER = '1' }
    $tokenFile = Join-Path $root 'config/director/gateway-token.txt'
    if (-not (Wait-For { Test-Path $tokenFile } 60)) { Stop-Setup 'the test Gateway never wrote its token' }
    $script:token = (Get-Content $tokenFile -Raw).Trim()
    $ready = Wait-For {
        try {
            $r = Invoke-WebRequest "$gatewayUrl/sessions" -Headers @{ Authorization = "Bearer $token" } -SkipHttpErrorCheck -TimeoutSec 5
            $r.StatusCode -eq 200
        } catch { $false }
    } 120 2000
    if (-not $ready) { Stop-Setup 'the test Gateway never became ready to serve' }
    Say "test Gateway ready (process $($gatewayProcess.Id))"
}

function Write-DirectorConfig([string]$PiPath) {
    $configDir = Join-Path $root 'instances/default/config'
    New-Item -ItemType Directory -Force -Path $configDir | Out-Null
    $config = [ordered]@{
        gateway = [ordered]@{ url = $gatewayUrl; token = $token; streamMode = $true }
        agent = [ordered]@{ entries = @([ordered]@{
            id = '00000000-0000-0000-0000-000000000005'; display_name = 'Pi'; type = 'Pi'; enabled = $true; executable_path = $PiPath
            preset_id = 'Standard'; default_model = ''; args_override = ''; launch_mode = 'Guided'
        }) }
    }
    $config | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path $configDir 'config.json') -Encoding utf8
}

function Start-TestDirector([string]$PiPath) {
    $exe = Get-DirectorExe
    Write-DirectorConfig $PiPath
    Say "starting the slot-$Slot test Director"
    if ($IsWindows) {
        # Task Scheduler, never this process tree (CLAUDE.md rule 0b). The task sets the root and starts the Director.
        $taskName = "cc-director$Slot-launch"
        # Never replace a task that is not this script's: the user may have registered one by this name.
        $existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($existing -and $existing.Description -ne 'API mode proof test Director') {
            Stop-Setup "a scheduled task named $taskName already exists and was not made by this script; it is left alone - remove or rename it, then run again"
        }
        $command = "`$env:CC_DIRECTOR_ROOT = '$root'; & '$exe'"
        # The PowerShell running this script, by its full path: the task does not see this session's PATH, and a
        # machine whose PowerShell 7 is not installed system-wide failed the launch with "file not found".
        $action = New-ScheduledTaskAction -Execute ([Environment]::ProcessPath) -Argument "-NoProfile -WindowStyle Hidden -Command `"$command`"" -WorkingDirectory (Split-Path $exe)
        $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5)
        # Normal priority: a task runs at Below Normal by default, and on a busy machine that took the first Windows
        # run 110 seconds to start the Director and another three and a half minutes before it registered.
        $settings = New-ScheduledTaskSettingsSet -Priority 4
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Description 'API mode proof test Director' -Force | Out-Null
        Start-ScheduledTask -TaskName $taskName
    } else {
        # A HOME of its own. A Director on a fresh root repairs its cc-* tools at start-up, and on Linux and macOS
        # the repair links them into ~/.local/bin - the user's real tools folder, whatever CC_DIRECTOR_ROOT says.
        # The first dry run did exactly that and pointed the machine's cc-devthrottle at a folder this script then
        # deleted. Pi is pointed back at the user's own agent folder, where its model and sign-in live.
        $piDir = Split-Path $PiPath
        $null = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru `
            -RedirectStandardOutput (Join-Path $logs 'director.out') -RedirectStandardError (Join-Path $logs 'director.err') `
            -Environment @{
                CC_DIRECTOR_ROOT = $root; HOME = $directorHome
                PI_CODING_AGENT_DIR = (Join-Path $HOME '.pi/agent')
                PATH = "$piDir$([IO.Path]::PathSeparator)$env:PATH"
            }
    }
    # Readiness is the instance registration the running Director writes, whose Pid is the process.
    $regDir = Join-Path $root 'instances/default/config/director/instances'
    $reg = Wait-For {
        Get-ChildItem $regDir -Filter *.json -ErrorAction SilentlyContinue |
            ForEach-Object { try { Get-Content $_.FullName -Raw | ConvertFrom-Json } catch { $null } } |
            Where-Object { $_.DirectorId -and $_.Pid } | Select-Object -First 1
    } 300 1000
    if (-not $reg) { Stop-Setup 'the test Director never wrote its instance registration' }
    $script:directorPid = [int]$reg.Pid
    $script:directorId = ([string]$reg.DirectorId).ToLowerInvariant()
    $joined = Wait-For { @((Invoke-Gateway GET '/directors').Json) | Where-Object { $_.directorId -eq $directorId } } 90 2000
    if (-not $joined) { Stop-Setup "the test Director $directorId never joined the test Gateway" }
    Say "test Director $directorId (process $directorPid) joined the test Gateway"
}

# ---------------------------------------------------------------------------------------------- teardown --

function Stop-TestDirector {
    if (-not $directorId) { return }
    Say "stopping the test Director with its named signal"
    $name = "cc-director-shutdown-$directorId"
    $sent = $false
    if ($IsWindows) {
        try {
            $evt = [System.Threading.EventWaitHandle]::OpenExisting("Local\$name")
            $evt.Set() | Out-Null; $evt.Dispose(); $sent = $true
        } catch { Say "nothing is listening on Local\$name" }
    } else {
        # The Unix arm of the named signal: a request file under the shared root, which the Director polls.
        $dir = Join-Path $root 'config/lifecycle-signals'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Set-Content -Path (Join-Path $dir "$name.request") -Value (Get-Date).ToUniversalTime().ToString('o') -NoNewline
        $sent = $true
    }
    $gone = Wait-For { -not (Get-Process -Id $directorPid -ErrorAction SilentlyContinue) } 60
    if ($sent -and $gone) { Add-Result 'Teardown: the test Director stopped on its named signal' $true "process $directorPid exited" }
    else { Add-Result 'Teardown: the test Director stopped on its named signal' $false "process $directorPid is still running; it was NOT killed - stop it by hand" }
    if ($IsWindows) {
        $mine = Get-ScheduledTask -TaskName "cc-director$Slot-launch" -ErrorAction SilentlyContinue
        if ($mine -and $mine.Description -eq 'API mode proof test Director') { Unregister-ScheduledTask -TaskName "cc-director$Slot-launch" -Confirm:$false }
    }
}

function Stop-TestGateway {
    if (-not $gatewayProcess -or $gatewayProcess.HasExited) { return }
    Say 'stopping the test Gateway'
    if ($IsWindows) {
        # A console Gateway has no shutdown request; this is the one process this script started, on its own root.
        Stop-Process -Id $gatewayProcess.Id
    } else {
        # /bin/kill by path: in PowerShell 'kill' is Stop-Process.
        & /bin/kill -TERM $gatewayProcess.Id
    }
    $null = Wait-For { $gatewayProcess.HasExited } 30
}

# Where every link in the user's ~/.local/bin points (Linux and macOS), so the run can prove it changed none.
function Get-UserBinLinks {
    if ($IsWindows) { return '' }
    $dir = Join-Path $HOME '.local/bin'
    if (-not (Test-Path $dir)) { return '' }
    (Get-ChildItem $dir | Where-Object { $_.LinkTarget } | Sort-Object Name | ForEach-Object { "$($_.Name) -> $($_.LinkTarget)" }) -join "`n"
}

# ----------------------------------------------------------------------------------------------- screens --

function Initialize-Screens {
    if ($NoScreens) { return }
    $gatewayDir = Split-Path (Get-GatewayExe)
    if (-not (Test-Path (Join-Path $gatewayDir 'wwwroot/c/index.html')) -or -not (Test-Path (Join-Path $gatewayDir 'wwwroot/mobile/index.html'))) {
        Stop-Setup 'the test Gateway was built without the Cockpit and phone app; run without -SkipBuild, or pass -NoScreens'
    }
    $script:node = (Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    if (-not $node) { Stop-Setup 'node is not on this PATH, so the screens cannot be taken; pass -NoScreens to skip them' }
    if (-not $Chrome) {
        $script:Chrome = if ($IsWindows) { Join-Path ${env:ProgramFiles} 'Google\Chrome\Application\chrome.exe' } else { '/opt/google/chrome/chrome' }
    }
    if (-not (Test-Path $Chrome)) { Stop-Setup "no Chrome at $Chrome; pass -Chrome or -NoScreens" }
    if (-not $PlaywrightDir) {
        $script:PlaywrightDir = Join-Path $rig 'playwright'
        New-Item -ItemType Directory -Force -Path $PlaywrightDir | Out-Null
        $npm = (Get-Command npm -ErrorAction SilentlyContinue | Select-Object -First 1).Source
        if (-not $npm) { Stop-Setup 'npm is not on this PATH, so playwright-core cannot be installed; pass -PlaywrightDir' }
        Say 'installing playwright-core 1.58.2 into the rig (no browser download)'
        & $npm install --prefix $PlaywrightDir --silent --no-audit --no-fund playwright-core@1.58.2 | Out-Null
        if ($LASTEXITCODE -ne 0) { Stop-Setup 'playwright-core did not install; pass -PlaywrightDir' }
    }
    New-Item -ItemType Directory -Force -Path $screensDir | Out-Null
}

# Start the screens script; with -Ready it runs in the background and the caller waits with Complete-WebScreens.
function Start-WebScreens([string[]]$ScreenArgs, [switch]$Background) {
    $env:GATEWAY_URL = $gatewayUrl; $env:GATEWAY_TOKEN = $token
    $env:PLAYWRIGHT_DIR = $PlaywrightDir; $env:CHROME = $Chrome
    $script = Join-Path $PSScriptRoot 'prove-api-mode-pi-screens.mjs'
    $outFile = Join-Path $logs "screens-$([guid]::NewGuid().ToString('N')).out"
    # Start-Process joins its arguments with spaces and quotes none of them, so "Working - turn" would arrive as three
    # arguments and the screen would be checked for "Working" alone. Each one is quoted here.
    $all = (@($script) + $ScreenArgs + @('--out', $screensDir) | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
    $proc = Start-Process -FilePath $node -ArgumentList $all -PassThru -NoNewWindow `
        -RedirectStandardOutput $outFile -RedirectStandardError "$outFile.err"
    $job = [pscustomobject]@{ Process = $proc; OutFile = $outFile; Args = $ScreenArgs }
    if ($Background) { return $job }
    return Complete-WebScreens $job
}

function Complete-WebScreens($Job, [int]$Seconds = 180) {
    if (-not $Job.Process.WaitForExit($Seconds * 1000)) { $Job.Process.Kill(); }
    $lines = @(Get-Content $Job.OutFile -ErrorAction SilentlyContinue)
    $parsed = $null
    if ($lines.Count -gt 0) { try { $parsed = $lines[-1] | ConvertFrom-Json } catch { } }
    if (-not $parsed) {
        $err = (Get-Content "$($Job.OutFile).err" -Raw -ErrorAction SilentlyContinue)
        Add-Result "Screen: $($Job.Args -join ' ')" $false "the screens script gave no result: $err"
        return @()
    }
    foreach ($r in @($parsed)) {
        $screens.Add([pscustomobject]@{ Name = $r.name; Surface = $r.surface; Ok = [bool]$r.ok; Detail = $r.detail
            Png = "screens/$($r.name)-$($r.surface).png"; Aria = "screens/$($r.name)-$($r.surface).aria.txt" })
        Add-Result "Screen: $($r.name) ($($r.surface))" ([bool]$r.ok) $r.detail
    }
    return @($parsed)
}

function Invoke-WebShot([string]$Name, [string]$Sid, [string]$Tab, [string]$Expect) {
    if ($NoScreens) { return }
    $null = Start-WebScreens @('shot', '--name', $Name, '--session', $Sid, '--tab', $Tab, '--expect', $Expect)
}

# ---- the desktop Director, on Windows only, through UI Automation (the same API scripts/ui-drive.ps1 drives) ----

function Initialize-DesktopAutomation {
    if ($script:uiaReady) { return }
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
    $script:uiaReady = $true
}

function Get-DesktopWindows {
    $cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $directorPid)
    @([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
}

function Find-DesktopElement($Root, [string]$AutomationId) {
    $cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

# Press an element whichever way it accepts: invoke, select, or toggle.
function Invoke-DesktopElement($Element) {
    foreach ($pattern in @([System.Windows.Automation.InvokePattern]::Pattern, [System.Windows.Automation.SelectionItemPattern]::Pattern, [System.Windows.Automation.TogglePattern]::Pattern)) {
        $p = $null
        if ($Element.TryGetCurrentPattern($pattern, [ref]$p)) {
            if ($p -is [System.Windows.Automation.InvokePattern]) { $p.Invoke() }
            elseif ($p -is [System.Windows.Automation.SelectionItemPattern]) { $p.Select() }
            else { $p.Toggle() }
            return
        }
    }
    throw "element '$($Element.Current.Name)' [$($Element.Current.AutomationId)] cannot be pressed"
}

# The window's accessibility tree, one element per line: control type, name, automation id.
function Write-DesktopTree($Element, [string]$Path) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $lines = [System.Collections.Generic.List[string]]::new()
    $visit = $null
    $visit = {
        param($e, $depth)
        if ($depth -gt 40) { return }
        $c = $e.Current
        $lines.Add(('  ' * $depth) + "- $($c.ControlType.ProgrammaticName -replace '^ControlType\.', '') `"$($c.Name)`"" + $(if ($c.AutomationId) { " [$($c.AutomationId)]" } else { '' }))
        $child = $walker.GetFirstChild($e)
        while ($child) { & $visit $child ($depth + 1); $child = $walker.GetNextSibling($child) }
    }
    & $visit $Element 0
    $lines | Set-Content -Path $Path -Encoding utf8
    return ($lines -join "`n")
}

function Save-DesktopPicture($Element, [string]$Path) {
    $r = $Element.Current.BoundingRectangle
    $bitmap = [System.Drawing.Bitmap]::new([int]$r.Width, [int]$r.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try { $graphics.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bitmap.Size); $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}

# One desktop screen: $Prepare gets the main window and returns the element to dump (a window).
function Invoke-DesktopShot([string]$Name, [string]$Expect, [scriptblock]$Prepare) {
    if ($NoScreens -or -not $IsWindows) { return }
    try {
        Initialize-DesktopAutomation
        $main = Wait-For { Get-DesktopWindows | Select-Object -First 1 } 30
        if (-not $main) { throw 'the test Director has no window' }
        $target = & $Prepare $main
        Start-Sleep -Milliseconds 1500
        $tree = Write-DesktopTree $target (Join-Path $screensDir "$Name-desktop.uia.txt")
        Save-DesktopPicture $target (Join-Path $screensDir "$Name-desktop.png")
        $ok = $tree -match [regex]::Escape($Expect)
        $detail = if ($ok) { "showed `"$Expect`"" } else { "the window's tree has no `"$Expect`"" }
    } catch {
        $ok = $false; $detail = "could not be taken: $($_.Exception.Message)"
    }
    $screens.Add([pscustomobject]@{ Name = $Name; Surface = 'desktop'; Ok = $ok; Detail = $detail
        Png = "screens/$Name-desktop.png"; Aria = "screens/$Name-desktop.uia.txt" })
    Add-Result "Screen: $Name (desktop)" $ok $detail
}

# The desktop's session list and Terminal tab, for the one session the test Director holds.
$DesktopTerminalTab = {
    param($main)
    $list = Find-DesktopElement $main 'SessionList'
    if (-not $list) { throw 'no SessionList' }
    $itemCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $item = $list.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $itemCond)
    if (-not $item) { throw 'no session in SessionList' }
    Invoke-DesktopElement $item
    Start-Sleep -Milliseconds 800
    $tab = Find-DesktopElement $main 'TerminalTabButton'
    if (-not $tab) { throw 'no TerminalTabButton' }
    Invoke-DesktopElement $tab
    return $main
}

# The desktop New Session dialog with Pi and API mode chosen; closed with Cancel, so nothing starts.
$DesktopNewSession = {
    param($main)
    $button = Find-DesktopElement $main 'BtnNewSession'
    if (-not $button) { throw 'no BtnNewSession' }
    Invoke-DesktopElement $button
    $dialog = Wait-For { Get-DesktopWindows | Where-Object { $_.Current.NativeWindowHandle -ne $main.Current.NativeWindowHandle } | Select-Object -First 1 } 20
    if (-not $dialog) { throw 'the New Session dialog did not open' }
    $agents = Find-DesktopElement $dialog 'AgentEntryList'
    $nameCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $pi = @($agents.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nameCond)) | Where-Object { $_.Current.Name -match '\bPi\b' } | Select-Object -First 1
    if ($pi) { Invoke-DesktopElement $pi; Start-Sleep -Milliseconds 500 }
    $radio = Find-DesktopElement $dialog 'ConnectionApiRadio'
    if (-not $radio) { throw 'no ConnectionApiRadio: the Connection row is not shown' }
    Invoke-DesktopElement $radio
    $script:desktopDialog = $dialog
    return $dialog
}

function Close-DesktopDialog {
    if (-not $script:desktopDialog) { return }
    try {
        $cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Cancel')
        $cancel = $script:desktopDialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($cancel) { Invoke-DesktopElement $cancel }
    } catch { Say "the desktop New Session dialog could not be closed: $($_.Exception.Message)" }
    $script:desktopDialog = $null
}

# ------------------------------------------------------------------------------------------------- spend --

# Pi's token counts for every Pi session this run opened, from Pi's own session files, priced as spend.md does:
# 5 dollars per million input and cache-write tokens, 0.50 per million cache reads, 40 per million output. A reply
# Pi cut off (Stop) records zero; it is estimated as spend.md says, as the first request's size plus 500 output.
function Measure-Spend {
    $sessionsDir = Join-Path $HOME '.pi/agent/sessions'
    $totals = [ordered]@{ input = 0; output = 0; cacheRead = 0; cacheWrite = 0; estimatedCutOff = 0 }
    $files = @(Get-ChildItem $sessionsDir -Recurse -Filter *.jsonl -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $started })
    $firstRequest = 0
    foreach ($f in $files) {
        $lines = Get-Content $f.FullName
        if (-not $lines -or $lines[0] -notmatch [regex]::Escape((Split-Path $rig -Leaf))) { continue }
        foreach ($line in $lines) {
            $rec = $null; try { $rec = $line | ConvertFrom-Json -Depth 50 } catch { continue }
            if ($rec.type -ne 'message' -or $rec.message.role -ne 'assistant' -or -not $rec.message.usage) { continue }
            $u = $rec.message.usage
            if (($u.input + $u.output + $u.cacheRead + $u.cacheWrite) -eq 0) { $totals.estimatedCutOff++; continue }
            $totals.input += $u.input; $totals.output += $u.output; $totals.cacheRead += $u.cacheRead; $totals.cacheWrite += $u.cacheWrite
            if ($firstRequest -eq 0) { $firstRequest = $u.input + $u.cacheRead + $u.cacheWrite }
        }
    }
    $cutIn = $totals.estimatedCutOff * $firstRequest
    $cutOut = $totals.estimatedCutOff * 500
    $dollars = (($totals.input + $totals.cacheWrite + $cutIn) * 5 + $totals.cacheRead * 0.5 + ($totals.output + $cutOut) * 40) / 1e6
    return [pscustomobject]@{ Totals = $totals; CutOffInput = $cutIn; CutOffOutput = $cutOut; Dollars = [math]::Round($dollars, 4) }
}

# --------------------------------------------------------------------------------------------- the flow --

function Invoke-Flow {
    # Step 1: spawn with --connection api; PONG with a Delivered receipt.
    $spawn = Start-PiSession 'API mode proof - flow'
    if (-not $spawn.Sid) { Add-Result '1. Spawn Pi with --connection api' $false $spawn.Output; return }
    $sid = $spawn.Sid
    $connected = Wait-For { $row = Get-Row $sid; if ($row -and $row.connectionMode -eq 'api' -and $row.apiMode.agentConnected) { $row } } 90 2000
    Add-Result '1a. Spawn Pi with --connection api' ([bool]$connected) $(if ($connected) { "session $sid, connection api, $($connected.apiMode.agentLabel)" } else { 'the session never showed API mode with the agent connected' })
    if (-not $connected) { return }

    # Screen: New Session with API mode chosen (nothing is submitted).
    if (-not $NoScreens) { $null = Start-WebScreens @('newsession') }
    Invoke-DesktopShot 'new-session-api-mode' 'API mode' $DesktopNewSession
    Close-DesktopDialog

    $text = 'Reply with exactly the word PONG'
    $null = Send-Prompt $sid $text
    $receipt = Wait-TurnEnded $sid $text
    $reply = if ($receipt) { Get-ReplyText $sid $receipt.turnId } else { '' }
    Add-Result '1b. PONG: a Delivered receipt and the reply PONG' ($receipt -and $receipt.state -eq 'Delivered' -and $reply -eq 'PONG') `
        "receipt $($receipt.state) ($($receipt.words)), acknowledgement $($receipt.acknowledgement), reply '$reply'"

    # Step 2: a bash tool; its card finished.
    $text = 'Use your bash tool to run: echo TOOL-OK. Then reply with exactly DONE.'
    $null = Send-Prompt $sid $text
    $receipt = Wait-TurnEnded $sid $text
    $cards = @(Get-Items $sid | Where-Object { $_.kind -eq 'toolCard' -and $receipt -and $_.turnId -eq $receipt.turnId })
    $last = $cards | Select-Object -Last 1
    Add-Result '2. A bash tool: a tool card that finished' ($last -and $last.status -eq 'done') `
        "tool card '$($last.text)' statuses: $(($cards | ForEach-Object { $_.status }) -join ' -> '), input $($last.detail)"
    Invoke-WebShot 'chat-tool-card' $sid 'chat' 'TOOL-OK'

    # Step 3: a long prompt, then Stop; the turn ended cancelled. The mid-turn screen is opened first, the prompt is
    # sent once both pages are ready, and Stop waits until the screen has been taken while the turn runs.
    $text = 'Write the numbers from 1 to 600, one per line, with no other text.'
    $midTurn = $null
    if (-not $NoScreens) {
        $ready = Join-Path $logs 'midturn.ready'
        $midTurn = Start-WebScreens @('shot', '--name', 'chat-midturn', '--session', $sid, '--tab', 'chat', '--expect', 'Working - turn', '--ready', $ready, '--timeout', '60') -Background
        $null = Wait-For { Test-Path $ready } 90 500
    }
    $null = Send-Prompt $sid $text
    if ($midTurn) { $null = Complete-WebScreens $midTurn 120 }
    $streaming = Wait-For {
        $r = Get-ReceiptFor $sid $text
        if ($r -and $r.turnId -and @(Get-Items $sid | Where-Object { $_.kind -eq 'replyText' -and $_.itemId.StartsWith("reply:$($r.turnId)-") }).Count -gt 0) { $r }
    } 60 500
    $stop = Invoke-Gateway POST "/sessions/$sid/turn/stop"
    $receipt = Wait-TurnEnded $sid $text 60
    Add-Result '3. Stop: the turn ended cancelled' ($streaming -and $stop.Status -eq 200 -and $receipt.turnOutcome -eq 'cancelled') `
        "stop answered $($stop.Status); turn outcome $($receipt.turnOutcome) ($($receipt.turnOutcomeReason)), receipt '$($receipt.words)'"

    Invoke-WebShot 'chat-after-stop' $sid 'chat' 'stopped'

    # Step 4: take the wheel, type the code word into the terminal, hand back, ask for it in API mode.
    Invoke-WebShot 'terminal-before-take' $sid 'terminal' 'Take the wheel'
    Invoke-DesktopShot 'terminal-before-take' 'Take the wheel' $DesktopTerminalTab
    $take = Invoke-Gateway POST "/sessions/$sid/wheel/take"
    $held = Wait-For { $row = Get-Row $sid; if ($row.wheelHolder -eq 'user') { $row } } 30
    # The terminal's Pi starts a moment after the holder changes; wait for it before counting.
    $piCount = Wait-For { $n = Get-PiProcessCount; if ($n -ge 1) { $n } } 20
    Add-Result '4a. Take the wheel' ($take.Status -eq 200 -and $held -and $piCount -eq 1) "answered $($take.Status); holder $($held.wheelHolder), button '$($held.apiMode.wheelAction.label)'; Pi processes $piCount"
    Invoke-WebShot 'terminal-after-take' $sid 'terminal' 'Hand back'
    Invoke-DesktopShot 'terminal-after-take' 'Hand back' $DesktopTerminalTab

    $again = Invoke-Gateway POST "/sessions/$sid/wheel/take"
    Add-Result 'Failure case: take the wheel twice - the second is refused' ($again.Status -ge 400 -and $again.Status -lt 500) `
        "answered $($again.Status) $($again.Json.code): $($again.Json.error)"

    $early = Send-TerminalLine $sid 'Remember the code word OTTER-58' -HandBackAtOnce
    $piCount = Get-PiProcessCount
    $rowAfter = Get-Row $sid
    if ($early.Status -ge 400) {
        Add-Result 'Failure case: hand back while the terminal is mid-turn - refused with a reason, never two holders' ($rowAfter.wheelHolder -eq 'user' -and $piCount -le 1) `
            "answered $($early.Status): $($early.Json.error) Holder after: $($rowAfter.wheelHolder); Pi processes $piCount"
    } else {
        # Pi finished before the hand back reached the Director: the case did not arise in this run. Not a pass.
        Add-Result 'Failure case: hand back while the terminal is mid-turn - refused with a reason, never two holders' $false `
            "not provoked: the hand back answered $($early.Status) because Pi had already finished; see the unit tests"
    }
    $handBack = Wait-For { $r = Invoke-Gateway POST "/sessions/$sid/wheel/hand-back"; if ($r.Status -eq 200) { $r } } 120 3000
    if (-not $handBack -and $early.Status -eq 200) { $handBack = $early }
    $back = Wait-For { $row = Get-Row $sid; if ($row.wheelHolder -eq 'protocol' -and $row.apiMode.agentConnected) { $row } } 60 2000
    Add-Result '4b. Hand back' ([bool]$back) "holder $($back.wheelHolder), $($back.apiMode.agentLabel); Pi processes $(Get-PiProcessCount)"

    $text = 'What is the code word I asked you to remember? Reply with only the code word.'
    $null = Send-Prompt $sid $text
    $receipt = Wait-TurnEnded $sid $text
    $reply = if ($receipt) { Get-ReplyText $sid $receipt.turnId } else { '' }
    Add-Result '4c. The code word typed in the terminal survives the hand back: OTTER-58' ($reply -match 'OTTER-58') "receipt $($receipt.state), reply '$reply'"

    # Step 5: a prompt sent while holding the wheel is queued, then delivered on hand back.
    $take = Invoke-Gateway POST "/sessions/$sid/wheel/take"
    $null = Wait-For { $row = Get-Row $sid; if ($row.wheelHolder -eq 'user') { $row } } 30
    $text = 'Reply with exactly the word QUEUED-OK'
    $sent = Send-Prompt $sid $text
    $queued = Wait-For { $r = Get-ReceiptFor $sid $text; if ($r -and $r.state -eq 'Queued') { $r } } 30
    $row = Get-Row $sid
    Add-Result '5a. A prompt sent while holding the wheel is queued' ([bool]$queued) `
        "receipt '$($queued.words)' (reason $($queued.queueReason)); session says '$($row.apiMode.queueLabel)'; the tool said: $($sent.Output.Trim())"
    $handBack = Wait-For { $r = Invoke-Gateway POST "/sessions/$sid/wheel/hand-back"; if ($r.Status -eq 200) { $r } } 60 3000
    $receipt = Wait-TurnEnded $sid $text
    $reply = if ($receipt) { Get-ReplyText $sid $receipt.turnId } else { '' }
    Add-Result '5b. ...then delivered on hand back' ($handBack -and $receipt.state -eq 'Delivered' -and $reply -eq 'QUEUED-OK') `
        "receipt $($receipt.state) ($($receipt.words)), turn $($receipt.turnOutcome), reply '$reply'; Pi processes $(Get-PiProcessCount)"
}

# Pi's executable missing: spawn fails with a stated reason. The Director reads the agent entry on every spawn, so
# pointing it at a path that does not exist needs no restart, and no model call is made.
function Invoke-MissingExecutable([string]$PiPath) {
    $missing = Join-Path $rig 'no-such-pi/pi'
    Write-DirectorConfig $missing
    try {
        $spawn = Start-PiSession 'API mode proof - missing Pi'
        $named = $spawn.Output -match [regex]::Escape($missing)
        Add-Result "Failure case: Pi's executable missing - spawn fails with a stated reason" (-not $spawn.Sid -and $spawn.Exit -ne 0 -and $named) `
            "exit $($spawn.Exit): $($spawn.Output.Trim())"
    } finally {
        Write-DirectorConfig $PiPath
    }
}

# ------------------------------------------------------------------------------------------------- main --

$exitCode = 0
try {
    New-Item -ItemType Directory -Force -Path $rig, $root, $work, $logs, $importRoot, $directorHome, $OutDir | Out-Null
    $userBinBefore = Get-UserBinLinks
    Say "rig: $rig"
    Say "evidence: $OutDir"
    Assert-SwitchOn
    $Python = Resolve-Python
    $piPath = Resolve-Pi
    $piVersion = (& $piPath --version 2>&1 | Out-String).Trim()
    Say "pi $piVersion at $piPath"
    Copy-Item -Recurse -Force (Join-Path $repo 'tools/cc-devthrottle/src') (Join-Path $importRoot 'cc_devthrottle')
    Copy-Item -Recurse -Force (Join-Path $repo 'tools/cc_shared') (Join-Path $importRoot 'cc_shared')
    Build-Rig
    Initialize-Screens
    Start-TestGateway
    Start-TestDirector $piPath
    Invoke-Flow
    Invoke-MissingExecutable $piPath
} catch {
    Add-Result 'The run' $false "stopped early: $($_.Exception.Message)"
    $exitCode = 2
} finally {
    foreach ($sid in $sessionsToStop) { $null = Invoke-Tool @('session', 'stop', $sid, '--reason', 'API mode proof finished') }
    Stop-TestDirector
    Stop-TestGateway
    if (-not $IsWindows -and $null -ne $userBinBefore) {
        $userBinAfter = Get-UserBinLinks
        $changed = @(Compare-Object @($userBinBefore -split "`n") @($userBinAfter -split "`n") | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" })
        Add-Result "Teardown: the user's ~/.local/bin links are unchanged" ($changed.Count -eq 0 -and $userBinBefore.Length -gt 0) `
            $(if ($changed.Count -eq 0) { "$(@($userBinBefore -split "`n").Count) links, all as before" } else { "CHANGED: $($changed -join '; ')" })
        # The one link every session on this machine depends on, named on its own so a failure cannot hide in a list.
        $expected = Join-Path $HOME '.local/share/cc-director/pyenv/bin/cc-devthrottle'
        $actual = (Get-Item (Join-Path $HOME '.local/bin/cc-devthrottle') -ErrorAction SilentlyContinue).LinkTarget
        Add-Result "Teardown: ~/.local/bin/cc-devthrottle still points at the user's own install" ($actual -eq $expected) `
            "it points at $actual"
        if ($actual -ne $expected) { Say "LOUD: ~/.local/bin/cc-devthrottle points at '$actual', not '$expected'. Every session on this machine needs it repaired." }
    }

    $spend = Measure-Spend
    $directorLog = Get-ChildItem (Join-Path $root 'instances/default/logs/director') -Filter *.log -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | ForEach-Object { Get-Content $_.FullName } |
        Where-Object { $_ -match '\[PiDialect\]|\[ApiMode|\[PiApiMode|ApiModeWheel' }
    ($directorLog | ForEach-Object { Protect-Text $_ }) | Set-Content (Join-Path $OutDir 'director-pi.log') -Encoding utf8
    $wire | Set-Content (Join-Path $OutDir 'wire.jsonl') -Encoding utf8
    # The accessibility dumps are text, so they are scrubbed like the rest; the pictures cannot be (see -RigRoot).
    foreach ($dump in @(Get-ChildItem $screensDir -Filter *.txt -ErrorAction SilentlyContinue)) {
        Protect-Text (Get-Content $dump.FullName -Raw) | Set-Content $dump.FullName -Encoding utf8 -NoNewline
    }
    # What this run did not take, said in words rather than left out.
    $notTaken = [System.Collections.Generic.List[string]]::new()
    if ($NoScreens) { $notTaken.Add('No screens: the run was made with -NoScreens.') }
    elseif (-not $IsWindows) { $notTaken.Add('The desktop Director''s screens: Linux has no accessibility reader for the desktop here; the Windows run dumps them through UI Automation.') }
    if (-not $NoScreens) { $notTaken.Add('The desktop Director''s Chat tab mid-turn: the desktop has no API-mode Chat screen to take; the Cockpit and phone shots are the Chat evidence.') }

    $failed = @($results | Where-Object { $_.Result -eq 'FAIL' }).Count
    if ($failed -gt 0 -and $exitCode -eq 0) { $exitCode = 1 }
    $t = $spend.Totals
    $spendLine = "| $($started.ToUniversalTime().ToString('yyyy-MM-dd')) | Developer, phase 6 | Proof run, $osWord ($stamp) | $($t.input + $spend.CutOffInput) | $($t.output + $spend.CutOffOutput) | $($t.cacheRead) | $($t.cacheWrite) | $($spend.Dollars) |"
    @(
        "Pi tokens for this run, from Pi's own session files: input $($t.input), output $($t.output), cache read $($t.cacheRead), cache write $($t.cacheWrite).",
        "Replies Pi cut off (recorded as zero, estimated per spend.md): $($t.estimatedCutOff), estimated as $($spend.CutOffInput) input and $($spend.CutOffOutput) output tokens.",
        "Estimated dollars: $($spend.Dollars)",
        '',
        'The row for docs/missions/api-mode-pi-2026-10-16/spend.md:',
        $spendLine
    ) | Set-Content (Join-Path $OutDir 'spend.txt') -Encoding utf8

    $summary = [System.Collections.Generic.List[string]]::new()
    $summary.Add("API mode proof on Pi (mission #4070, phase 6) - $osWord, $($started.ToUniversalTime().ToString('yyyy-MM-dd HH:mm')) UTC")
    # A run from a tree with uncommitted changes says so: its commit alone would name code that did not produce it.
    $dirty = @(git -C $repo status --porcelain --untracked-files=no)
    $treeWords = if ($dirty.Count -gt 0) { "PLUS UNCOMMITTED CHANGES to $($dirty.Count) file(s) - not evidence of this commit" } else { 'clean' }
    $summary.Add("Tree: $(git -C $repo rev-parse --short HEAD) ($(git -C $repo rev-parse --abbrev-ref HEAD)), $treeWords; pi $piVersion; test Gateway from this tree on port $GatewayPort; slot-$Slot test Director")
    $summary.Add('')
    foreach ($r in $results) { $summary.Add("$($r.Result)  $($r.Step)"); $summary.Add("      $($r.Detail)") }
    $summary.Add('')
    $summary.Add('Proven by unit tests instead of this run:')
    foreach ($case in $UnitTestProven.Keys) { $summary.Add("  $case"); foreach ($test in $UnitTestProven[$case]) { $summary.Add("    $test") } }
    if ($notTaken.Count -gt 0) { $summary.Add(''); $summary.Add('Not taken in this run:'); foreach ($n in $notTaken) { $summary.Add("  $n") } }
    $summary.Add('')
    $summary.Add("Spend: $($spend.Dollars) dollars estimated (spend.txt)")
    $summary.Add("Result: $(if ($exitCode -eq 0) { 'every step passed' } else { "$failed step(s) failed" })")
    ($summary | ForEach-Object { Protect-Text $_ }) | Set-Content (Join-Path $OutDir 'summary.txt') -Encoding utf8
    # The same, machine-readable, for scripts/build-api-mode-qa-report.ps1.
    $machine = [ordered]@{
        platform = $osWord; startedUtc = $started.ToUniversalTime().ToString('o'); stamp = $stamp
        commit = (git -C $repo rev-parse HEAD); tree = $treeWords; pi = $piVersion
        steps = @($results | ForEach-Object { [ordered]@{ step = $_.Step; result = $_.Result; detail = $_.Detail } })
        screens = @($screens | ForEach-Object { [ordered]@{ name = $_.Name; surface = $_.Surface; ok = $_.Ok; detail = (Protect-Text $_.Detail); png = $_.Png; aria = $_.Aria } })
        notTaken = @($notTaken)
        unitTestProven = @($UnitTestProven.Keys | ForEach-Object { [ordered]@{ case = $_; tests = @($UnitTestProven[$_]) } })
        spendDollars = $spend.Dollars; exitCode = $exitCode
    }
    Protect-Text ($machine | ConvertTo-Json -Depth 10) | Set-Content (Join-Path $OutDir 'results.json') -Encoding utf8
    Say "summary: $(Join-Path $OutDir 'summary.txt')"
    # Delete only the folder this run created, and only once nothing it started is still running in it.
    $directorGone = -not $directorPid -or -not (Get-Process -Id $directorPid -ErrorAction SilentlyContinue)
    $gatewayGone = -not $gatewayProcess -or $gatewayProcess.HasExited
    if ($KeepRig -or -not ($directorGone -and $gatewayGone)) { Say "rig kept: $rig" }
    else { Remove-Item -Recurse -Force $rig; Say 'rig removed' }
    Say "spend: $($spend.Dollars) dollars estimated"
}
exit $exitCode
