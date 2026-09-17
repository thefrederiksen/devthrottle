#Requires -Version 5.1
<#
.SYNOPSIS
    The isolated rig for the dev report viewer proof (issue #3010, phase 3 of #2936): its own storage root,
    its own Gateway, its own launcher, its own Director and one live session on it, all built from this tree.

.DESCRIPTION
    Derived from scripts\restart-qa-rig.ps1 (issue 2719), which stands the same isolated world. It is a separate
    copy with its own root, port and scheduled task names so the two rigs can never overwrite each other's tasks.

    What "up" stands, every process with CC_DIRECTOR_ROOT=<root>:

      <root>\gateway\devthrottle-gateway.exe   the desktop Gateway, auth ON, CC_GATEWAY_NO_TAILSCALE=1, own port, --no-autostart
      <root>\launcher\cc-launcher.exe          the launcher, --no-autostart, not --managed
      <root>\app\cc-director.exe               the Director (a slot 5 build from this tree), started by the launcher
                                               through the rig Gateway

    The Gateway and the launcher start from Windows scheduled tasks (clean svchost parentage, repository
    CLAUDE.md rule 0b), so the Director the launcher starts is outside this agent's console too. Nothing here
    touches %LOCALAPPDATA%\cc-director, the hosted Gateway, port 443 or any other Director: the root, the port,
    the signal names and the tasks are all the rig's own.

.PARAMETER Command
    build         publish the Gateway (with the Cockpit and mobile app built from this tree), the launcher and
                  the Director
    up            lay out the root, start the Gateway and the launcher, have the launcher start the Director
    session       open one Claude Code session on the rig Director and write its Gateway environment
                  (CC_GATEWAY_URL, CC_GATEWAY_SESSION_KEY, CC_SESSION_ID) to <root>\session.json, so the proof
                  can run cc-dev-reports AS that session
    stage-shells  copy apps\cockpit\dist and apps\mobile\dist from this tree into the running Gateway's wwwroot
    status        what is running (by exact image path) and what the rig Gateway sees
    down          Director by its named signal, launcher by its named signal, Gateway by POST /shutdown, then
                  unregister the tasks. Never force-kills; says what is left.
    reset         down, then delete the root (only a root carrying this script's sentinel)

.EXAMPLE
    .\rig.ps1 build
    .\rig.ps1 up
    .\rig.ps1 session
    .\rig.ps1 down
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('build', 'up', 'session', 'stage-shells', 'status', 'down', 'reset')]
    [string]$Command,
    [string]$Root = (Join-Path $env:LOCALAPPDATA "dev-report-proof-rig"),
    [int]$GatewayPort = 7931,
    [string]$Builds = (Join-Path $env:TEMP "dev-report-proof-rig-builds"),
    [int]$Slot = 5,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = [System.IO.Path]::GetFullPath((Join-Path $here "..\..\..\.."))
$sep = [System.IO.Path]::DirectorySeparatorChar

function Say([string]$text) { Write-Host "[dev-report-rig] $text" }
function Fail([string]$text) { throw "[dev-report-rig] $text" }

# ---------------------------------------------------------------------------
# The deletion boundary is an ALLOW-LIST, the same shape as restart-qa-rig.ps1.
# ---------------------------------------------------------------------------
$RigMarker    = "dev-report-proof-rig"
$SentinelName = ".dev-report-proof-rig-owns-this-directory"

$Root          = [System.IO.Path]::GetFullPath($Root).TrimEnd($sep)
$scratchParent = [System.IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd($sep)
$realRoot      = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "cc-director")).TrimEnd($sep)

function Assert-Disposable([string]$path) {
    if ($path -ieq $scratchParent) { Fail "REFUSING: $path is the scratch parent itself." }
    if (-not $path.StartsWith($scratchParent + $sep, [System.StringComparison]::OrdinalIgnoreCase)) {
        Fail "REFUSING: $path is not inside $scratchParent."
    }
    if ($path -ieq $realRoot -or
        $path.StartsWith($realRoot + $sep, [System.StringComparison]::OrdinalIgnoreCase) -or
        $realRoot.StartsWith($path + $sep, [System.StringComparison]::OrdinalIgnoreCase)) {
        Fail "REFUSING: $path is the machine's real cc-director root, is inside it, or is an ancestor of it."
    }
    if ((Split-Path -Leaf $path) -notlike "*$RigMarker*") {
        Fail "REFUSING: $path does not carry '$RigMarker' in its leaf name."
    }
    if ((Test-Path $path) -and -not (Test-Path (Join-Path $path $SentinelName))) {
        Fail "REFUSING: $path exists but carries no $SentinelName, so this script did not create it."
    }
}

Assert-Disposable $Root
if ($GatewayPort -eq 7878) { Fail "REFUSING: 7878 is the live Gateway's port." }
if ($Slot -lt 5) { Fail "REFUSING: slots 1-4 are the owner's working Directors; use 5 or higher." }

$sentinel      = Join-Path $Root $SentinelName
$appDir        = Join-Path $Root "app"
$launcherDir   = Join-Path $Root "launcher"
$gatewayDir    = Join-Path $Root "gateway"
$rootConfig    = Join-Path $Root "config\config.json"
$tokenFile     = Join-Path $Root "config\director\gateway-token.txt"
$launcherReg   = Join-Path $Root "config\launcher\launcher.json"
$instanceHome  = Join-Path $Root "instances\default"
$instConfig    = Join-Path $instanceHome "config\config.json"
$instRegDir    = Join-Path $instanceHome "config\director\instances"
$logsDir       = Join-Path $Root "logs"
$sessionFile   = Join-Path $Root "session.json"
$gatewayUrl    = "http://127.0.0.1:$GatewayPort"
$machine       = $env:COMPUTERNAME
$taskGateway   = "dev-report-proof-rig-gateway"
$taskLauncher  = "dev-report-proof-rig-launcher"

$buildGateway  = Join-Path $Builds "gateway"
$buildLauncher = Join-Path $Builds "launcher"
$buildDirector = Join-Path $Builds "director"
$gatewayExe    = Join-Path $buildGateway "devthrottle-gateway.exe"
$launcherExe   = Join-Path $buildLauncher "cc-launcher.exe"
$directorExe   = Join-Path $buildDirector "cc-director.exe"

# The launcher names its signals by the sha256 of the lower-cased root, first six bytes, hex.
function Get-RootKey([string]$root) {
    $normalized = $root.TrimEnd('\', '/').ToLowerInvariant()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized)) } finally { $sha.Dispose() }
    return (($hash[0..5] | ForEach-Object { $_.ToString("x2") }) -join "")
}

function Get-RigProcesses {
    Get-Process | Where-Object {
        $_.Path -and $_.Path.StartsWith($Root + $sep, [System.StringComparison]::OrdinalIgnoreCase)
    }
}

function Read-Token {
    if (-not (Test-Path $tokenFile)) { Fail "no rig token at $tokenFile - run 'up' first." }
    return (Get-Content $tokenFile -Raw).Trim()
}

function Invoke-Gateway([string]$method, [string]$path, [string]$body = $null, [int]$timeoutSec = 30) {
    $headers = @{ Authorization = "Bearer $(Read-Token)" }
    $a = @{ Method = $method; Uri = ($gatewayUrl + $path); Headers = $headers; UseBasicParsing = $true; TimeoutSec = $timeoutSec }
    if ($body) { $a.Body = $body; $a.ContentType = 'application/json' }
    return Invoke-WebRequest @a
}

function Wait-Until([string]$what, [scriptblock]$test, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $test) { return $true }
        Start-Sleep -Milliseconds 750
    }
    Say "TIMEOUT after ${seconds}s waiting for: $what"
    return $false
}

function Register-RigTask([string]$name, [string]$cmdFile) {
    $action  = New-ScheduledTaskAction -Execute "cmd.exe" -Argument "/c `"$cmdFile`"" -WorkingDirectory $Root
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5)
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
}

function Unregister-RigTask([string]$name) {
    if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $name -Confirm:$false
        Say "task $name unregistered"
    }
}

function Set-NamedEvent([string]$name) {
    try {
        $evt = [System.Threading.EventWaitHandle]::OpenExisting("Local\$name")
        try { [void]$evt.Set() } finally { $evt.Dispose() }
        return $true
    } catch [System.Threading.WaitHandleCannotBeOpenedException] {
        return $false
    }
}

function Get-Items($parsed, [string]$prop) {
    if ($parsed -is [array]) { return $parsed }
    if ($parsed.PSObject.Properties[$prop]) { return @($parsed.$prop) }
    return @($parsed)
}

# ---------------------------------------------------------------------------
function Invoke-Build {
    New-Item -ItemType Directory -Force -Path $buildGateway, $buildLauncher, $buildDirector | Out-Null
    $head = (& git -C $repo rev-parse HEAD).Trim()
    Say "building from $repo at $head"

    if ($Force -or -not (Test-Path $gatewayExe)) {
        Say "publishing the desktop Gateway with the Cockpit and mobile app built from this tree (minutes)"
        & dotnet publish (Join-Path $repo "src\CcDirector.GatewayApp\CcDirector.GatewayApp.csproj") `
            -c Release -r win-x64 --self-contained false `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:RunMobileBuild=true -p:RunCockpitBuild=true -o $buildGateway --nologo -v q
        if ($LASTEXITCODE -ne 0) { Fail "Gateway publish failed" }
        foreach ($shell in @('c', 'mobile')) {
            $idx = Join-Path $buildGateway "wwwroot\$shell\index.html"
            if (-not (Test-Path $idx)) { Fail "the publish did not stage the $shell shell at $idx" }
        }
    } else { Say "Gateway already published (pass -Force to rebuild)" }

    if ($Force -or -not (Test-Path $launcherExe)) {
        Say "publishing the launcher"
        & dotnet publish (Join-Path $repo "src\CcDirector.Launcher\CcDirector.Launcher.csproj") `
            -c Release -f net10.0-windows -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -o $buildLauncher --nologo -v q
        if ($LASTEXITCODE -ne 0) { Fail "launcher publish failed" }
    } else { Say "launcher already published (pass -Force to rebuild)" }

    if ($Force -or -not (Test-Path $directorExe)) {
        Say "building the Director (slot $Slot) into this worktree's scripts\local-build"
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo "scripts\local-build-avalonia.ps1") `
            -Slot $Slot -OutputDir (Join-Path $repo "scripts\local-build")
        if ($LASTEXITCODE -ne 0) { Fail "Director build failed" }
        Copy-Item (Join-Path $repo "scripts\local-build\cc-director$Slot.exe") $directorExe -Force
    } else { Say "Director already built (pass -Force to rebuild)" }

    foreach ($exe in @($gatewayExe, $launcherExe, $directorExe)) {
        $v = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
        Say ("built: {0}  version {1}  {2:N1} MB" -f $exe, $v, ((Get-Item $exe).Length / 1MB))
    }
}

# ---------------------------------------------------------------------------
function Invoke-Up {
    foreach ($exe in @($gatewayExe, $launcherExe, $directorExe)) {
        if (-not (Test-Path $exe)) { Fail "not built: $exe - run 'build' first." }
    }
    if (Get-RigProcesses) { Fail "something is already running from $Root - run 'status' or 'down' first." }
    foreach ($t in @($taskGateway, $taskLauncher)) {
        if (Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue) {
            Say "task $t is already registered (a rig that did not come down cleanly); it is replaced"
        }
    }

    Say "laying out the rig root: $Root"
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    Set-Content -Path $sentinel -Value "created by dev-report-viewer-proof\rig.ps1 on $(Get-Date -Format s)" -Encoding ascii
    New-Item -ItemType Directory -Force -Path $appDir, $launcherDir, $gatewayDir, (Split-Path $rootConfig), (Split-Path $tokenFile), (Split-Path $instConfig), $logsDir | Out-Null

    Copy-Item $directorExe (Join-Path $appDir "cc-director.exe") -Force
    Copy-Item $launcherExe (Join-Path $launcherDir "cc-launcher.exe") -Force
    Copy-Item (Join-Path $buildGateway "*") $gatewayDir -Recurse -Force

    if (-not (Test-Path $tokenFile)) {
        $bytes = New-Object byte[] 32
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        Set-Content -Path $tokenFile -Value $token -Encoding ascii -NoNewline
    }
    $token = Read-Token

    $gatewaySection = @{ url = $gatewayUrl; token = $token; streamMode = $true }
    $rootCfg = @{ autoUpdate = @{ enabled = $false }; gateway = $gatewaySection }
    Set-Content -Path $rootConfig -Value ($rootCfg | ConvertTo-Json -Depth 5) -Encoding ascii

    # The agent table is copied from the real default instance so the rig Director knows the same agent
    # executables. Nothing else of the real instance is copied.
    $realInstConfig = Join-Path $realRoot "instances\default\config\config.json"
    $agents = $null
    if (Test-Path $realInstConfig) {
        $realCfg = Get-Content $realInstConfig -Raw | ConvertFrom-Json
        if ($realCfg.PSObject.Properties['agent']) { $agents = $realCfg.agent }
    }
    if ($null -eq $agents) { Fail "no agent table at $realInstConfig to copy - the rig Director would know no agents." }
    $instCfg = [ordered]@{
        onboarding = @{ completed = $true }
        autoUpdate = @{ enabled = $false }
        gateway    = $gatewaySection
        agent      = $agents
    }
    Set-Content -Path $instConfig -Value ($instCfg | ConvertTo-Json -Depth 10) -Encoding ascii

    $gatewayCmd = Join-Path $Root "rig-gateway.cmd"
    @(
        "@echo off",
        "set CC_DIRECTOR_ROOT=$Root",
        "set CC_GATEWAY_NO_TAILSCALE=1",
        "set CC_AUTOUPDATE=0",
        "cd /d `"$gatewayDir`"",
        "start `"`" `"$gatewayDir\devthrottle-gateway.exe`" --port $GatewayPort --no-autostart"
    ) | Set-Content -Path $gatewayCmd -Encoding ascii

    $launcherCmd = Join-Path $Root "rig-launcher.cmd"
    @(
        "@echo off",
        "set CC_DIRECTOR_ROOT=$Root",
        "set CC_AUTOUPDATE=0",
        "cd /d `"$launcherDir`"",
        "start `"`" `"$launcherDir\cc-launcher.exe`" --no-autostart"
    ) | Set-Content -Path $launcherCmd -Encoding ascii

    Say "starting the rig Gateway on $gatewayUrl (task $taskGateway)"
    Register-RigTask $taskGateway $gatewayCmd
    Start-ScheduledTask -TaskName $taskGateway
    $ok = Wait-Until "Gateway /healthz" {
        try { (Invoke-WebRequest "$gatewayUrl/healthz" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { $false }
    } 90
    if (-not $ok) { Fail "the rig Gateway never answered /healthz on $gatewayUrl (logs under $logsDir)" }
    $health = (Invoke-WebRequest "$gatewayUrl/healthz" -UseBasicParsing).Content
    Say "Gateway healthy: $health"

    Say "starting the rig launcher (task $taskLauncher)"
    Register-RigTask $taskLauncher $launcherCmd
    Start-ScheduledTask -TaskName $taskLauncher
    $ok = Wait-Until "launcher registration $launcherReg" { Test-Path $launcherReg } 60
    if (-not $ok) { Fail "the rig launcher never wrote $launcherReg" }
    $reg = Get-Content $launcherReg -Raw | ConvertFrom-Json
    Say "launcher registered: pid $($reg.pid), version $($reg.version)"

    $ok = Wait-Until "Gateway to list the launcher for $machine" {
        try {
            $items = Get-Items ((Invoke-Gateway GET "/launchers").Content | ConvertFrom-Json) 'launchers'
            @($items | Where-Object { $_.machineName -ieq $machine }).Count -gt 0
        } catch { $false }
    } 60
    if (-not $ok) { Fail "the rig Gateway never listed a launcher for $machine" }

    Say "asking the launcher (through the rig Gateway) to start the Director"
    $started = $false
    foreach ($attempt in 1..12) {
        try {
            $resp = Invoke-Gateway POST "/machines/$machine/director/start" "{}"
            Say "director/start -> $($resp.StatusCode) $($resp.Content)"
            $started = $true
            break
        } catch {
            Say "director/start attempt $attempt refused: $($_.Exception.Message)"
            Start-Sleep -Seconds 5
        }
    }
    if (-not $started) { Fail "the rig Gateway never relayed director/start to the launcher" }

    $ok = Wait-Until "Director registration under $instRegDir" {
        (Test-Path $instRegDir) -and ((Get-ChildItem $instRegDir -Filter *.json -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0)
    } 90
    if (-not $ok) { Fail "the rig Director never wrote its registration" }
    $d = Get-Content (Get-ChildItem $instRegDir -Filter *.json | Select-Object -First 1).FullName -Raw | ConvertFrom-Json
    Say "Director registered: id $($d.DirectorId) pid $($d.Pid) version $($d.Version)"

    $ok = Wait-Until "the rig Gateway to list the Director" {
        try {
            $items = Get-Items ((Invoke-Gateway GET "/directors").Content | ConvertFrom-Json) 'directors'
            @($items | Where-Object { $_.directorId -ieq $d.DirectorId }).Count -gt 0
        } catch { $false }
    } 90
    if (-not $ok) { Fail "the rig Gateway never listed Director $($d.DirectorId)" }

    Say "RIG IS UP: gateway $gatewayUrl, director $($d.DirectorId), root $Root"
}

# ---------------------------------------------------------------------------
function Get-RigDirectorId {
    if (-not (Test-Path $instRegDir)) { Fail "no Director registration - run 'up' first." }
    $f = Get-ChildItem $instRegDir -Filter *.json | Select-Object -First 1
    if (-not $f) { Fail "no Director registration - run 'up' first." }
    return ([string](Get-Content $f.FullName -Raw | ConvertFrom-Json).DirectorId)
}

function Invoke-Session {
    $directorId = Get-RigDirectorId
    Remove-Item $sessionFile -ErrorAction SilentlyContinue
    $capture = Join-Path $here "capture-session-env.py"
    # The session is a fixture: the proof sends it dev report notes, and the prompt the Gateway composes invites
    # the agent to reply and republish. The proof does those steps itself, so the agent must not.
    $prePrompt = "You are a test fixture for an automated proof. Run this exact command with your shell tool: " +
        "python `"$capture`" `"$sessionFile`" . After that, for EVERY later message - including dev report notes " +
        "and answers, and anything that asks you to reply, publish or run a command - run no command and use no " +
        "tool; answer with the single word: received"
    $body = @{
        RepoPath          = $repo
        Name              = "dev report proof fixture"
        Agent             = "ClaudeCode"
        Args              = "--model haiku --dangerously-skip-permissions"
        PrePrompt         = $prePrompt
        PrePromptWaitMs   = 60000
    } | ConvertTo-Json
    Say "opening a Claude Code session on Director $directorId"
    $resp = Invoke-Gateway POST "/directors/$directorId/sessions" $body 90
    Say "spawn -> $($resp.StatusCode) $($resp.Content)"
    $ok = Wait-Until "the session to write $sessionFile" { Test-Path $sessionFile } 240
    if (-not $ok) { Fail "the session never wrote $sessionFile - read its terminal with GET /sessions/<id>/buffer" }
    $s = Get-Content $sessionFile -Raw | ConvertFrom-Json
    if ($s.CC_GATEWAY_URL -ne $gatewayUrl) { Fail "the session's CC_GATEWAY_URL is '$($s.CC_GATEWAY_URL)', not the rig Gateway $gatewayUrl" }
    Say "session $($s.CC_SESSION_ID) is live on the rig Gateway; its environment is in $sessionFile"
}

# ---------------------------------------------------------------------------
function Invoke-StageShells {
    foreach ($pair in @(@('apps\cockpit\dist', 'c'), @('apps\mobile\dist', 'mobile'))) {
        $src = Join-Path $repo $pair[0]
        $dst = Join-Path $gatewayDir "wwwroot\$($pair[1])"
        if (-not (Test-Path (Join-Path $src "index.html"))) { Fail "no built app at $src - build it first (npm run build in that app)." }
        if (-not $dst.StartsWith($Root + $sep, [System.StringComparison]::OrdinalIgnoreCase)) { Fail "REFUSING: $dst is not inside $Root" }
        if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
        Copy-Item $src $dst -Recurse -Force
        Say "staged $src -> $dst"
    }
}

# ---------------------------------------------------------------------------
function Invoke-Status {
    Say "root: $Root  (exists: $(Test-Path $Root))"
    $procs = @(Get-RigProcesses)
    if ($procs.Count -eq 0) { Say "no process is running from the rig root" }
    foreach ($p in $procs) { Say ("running: pid {0,-7} {1}" -f $p.Id, $p.Path) }
    foreach ($t in @($taskGateway, $taskLauncher)) {
        $task = Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
        Say ("task {0}: {1}" -f $t, $(if ($task) { $task.State } else { "not registered" }))
    }
    try {
        Say "Gateway /healthz: $((Invoke-WebRequest "$gatewayUrl/healthz" -UseBasicParsing -TimeoutSec 3).Content)"
        if (Test-Path $tokenFile) {
            Say "Gateway /directors: $((Invoke-Gateway GET "/directors").Content)"
            $items = Get-Items ((Invoke-Gateway GET "/sessions").Content | ConvertFrom-Json) 'sessions'
            Say "Gateway /sessions: $(@($items).Count) session(s)"
        }
    } catch { Say "Gateway on $gatewayUrl not answering: $($_.Exception.Message)" }
}

# ---------------------------------------------------------------------------
function Invoke-Down {
    if (Test-Path $instRegDir) {
        foreach ($f in Get-ChildItem $instRegDir -Filter *.json) {
            $d = Get-Content $f.FullName -Raw | ConvertFrom-Json
            $id = ([string]$d.DirectorId).ToLowerInvariant()
            if (Set-NamedEvent "cc-director-shutdown-$id") {
                Say "Director ${id}: shutdown signalled, waiting for pid $($d.Pid) to exit"
                $gone = Wait-Until "Director pid $($d.Pid) to exit" { -not (Get-Process -Id $d.Pid -ErrorAction SilentlyContinue) } 60
                if (-not $gone) { Say "Director pid $($d.Pid) is STILL RUNNING after the signal - not forced" }
            } else {
                Say "Director ${id}: nothing is listening on its shutdown signal"
            }
        }
    }

    $key = Get-RootKey $Root
    if (Set-NamedEvent "cc-director-launcher-shutdown-$key") {
        Say "launcher: shutdown signalled (root key $key)"
        $gone = Wait-Until "launcher to exit" {
            @(Get-RigProcesses | Where-Object { $_.Path -ieq (Join-Path $launcherDir "cc-launcher.exe") }).Count -eq 0
        } 60
        if (-not $gone) { Say "launcher is STILL RUNNING after the signal - not forced" }
    } else {
        Say "launcher: nothing is listening on its shutdown signal"
    }

    if (Test-Path $tokenFile) {
        try {
            $r = Invoke-Gateway POST "/shutdown" "{}"
            Say "Gateway POST /shutdown -> $($r.StatusCode)"
            $gone = Wait-Until "Gateway to exit" {
                @(Get-RigProcesses | Where-Object { $_.Path -ieq (Join-Path $gatewayDir "devthrottle-gateway.exe") }).Count -eq 0
            } 60
            if (-not $gone) { Say "Gateway is STILL RUNNING after /shutdown - not forced" }
        } catch { Say "Gateway shutdown not sent: $($_.Exception.Message)" }
    }

    Unregister-RigTask $taskGateway
    Unregister-RigTask $taskLauncher

    $left = @(Get-RigProcesses)
    if ($left.Count -gt 0) {
        foreach ($p in $left) { Say ("STILL RUNNING: pid {0} {1}" -f $p.Id, $p.Path) }
        Say "nothing was force-killed; these run from $Root"
    } else {
        Say "rig is down: nothing runs from $Root"
    }
}

function Invoke-Reset {
    Invoke-Down
    if (@(Get-RigProcesses).Count -gt 0) { Fail "refusing to delete $Root while something still runs from it" }
    if (Test-Path $Root) {
        Assert-Disposable $Root
        $links = Get-ChildItem $Root -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }
        if ($links) { Fail "refusing to delete $Root - it contains a link: $($links[0].FullName)" }
        Remove-Item -Recurse -Force $Root
        Say "deleted $Root"
    }
}

switch ($Command) {
    'build'        { Invoke-Build }
    'up'           { Invoke-Up }
    'session'      { Invoke-Session }
    'stage-shells' { Invoke-StageShells }
    'status'       { Invoke-Status }
    'down'         { Invoke-Down }
    'reset'        { Invoke-Reset }
}
