#Requires -Version 5.1
<#
.SYNOPSIS
    Checks that the gate's record locations are a writable per-user directory on THIS machine,
    whatever platform it is - issue #4806.

.DESCRIPTION
    scripts\gate-records.ps1 used to answer "/" for the gate's home on anything that was not Windows,
    so on a Mac or a Linux box the records landed at the filesystem ROOT: /test-gate/gated, and the
    runs log at /test-gate/runs.jsonl. Nobody can write there. The gate therefore failed at the very
    end, while recording that it had passed - after the whole suite had run.

    This asserts the property that matters and is otherwise only discovered at the end of an hour-long
    run: the directory the gate will write its record into can really be created and written, here,
    now. It writes only inside a throwaway subdirectory of that location and removes it again; it
    never reads or writes a real gate record.

    It starts no other process, but it does touch the real filesystem, so like its neighbours it is a
    script you run by hand rather than a test in the gate.

.EXAMPLE
    .\scripts\test\test-gate-records.ps1
#>
$ErrorActionPreference = "Stop"
. (Join-Path (Split-Path -Parent $PSScriptRoot) "gate-records.ps1")

$failures = 0
function Test-That([string] $What, [scriptblock] $Check) {
    try {
        $problem = & $Check
        if ([string]::IsNullOrWhiteSpace($problem)) {
            Write-Host "  PASS  $What"
        } else {
            Write-Host "  FAIL  $What - $problem"
            $script:failures++
        }
    } catch {
        Write-Host "  FAIL  $What - threw: $($_.Exception.Message)"
        $script:failures++
    }
}

$home_ = Get-GateHomeDirectory
$records = Get-GateRecordDirectory
$runsLog = Join-Path (Split-Path -Parent $records) "runs.jsonl"

Write-Host "gate home:    $home_"
Write-Host "records:      $records"
Write-Host "runs log:     $runsLog"
Write-Host ""

Test-That "the gate home is not the filesystem root" {
    $rooted = [System.IO.Path]::GetPathRoot($home_)
    if ($home_ -eq "/" -or $home_ -eq $rooted) { return "it is '$home_', which no ordinary user can write" }
    return $null
}

Test-That "the gate home is under the per-user application data directory" {
    $expected = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData, [Environment+SpecialFolderOption]::DoNotVerify)
    if ([string]::IsNullOrWhiteSpace($expected)) { return "this platform reports no per-user application data directory" }
    if (-not $home_.StartsWith($expected, [StringComparison]::Ordinal)) {
        return "it is '$home_', which is not under '$expected'"
    }
    return $null
}

Test-That "the runs log's own directory can be created and written" {
    # THE EXACT FAILURE: Add-Content to the runs log, which is what the gate does last.
    $dir = Split-Path -Parent $runsLog
    New-Item -ItemType Directory -Force $dir | Out-Null
    $probe = Join-Path $dir ("write-probe-" + [Guid]::NewGuid().ToString("N").Substring(0, 8) + ".jsonl")
    try {
        Add-Content -Path $probe -Value '{"probe":true}'
        if (-not (Test-Path $probe)) { return "Add-Content reported success but wrote nothing to $dir" }
        return $null
    } finally {
        if (Test-Path $probe) { Remove-Item -Force $probe }
    }
}

Test-That "the record directory can be created and written" {
    New-Item -ItemType Directory -Force $records | Out-Null
    $probe = Join-Path $records ("write-probe-" + [Guid]::NewGuid().ToString("N").Substring(0, 8) + ".json")
    try {
        Set-Content -Path $probe -Value '{"probe":true}'
        if (-not (Test-Path $probe)) { return "Set-Content reported success but wrote nothing to $records" }
        return $null
    } finally {
        if (Test-Path $probe) { Remove-Item -Force $probe }
    }
}

# =====================================================================================================
# WHICH SUITES THIS PLATFORM CANNOT RUN (issue #4806)
#
# The rule lives in scripts\platform-suites.ps1 so that it can be checked here. It is worth checking:
# the first draft normalized separators with a two-character literal that matched nothing, and the
# second read //TargetFramework out of the csproj, which under-detects in three ordinary shapes - each
# of which sends the gate back to NEVER RAN. The rule now asks MSBuild for the EVALUATED frameworks,
# and the last two cases below are the shapes that settled it. These run against made-up project files
# rather than the repository, so they say the same thing on every machine.
# =====================================================================================================
Write-Host ""
Write-Host "the platform rule:"
. (Join-Path (Split-Path -Parent $PSScriptRoot) "platform-suites.ps1")
$dotnetExe = Get-DotnetExecutable

$probeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("cc-platform-suites-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
try {
    function New-Project([string] $RelativePath, [string] $Frameworks, [string] $Element) {
        $full = Join-Path $probeRoot $RelativePath
        New-Item -ItemType Directory -Force (Split-Path -Parent $full) | Out-Null
        Set-Content -Path $full -Encoding Ascii -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <$Element>$Frameworks</$Element>
  </PropertyGroup>
</Project>
"@
        return $RelativePath
    }

    # Written with WINDOWS separators, exactly as the gate's own project list is - this is the case the
    # no-op normalization broke.
    $windowsOnly = New-Project "sub\winonly\WinOnly.csproj"   "net10.0-windows" "TargetFramework"
    $portable    = New-Project "sub\portable\Portable.csproj" "net10.0"         "TargetFramework"
    $multi       = New-Project "sub\multi\Multi.csproj"       "net10.0-windows;net10.0" "TargetFrameworks"
    $multiWin    = New-Project "sub\multiwin\MultiWin.csproj" "net10.0-windows;net9.0-windows" "TargetFrameworks"
    $noFramework = New-Project "sub\none\None.csproj"         ""                "PropertyWithNoFramework"

    $onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT

    Test-That "a project targeting Windows alone is held back" {
        $held = @(Get-SuitesThisPlatformCannotRun @($windowsOnly) $probeRoot)
        if ($onWindows) {
            if ($held.Count -ne 0) { return "on Windows nothing should ever be held back, but it named $($held -join ', ')" }
            return $null
        }
        if ($held.Count -ne 1) { return "expected it to be held back, got $($held.Count) suite(s)" }
        return $null
    }

    Test-That "a portable project is never held back" {
        $held = @(Get-SuitesThisPlatformCannotRun @($portable) $probeRoot)
        if ($held.Count -ne 0) { return "it named $($held -join ', ')" }
        return $null
    }

    Test-That "a multi-targeted project with a portable target is not held back" {
        $held = @(Get-SuitesThisPlatformCannotRun @($multi) $probeRoot)
        if ($held.Count -ne 0) { return "it named $($held -join ', '), but its net10.0 target runs here" }
        return $null
    }

    Test-That "a project whose every target is a Windows one is held back" {
        $held = @(Get-SuitesThisPlatformCannotRun @($multiWin) $probeRoot)
        $expected = if ($onWindows) { 0 } else { 1 }
        if ($held.Count -ne $expected) { return "expected $expected, got $($held.Count)" }
        return $null
    }

    Test-That "a platform-versioned Windows framework is still recognised as Windows-only" {
        # net10.0-windows10.0.17763.0 is a real spelling in this repository (tools/cc-computer). The
        # substring test has to catch it, or such a project is run here and the gate reports NEVER RAN.
        $versioned = New-Project "sub\versioned\Versioned.csproj" "net10.0-windows10.0.17763.0" "TargetFramework"
        $held = @(Get-SuitesThisPlatformCannotRun @($versioned) $probeRoot)
        $expected = if ($onWindows) { 0 } else { 1 }
        if ($held.Count -ne $expected) { return "expected $expected held back, got $($held.Count)" }
        return $null
    }

    Test-That "a project declaring no target framework is run, not held back" {
        # Cannot tell is not the same as cannot run. A real failure beats a silent omission.
        $held = @(Get-SuitesThisPlatformCannotRun @($noFramework) $probeRoot)
        if ($held.Count -ne 0) { return "it named $($held -join ', ')" }
        return $null
    }

    Test-That "a project path that does not exist is run, not held back" {
        $held = @(Get-SuitesThisPlatformCannotRun @("sub\absent\Absent.csproj") $probeRoot)
        if ($held.Count -ne 0) { return "it named $($held -join ', ')" }
        return $null
    }

    Test-That "a hand-concatenated Windows-spelled path still reaches the project" {
        # THE REGRESSION, asserted on the property that matters rather than on the mechanism.
        #
        # The first draft of the rule normalized with "\\" - two backslashes in PowerShell - so the
        # normalization was a no-op. It passed anyway, and the reason is worth pinning: Join-Path ITSELF
        # rewrites Windows separators on a Unix host, so a Join-Path caller never exercises the
        # normalization at all. This case builds the path by hand so the backslashes genuinely survive,
        # which is the only way the rule's own Replace is reached.
        $spelled = $probeRoot + [IO.Path]::DirectorySeparatorChar + "sub\winonly\WinOnly.csproj"
        # IndexOf rather than Contains(char): string.Contains(char) does not exist on .NET Framework,
        # which #Requires -Version 5.1 admits. It was reached only behind the -not $onWindows
        # short-circuit, which is the same "works by accident" shape this file exists to catch.
        if (-not $onWindows -and $spelled.IndexOf([char] 92) -lt 0) {
            return "this case proves nothing - the path it built has no backslash in it"
        }
        if (-not (Test-ProjectIsWindowsOnly $spelled $dotnetExe)) {
            return "the rule could not read '$spelled', so a hand-built Windows-spelled path does not reach the project"
        }
        return $null
    }

    Test-That "a framework inherited from Directory.Build.props is still seen" {
        # SHAPE (a). The csproj declares no framework at all - it comes from a sibling property file.
        # A rule that reads the csproj's own XML sees nothing here, answers "cannot tell", runs the
        # project, and the gate reports NEVER RAN. Asking MSBuild resolves the import.
        $root = Join-Path $probeRoot "inherited"
        New-Item -ItemType Directory -Force (Join-Path $root "sub") | Out-Null
        Set-Content -Encoding Ascii -Path (Join-Path $root "Directory.Build.props") -Value `
            '<Project><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup></Project>'
        Set-Content -Encoding Ascii -Path (Join-Path $root "sub\Inherited.csproj") -Value `
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>'

        $frameworks = Get-ProjectTargetFrameworks (Join-Path $root "sub\Inherited.csproj") $dotnetExe
        if ($frameworks -notcontains "net10.0-windows") {
            return "expected net10.0-windows from the property file, got '$($frameworks -join ", ")'"
        }
        $held = @(Get-SuitesThisPlatformCannotRun @("inherited\sub\Inherited.csproj") $probeRoot)
        $expected = if ($onWindows) { 0 } else { 1 }
        if ($held.Count -ne $expected) { return "expected $expected held back, got $($held.Count)" }
        return $null
    }

    Test-That "a project carrying the old 2003 default namespace is still seen" {
        # SHAPE (b). //TargetFramework matches nothing under a default XML namespace, so a textual rule
        # reads this project as declaring no framework and runs it - NEVER RAN again.
        $root = Join-Path $probeRoot "oldns"
        New-Item -ItemType Directory -Force $root | Out-Null
        Set-Content -Encoding Ascii -Path (Join-Path $root "Legacy.csproj") -Value `
            '<Project Sdk="Microsoft.NET.Sdk" xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup></Project>'

        $frameworks = Get-ProjectTargetFrameworks (Join-Path $root "Legacy.csproj") $dotnetExe
        if ($frameworks -notcontains "net10.0-windows") {
            return "expected net10.0-windows despite the 2003 namespace, got '$($frameworks -join ", ")'"
        }
        return $null
    }

    # ---- issue #4883: the rule must FIND the software development kit, and refuse loudly when it cannot ----

    # These cases CHANGE THE ENVIRONMENT and restore it. The earlier drafts did not, which is why they
    # proved less than their names said: "found without relying on the PATH" never removed the PATH, and
    # the refusal case passed an explicit bogus path, so it exercised the file check and never the lookup
    # returning nothing.
    function Invoke-WithEnvironment([hashtable] $Set, [scriptblock] $Body) {
        $saved = @{}
        foreach ($k in $Set.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k) }
        try {
            foreach ($k in $Set.Keys) {
                if ($null -eq $Set[$k]) { Remove-Item -Path ("Env:" + $k) -ErrorAction SilentlyContinue }
                else { Set-Item -Path ("Env:" + $k) -Value $Set[$k] }
            }
            return & $Body
        } finally {
            foreach ($k in $Set.Keys) {
                if ($null -eq $saved[$k]) { Remove-Item -Path ("Env:" + $k) -ErrorAction SilentlyContinue }
                else { Set-Item -Path ("Env:" + $k) -Value $saved[$k] }
            }
        }
    }

    Test-That "the kit is found with NOTHING on the PATH and no DOTNET_ROOT" {
        # THE #4883 REGRESSION, with the PATH actually taken away. The first version invoked a bare
        # "dotnet"; on this fleet's Mac the kit is at ~/.dotnet and is not on the session PATH, so every
        # query failed and the fail-open rule held NOTHING back.
        if ($onWindows) { return $null }
        $found = Invoke-WithEnvironment @{ PATH = "/nonexistent-for-this-test"; DOTNET_ROOT = $null; DOTNET_HOST_PATH = $null } {
            Get-DotnetExecutable
        }
        if ([string]::IsNullOrWhiteSpace($found)) { return "nothing was found with the PATH removed, so the ~/.dotnet fallback is not working" }
        if ($found -notmatch "\.dotnet") { return "it found '$found', which is not the ~/.dotnet fallback" }
        return $null
    }

    Test-That "a kit is accepted only if it ANSWERS, not merely because the file exists" {
        # ROUND TWO OF #4883. A runtime-only install is a real file that prints "A compatible .NET SDK
        # was not found" and exits 145. The first repair checked only Test-Path, so such a kit was
        # accepted, every project came back unestablished, and 0 were held back with nothing thrown -
        # the original defect by another door. Worse, a dead ~/.dotnet outranked a working PATH kit.
        if ($onWindows) { return $null }   # the shim below is a shell script
        $dead = Join-Path $probeRoot "deadkit"
        New-Item -ItemType Directory -Force $dead | Out-Null
        $deadExe = Join-Path $dead "dotnet"
        Set-Content -Path $deadExe -Encoding Ascii -Value "#!/bin/sh`necho 'A compatible .NET SDK was not found.' >&2`nexit 145"
        & /bin/chmod +x $deadExe

        if (Test-DotnetAnswers $deadExe) { return "a runtime-only kit was accepted as able to answer" }

        # And it must not outrank a working kit: with DOTNET_ROOT pointing at the dead one, the lookup
        # has to pass over it and still find a working kit.
        $found = Invoke-WithEnvironment @{ DOTNET_ROOT = $dead; DOTNET_HOST_PATH = $null } { Get-DotnetExecutable }
        if ($found -eq $deadExe) { return "the dead kit at DOTNET_ROOT was chosen over a working one" }
        if ([string]::IsNullOrWhiteSpace($found)) { return "passing over the dead kit found nothing at all" }
        return $null
    }

    Test-That "a SYMLINK's folder is never called a DOTNET_ROOT" {
        # ROUND THREE OF #4883, and the only finding so far that would make a machine WORSE than before
        # the change. An apphost honours DOTNET_ROOT literally and does not look through a symbolic link,
        # and a link is the ORDINARY install shape: Microsoft's package links /usr/local/bin/dotnet,
        # Homebrew links /opt/homebrew/bin/dotnet, Ubuntu links /usr/bin/dotnet. Measured on this machine:
        # DOTNET_ROOT at the real root, a framework-dependent executable exits 0; DOTNET_ROOT at a folder
        # holding only a symlink to that same kit, exit 131. So the folder must hold "shared" to count.
        if ($onWindows) { return $null }
        if ([string]::IsNullOrWhiteSpace($dotnetExe)) { return "no kit to link to" }

        $linkDir = Join-Path $probeRoot "linkdir"
        New-Item -ItemType Directory -Force $linkDir | Out-Null
        $link = Join-Path $linkDir "dotnet"
        $realExe = Resolve-DotnetRealPath $dotnetExe
        & /bin/ln -s $realExe $link
        if (-not (Test-Path -LiteralPath $link)) { return "could not create the symlink to test with" }

        # It must still be usable - the link is a fine thing to INVOKE.
        if (-not (Test-DotnetAnswers $link)) { return "the symlinked kit could not answer, so this case proves nothing" }

        # ...but its folder is not a root, and must not be offered as one.
        $root = Get-DotnetRootFolder $link
        if ($root -eq $linkDir) { return "the symlink's own folder '$linkDir' was called a root; it holds no 'shared'" }

        # It should resolve THROUGH the link to the real root, which does hold 'shared'.
        $expected = Split-Path -Parent $realExe
        if ($root -ne $expected) { return "expected the real root '$expected', got '$root'" }
        return $null
    }

    Test-That "a folder with no shared subfolder is never called a DOTNET_ROOT" {
        # The positive rule, held to its word: only a folder that can be PROVED to be a root counts.
        if ($onWindows) { return $null }
        $bare = Join-Path $probeRoot "barekit"
        New-Item -ItemType Directory -Force $bare | Out-Null
        $bareExe = Join-Path $bare "dotnet"
        Set-Content -Path $bareExe -Encoding Ascii -Value "#!/bin/sh`necho 10.0.403"
        & /bin/chmod +x $bareExe
        if ($null -ne (Get-DotnetRootFolder $bareExe)) { return "a folder holding no 'shared' was called a root" }
        return $null
    }

    Test-That "a kit older than version 8 is rejected, because -getProperty needs 8" {
        # It would PASS a bare --version check and then fail every query with MSB1001, which produced the
        # right refusal with the wrong explanation. Rejecting it here says why.
        if ($onWindows) { return $null }
        $old = Join-Path $probeRoot "oldkit"
        New-Item -ItemType Directory -Force $old | Out-Null
        $oldExe = Join-Path $old "dotnet"
        Set-Content -Path $oldExe -Encoding Ascii -Value "#!/bin/sh`necho 6.0.400"
        & /bin/chmod +x $oldExe
        if (Test-DotnetAnswers $oldExe) { return "a version 6 kit was accepted, but -getProperty needs 8" }
        return $null
    }

    Test-That "a kit that prints a banner before its version is still accepted" {
        # The probe reads the LAST non-empty line of --version, not the whole output joined. This kit
        # does not print a banner today, and under a fresh DOTNET_CLI_HOME it still prints the version
        # alone - but a joined string would be unparseable if that ever changed, and rejecting a good
        # kit is the failure this whole issue is about.
        if ($onWindows) { return $null }
        $banner = Join-Path $probeRoot "bannerkit"
        New-Item -ItemType Directory -Force (Join-Path $banner "shared") | Out-Null
        $bannerExe = Join-Path $banner "dotnet"
        Set-Content -Path $bannerExe -Encoding Ascii -Value "#!/bin/sh`necho 'Welcome to .NET!'`necho '----------------'`necho 10.0.403"
        & /bin/chmod +x $bannerExe
        if (-not (Test-DotnetAnswers $bannerExe)) { return "a kit printing a banner before its version was rejected" }
        return $null
    }

    Test-That "the kit it finds can really answer a framework query" {
        $frameworks = Get-ProjectTargetFrameworks (Join-Path $probeRoot $windowsOnly) $dotnetExe
        if ($frameworks -notcontains "net10.0-windows") {
            return "the executable at '$dotnetExe' returned '$($frameworks -join ", ")' instead of net10.0-windows"
        }
        return $null
    }

    Test-That "NO working kit anywhere REFUSES rather than answering that nothing is held back" {
        # Proved through the LOOKUP returning nothing rather than through an explicit bogus path. HOME is
        # pointed at an empty folder so the ~/.dotnet fallback misses too.
        if ($onWindows) { return $null }
        $emptyHome = Join-Path $probeRoot "emptyhome"
        New-Item -ItemType Directory -Force $emptyHome | Out-Null
        return Invoke-WithEnvironment @{ PATH = "/nonexistent-for-this-test"; HOME = $emptyHome; DOTNET_ROOT = $null; DOTNET_HOST_PATH = $null } {
            try {
                $held = @(Get-SuitesThisPlatformCannotRun @($windowsOnly) $probeRoot)
                return "returned $($held.Count) suite(s) instead of throwing"
            } catch {
                if ($_.Exception.Message -notmatch "no working .NET") { return "threw the wrong thing: $($_.Exception.Message)" }
                return $null
            }
        }
    }

    Test-That "every project failing is a broken instrument and REFUSES" {
        # The second line behind the probe. Two or more asked and not one answered cannot be two or more
        # unreadable projects; it is the instrument. One project is allowed, because it cannot be told
        # apart from a project that is simply absent - which an earlier case pins.
        if ($onWindows) { return $null }
        try {
            $held = @(Get-SuitesThisPlatformCannotRun @("sub\absent\A.csproj", "sub\absent\B.csproj") $probeRoot)
            return "returned $($held.Count) suite(s) instead of throwing for 2 of 2 unestablished"
        } catch {
            if ($_.Exception.Message -notmatch "broken instrument") { return "threw the wrong thing: $($_.Exception.Message)" }
            return $null
        }
    }

    Test-That "the decision reports which projects could not be established" {
        if ($onWindows) { return $null }
        $decision = Get-PlatformSuiteDecision @($windowsOnly, "sub\absent\Absent.csproj") $probeRoot
        if ($decision.Unestablished -notcontains "sub\absent\Absent.csproj") {
            return "the absent project is not named in Unestablished: '$($decision.Unestablished -join ", ")'"
        }
        if ($decision.HeldBack -notcontains $windowsOnly) {
            return "the Windows-only project is not in HeldBack: '$($decision.HeldBack -join ", ")'"
        }
        return $null
    }

    Test-That "an empty project list is answered with an empty set" {
        $held = @(Get-SuitesThisPlatformCannotRun @() $probeRoot)
        if ($held.Count -ne 0) { return "it named $($held -join ', ')" }
        return $null
    }
} finally {
    if (Test-Path $probeRoot) { Remove-Item -Recurse -Force $probeRoot }
}

Write-Host ""
if ($failures -gt 0) {
    Write-Host "RESULT: $failures case(s) FAILED."
    exit 1
}
Write-Host "RESULT: the gate can record its run on this machine, and the platform rule holds."
exit 0
