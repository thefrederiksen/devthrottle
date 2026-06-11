# generate-large-repo-fixture.ps1
#
# Creates a throwaway git repository with:
#   - 2,000+ modified/untracked files spread across subdirectories
#   - At least one 10,000-line diff (a tracked file that is heavily modified)
#
# Usage:
#   powershell -NoProfile -File scripts\generate-large-repo-fixture.ps1 [-OutPath <dir>]
#
# If -OutPath is omitted, the repo is created under %TEMP%\ccd-fixture-<guid>.
# The script prints the repo path on the last line so callers can capture it.
#
# Cleanup: Remove-Item -Recurse -Force <OutPath>
#
# This fixture is used by the Source Control tab review (issue #334) to exercise
# responsiveness and correctness on a large working tree.

param(
    [string]$OutPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($OutPath -eq "") {
    $OutPath = Join-Path $env:TEMP ("ccd-fixture-" + [guid]::NewGuid().ToString("N"))
}

if (Test-Path $OutPath) {
    Remove-Item -Recurse -Force $OutPath
}
New-Item -ItemType Directory -Path $OutPath | Out-Null

function Run-Git {
    param([string[]]$GitArgs)
    # Run git from the repo directory so we don't need -C (Windows PS 5.1 compat)
    Push-Location $OutPath
    try {
        $prevPref = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        $null = & git @GitArgs 2>$null
        $ec = $LASTEXITCODE
        $ErrorActionPreference = $prevPref
        if ($ec -ne 0) {
            throw "git $($GitArgs -join ' ') failed (exit $ec)"
        }
    }
    finally {
        Pop-Location
    }
}

Write-Host "[fixture] Initialising git repo at $OutPath"
$initResult = & git init $OutPath 2>&1
if ($LASTEXITCODE -ne 0) { throw "git init failed: $initResult" }
Run-Git @("config", "user.email", "fixture@cc-director.local") | Out-Null
Run-Git @("config", "user.name", "CC Director Fixture") | Out-Null
Run-Git @("config", "commit.gpgsign", "false") | Out-Null

# --- Step 1: create a tracked file with 10,000 lines and commit it --------
$bigFile = Join-Path $OutPath "large-tracked.txt"
$lines = 1..10000 | ForEach-Object { "Line $_ - original content $(([guid]::NewGuid().ToString('N')))" }
[System.IO.File]::WriteAllLines($bigFile, $lines)
Run-Git @("add", "large-tracked.txt") | Out-Null
Run-Git @("commit", "-m", "initial: add large-tracked.txt") | Out-Null

# --- Step 2: modify large-tracked.txt to produce a 10,000-line diff -------
Write-Host "[fixture] Writing 10,000-line diff on large-tracked.txt"
$modifiedLines = 1..10000 | ForEach-Object { "Line $_ - MODIFIED $([guid]::NewGuid().ToString('N'))" }
[System.IO.File]::WriteAllLines($bigFile, $modifiedLines)
# large-tracked.txt is now a tracked modified file (unstaged)

# --- Step 3: create 2,000 untracked files across subdirectories -----------
Write-Host "[fixture] Creating 2,000 untracked files"
$fileCount = 0

# src/ subtree - 800 files across 8 directories
$srcDirs = @("src/alpha", "src/beta", "src/gamma", "src/delta",
             "src/epsilon", "src/zeta", "src/eta", "src/theta")
foreach ($dir in $srcDirs) {
    $fullDir = Join-Path $OutPath $dir
    New-Item -ItemType Directory -Path $fullDir -Force | Out-Null
    1..100 | ForEach-Object {
        $path = Join-Path $fullDir "file-$_.cs"
        [System.IO.File]::WriteAllText($path, "// Generated file $_ in $dir`npublic class C$_ {}")
        $fileCount++
    }
}

# tests/ subtree - 400 files across 4 directories
$testDirs = @("tests/unit", "tests/integration", "tests/e2e", "tests/perf")
foreach ($dir in $testDirs) {
    $fullDir = Join-Path $OutPath $dir
    New-Item -ItemType Directory -Path $fullDir -Force | Out-Null
    1..100 | ForEach-Object {
        $path = Join-Path $fullDir "test-$_.cs"
        [System.IO.File]::WriteAllText($path, "// Test $_ in $dir`n[Fact] public void Test$_() {}")
        $fileCount++
    }
}

# docs/ subtree - 400 files
$docsDirs = @("docs/api", "docs/guides", "docs/reference", "docs/examples")
foreach ($dir in $docsDirs) {
    $fullDir = Join-Path $OutPath $dir
    New-Item -ItemType Directory -Path $fullDir -Force | Out-Null
    1..100 | ForEach-Object {
        $path = Join-Path $fullDir "page-$_.md"
        [System.IO.File]::WriteAllText($path, "# Page $_ in $dir")
        $fileCount++
    }
}

# tools/ subtree - 200 files
$toolsDirs = @("tools/build", "tools/deploy")
foreach ($dir in $toolsDirs) {
    $fullDir = Join-Path $OutPath $dir
    New-Item -ItemType Directory -Path $fullDir -Force | Out-Null
    1..100 | ForEach-Object {
        $path = Join-Path $fullDir "script-$_.ps1"
        [System.IO.File]::WriteAllText($path, "# Script $_ in $dir")
        $fileCount++
    }
}

# config/ subtree - 200 files
$configDirs = @("config/dev", "config/prod")
foreach ($dir in $configDirs) {
    $fullDir = Join-Path $OutPath $dir
    New-Item -ItemType Directory -Path $fullDir -Force | Out-Null
    1..100 | ForEach-Object {
        $path = Join-Path $fullDir "config-$_.json"
        [System.IO.File]::WriteAllText($path, "{`"id`": $_}")
        $fileCount++
    }
}

# --- Step 4: stage 50 files, leave the rest untracked --------------------
Write-Host "[fixture] Staging 50 files from src/alpha"
$alphaDir = "src/alpha"
1..50 | ForEach-Object {
    Run-Git @("add", "$alphaDir/file-$_.cs") | Out-Null
}

# Verification: count via git status --porcelain
Push-Location $OutPath
$statusOutput = (& git status "--porcelain=v1" "-u" 2>$null) -join "`n"
Pop-Location
$totalLines = ($statusOutput -split "`n" | Where-Object { $_ -ne "" }).Count

Write-Host "[fixture] Done. Total porcelain lines: $totalLines (files: $fileCount untracked + 1 modified + 50 staged)"
Write-Host "[fixture] Path: $OutPath"

# Output the path as the last line for scripted capture
Write-Output $OutPath
