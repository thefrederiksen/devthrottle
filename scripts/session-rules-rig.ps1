#Requires -Version 5.1
<#
.SYNOPSIS
    The Session Rules live rig on Windows: a Gateway of its own, a slot-6 test Director of its own, and
    the worktree's cc-devthrottle pointed at them - the Windows twin of scripts/session-rules-rig.sh.

.DESCRIPTION
    What "up" stands, all under one rig folder (default %LOCALAPPDATA%\session-rules-rig-win):

      <rig>\builds\gateway\CcDirector.Gateway.exe   the self-host console Gateway, published from the worktree
      <worktree>\scripts\local-build\cc-director6.exe   the test Director (copied in by "director")
      <rig>\root                                    CC_DIRECTOR_ROOT for both: data, token, logs, rules files

    How it keeps away from the owner's fleet:
      - Both processes run with CC_DIRECTOR_ROOT=<rig>\root, so every registration, signal name, token and
        log is keyed to the rig root, and the Director is given only the rig Gateway's address and token.
      - Both are started by Windows scheduled tasks the rig owns (session-rules-rig-gateway and
        session-rules-rig-director), never from an agent's console: a Director started inside an agent's
        console loses the sessions it starts to nested-console detection (repository rule 0b). The task
        body is a .cmd that sets the environment, because a task action cannot carry environment
        variables. The shared cc-director-launch task is not touched: other seats point it at their slot.
      - Port 7878 and slots below 6 are refused. Processes are found by exact image path, never by name.
      - The Director stops on its own named shutdown signal, the Gateway on POST /shutdown. Nothing is
        force-killed; "down" names whatever is left.

    Unlike the Linux rig, the sessions use the owner's own Claude Code sign-in and ~/.claude: on Windows
    the agent's home is the user profile, and every Director session the owner runs shares it already.

    The screen-rule model key: pass -ScreenKeySecret devthrottle-api-key to "up" and the Gateway is started
    under "cc-secrets run", so the key goes from the secret store into the Gateway's environment (and from
    there into its key vault) without being printed or written to the .cmd.

.EXAMPLE
    .\scripts\session-rules-rig.ps1 build
    .\scripts\session-rules-rig.ps1 director -DirectorExe <downloaded cc-director.exe> -DirectorSha256 <hash>
    .\scripts\session-rules-rig.ps1 up -ScreenKeySecret devthrottle-api-key
    .\scripts\session-rules-rig.ps1 make-repos
    .\scripts\session-rules-rig.ps1 rules create-hook-probe repo -Repo github.com/session-rules-rig/repo-a -Match "echo RULE-PROBE"
    .\scripts\session-rules-rig.ps1 spawn <rig>\repos\repo-a -Name "Session Rules rig - repo A"
    .\scripts\session-rules-rig.ps1 prompt <session> "Run exactly this shell command and nothing else: echo RULE-PROBE"
    .\scripts\session-rules-rig.ps1 evidence <session>
    .\scripts\session-rules-rig.ps1 down
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('build', 'director', 'up', 'status', 'down', 'make-repos', 'cli', 'spawn', 'prompt', 'screen', 'rules', 'evidence', 'commands')]
    [string]$Command,
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Rest = @(),
    [string]$Rig = (Join-Path $env:LOCALAPPDATA "session-rules-rig-win"),
    [int]$Port = 7933,
    [int]$Slot = 6,
    [string]$DirectorExe = "",
    [string]$DirectorSha256 = "",
    [string]$ScreenKeySecret = "",
    [string]$Agent = "ClaudeCode",
    [string]$AgentCommand = "",
    [string]$Name = "",
    [string]$Repo = "",
    [string[]]$Machine = @(),
    [string[]]$ExceptMachine = @(),
    [string]$Match = "echo RULE-PROBE",
    [string]$Result = "deny",
    [string]$ExtraEnvironment = ""
)

$ErrorActionPreference = 'Stop'
$worktree = Split-Path -Parent $PSScriptRoot
$sep = [System.IO.Path]::DirectorySeparatorChar

function Say([string]$text) { Write-Host "[session-rules-rig] $text" }
function Fail([string]$text) { throw "[session-rules-rig] $text" }

$Rig = [System.IO.Path]::GetFullPath($Rig).TrimEnd($sep)
$realRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "cc-director")).TrimEnd($sep)
if ((Split-Path -Leaf $Rig) -notlike "*session-rules-rig*") { Fail "REFUSING: $Rig does not carry 'session-rules-rig' in its leaf name." }
if ($Rig -ieq $realRoot -or $Rig.StartsWith($realRoot + $sep, 'OrdinalIgnoreCase') -or $realRoot.StartsWith($Rig + $sep, 'OrdinalIgnoreCase')) {
    Fail "REFUSING: $Rig is the machine's real cc-director root, inside it, or around it."
}
if ($Port -eq 7878) { Fail "REFUSING: 7878 is the live Gateway's port." }
if ($Slot -lt 6) { Fail "REFUSING: slots below 6 belong to the owner and other seats." }

$root         = Join-Path $Rig "root"
$logs         = Join-Path $Rig "logs"
$repos        = Join-Path $Rig "repos"
$toolDir      = Join-Path $Rig "tool"
$gatewayDir   = Join-Path $Rig "builds\gateway"
$gatewayExe   = Join-Path $gatewayDir "CcDirector.Gateway.exe"
$localBuild   = Join-Path $worktree "scripts\local-build"
$directorPath = Join-Path $localBuild "cc-director$Slot.exe"
$tokenFile    = Join-Path $root "config\director\gateway-token.txt"
$instanceHome = Join-Path $root "instances\default"
$instConfig   = Join-Path $instanceHome "config\config.json"
$instRegDir   = Join-Path $instanceHome "config\director\instances"
$gatewayUrl   = "http://127.0.0.1:$Port"
$taskGateway  = "session-rules-rig-gateway"
$taskDirector = "session-rules-rig-director"
$toolPython   = Join-Path $realRoot "pyenv\Scripts\python.exe"
$machineName  = $env:COMPUTERNAME

function Get-RigProcesses {
    Get-Process | Where-Object {
        $_.Path -and ($_.Path -ieq $directorPath -or $_.Path.StartsWith($Rig + $sep, [System.StringComparison]::OrdinalIgnoreCase))
    }
}

function Read-Token {
    if (-not (Test-Path $tokenFile)) { Fail "no rig token at $tokenFile - run 'up' first." }
    return (Get-Content $tokenFile -Raw).Trim()
}

function Invoke-Gateway([string]$method, [string]$path, $body = $null) {
    $request = @{ Method = $method; Uri = ($gatewayUrl + $path); Headers = @{ Authorization = "Bearer $(Read-Token)" }; UseBasicParsing = $true; TimeoutSec = 30 }
    if ($null -ne $body) {
        $request.Body = [System.Text.Encoding]::UTF8.GetBytes($(if ($body -is [string]) { $body } else { $body | ConvertTo-Json -Depth 10 -Compress }))
        $request.ContentType = 'application/json; charset=utf-8'
    }
    try {
        $response = Invoke-WebRequest @request
        return [System.Text.Encoding]::UTF8.GetString($response.RawContentStream.ToArray())
    } catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($null -eq $r) { throw }
        $reader = New-Object System.IO.StreamReader($r.GetResponseStream())
        Fail "$method $path -> HTTP $([int]$r.StatusCode): $($reader.ReadToEnd())"
    }
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

function Register-RigTask([string]$name, [string]$cmdFile, [string]$workingDirectory) {
    $action   = New-ScheduledTaskAction -Execute "cmd.exe" -Argument "/c `"$cmdFile`"" -WorkingDirectory $workingDirectory
    $trigger  = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5)
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
}

function Get-DirectorRegistrations {
    if (-not (Test-Path $instRegDir)) { return @() }
    foreach ($f in Get-ChildItem $instRegDir -Filter *.json) {
        $d = Get-Content $f.FullName -Raw | ConvertFrom-Json
        $p = Get-Process -Id $d.Pid -ErrorAction SilentlyContinue
        [pscustomobject]@{ Id = ([string]$d.DirectorId).ToLowerInvariant(); Pid = $d.Pid; Alive = ($p -and $p.Path -ieq $directorPath) }
    }
}

# ---------------------------------------------------------------------------
function Invoke-Build {
    if (@(Get-RigProcesses).Count -gt 0) { Fail "the rig is running - 'down' first, so a build never replaces a running executable." }
    $commit = (git -C $worktree rev-parse HEAD).Trim()
    Say "publishing the console Gateway from $worktree at $commit -> $gatewayDir (no Cockpit, no mobile app, no web typecheck)"
    New-Item -ItemType Directory -Force -Path $gatewayDir | Out-Null
    & dotnet publish (Join-Path $worktree "src\CcDirector.Gateway\CcDirector.Gateway.csproj") -c Release `
        -p:RunMobileBuild=false -p:RunCockpitBuild=false -p:RunWorkspaceTypecheck=false -o $gatewayDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "Gateway publish failed with exit code $LASTEXITCODE" }
    if (-not (Test-Path $gatewayExe)) { Fail "the publish did not produce $gatewayExe" }

    Say "copying the worktree's cc-devthrottle -> $toolDir"
    foreach ($d in @("cc_devthrottle", "cc_shared")) { if (Test-Path (Join-Path $toolDir $d)) { Remove-Item -Recurse -Force (Join-Path $toolDir $d) } }
    New-Item -ItemType Directory -Force -Path $toolDir | Out-Null
    Copy-Item -Recurse (Join-Path $worktree "tools\cc-devthrottle\src") (Join-Path $toolDir "cc_devthrottle")
    Copy-Item -Recurse (Join-Path $worktree "tools\cc_shared") (Join-Path $toolDir "cc_shared")
    Say ("gateway: {0}  sha256 {1} (dll)" -f $gatewayExe, (Get-FileHash (Join-Path $gatewayDir "CcDirector.Gateway.dll") -Algorithm SHA256).Hash)
}

# The Director is not built here: it embeds the native evaluator, which needs C++ build tools. A build made
# elsewhere is copied in, and only after its SHA-256 matches the one the caller was given.
function Invoke-Director {
    if (-not $DirectorExe -or -not $DirectorSha256) { Fail "usage: director -DirectorExe <cc-director.exe> -DirectorSha256 <expected hash>" }
    if (@(Get-RigProcesses).Count -gt 0) { Fail "the rig is running - 'down' first." }
    $hash = (Get-FileHash $DirectorExe -Algorithm SHA256).Hash
    if ($hash -ne $DirectorSha256.ToUpperInvariant()) { Fail "SHA-256 of $DirectorExe is $hash, not the expected $DirectorSha256 - refusing to use it." }
    New-Item -ItemType Directory -Force -Path $localBuild | Out-Null
    Copy-Item $DirectorExe $directorPath -Force
    Say "Director copied to $directorPath (sha256 $hash, matches)"
}

# ---------------------------------------------------------------------------
function Write-DirectorConfig {
    $claude = (Get-Command claude.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
    if (-not $claude) { Fail "claude.exe is not on the PATH - the rig Director would have no Claude Code to start." }
    $config = [ordered]@{
        onboarding = @{ completed = $true }
        autoUpdate = @{ enabled = $false }
        gateway    = @{ url = $gatewayUrl; token = (Read-Token); streamMode = $true }
        agent      = @{ entries = @(
            [ordered]@{ id = "00000000-0000-0000-0000-00000000c1a0"; display_name = "Claude Code"; type = "ClaudeCode"; enabled = $true
                        executable_path = $claude.Source; preset_id = "Skip permissions"; default_model = ""; args_override = ""; launch_mode = "Guided" },
            [ordered]@{ id = "00000000-0000-0000-0000-00000000ba5e"; display_name = "Shell"; type = "RawCli"; enabled = $true
                        executable_path = (Join-Path ([Environment]::SystemDirectory) "cmd.exe"); preset_id = "Standard"; default_model = ""; args_override = ""; launch_mode = "Guided" }
        ) }
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $instConfig) | Out-Null
    Set-Content -Path $instConfig -Value ($config | ConvertTo-Json -Depth 10) -Encoding ascii
}

function Invoke-Up {
    if (-not (Test-Path $gatewayExe)) { Fail "no Gateway at $gatewayExe - run 'build' first." }
    if (-not (Test-Path $directorPath)) { Fail "no Director at $directorPath - run 'director' first." }
    if (@(Get-RigProcesses).Count -gt 0) { Fail "something already runs from the rig - run 'status' or 'down'." }
    try { Invoke-WebRequest "$gatewayUrl/healthz" -UseBasicParsing -TimeoutSec 2 | Out-Null; Fail "something already answers on port $Port." } catch [System.Net.WebException] { }
    New-Item -ItemType Directory -Force -Path $root, $logs | Out-Null

    $gatewayOut = Join-Path $logs "gateway.out"
    $launch = "`"$gatewayExe`" --port $Port"
    if ($ScreenKeySecret) {
        $secrets = (Get-Command cc-secrets -ErrorAction SilentlyContinue | Select-Object -First 1)
        if (-not $secrets) { Fail "cc-secrets is not on the PATH, so the screen-rule key cannot be supplied." }
        $launch = "call `"$($secrets.Source)`" run $ScreenKeySecret --via env --env-name DEVTHROTTLE_API_KEY --timeout 0 -- $launch"
        Say "screen-rule model key: from the secret store entry '$ScreenKeySecret' (never printed)"
    } else {
        Say "screen-rule model key: NONE supplied - hook rules work; a screen rule will record that the model could not be asked"
    }
    $gatewayCmd = Join-Path $Rig "rig-gateway.cmd"
    @(
        "@echo off",
        "set CC_DIRECTOR_ROOT=$root",
        "set CC_GATEWAY_NO_TAILSCALE=1",
        "set CC_AUTOUPDATE=0",
        "set CC_GATEWAY_NO_AUTH=",
        "set CC_GATEWAY_AUTH=",
        "cd /d `"$gatewayDir`"",
        "$launch > `"$gatewayOut`" 2>&1"
    ) | Set-Content -Path $gatewayCmd -Encoding ascii
    Say "starting the rig Gateway on $gatewayUrl (task $taskGateway, root $root)"
    Register-RigTask $taskGateway $gatewayCmd $gatewayDir
    Start-ScheduledTask -TaskName $taskGateway
    if (-not (Wait-Until "the Gateway's token file" { Test-Path $tokenFile } 120)) { Get-Content $gatewayOut -Tail 30 -ErrorAction SilentlyContinue; Fail "the Gateway never wrote its token" }
    $ready = Wait-Until "the Gateway to answer /sessions with the token" { try { Invoke-Gateway GET "/sessions" | Out-Null; $true } catch { $false } } 120
    if (-not $ready) { Get-Content $gatewayOut -Tail 30 -ErrorAction SilentlyContinue; Fail "the Gateway never became ready" }
    Say "Gateway ready: $((Invoke-WebRequest "$gatewayUrl/healthz" -UseBasicParsing).Content)"
    $unauth = try { (Invoke-WebRequest "$gatewayUrl/sessions" -UseBasicParsing -TimeoutSec 5).StatusCode } catch [System.Net.WebException] { [int]$_.Exception.Response.StatusCode }
    Say "auth check: /sessions WITHOUT the token answers $unauth (must not be 200)"
    if ($unauth -eq 200) { Fail "the rig Gateway answered without a token - auth is OFF; refusing to go on." }

    Write-DirectorConfig
    $directorCmd = Join-Path $Rig "rig-director.cmd"
    $directorLines = @(
        "@echo off",
        "set CC_DIRECTOR_ROOT=$root",
        "set CC_AUTOUPDATE=0"
    )
    # A variable for the Director and every session it starts, e.g. CLAUDE_CODE_GIT_BASH_PATH=<nothing there>.
    if ($ExtraEnvironment) { $directorLines += "set $ExtraEnvironment"; Say "extra Director environment: $ExtraEnvironment" }
    $directorLines += @("cd /d `"$localBuild`"", "start `"`" `"$directorPath`"")
    $directorLines | Set-Content -Path $directorCmd -Encoding ascii
    Say "starting the slot-$Slot test Director (task $taskDirector)"
    Register-RigTask $taskDirector $directorCmd $localBuild
    Start-ScheduledTask -TaskName $taskDirector
    if (-not (Wait-Until "the Director's instance registration" { @(Get-DirectorRegistrations | Where-Object Alive).Count -gt 0 } 180)) { Fail "the Director never registered; run 'down'." }
    $d = Get-DirectorRegistrations | Where-Object Alive | Select-Object -First 1
    $joined = Wait-Until "the rig Gateway to list Director $($d.Id)" { (Invoke-Gateway GET "/directors") -match $d.Id } 120
    if (-not $joined) { Fail "Director $($d.Id) never joined the rig Gateway; run 'down'." }
    Say ""
    Say "RIG IS UP"
    Say "  gateway:  $gatewayUrl  (token in $tokenFile)"
    Say "  director: $($d.Id)  process $($d.Pid)  slot $Slot  machine $machineName"
    Say "  root:     $root"
    Say "  logs:     $logs and $instanceHome\logs"
}

function Invoke-Status {
    foreach ($p in @(Get-RigProcesses)) { Say ("running: pid {0,-7} {1}" -f $p.Id, $p.Path) }
    foreach ($r in @(Get-DirectorRegistrations)) { Say "Director registration: $($r.Id) pid $($r.Pid) alive=$($r.Alive)" }
    $hook = Join-Path $instanceHome "claude-hooks\cc-rule-hook.exe"
    Say "cc-rule-hook installed: $(Test-Path $hook) ($hook)"
    try { Say "Gateway /healthz: $((Invoke-WebRequest "$gatewayUrl/healthz" -UseBasicParsing -TimeoutSec 3).Content)" } catch { Say "nothing answers on $gatewayUrl" }
}

function Invoke-Down {
    foreach ($r in @(Get-DirectorRegistrations)) {
        if (-not $r.Alive) { Say "Director $($r.Id): registration names process $($r.Pid), which is not the rig Director - nothing to stop"; continue }
        try {
            $evt = [System.Threading.EventWaitHandle]::OpenExisting("Local\cc-director-shutdown-$($r.Id)")
            try { [void]$evt.Set() } finally { $evt.Dispose() }
            Say "Director $($r.Id): named shutdown signal sent; waiting for process $($r.Pid)"
            if (Wait-Until "Director process $($r.Pid) to exit" { -not (Get-Process -Id $r.Pid -ErrorAction SilentlyContinue) } 90) { Say "Director process $($r.Pid) exited on its signal" }
            else { Say "Director process $($r.Pid) is STILL RUNNING after its signal - NOT forced" }
        } catch [System.Threading.WaitHandleCannotBeOpenedException] {
            Say "Director $($r.Id): nothing listens on its shutdown signal"
        }
    }
    if (Test-Path $tokenFile) {
        try {
            Invoke-Gateway POST "/shutdown" "{}" | Out-Null
            Say "Gateway: POST /shutdown sent"
            if (-not (Wait-Until "the Gateway to exit" { @(Get-RigProcesses | Where-Object { $_.Path -ieq $gatewayExe }).Count -eq 0 } 60)) { Say "Gateway is STILL RUNNING after /shutdown - NOT forced" }
        } catch { Say "Gateway shutdown not sent: $($_.Exception.Message)" }
    }
    foreach ($t in @($taskGateway, $taskDirector)) {
        if (Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $t -Confirm:$false; Say "task $t unregistered" }
    }
    $left = @(Get-RigProcesses)
    if ($left.Count -gt 0) { foreach ($p in $left) { Say ("STILL RUNNING: pid {0} {1}" -f $p.Id, $p.Path) }; Say "nothing was force-killed." }
    else { Say "rig is down: nothing runs from the rig" }
}

# ---------------------------------------------------------------------------
function Invoke-MakeRepos {
    foreach ($pair in @(@("repo-a", "https://github.com/session-rules-rig/repo-a.git"), @("repo-b", "git@github.com:session-rules-rig/repo-b.git"))) {
        $dir = Join-Path $repos $pair[0]
        if (-not (Test-Path (Join-Path $dir ".git"))) {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            git -C $dir init -q -b main
            git -C $dir remote add origin $pair[1]
            Set-Content -Path (Join-Path $dir "README.md") -Value "# $($pair[0]) - a scratch repository for the Session Rules live rig" -Encoding ascii
            git -C $dir add README.md
            git -C $dir -c user.name=rig -c user.email=rig@invalid commit -q -m "rig scratch repository"
        }
        Say "$dir  origin $(git -C $dir remote get-url origin)"
    }
}

# The worktree's own cc-devthrottle, acting with the rig Gateway's token and as no session.
function Invoke-Cli([string[]]$arguments) {
    if (-not (Test-Path (Join-Path $toolDir "cc_devthrottle"))) { Fail "no tool under $toolDir - run 'build' first." }
    foreach ($v in @("CC_SESSION_ID", "CC_DIRECTOR_ID", "CC_DIRECTOR_ROOT", "CC_SESSION_POINTER_FILE", "CC_SESSION_PREAMBLE_FILE", "CC_SESSION_KEY")) { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
    $env:PYTHONPATH = $toolDir
    $env:CC_GATEWAY_URL = $gatewayUrl
    $env:CC_GATEWAY_SESSION_KEY = Read-Token
    # An entry file rather than -c: Windows PowerShell 5.1 strips the double quotes inside a -c argument.
    $entry = Join-Path $toolDir "rig_cc_devthrottle.py"
    Set-Content -Path $entry -Encoding ascii -Value "import sys`nsys.argv[0] = 'cc-devthrottle'`nfrom cc_devthrottle.cli import tool_main`ntool_main()"
    & $toolPython $entry @arguments
}

function Invoke-Spawn {
    if ($Rest.Count -lt 1) { Fail "usage: spawn <dir> [-Agent ClaudeCode|RawCli] [-AgentCommand cmd] [-Name NAME]" }
    $dir = (Resolve-Path $Rest[0]).Path
    $d = Get-DirectorRegistrations | Where-Object Alive | Select-Object -First 1
    if (-not $d) { Fail "no rig Director - run 'up' first." }
    $label = if ($Name) { $Name } else { "Session Rules rig - $Agent - $(Split-Path -Leaf $dir)" }
    $arguments = @("session", "spawn", $dir, "--agent", $Agent, "--director", $d.Id, "--standalone", "--name", $label)
    if ($AgentCommand) { $arguments += @("--command", $AgentCommand) }
    Invoke-Cli $arguments
}

function New-HookProbeBody([string]$scope) {
    if ($scope -notin @("everywhere", "machine", "repo", "repo_on_machine")) { Fail "create-hook-probe <everywhere|machine|repo|repo_on_machine> ..." }
    $applies = [ordered]@{ kind = $scope }
    if ($Machine.Count) { $applies.machines = @($Machine) }
    if ($ExceptMachine.Count) { $applies.exceptMachines = @($ExceptMachine) }
    if ($Repo) { $applies.repo = $Repo }
    $verb = if ($Result -eq "deny") { "refused" } else { "allowed" }
    return [ordered]@{
        kind = "hook"; instruction = "Live rig probe: a shell command containing '$Match' is $verb ($scope)."
        appliesTo = $applies; event = "before_command"; tool = "shell"; handler = "match"; match = @($Match); result = $Result
    }
}

# The September usage-limit screen rule, unchanged from scripts/session-rules-rig.sh (and from the
# 2026-09-02 mission's qa-report.md, scoped to plain-shell sessions).
function New-UsageLimitBody {
    return [ordered]@{
        instruction = "If a session's screen says it has run out of its model allowance, type the command that shows me what is left."
        screenDescription = "A session that has stopped on a notice from its model provider saying its allowance for the current model is used up."
        triggerWords = @("limit", "usage-credits", "allowance", "out of credits")
        checks = @(@{ name = "matches_any"; arguments = @{ text = "<screen_text>"; terms = @("limit", "allowance") } })
        scope = @{ agent = "RawCli" }
        cooldownSeconds = 20
        dailyCap = 5
    }
}

function Invoke-Rules {
    $sub = if ($Rest.Count) { $Rest[0] } else { "" }
    $arg = if ($Rest.Count -gt 1) { $Rest[1] } else { "" }
    switch ($sub) {
        "list"        { Invoke-Gateway GET "/gateway/rules" }
        "get"         { Invoke-Gateway GET "/gateway/rules/$arg" }
        "firings"     { Invoke-Gateway GET "/gateway/rules/$arg/firings" }
        "delete"      { Invoke-Gateway DELETE "/gateway/rules/$arg" }
        "create"      { Invoke-Gateway POST "/gateway/rules" ([System.IO.File]::ReadAllText((Resolve-Path $arg).Path)) }
        "all-firings" {
            foreach ($r in (Invoke-Gateway GET "/gateway/rules" | ConvertFrom-Json).rules) { "== rule $($r.id)"; Invoke-Gateway GET "/gateway/rules/$($r.id)/firings" }
        }
        "effective"   {
            $m = if ($arg) { $arg } else { $machineName }
            $r = if ($Rest.Count -gt 2) { $Rest[2] } else { "" }
            Invoke-Gateway GET ("/gateway/rules/effective?kind=hook&machine=" + [uri]::EscapeDataString($m) + "&repo=" + [uri]::EscapeDataString($r))
        }
        "create-hook-probe"  { Invoke-Gateway POST "/gateway/rules" (New-HookProbeBody $arg) }
        "create-usage-limit" { Invoke-Gateway POST "/gateway/rules" (New-UsageLimitBody) }
        default { Fail "rules: list | get <id> | firings <id> | all-firings | create <json file> | create-hook-probe <scope> [-Repo R] [-Machine M] [-ExceptMachine M] [-Match TEXT] [-Result deny|allow] | create-usage-limit | delete <id> | effective [<machine> [<repo>]]" }
    }
}

# What the hook wrote: the per-session rules files (W2), the evaluation logs (W4) and decision files not yet
# forwarded (W3), read straight from the rig root. Read a session's log BEFORE closing it: it goes with it.
function Invoke-Evidence {
    $filter = if ($Rest.Count) { $Rest[0] } else { "" }
    "== rules files (W2):"
    Get-ChildItem $root -Recurse -Filter "*$filter*.json" -ErrorAction SilentlyContinue | Where-Object { $_.Directory.Name -eq "session-rules" } | ForEach-Object { "--- $($_.FullName)"; Get-Content $_.FullName -Raw }
    "== evaluation logs (W4):"
    Get-ChildItem $root -Recurse -Filter "*$filter*.eval.log" -ErrorAction SilentlyContinue | ForEach-Object { "--- $($_.FullName)"; Get-Content $_.FullName -Tail 50 }
    "== decision files waiting to be forwarded (W3):"
    Get-ChildItem $root -Recurse -Filter "*.json" -ErrorAction SilentlyContinue | Where-Object { $_.Directory.Name -eq "decisions" } | ForEach-Object { $_.FullName }
}

# Every shell command a Claude Code session under the rig's repositories ran, with its call id, read from
# Claude Code's own transcripts. The evaluation log ends each line with that call id (ruling P14).
function Invoke-Commands {
    $filter = if ($Rest.Count) { $Rest[0] } else { "" }
    $projects = Join-Path $env:USERPROFILE ".claude\projects"
    $prefix = ($repos -replace '[^A-Za-z0-9]', '-')
    Get-ChildItem $projects -Directory | Where-Object { $_.Name -like "$prefix*" -and $_.Name -like "*$filter*" } | ForEach-Object {
        foreach ($file in Get-ChildItem $_.FullName -Filter *.jsonl | Sort-Object LastWriteTime) {
            "--- $($file.FullName)"
            foreach ($line in Get-Content $file.FullName -Encoding UTF8) {
                try { $entry = $line | ConvertFrom-Json } catch { continue }
                if (-not $entry.message -or $entry.message.content -isnot [array]) { continue }
                foreach ($part in $entry.message.content) {
                    if ($part.type -eq "tool_use" -and $part.name -in @("Bash", "PowerShell")) { "$($entry.timestamp)  $($part.id)  $($part.name): $($part.input.command)" }
                    if ($part.type -eq "tool_result") { "$($entry.timestamp)  $($part.tool_use_id)  result: $(($part.content | Out-String).Trim())" }
                }
            }
        }
    }
}

switch ($Command) {
    'build'      { Invoke-Build }
    'director'   { Invoke-Director }
    'up'         { Invoke-Up }
    'status'     { Invoke-Status }
    'down'       { Invoke-Down }
    'make-repos' { Invoke-MakeRepos }
    'cli'        { Invoke-Cli $Rest }
    'spawn'      { Invoke-Spawn }
    'prompt'     { if ($Rest.Count -ne 2) { Fail "usage: prompt <session> <text>" }; Invoke-Cli @("session", "prompt", $Rest[0], $Rest[1]) }
    'screen'     { if ($Rest.Count -ne 1) { Fail "usage: screen <session>" }; Invoke-Cli @("session", "buffer", $Rest[0]) }
    'rules'      { Invoke-Rules }
    'evidence'   { Invoke-Evidence }
    'commands'   { Invoke-Commands }
}
