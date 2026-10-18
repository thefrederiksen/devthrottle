#Requires -Version 7
<#
.SYNOPSIS
    How much time does the Session Rules hook add to every shell command?

.DESCRIPTION
    Claude Code runs cc-rule-hook before EVERY shell command of every Director session (Session Rules
    part 2, phase 1, ruling P9), so its cost is paid per command. This script runs the EXACT command the
    Director registers - the quoted absolute path, handed to the system shell as one command string
    (cmd.exe on Windows, /bin/sh elsewhere) - with a real Claude Code PreToolUse event on standard input,
    many times, and prints the median and the 95th percentile for:

      baseline        a plain shell command - what any hook costs before it does anything
      no-session      the hook outside a Director session (no CC_SESSION_RULES_FILE): it does nothing
      no-rules-file   a Director session whose rules file is not there yet
      0 rules         a rules file with an empty rules list
      1 rule          one rule, which matches, so a decision file is written every run
      50 rules        fifty rules; the last matches, so all fifty are checked and a decision is written

    The hook's ADDED time is its row minus the baseline row. Every run is started the same way, so the
    cost of starting a process from PowerShell is in every row, the baseline included, and cancels out of
    the difference. Everything is written under a scratch folder deleted at the end.

    NOT PROVEN: that Claude Code on Windows runs a hook command string through cmd.exe. If it uses Git
    Bash or PowerShell instead, the baseline row is the one that changes; the hook rows still measure the
    hook.

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
    $EventFile = Join-Path $repoRoot "src/CcDirector.RuleHook.Tests/Fixtures/claude-code-2.1.302-pretooluse-bash.json"
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

# The command string the Director registers (ClaudeHookInstaller): the absolute path, quoted.
$hookCommand = '"' + $Hook + '"'

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

# One run of a command string through the system shell, the event on standard input. Returns elapsed ticks.
function Invoke-Once([string]$commandString, [hashtable]$environment) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    if ($IsWindows) {
        $psi.FileName = "cmd.exe"
        $psi.ArgumentList.Add("/d"); $psi.ArgumentList.Add("/s"); $psi.ArgumentList.Add("/c")
        $psi.ArgumentList.Add($commandString)
    } else {
        $psi.FileName = "/bin/sh"
        $psi.ArgumentList.Add("-c"); $psi.ArgumentList.Add($commandString)
    }
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
    if ($process.ExitCode -ne 0) { throw "exit code $($process.ExitCode) from: $commandString" }
    if ($stdout.Length -gt 0) { throw "the hook printed to standard output, which it must never do: $stdout" }
    return $watch.Elapsed.TotalMilliseconds
}

function Measure-Row([string]$label, [string]$commandString, [hashtable]$environment) {
    Invoke-Once $commandString $environment | Out-Null
    Invoke-Once $commandString $environment | Out-Null
    $samples = for ($i = 0; $i -lt $Runs; $i++) { Invoke-Once $commandString $environment }
    $sorted = $samples | Sort-Object
    $median = $sorted[[math]::Ceiling($Runs / 2) - 1]
    $p95 = $sorted[[math]::Ceiling($Runs * 0.95) - 1]
    "{0,-16} median {1,7:N2} ms   95th percentile {2,7:N2} ms" -f $label, $median, $p95
}

function Session([string]$rulesFile) { @{ CC_SESSION_RULES_FILE = $rulesFile; CC_SESSION_DECISION_DIR = $decisionDir } }

try {
    Write-Host "cc-rule-hook: $Hook ($((Get-Item -LiteralPath $Hook).Length) bytes)"
    Write-Host "event: $EventFile"
    Write-Host "runs per row: $Runs, on $([Environment]::MachineName), $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription), $([Environment]::ProcessorCount) processors"
    Write-Host ""

    $baseline = if ($IsWindows) { "rem" } else { "true" }
    Measure-Row "baseline" $baseline @{}
    Measure-Row "no-session" $hookCommand @{}
    Measure-Row "no-rules-file" $hookCommand (Session (Join-Path $rulesDir "absent.json"))
    Write-Rules 0 (Join-Path $rulesDir "zero.json")
    Measure-Row "0 rules" $hookCommand (Session (Join-Path $rulesDir "zero.json"))
    Write-Rules 1 (Join-Path $rulesDir "one.json")
    Measure-Row "1 rule" $hookCommand (Session (Join-Path $rulesDir "one.json"))
    Write-Rules 50 (Join-Path $rulesDir "fifty.json")
    Measure-Row "50 rules" $hookCommand (Session (Join-Path $rulesDir "fifty.json"))

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
