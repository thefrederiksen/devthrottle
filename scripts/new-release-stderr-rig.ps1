#Requires -Version 5.1
<#
.SYNOPSIS
    Runs new-release.ps1 -Claim and -Release -Yes end to end under Windows PowerShell 5.1 against a
    throwaway local remote whose git writes ordinary lines to stderr, and says whether the release
    completed.

.DESCRIPTION
    Issues #4330 and #4331: under Windows PowerShell 5.1, -Release -Yes stopped whenever git wrote a
    normal line to stderr - the CRLF warning from git add, the "remote:" hint after git push - although
    git exited 0. It happened on v2.24.0, v2.28.0 and v2.32.0, and each time a seat finished the tag, the
    tag push, the merge-back pull request and the claim release by hand.

    This rig reproduces both lines on purpose:
      - the release checkout sets core.autocrlf=true and the notes file has LF line endings, so git add
        warns "LF will be replaced by CRLF";
      - the bare remote has a post-receive hook that writes to stderr, so every push prints a
        "remote:" line.
    gh is replaced by a stand-in compiled here, which answers the calls the script makes (and writes to
    stderr too), so nothing reaches GitHub. Nothing outside the work folder is touched: no real remote,
    no real claim branch, no real tag.

    It is not a gate test: it spawns powershell.exe and git, which a gate test may not. Run it by hand
    after changing how new-release.ps1 calls git or gh. Point -ScriptUnderTest at an older copy of the
    script to watch that one stop.

    Exit 0 = PASS, exit 1 = FAIL.

.EXAMPLE
    powershell.exe -NoProfile -File scripts\new-release-stderr-rig.ps1
    git show origin/main:scripts/new-release.ps1 > $env:TEMP\old-new-release.ps1
    powershell.exe -NoProfile -File scripts\new-release-stderr-rig.ps1 -ScriptUnderTest $env:TEMP\old-new-release.ps1
#>
param(
    [string]$ScriptUnderTest = (Join-Path $PSScriptRoot "new-release.ps1"),
    # The shell the script under test runs in. powershell.exe (5.1) is where it broke; pwsh.exe checks 7.
    [string]$Shell = "powershell.exe",
    [string]$WorkRoot = (Join-Path ([System.IO.Path]::GetTempPath()) "new-release-stderr-rig-$([guid]::NewGuid().ToString('N').Substring(0, 8))")
)

# The rig runs git itself, so it holds itself to the rule it tests: exit codes decide, stderr never does.
$ErrorActionPreference = "Continue"
$version = "1.1.0"
$tagName = "v$version"

function Fail-Rig($message) {
    Write-Host ""
    Write-Host "RIG FAIL: $message" -ForegroundColor Red
    Write-Host "Work folder kept for inspection: $WorkRoot"
    exit 1
}

function Rig-Git {
    $lines = & git @args 2>&1
    if ($LASTEXITCODE -ne 0) { Fail-Rig "git $($args -join ' ') exited $LASTEXITCODE`n$($lines -join "`n")" }
    $lines | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }
}

function Write-LfFile($path, $text) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [System.IO.File]::WriteAllText($path, ($text -replace "`r`n", "`n"))
}

if (-not (Test-Path $ScriptUnderTest)) { Fail-Rig "No script at $ScriptUnderTest." }
$shellPath = (Get-Command $Shell -ErrorAction SilentlyContinue).Source
if (-not $shellPath) { Fail-Rig "$Shell is not on PATH." }

New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
Write-Host "Work folder: $WorkRoot" -ForegroundColor Gray
$origin  = Join-Path $WorkRoot "origin.git"
$seed    = Join-Path $WorkRoot "seed"
$release = Join-Path $WorkRoot "release"
$fakeBin = Join-Path $WorkRoot "fake-gh"

# --- A bare remote that writes to stderr on every push, the way GitHub's "Create a pull request" hint does ---
Rig-Git init --quiet --bare -b main $origin | Out-Null
Write-LfFile (Join-Path $origin "hooks\post-receive") "#!/bin/sh`necho 'Create a pull request for this branch by visiting: (rig hint)' >&2`n"

# --- The repository: a released v1.0.0, then one new commit to release ---
Rig-Git init --quiet -b main $seed | Out-Null
Rig-Git -C $seed config user.name "release rig" | Out-Null
Rig-Git -C $seed config user.email "rig@example.invalid" | Out-Null
Rig-Git -C $seed config core.autocrlf false | Out-Null
Write-LfFile (Join-Path $seed "Directory.Build.props") "<Project>`n  <PropertyGroup>`n    <Version>1.0.0</Version>`n  </PropertyGroup>`n</Project>`n"
New-Item -ItemType Directory -Force -Path (Join-Path $seed "scripts") | Out-Null
Copy-Item -Path $ScriptUnderTest -Destination (Join-Path $seed "scripts\new-release.ps1")
Rig-Git -C $seed add -A | Out-Null
Rig-Git -C $seed commit --quiet -m "the v1.0.0 code" | Out-Null
Rig-Git -C $seed tag v1.0.0 | Out-Null
Write-LfFile (Join-Path $seed "work.txt") "new work since v1.0.0`n"
Rig-Git -C $seed add -A | Out-Null
Rig-Git -C $seed commit --quiet -m "new work since v1.0.0" | Out-Null
Rig-Git -C $seed push --quiet $origin main --tags | Out-Null

# --- The release checkout: autocrlf on and LF notes, so git add warns about CRLF ---
Rig-Git clone --quiet $origin $release | Out-Null
Rig-Git -C $release config user.name "release rig" | Out-Null
Rig-Git -C $release config user.email "rig@example.invalid" | Out-Null
Rig-Git -C $release config core.autocrlf true | Out-Null
$notes = "# DevThrottle $tagName`n`n" + ("This is a release written by the release rig. It stands in for the real notes and is long enough to clear the two hundred character floor that the script and the workflow both apply to a notes file.`n" * 2)
Write-LfFile (Join-Path $release "docs\public\release-notes\$tagName.md") $notes
$headSha = @(Rig-Git -C $release rev-parse origin/main)[0]

# --- A stand-in gh: answers the calls new-release.ps1 makes, writes to stderr like the real one, logs each call ---
New-Item -ItemType Directory -Force -Path $fakeBin | Out-Null
[System.IO.File]::WriteAllText((Join-Path $fakeBin "head.txt"), $headSha)
$fakeGhSource = @'
using System;
using System.IO;
public static class FakeGh {
    public static int Main(string[] args) {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        File.AppendAllText(Path.Combine(dir, "calls.log"), string.Join(" ", args) + Environment.NewLine);
        if (args.Length == 0) return 9;
        if (args[0] == "auth") { Console.Error.WriteLine("Logged in to github.com (rig stand-in)"); return 0; }
        string head = File.ReadAllText(Path.Combine(dir, "head.txt")).Trim();
        if (args[0] == "api" && args.Length > 1) {
            // Green only for the real head commit, asked for by name: an empty or wrong sha (a variable that did
            // not reach the call) is refused, so the release stops instead of passing.
            if (args[1].Contains("check-runs")) {
                if (args[1].Contains("/commits/" + head + "/check-runs?")) { Console.WriteLine("success"); return 0; }
                Console.Error.WriteLine("rig stand-in: no such commit in " + args[1]);
                return 1;
            }
            if (args[1].Contains("workflows/ci.yml/runs")) { Console.WriteLine(head); return 0; }
            return 9;
        }
        if (args[0] == "pr" && args.Length > 1) {
            // The merge-back must name the release branch, or it is merging something else.
            if (!Array.Exists(args, a => a == "release/v1.1.0")) { Console.Error.WriteLine("rig stand-in: pr call without the release branch"); return 1; }
            Console.Error.WriteLine("rig stand-in: gh pr " + args[1] + " done");
            if (args[1] == "create") Console.WriteLine("https://example.invalid/pull/1");
            return 0;
        }
        return 9;
    }
}
'@
Add-Type -TypeDefinition $fakeGhSource -OutputAssembly (Join-Path $fakeBin "gh.exe") -OutputType ConsoleApplication
if (-not (Test-Path (Join-Path $fakeBin "gh.exe"))) { Fail-Rig "Could not build the stand-in gh.exe." }

$env:PATH = "$fakeBin;$env:PATH"
$env:CC_SESSION_ID = "release-rig"
$scriptInRelease = Join-Path $release "scripts\new-release.ps1"

function Invoke-ReleaseScript([string[]]$scriptArgs) {
    Write-Host ""
    # Called the way a session's shell calls it: inside a PowerShell session whose output is captured.
    # Under -File with the console's stderr, git writes straight to the handle it inherited and 5.1 never
    # sees the line, so the fault does not show. Capturing the script's output is what makes 5.1 read
    # native stderr and wrap each line as a NativeCommandError - and that is how every seat ran it.
    Write-Host ">>> ${Shell}: & new-release.ps1 $($scriptArgs -join ' ') 2>&1 (output captured)" -ForegroundColor Yellow
    $call = "& '$scriptInRelease' $($scriptArgs -join ' ') 2>&1 | ForEach-Object { `$_.ToString() }; exit `$LASTEXITCODE"
    $output = & $shellPath -NoProfile -ExecutionPolicy Bypass -Command $call 2>&1
    $code = $LASTEXITCODE
    $text = ($output | ForEach-Object { $_.ToString() }) -join "`n"
    Write-Host $text
    Write-Host "<<< exit code $code" -ForegroundColor Yellow
    return [pscustomobject]@{ Code = $code; Text = $text }
}

$claimRun = Invoke-ReleaseScript @("-Claim", "-Version", $version)
$releaseRun = Invoke-ReleaseScript @("-Release", "-Version", $version, "-Yes")

# --- Verdict: every check is a PRESENCE, so an empty or skipped run cannot pass ---
$problems = @()
$both = "$($claimRun.Text)`n$($releaseRun.Text)"
if ($both -notmatch "remote: Create a pull request") { $problems += "No 'remote:' line was seen, so the push stderr this rig exists to produce never happened." }
if ($releaseRun.Text -notmatch "LF will be replaced by CRLF") { $problems += "No CRLF warning was seen, so the git add stderr this rig exists to produce never happened." }
if ($claimRun.Code -ne 0) { $problems += "-Claim exited $($claimRun.Code), expected 0." }
if ($releaseRun.Code -ne 0) { $problems += "-Release -Yes exited $($releaseRun.Code), expected 0." }
if ($releaseRun.Text -notmatch "Released $([regex]::Escape($tagName))\.") { $problems += "-Release never printed 'Released $tagName.'" }
$remoteRefs = (& git -C $release ls-remote $origin 2>$null) -join "`n"
if ($remoteRefs -notmatch "refs/tags/$([regex]::Escape($tagName))") { $problems += "The tag $tagName is not on the remote." }
if ($remoteRefs -notmatch "refs/heads/release/$([regex]::Escape($tagName))") { $problems += "The branch release/$tagName is not on the remote." }
if ($remoteRefs -match "refs/heads/release-claim") { $problems += "The claim branch release-claim is still on the remote: the claim was never given up." }
$ghCalls = Get-Content (Join-Path $fakeBin "calls.log") -ErrorAction SilentlyContinue
if (-not ($ghCalls -match "^pr create ")) { $problems += "gh pr create was never called: the merge-back pull request was not opened." }
if (-not ($ghCalls -match "^pr merge ")) { $problems += "gh pr merge was never called: the merge-back pull request was not merged." }

Write-Host ""
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Fail-Rig "$($problems.Count) problem(s)."
}
Write-Host "RIG PASS: -Claim and -Release -Yes completed under $Shell through a CRLF warning and 'remote:' lines; tag, branch, merge-back and claim release all done." -ForegroundColor Green
Remove-Item -Recurse -Force $WorkRoot
exit 0
