#Requires -Version 7
<#
.SYNOPSIS
    How much time does the Session Rules hook add to every shell command?

.DESCRIPTION
    Claude Code runs cc-rule-hook before EVERY shell command of every Director session (Session Rules
    part 2, phase 1, ruling P9), so its cost is paid per command. The Director registers the hook in EXEC
    form (ClaudeHookInstaller: the absolute path as the command, an empty args list), so Claude Code starts
    the program itself with no shell in front of it, on every platform. This script does the same - it
    starts the evaluator directly, with no shell - with a real Claude Code PreToolUse event on standard
    input, many times, and prints the median and the 95th percentile for:

      baseline        a plain program started directly - what any exec-form hook costs before it does anything
      through a shell the shell starting the baseline program (Git Bash -c /usr/bin/true on Windows, /bin/sh -c
                      /bin/true elsewhere), for comparison only: what the earlier SHELL-form registration paid.
                      Not paid since the change to exec form.
      no-session      the hook outside a Director session (no CC_SESSION_RULES_FILE): it does nothing
      no-rules-file   a Director session whose rules file is not there yet
      0 rules         a rules file with an empty rules list
      1 rule          one rule, which matches, so a decision file is written every run
      50 rules        fifty rules; the last matches, so all fifty are checked and a decision is written

    The hook's ADDED time is its row minus the baseline row (not the through-a-shell row). Every run is started the same way, so the
    cost of starting a process from PowerShell is in every row, the baseline included, and cancels out of
    the difference. Everything is written under a scratch folder deleted at the end.

    On Windows Git for Windows is still needed, but only for the two reference rows: the baseline program is
    its true.exe, and the through-a-shell row is its bash.exe starting that true.exe. Git Bash is found as Claude Code finds it:
    CLAUDE_CODE_GIT_BASH_PATH when set, otherwise the bash.exe of the Git for Windows that provides git on
    the PATH. Neither present is an error. The hook rows never go through it.

.PARAMETER Hook
    The cc-rule-hook executable. Defaults to the Release publish output, publishing it first if absent.

.PARAMETER Runs
    Runs per row. Defaults to 300.

.EXAMPLE
    .\scripts\measure-rule-hook.ps1
    .\scripts\measure-rule-hook.ps1 -Hook C:\path\cc-rule-hook.exe -Runs 500
#>
param(
    [string]$Hook = "",
    [int]$Runs = 300,
    [string]$EventFile = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $EventFile) {
    # The real event for this platform, captured from the installed Claude Code.
    $fixture = if ($IsWindows) { "claude-code-2.1.302-windows-pretooluse-bash.json" } else { "claude-code-2.1.302-pretooluse-bash.json" }
    $EventFile = Join-Path $repoRoot "src/CcDirector.RuleHook.Tests/Fixtures/$fixture"
}
if (-not (Test-Path -LiteralPath $EventFile)) { throw "No event file at $EventFile" }
$eventText = [System.IO.File]::ReadAllText($EventFile)

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$archName = switch ($arch) { "X64" { "x64" } "Arm64" { "arm64" } default { throw "Unsupported architecture $arch" } }
if ($IsWindows) { $rid = "win-$archName"; $exeName = "cc-rule-hook.exe" }
elseif ($IsMacOS) { $rid = "osx-$archName"; $exeName = "cc-rule-hook" }
else { $rid = "linux-$archName"; $exeName = "cc-rule-hook" }

if (-not $Hook) {
    $Hook = Join-Path $repoRoot "src/CcDirector.RuleHook/bin/Release/net10.0/$rid/publish/$exeName"
    if (-not (Test-Path -LiteralPath $Hook)) {
        Write-Host "Publishing cc-rule-hook for $rid..."
        & dotnet publish (Join-Path $repoRoot "src/CcDirector.RuleHook/CcDirector.RuleHook.csproj") -c Release -r $rid --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    }
}
if (-not (Test-Path -LiteralPath $Hook)) { throw "No hook executable at $Hook" }
$Hook = (Resolve-Path -LiteralPath $Hook).Path

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("measure-rule-hook-" + [guid]::NewGuid().ToString("N"))
$rulesDir = Join-Path $scratch "session-rules"
$decisionDir = Join-Path $scratch "decisions"
New-Item -ItemType Directory -Path $rulesDir, $decisionDir | Out-Null

# The reference rows' programs. The hook rows start $Hook itself, as the exec-form registration does.
$gitBash = $null
if ($IsWindows) {
    if ($env:CLAUDE_CODE_GIT_BASH_PATH) {
        $gitBash = $env:CLAUDE_CODE_GIT_BASH_PATH
    } else {
        $git = Get-Command git.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $git) { throw "git.exe is not on the PATH, so Git Bash cannot be found. Install Git for Windows or set CLAUDE_CODE_GIT_BASH_PATH to its bash.exe." }
        # git.exe may be <Git>\cmd, <Git>\bin or <Git>\mingw64\bin depending on the PATH; its exec path is always
        # <Git>/mingw64/libexec/git-core, three levels below the install, and bash.exe is <Git>\bin\bash.exe.
        $execPath = (& $git.Source --exec-path).Trim()
        if ($LASTEXITCODE -ne 0) { throw "git --exec-path failed with exit code $LASTEXITCODE" }
        $gitRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $execPath))
        $gitBash = Join-Path $gitRoot "bin\bash.exe"
    }
    if (-not (Test-Path -LiteralPath $gitBash)) { throw "No Git Bash at $gitBash. Install Git for Windows or set CLAUDE_CODE_GIT_BASH_PATH to its bash.exe." }
    # true.exe is <Git>\usr\bin\true.exe. The Git Bash may be <Git>\usr\bin\bash.exe (a valid
    # CLAUDE_CODE_GIT_BASH_PATH), with true.exe beside it, or <Git>\bin\bash.exe, two levels below <Git>.
    $besideBash = Join-Path (Split-Path -Parent $gitBash) "true.exe"
    $underGitRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $gitBash)) "usr\bin\true.exe"
    $trueProgram = @($besideBash, $underGitRoot) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $trueProgram) { throw "No true.exe for the Git Bash at $gitBash. Looked at $besideBash and at $underGitRoot." }
    $shellProgram = $gitBash
    # The same true.exe, by the path Git Bash itself knows it by.
    $trueInShell = "/usr/bin/true"
} else {
    $trueProgram = "/bin/true"
    $shellProgram = "/bin/sh"
    $trueInShell = $trueProgram
}

function Write-Rules([int]$count, [string]$path) {
    $rules = for ($i = 1; $i -le $count; $i++) {
        # The LAST rule matches the event's command (echo RULE-PROBE).
        $text = if ($i -eq $count) { "echo RULE-PROBE" } else { "never-matches-$i" }
        [ordered]@{ id = "rule-$i"; rank = 3; appliesTo = "repo"; event = "before_command"; tool = "shell";
                    match = @($text); result = "deny"; reason = "measurement rule"; state = "dry_run" }
    }
    $file = [ordered]@{ version = 1; sessionId = "measure"; machine = [Environment]::MachineName;
                        repo = "github.com/example/measure"; generatedUtc = "2026-10-18T00:00:00.0000000Z";
                        rules = @($rules) }
    [System.IO.File]::WriteAllText($path, ($file | ConvertTo-Json -Depth 5))
}

# One run of a program started directly, no shell, the event on standard input. Returns elapsed milliseconds.
function Invoke-Once([string]$program, [string[]]$arguments, [hashtable]$environment) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $program
    foreach ($argument in $arguments) { $psi.ArgumentList.Add($argument) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.Environment.Remove("CC_SESSION_RULES_FILE") | Out-Null
    $psi.Environment.Remove("CC_SESSION_DECISION_DIR") | Out-Null
    foreach ($key in $environment.Keys) { $psi.Environment[$key] = $environment[$key] }

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start($psi)
    # The baseline command never reads its input and may exit before the event is written; the broken
    # pipe that leaves is expected for it, and the exit code below still has to be 0.
    try { $process.StandardInput.Write($eventText); $process.StandardInput.Close() }
    catch [System.IO.IOException] { }
    catch [System.Management.Automation.MethodInvocationException] { if ($_.Exception.InnerException -isnot [System.IO.IOException]) { throw } }
    $stdout = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    $watch.Stop()
    if ($process.ExitCode -ne 0) { throw "exit code $($process.ExitCode) from: $program $arguments" }
    if ($stdout.Length -gt 0) { throw "the hook printed to standard output, which it must never do: $stdout" }
    return $watch.Elapsed.TotalMilliseconds
}

function Measure-Row([string]$label, [string]$program, [string[]]$arguments, [hashtable]$environment) {
    Invoke-Once $program $arguments $environment | Out-Null
    Invoke-Once $program $arguments $environment | Out-Null
    $samples = for ($i = 0; $i -lt $Runs; $i++) { Invoke-Once $program $arguments $environment }
    $sorted = $samples | Sort-Object
    $median = $sorted[[math]::Ceiling($Runs / 2) - 1]
    $p95 = $sorted[[math]::Ceiling($Runs * 0.95) - 1]
    "{0,-16} median {1,7:N2} ms   95th percentile {2,7:N2} ms" -f $label, $median, $p95
}

function Session([string]$rulesFile) { @{ CC_SESSION_RULES_FILE = $rulesFile; CC_SESSION_DECISION_DIR = $decisionDir } }

try {
    Write-Host "cc-rule-hook: $Hook ($((Get-Item -LiteralPath $Hook).Length) bytes)"
    Write-Host "event: $EventFile"
    Write-Host "baseline program: $trueProgram; through-a-shell row: $shellProgram -c $trueInShell"
    Write-Host "runs per row: $Runs, on $([Environment]::MachineName), $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription), $([Environment]::ProcessorCount) processors"
    Write-Host ""

    # The baseline starts a real program directly, as every hook row does, so the difference is the hook's own work.
    Measure-Row "baseline" $trueProgram @() @{}
    Measure-Row "through a shell" $shellProgram @("-c", $trueInShell) @{}
    Measure-Row "no-session" $Hook @() @{}
    Measure-Row "no-rules-file" $Hook @() (Session (Join-Path $rulesDir "absent.json"))
    Write-Rules 0 (Join-Path $rulesDir "zero.json")
    Measure-Row "0 rules" $Hook @() (Session (Join-Path $rulesDir "zero.json"))
    Write-Rules 1 (Join-Path $rulesDir "one.json")
    Measure-Row "1 rule" $Hook @() (Session (Join-Path $rulesDir "one.json"))
    Write-Rules 50 (Join-Path $rulesDir "fifty.json")
    Measure-Row "50 rules" $Hook @() (Session (Join-Path $rulesDir "fifty.json"))

    # A presence check, so a row cannot have measured a hook that silently did nothing.
    $decisions = @(Get-ChildItem -LiteralPath $decisionDir -Filter *.json).Count
    $expected = 2 * ($Runs + 2)
    Write-Host ""
    Write-Host "decision files written: $decisions (expected $expected from the 1-rule and 50-rule rows)"
    foreach ($name in "absent", "zero", "one", "fifty") {
        $log = Get-Content -LiteralPath (Join-Path $rulesDir "$name.eval.log")
        Write-Host "evaluation log ${name}: $($log.Count) lines, last: $($log[-1])"
    }
    if ($decisions -ne $expected) { throw "the hook did not record what it was measured doing" }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
