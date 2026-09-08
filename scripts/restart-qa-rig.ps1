#Requires -Version 5.1
<#
.SYNOPSIS
    The isolated rig for the Director restart QA runs (issue 2719, Phase 7 and Phase 5): its own
    storage root, its own Gateway, its own launcher and its own Director, all built from this tree,
    with the machine's real Gateway, launcher and Directors unreachable by construction.

.DESCRIPTION
    WHY A RIG AND NOT A SLOT. A Director built into a numbered slot (cc-director7.exe) in the shared
    root cannot be restarted by ANY launcher: DirectorSupervisor supervises exactly one Director, the
    installed one at <root>\app\cc-director.exe in <root>\instances\default, and the launcher's
    director/restart handler ignores the Path the command carries (issue 2743). And a second launcher
    on this machine pointed at the production Gateway would REPLACE DevThrottle_1's launcher entry and
    command stream, because the Gateway keys launchers on tenant plus the bare machine name, last
    writer wins (issue 2742). So the only shape in which a launcher genuinely restarts a test Director
    without touching the real one is a whole isolated world: a root of its own, a Gateway of its own.

    What "up" stands:

      <root>\gateway\devthrottle-gateway.exe        the Gateway (desktop tray build), auth ON, tailscale OFF, own port
      <root>\launcher\cc-launcher.exe               the launcher, --no-autostart, not --managed
      <root>\app\cc-director.exe                    the Director the launcher supervises

    all three run with CC_DIRECTOR_ROOT=<root>, so every registration, signal name, log and token is
    keyed to the rig root and none of them can collide with %LOCALAPPDATA%\cc-director. The Gateway and
    the launcher are started by Windows scheduled tasks (clean svchost parentage - a Director spawned
    from an agent's console loses every session it hosts to nested-console detection), and the Director
    is started by the LAUNCHER, through the rig Gateway's POST /machines/<machine>/director/start, which
    is the production path and is itself the first thing the rig proves.

    Auto-update is off three ways (CC_AUTOUPDATE=0, autoUpdate.enabled=false, and the launcher is not
    --managed) so a release published mid-run cannot replace the build under test.

    RUN TWO IS A CHANGE OF TARGET. Nothing in the QA run scripts beside this one knows the rig: they
    take a Gateway address, a credential, a machine name and a Director id. Point them at production
    from a driver on another machine and the same tooling drives DevThrottle_1.

.PARAMETER Command
    build    publish the Gateway host, the launcher and the Director from this tree into -Builds
    up       lay out the root, start the Gateway and the launcher, have the launcher start the Director
    status   what is running, by exact image path, and what the rig Gateway sees
    down     stop the Director (its shutdown signal), the launcher (its shutdown signal) and the
             Gateway (POST /shutdown), then unregister the tasks. NEVER force-kills; says what is left.
    reset    down, then delete the root - only a root this script can prove it owns

.PARAMETER Root
    The rig root. Refused unless it is inside %LOCALAPPDATA%, carries "restart-qa-rig" in its leaf
    name, is not the real cc-director root or any ancestor or descendant of it, and either does not
    exist or carries this script's sentinel file.

.PARAMETER GatewayPort
    The rig Gateway's loopback port. Must not be the live Gateway's (7878).

.PARAMETER Builds
    Where "build" publishes to and "up" copies from.

.PARAMETER WebShells
    Where the Cockpit (wwwroot\c) and the mobile app (wwwroot\mobile) the rig Gateway serves come from.
    "tree" (the default) builds both from this tree during "build" (npm ci plus two vite builds - it
    needs node and takes minutes) and "up" refuses to stand the rig up without them, because Phase 6's
    accept screen lives in the Cockpit and an accept exercised on a copied release build is an accept
    exercised on a screen that does not contain the feature. "installed" copies the shells from the
    machine's installed Gateway instead - only for a rig that is proving something below the web shells.

.PARAMETER Force
    build: publish again even when an executable is already there.

.EXAMPLE
    .\scripts\restart-qa-rig.ps1 build
    .\scripts\restart-qa-rig.ps1 up
    .\scripts\restart-qa-rig.ps1 status
    .\scripts\restart-qa-rig.ps1 down
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('build', 'up', 'status', 'down', 'reset')]
    [string]$Command,
    [string]$Root = (Join-Path $env:LOCALAPPDATA "cc-director-restart-qa-rig"),
    [int]$GatewayPort = 7911,
    [string]$Builds = (Join-Path $env:TEMP "restart-qa-rig-builds"),
    [ValidateSet('tree', 'installed')]
    [string]$WebShells = 'tree',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$sep = [System.IO.Path]::DirectorySeparatorChar

function Say([string]$text) { Write-Host "[restart-qa-rig] $text" }
function Fail([string]$text) { throw "[restart-qa-rig] $text" }

# ---------------------------------------------------------------------------
# The deletion boundary is an ALLOW-LIST (the shape Phase 0's rig settled on after its first version
# deleted by deny-list and failed open for every path nobody had listed).
# ---------------------------------------------------------------------------
$RigMarker    = "restart-qa-rig"
$SentinelName = ".restart-qa-rig-owns-this-directory"

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

# Paths inside the rig.
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
$gatewayUrl    = "http://127.0.0.1:$GatewayPort"
$machine       = $env:COMPUTERNAME
$taskGateway   = "restart-qa-rig-gateway"
$taskLauncher  = "restart-qa-rig-launcher"

# Build outputs.
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
    try {
        $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized))
    } finally { $sha.Dispose() }
    return (($hash[0..5] | ForEach-Object { $_.ToString("x2") }) -join "")
}

function Get-RigProcesses {
    # Every process whose image lives under the rig root - by exact path prefix, never by name.
    Get-Process | Where-Object {
        $_.Path -and $_.Path.StartsWith($Root + $sep, [System.StringComparison]::OrdinalIgnoreCase)
    }
}

function Read-Token {
    if (-not (Test-Path $tokenFile)) { Fail "no rig token at $tokenFile - run 'up' first." }
    return (Get-Content $tokenFile -Raw).Trim()
}

function Invoke-Gateway([string]$method, [string]$path, [string]$body = $null, [int]$timeoutSec = 15) {
    $headers = @{ Authorization = "Bearer $(Read-Token)" }
    $args = @{ Method = $method; Uri = ($gatewayUrl + $path); Headers = $headers; UseBasicParsing = $true; TimeoutSec = $timeoutSec }
    if ($body) { $args.Body = $body; $args.ContentType = 'application/json' }
    return Invoke-WebRequest @args
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
    # Returns $true when a listener existed and was signalled, $false when nothing is listening.
    try {
        $evt = [System.Threading.EventWaitHandle]::OpenExisting("Local\$name")
        try { [void]$evt.Set() } finally { $evt.Dispose() }
        return $true
    } catch [System.Threading.WaitHandleCannotBeOpenedException] {
        return $false
    }
}

# ---------------------------------------------------------------------------
function Invoke-Build {
    New-Item -ItemType Directory -Force -Path $buildGateway, $buildLauncher, $buildDirector | Out-Null

    if ($Force -or -not (Test-Path $gatewayExe)) {
        # The DESKTOP Gateway (the tray application every self-hosted machine runs), not
        # CcDirector.Gateway.Host: that one is the hosted container image and refuses to start outside
        # the hosted contract (CC_GATEWAY_HOSTED, a public URL and a Postgres connection). The natives
        # flag is required for a single-file Avalonia publish; without it the exe dies on libSkiaSharp.
        Say "publishing the desktop Gateway from this tree -> $buildGateway"
        $shells = if ($WebShells -eq 'tree') { 'true' } else { 'false' }
        if ($WebShells -eq 'tree') { Say "  with the Cockpit and mobile app built from this tree (npm ci + two builds; minutes)" }
        & dotnet publish (Join-Path $repo "src\CcDirector.GatewayApp\CcDirector.GatewayApp.csproj") `
            -c Release -r win-x64 --self-contained false `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:RunMobileBuild=$shells -p:RunCockpitBuild=$shells -o $buildGateway --nologo -v q
        if ($LASTEXITCODE -ne 0) { Fail "Gateway publish failed" }
        if ($WebShells -eq 'tree') {
            foreach ($shell in @('c', 'mobile')) {
                $idx = Join-Path $buildGateway "wwwroot\$shell\index.html"
                if (-not (Test-Path $idx)) { Fail "the publish did not stage the $shell shell at $idx - a tree-built shell was asked for and is not there" }
            }
            Say "  tree-built shells staged under $buildGateway\wwwroot"
        }
    } else { Say "Gateway already published (pass -Force to rebuild)" }

    if ($Force -or -not (Test-Path $launcherExe)) {
        Say "publishing the launcher from this tree -> $buildLauncher"
        & dotnet publish (Join-Path $repo "src\CcDirector.Launcher\CcDirector.Launcher.csproj") `
            -c Release -f net10.0-windows -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -o $buildLauncher --nologo -v q
        if ($LASTEXITCODE -ne 0) { Fail "launcher publish failed" }
    } else { Say "launcher already published (pass -Force to rebuild)" }

    if ($Force -or -not (Test-Path $directorExe)) {
        # The Director is the Phase 7 slot build (slot 7 is the number reserved for this run) copied
        # under the name the launcher supervises. Same binary, one name.
        Say "building the Director (slot 7) from this tree"
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo "scripts\local-build-avalonia.ps1") `
            -Slot 7 -OutputDir (Join-Path $repo "scripts\local-build")
        if ($LASTEXITCODE -ne 0) { Fail "Director build failed" }
        Copy-Item (Join-Path $repo "scripts\local-build\cc-director7.exe") $directorExe -Force
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

    Say "laying out the rig root: $Root"
    New-Item -ItemType Directory -Force -Path $Root, $appDir, $launcherDir, $gatewayDir, (Split-Path $rootConfig), (Split-Path $tokenFile), (Split-Path $instConfig), $logsDir | Out-Null
    Set-Content -Path $sentinel -Value "created by scripts\restart-qa-rig.ps1 on $(Get-Date -Format s)" -Encoding ascii

    Copy-Item $directorExe (Join-Path $appDir "cc-director.exe") -Force
    Copy-Item $launcherExe (Join-Path $launcherDir "cc-launcher.exe") -Force
    Copy-Item (Join-Path $buildGateway "*") $gatewayDir -Recurse -Force

    # The Cockpit and the mobile app are static files the Gateway serves from beside itself. The
    # owner's accept screen (Phase 6) lives in the Cockpit, so by default they come from THIS TREE -
    # staged by the publish - and a rig without them does not come up. build.json in each says
    # which build it is, so the report can name the shell the accept was exercised on.
    if ($WebShells -eq 'tree') {
        $treeWwwroot = Join-Path $buildGateway "wwwroot"
        foreach ($shell in @('c', 'mobile')) {
            if (-not (Test-Path (Join-Path $treeWwwroot "$shell\index.html"))) {
                Fail "no tree-built $shell shell under $treeWwwroot - run 'build' with -WebShells tree (the default) first, or pass -WebShells installed and say so in the report."
            }
        }
        Say "web shells: built from this tree ($treeWwwroot)"
    } else {
        $installedWwwroot = Join-Path $realRoot "gateway\wwwroot"
        if (-not (Test-Path $installedWwwroot)) { Fail "no installed Gateway wwwroot at $installedWwwroot to copy" }
        Copy-Item $installedWwwroot (Join-Path $gatewayDir "wwwroot") -Recurse -Force
        Say "web shells: COPIED from the installed Gateway ($installedWwwroot) - NOT this tree. Say so in the report."
    }

    # One shared token for the whole rig. The Gateway reads or creates it at <root>\config\director\
    # gateway-token.txt; the launcher and the Director present it from their config.json.
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

    # The Director's own config: onboarding done (or the first-run wizard covers the screen), the rig
    # Gateway, and the agent table copied from the real default instance so the same agent executables
    # are known here. Nothing else of the real instance is copied.
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

    # The two task bodies. Environment is set HERE, in the .cmd, because a scheduled task action cannot
    # carry environment variables and the root MUST be named positively (Phase 0's rig found that
    # merely unsetting it resolves to the machine root).
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
    } 60
    if (-not $ok) {
        $gwLog = Get-ChildItem (Join-Path $logsDir "director") -Filter "director-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
        if ($gwLog) { Say "last lines of $($gwLog.FullName):"; Get-Content $gwLog.FullName -Tail 30 | Write-Host }
        Fail "the rig Gateway never answered /healthz on $gatewayUrl"
    }
    $health = (Invoke-WebRequest "$gatewayUrl/healthz" -UseBasicParsing).Content
    Say "Gateway healthy: $health"

    Say "starting the rig launcher (task $taskLauncher)"
    Register-RigTask $taskLauncher $launcherCmd
    Start-ScheduledTask -TaskName $taskLauncher
    $ok = Wait-Until "launcher registration $launcherReg" { Test-Path $launcherReg } 60
    if (-not $ok) { Fail "the rig launcher never wrote $launcherReg" }
    $reg = Get-Content $launcherReg -Raw | ConvertFrom-Json
    Say "launcher registered: pid $($reg.pid), version $($reg.version)"

    # The launcher's command stream: the ONLY path a restart can travel. Wait until the rig Gateway
    # lists this machine's launcher, then have it start the Director - the production path, and the
    # first proof the rig gives: a launcher that can START this Director is one that can restart it.
    $ok = Wait-Until "Gateway to list the launcher for $machine" {
        try {
            $list = (Invoke-Gateway GET "/launchers").Content | ConvertFrom-Json
            $items = if ($list -is [array]) { $list } elseif ($list.PSObject.Properties['launchers']) { $list.launchers } else { @($list) }
            @($items | Where-Object { $_.machineName -ieq $machine }).Count -gt 0
        } catch { $false }
    } 60
    if (-not $ok) { Fail "the rig Gateway never listed a launcher for $machine" }

    # A fresh Hello can lag the registration by a few seconds; a start relayed before the stream is
    # bound is refused as NotConnected. Retry the start itself rather than guessing a delay.
    Say "asking the launcher (through the rig Gateway) to start the Director"
    $started = $false
    foreach ($attempt in 1..12) {
        try {
            $resp = Invoke-Gateway POST "/machines/$machine/director/start" "{}"
            Say "director/start -> $($resp.StatusCode) $($resp.Content)"
            $started = $true
            break
        } catch {
            $msg = $_.Exception.Message
            Say "director/start attempt $attempt refused: $msg"
            Start-Sleep -Seconds 5
        }
    }
    if (-not $started) { Fail "the rig Gateway never relayed director/start to the launcher" }

    $ok = Wait-Until "Director registration under $instRegDir" {
        (Test-Path $instRegDir) -and ((Get-ChildItem $instRegDir -Filter *.json -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0)
    } 90
    if (-not $ok) { Fail "the rig Director never wrote its registration" }
    $dreg = Get-ChildItem $instRegDir -Filter *.json | Select-Object -First 1
    $d = Get-Content $dreg.FullName -Raw | ConvertFrom-Json
    Say "Director registered: id $($d.DirectorId) pid $($d.Pid) version $($d.Version)"

    $ok = Wait-Until "the rig Gateway to list the Director" {
        try {
            $list = (Invoke-Gateway GET "/directors").Content | ConvertFrom-Json
            $items = if ($list -is [array]) { $list } elseif ($list.PSObject.Properties['directors']) { $list.directors } else { @($list) }
            @($items | Where-Object { $_.directorId -ieq $d.DirectorId }).Count -gt 0
        } catch { $false }
    } 90
    if (-not $ok) { Fail "the rig Gateway never listed Director $($d.DirectorId)" }

    Say ""
    Say "RIG IS UP"
    Say "  root:          $Root"
    Say "  gateway:       $gatewayUrl   (token: $tokenFile)"
    Say "  machine:       $machine"
    Say "  director id:   $($d.DirectorId)"
    Say "  launcher:      pid $($reg.pid) version $($reg.version), root key $(Get-RootKey $Root)"
    Say "  drive it with: CC_GATEWAY_URL=$gatewayUrl and Bearer <token> (self-host shared token)"
    Say "  logs:          $logsDir and $instanceHome\logs\director"
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
    if (Test-Path $launcherReg) {
        $reg = Get-Content $launcherReg -Raw | ConvertFrom-Json
        Say "launcher registration: pid $($reg.pid) version $($reg.version) started $($reg.startedAtUtc)"
    } else { Say "launcher registration: none" }
    if (Test-Path $instRegDir) {
        foreach ($f in Get-ChildItem $instRegDir -Filter *.json) {
            $d = Get-Content $f.FullName -Raw | ConvertFrom-Json
            Say "Director registration: id $($d.DirectorId) pid $($d.Pid) version $($d.Version)"
        }
    } else { Say "Director registration: none" }
    try {
        $h = (Invoke-WebRequest "$gatewayUrl/healthz" -UseBasicParsing -TimeoutSec 3).Content
        Say "Gateway /healthz: $h"
        if (Test-Path $tokenFile) {
            $dirs = (Invoke-Gateway GET "/directors").Content
            Say "Gateway /directors: $dirs"
            $ls = (Invoke-Gateway GET "/launchers").Content
            Say "Gateway /launchers: $ls"
            $ss = (Invoke-Gateway GET "/sessions").Content | ConvertFrom-Json
            $items = if ($ss -is [array]) { $ss } elseif ($ss.PSObject.Properties['sessions']) { $ss.sessions } else { @() }
            Say "Gateway /sessions: $(@($items).Count) session(s)"
        }
    } catch { Say "Gateway on $gatewayUrl not answering: $($_.Exception.Message)" }
}

# ---------------------------------------------------------------------------
function Invoke-Down {
    # 1. The Director, by its own shutdown signal (named for its id), read from its registration.
    if (Test-Path $instRegDir) {
        foreach ($f in Get-ChildItem $instRegDir -Filter *.json) {
            $d = Get-Content $f.FullName -Raw | ConvertFrom-Json
            $id = ([string]$d.DirectorId).ToLowerInvariant()
            if (Set-NamedEvent "cc-director-shutdown-$id") {
                Say "Director ${id}: shutdown signalled, waiting for pid $($d.Pid) to exit"
                $gone = Wait-Until "Director pid $($d.Pid) to exit" { -not (Get-Process -Id $d.Pid -ErrorAction SilentlyContinue) } 60
                if (-not $gone) { Say "Director pid $($d.Pid) is STILL RUNNING after the signal - not forced; stop it by hand if it is genuinely stuck" }
            } else {
                Say "Director ${id}: nothing is listening on its shutdown signal (registration may be stale)"
            }
        }
    }

    # 2. The launcher, by its own shutdown signal (named for the root key).
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

    # 3. The Gateway, by its shutdown route.
    try {
        $r = Invoke-Gateway POST "/shutdown" "{}"
        Say "Gateway POST /shutdown -> $($r.StatusCode)"
        $gone = Wait-Until "Gateway to exit" {
            @(Get-RigProcesses | Where-Object { $_.Path -ieq (Join-Path $gatewayDir "devthrottle-gateway.exe") }).Count -eq 0
        } 60
        if (-not $gone) { Say "Gateway is STILL RUNNING after /shutdown - not forced" }
    } catch { Say "Gateway shutdown not sent: $($_.Exception.Message)" }

    Unregister-RigTask $taskGateway
    Unregister-RigTask $taskLauncher

    $left = @(Get-RigProcesses)
    if ($left.Count -gt 0) {
        foreach ($p in $left) { Say ("STILL RUNNING: pid {0} {1}" -f $p.Id, $p.Path) }
        Say "nothing was force-killed. These are the rig's own processes (image path under $Root); stop them by hand if they are stuck."
    } else {
        Say "rig is down: nothing runs from $Root"
    }
}

function Invoke-Reset {
    Invoke-Down
    if (@(Get-RigProcesses).Count -gt 0) { Fail "refusing to delete $Root while something still runs from it" }
    if (Test-Path $Root) {
        Assert-Disposable $Root
        # No junction or symbolic link is created by this script; a link found here is not ours to
        # descend into, and the delete stops rather than emptying whatever it points at.
        $links = Get-ChildItem $Root -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }
        if ($links) { Fail "refusing to delete $Root - it contains a link: $($links[0].FullName)" }
        Remove-Item -Recurse -Force $Root
        Say "deleted $Root"
    }
}

switch ($Command) {
    'build'  { Invoke-Build }
    'up'     { Invoke-Up }
    'status' { Invoke-Status }
    'down'   { Invoke-Down }
    'reset'  { Invoke-Reset }
}
