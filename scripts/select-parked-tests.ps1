#Requires -Version 5.1
<#
.SYNOPSIS
    Decide WHICH TEST CLASSES of the parked suites a change could break, so the default gate runs them.

.DESCRIPTION
    Two parked suites - CcDirector.Gateway.UnitTests (about 14,000 tests, about fourteen minutes) and
    CcDirector.Core.Tests (about 4,500 tests, about a minute and a half) - do not fit the default gate's
    two-minute budget, so until October 2026 a change that touched them was only WARNED about, and the
    warning was ignored: on 16 October 2026 a change broke two Gateway unit test classes, merged on a
    green default run, and turned main red for a day, which blocked every release behind it.

    select-tests.ps1 answers which SUITES a change can reach, from the project reference graph. For these
    two that answer is almost always yes, because nearly everything references CcDirector.Core. This
    script narrows it to CLASSES, by reading the code:

      - every type (class, record, struct, interface, enum) declared in a changed .cs file, before or
        after the change, is a CHANGED TYPE;
      - a test file is HIT when it is itself changed, or when its text names a changed type;
      - a changed file that is not C# (SQL, JSON, a script the code embeds) hits the test files that name
        it. When none does, nothing can be ruled out and the whole suite runs.

    The classes the hit files declare are the answer. A class name that turns out not to hold tests
    matches nothing in the filter and costs nothing.

    IT FAILS TOWARD RUNNING MORE. When select-tests.ps1 cannot reason about the change (a project file,
    a build-wide property, the gate itself, an unrecognised path) the WHOLE suite is selected, and so it
    is when the class list grows too long for one command line. A wrong selection that runs too much
    costs minutes; one that skips costs a red main.

    WHAT THIS DOES NOT COVER, said plainly: a test that reaches the changed code without naming any type
    itself - through a shared harness or base class that names it, reflection, or a string. Following
    the harnesses was measured and selects the whole suite (see ONE HOP below). Those reach main and are caught by the scheduled full run on GitHub every two
    hours. CcDirector.Gateway.Tests, the third parked suite, is host-bound and holds the machine's one
    release-gate lock, so it is not selected here: test-local.ps1 still warns about it.

.PARAMETER Base
    The ref to diff against. Defaults to origin/main.

.PARAMETER Files
    An explicit list of changed paths instead of a diff, for replaying historical changes.

.PARAMETER Explain
    Print which types were followed and which test files were hit.

.EXAMPLE
    .\scripts\select-parked-tests.ps1 -Explain
    .\scripts\select-parked-tests.ps1 -Base HEAD~1 -Explain
#>
param(
    [string]$Base = "origin/main",
    [string[]]$Files,
    [switch]$Explain
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

# The parked suites this script selects classes in. Gateway.Tests is deliberately absent (see the header).
$selectable = @(
    [pscustomobject]@{ Name = "CcDirector.Gateway.UnitTests"; Project = "src\CcDirector.Gateway.UnitTests\CcDirector.Gateway.UnitTests.csproj" },
    [pscustomobject]@{ Name = "CcDirector.Core.Tests";        Project = "src\CcDirector.Core.Tests\CcDirector.Core.Tests.csproj" }
)

# Above this many classes the filter is replaced by the whole suite: a dotnet test command line holds at most
# 32,767 characters, and each class costs about fifty in the filter.
$MaxClasses = 400

$declaration = [regex]'\b(?:class|record|struct|interface|enum)\s+([A-Z][A-Za-z0-9_]*)'

# The same diff select-tests.ps1 reads, so the two scripts always reason about the same change.
$selArgs = @{ Base = $Base }
if ($PSBoundParameters.ContainsKey('Files')) { $selArgs.Files = $Files }
$suites = & (Join-Path $PSScriptRoot "select-tests.ps1") @selArgs
$changed = @($suites.Changed | ForEach-Object { $_ -replace '\\', '/' })

# A git call whose error output is expected (a path absent at the merge base). Windows PowerShell 5.1 turns a
# redirected native error line into a terminating error under "Stop", so the preference is lowered for exactly
# this call; the exit code is the answer.
function Invoke-GitQuiet([string[]] $GitArgs) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $out = @(& git -C $repoRoot @GitArgs 2>$null)
        return [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Text = ($out -join "`n") }
    } finally { $ErrorActionPreference = $previous }
}

$mergeBase = $null
if (-not $PSBoundParameters.ContainsKey('Files')) {
    $mb = Invoke-GitQuiet @("merge-base", $Base, "HEAD")
    if ($mb.Ok) { $mergeBase = $mb.Text.Trim() }
}

# A file's text as the change left it, and as it was before. A deleted file has only the before; a new one
# only the after. Both count, because a test naming a type that was removed or renamed is affected too.
function Get-FileVersions([string] $Path) {
    $texts = @()
    $full = Join-Path $repoRoot $Path
    if (Test-Path -LiteralPath $full) { $texts += [System.IO.File]::ReadAllText($full) }
    if ($mergeBase) {
        # A file the change ADDED has no before; git says so on its error output, which is not a failure here.
        $old = Invoke-GitQuiet @("show", "${mergeBase}:$Path")
        if ($old.Ok -and $old.Text) { $texts += $old.Text }
    }
    return $texts
}

# Every test file of a suite: the files under its own folder, and the files its project compiles in from
# elsewhere (Gateway.UnitTests links in more than a dozen shared helpers from Gateway.Tests).
function Get-SuiteFiles($Suite) {
    $projPath = Join-Path $repoRoot $Suite.Project
    $projDir = Split-Path -Parent $projPath
    $list = @(Get-ChildItem -LiteralPath $projDir -Filter *.cs -Recurse |
              Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName })
    $projText = [System.IO.File]::ReadAllText($projPath)
    foreach ($m in [regex]::Matches($projText, '<Compile\s+Include="([^"]+\.cs)"')) {
        $linked = [System.IO.Path]::GetFullPath((Join-Path $projDir $m.Groups[1].Value))
        if (Test-Path -LiteralPath $linked) { $list += $linked }
    }
    return @($list | Select-Object -Unique | ForEach-Object {
        [pscustomobject]@{
            Path     = $_.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
            Text     = [System.IO.File]::ReadAllText($_)
        }
    })
}

$results = @()
foreach ($suite in $selectable) {
    if ($suite.Name -notin @($suites.Suites)) { continue }

    $reasons = New-Object System.Collections.Generic.List[string]
    $whole = $false
    if ($suites.RunAll) {
        $whole = $true
        $reasons.Add("select-tests cannot reason about this change, so nothing can be ruled out")
    }

    $testFiles = Get-SuiteFiles $suite
    $byPath = @{}
    foreach ($tf in $testFiles) { $byPath[$tf.Path] = $tf.Text }
    $hit = @{}
    $types = New-Object 'System.Collections.Generic.HashSet[string]'

    if (-not $whole) {
        foreach ($f in $changed) {
            if (-not $f.StartsWith("src/")) { continue }
            if ($f -match '\.cs$') {
                # A changed test file runs itself AND reaches the test files that use what it declares - a changed
                # rig or fake is a change to every test built on it.
                if ($byPath.ContainsKey($f)) { $hit[$f] = "changed itself" }
                $before = $types.Count
                foreach ($t in (Get-FileVersions $f)) {
                    foreach ($m in $declaration.Matches($t)) { [void]$types.Add($m.Groups[1].Value) }
                }
                if ($types.Count -eq $before) {
                    # Top-level statements, global usings, assembly attributes: a change no type name leads from.
                    $whole = $true
                    $reasons.Add("$f changed and declares no type a test could name, so nothing can be ruled out")
                    break
                }
            } elseif ($f -notmatch '\.md$') {
                # Not C#: the tests that read it name it. The file name without its folder, and without its
                # last extension, so a test that builds the path from parts is still found.
                $leaf = Split-Path -Leaf $f
                $stem = [System.IO.Path]::GetFileNameWithoutExtension($leaf)
                $namers = @($testFiles | Where-Object { $_.Text.Contains($leaf) -or ($stem.Length -ge 4 -and $_.Text.Contains($stem)) })
                if ($namers.Count -eq 0) {
                    $whole = $true
                    $reasons.Add("$f changed and no test file names it, so nothing can be ruled out")
                    break
                }
                foreach ($n in $namers) { if (-not $hit.ContainsKey($n.Path)) { $hit[$n.Path] = "names $leaf" } }
            }
        }
    }

    if (-not $whole -and $types.Count -gt 0) {
        # ONE HOP, NOT A CLOSURE. Following the types a hit test file declares, until nothing new is hit, was
        # tried first and measured on the change that turned main red on 16 October 2026: it reached 1,045
        # of the Gateway unit suite's classes - the whole suite - because every test file declares helpers
        # that other test files name. The direct rule picked 142 classes, both broken ones among them.
        $pattern = [regex]('\b(' + (($types | Sort-Object | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\b')
        foreach ($tf in $testFiles) {
            if ($hit.ContainsKey($tf.Path)) { continue }
            $m = $pattern.Match($tf.Text)
            if ($m.Success) { $hit[$tf.Path] = "names $($m.Groups[1].Value)" }
        }
    }

    $classes = @()
    if (-not $whole) {
        # Only files that HOLD tests name classes to run: a helper, rig or fake has none, and a filter naming only
        # those would collect nothing and end the gate as a suite that never ran.
        $classes = @($hit.Keys | Where-Object { $byPath[$_] -match '\[[A-Za-z]*(Fact|Theory)\b' } | ForEach-Object {
            [regex]::Matches($byPath[$_], '\bclass\s+([A-Z][A-Za-z0-9_]*)') | ForEach-Object { $_.Groups[1].Value }
        } | Sort-Object -Unique)
        if ($classes.Count -gt $MaxClasses) {
            $whole = $true
            $reasons.Add("$($classes.Count) classes are affected, more than one filter can carry ($MaxClasses)")
            $classes = @()
        }
    }

    if ($Explain) {
        Write-Host "$($suite.Name):"
        if ($whole) { foreach ($r in $reasons) { Write-Host "  WHOLE SUITE - $r" } }
        else {
            Write-Host "  $($hit.Count) test file(s) hit, $($classes.Count) class(es):"
            foreach ($k in ($hit.Keys | Sort-Object)) { Write-Host "    $k - $($hit[$k])" }
        }
        Write-Host ""
    }

    $results += [pscustomobject]@{
        Suite   = $suite.Name
        Project = $suite.Project
        Whole   = $whole
        Classes = $classes
        Reasons = @($reasons)
    }
}

# The machine-readable answer for test-local.ps1: one entry per parked suite that has something to run.
# A suite the change cannot reach, or reaches with no test file hit, is absent.
$results | Where-Object { $_.Whole -or $_.Classes.Count -gt 0 }
