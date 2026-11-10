#Requires -Version 5.1
<#
.SYNOPSIS
    Which test projects this operating system cannot RUN, so the gate can name them instead of silently
    dropping them or pretending they passed (issues #4806 and #4883).

.DESCRIPTION
    Some test projects target Windows ALONE. With EnableWindowsTargeting they BUILD on a Mac or a Linux
    box, but a Windows-targeted assembly cannot EXECUTE there: the run reports zero tests, and
    test-local.ps1's own honesty check calls that "NEVER RAN" and refuses a verdict on anything.

    IT ASKS MSBUILD RATHER THAN READING THE XML. Reading TargetFramework out of the csproj sees only what
    that one file says, so it misses a framework set in Directory.Build.props or an imported .props, a
    project carrying the old xmlns="http://schemas.microsoft.com/developer/msbuild/2003" default
    namespace, and a conditional PropertyGroup. Each of those sends the gate back to NEVER RAN.

        dotnet msbuild <csproj> -getProperty:TargetFrameworks -getProperty:TargetFramework

    returns the EVALUATED values, with imports, property files and conditions resolved for THIS operating
    system. About 0.2 seconds a project, 3 seconds for the fifteen the gate lists, paid only where it is
    needed. Needs software development kit 8 or later for -getProperty, and does NOT need
    EnableWindowsTargeting, because evaluating a property does not trip NETSDK1100.

    IT PROVES THE KIT ANSWERS, RATHER THAN THAT A FILE EXISTS (#4883, round two). The first version
    invoked a bare "dotnet", which on this fleet's Mac is not on the PATH at all - so every query failed,
    every answer came back empty, the fail-open rule read that as "cannot tell", and NOTHING was held
    back. The second version looked the executable up but only checked that the FILE was there. A
    runtime-only install - what dotnet-install.sh writes when asked for a runtime - is a real file that
    prints "A compatible .NET SDK was not found" and exits 145, so the same defect came straight back by
    another door: measured 15 projects, 0 held back, nothing thrown. Worse, a dead ~/.dotnet outranked a
    working kit on the PATH.

    So each candidate is PROBED and a candidate that cannot answer is passed over. That makes the
    ordering safe: an explicitly configured kit still wins, but only if it works.

    AND "CANNOT TELL" IS THREE THINGS, NOT ONE, because they must not fail the same way:

      - A PROJECT that cannot be evaluated is reported by name and RUN. A real failure beats a silent
        omission, and the gate's NEVER RAN check is the backstop.
      - EVERY project in a list of two or more failing is a broken instrument, not a coincidence, and
        throws. The probe should have caught it; this is the second line.
      - NO WORKING KIT AT ALL throws. There is no honest answer to "which suites can this platform run"
        without one, and an empty answer is indistinguishable from "this machine has no Windows-only
        projects" - a check whose pass condition is an absence, which is the trap that produced all of
        this.
#>

<#
.SYNOPSIS
    True when this executable is a software development kit that can answer a property query - not merely
    a file, and not a runtime-only install.
#>
function Test-DotnetAnswers([string] $Exe) {
    if ([string]::IsNullOrWhiteSpace($Exe)) { return $false }
    if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { return $false }
    try {
        $out = & $Exe --version 2>$null
        if ($LASTEXITCODE -ne 0) { return $false }

        # THE LAST non-empty line, not the whole output joined together. --version prints the version
        # alone on this kit, including under a fresh DOTNET_CLI_HOME, but a first-run or welcome banner
        # ahead of it would make a joined string unparseable and reject a perfectly good kit.
        $text = @($out | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                  ForEach-Object { ([string] $_).Trim() }) | Select-Object -Last 1
        if ([string]::IsNullOrWhiteSpace($text)) { return $false }

        # -getProperty, which is the only thing this file actually needs, arrived in version 8. A kit
        # older than that PASSES --version and fails the query with MSB1001, so without this check every
        # project came back unestablished and the broken-instrument rule threw - the right refusal with
        # the wrong explanation. Rejecting it here says why.
        #
        # NOTE FOR THE NEXT READER: --version answers from the CURRENT DIRECTORY's global.json, so this
        # probe is as directory-dependent as the build is. That is the RIGHT dependence - a kit the build
        # could not use should be rejected - but run it from a folder whose global.json pins a version
        # that is not installed and it fails, which looks like a broken probe and is not.
        $major = 0
        if (-not [int]::TryParse(($text -split "[.\-]")[0], [ref] $major)) { return $false }
        return ($major -ge 8)
    } catch {
        return $false
    }
}

<#
.SYNOPSIS
    The real file a dotnet executable is, following a symbolic link. The ORDINARY install shape is a
    link: Microsoft's package puts the kit in /usr/local/share/dotnet and links /usr/local/bin/dotnet,
    Homebrew links /opt/homebrew/bin/dotnet, and Ubuntu links /usr/bin/dotnet to /usr/lib/dotnet.
#>
function Resolve-DotnetRealPath([string] $Exe) {
    if ([string]::IsNullOrWhiteSpace($Exe)) { return $Exe }
    try {
        if ($PSVersionTable.PSVersion.Major -ge 7) {
            $target = [IO.File]::ResolveLinkTarget($Exe, $true)
            if ($null -ne $target) { return $target.FullName }
        } else {
            $item = Get-Item -LiteralPath $Exe -Force -ErrorAction SilentlyContinue
            if ($null -ne $item -and -not [string]::IsNullOrWhiteSpace($item.Target)) {
                return ([string] (@($item.Target)[0]))
            }
        }
    } catch { }
    return $Exe
}

<#
.SYNOPSIS
    The folder to put in DOTNET_ROOT for this executable, or $null when it cannot be proved to be one.
    A root is a folder that holds "shared"; nothing else may be called one.

.DESCRIPTION
    THIS EXISTS BECAUSE SETTING DOTNET_ROOT WRONG IS WORSE THAN NOT SETTING IT (#4883, round three).
    An apphost honours DOTNET_ROOT LITERALLY and does not look through a symbolic link, so pointing it
    at a link's folder - /usr/local/bin, /opt/homebrew/bin, /usr/bin - makes a framework-dependent
    executable fail with "You must install .NET to run this application". Measured on this machine: with
    DOTNET_ROOT at the real root an apphost exits 0; with DOTNET_ROOT at a folder holding only a symlink
    to that same kit it exits 131. Left unset, the host finds a default-location install by itself. So
    the only safe rule is to set it when the folder can be PROVED to be a root, and otherwise not at all.
#>
function Get-DotnetRootFolder([string] $Exe) {
    $real = Resolve-DotnetRealPath $Exe
    if ([string]::IsNullOrWhiteSpace($real)) { return $null }
    $dir = Split-Path -Parent $real
    if ([string]::IsNullOrWhiteSpace($dir)) { return $null }
    if (-not (Test-Path -LiteralPath (Join-Path $dir "shared"))) { return $null }
    return $dir
}

<#
.SYNOPSIS
    The .NET executable to ask, or $null when none of the candidates can answer. Looks where this fleet
    actually keeps it before trusting the PATH, because on the Mac it is at ~/.dotnet and the PATH does
    not have it - but every candidate must PROVE it answers, so a dead kit does not outrank a live one.
#>
function Get-DotnetExecutable {
    $exeName = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { "dotnet.exe" } else { "dotnet" }

    $candidates = @()
    # The kit sets this for processes it spawns, so it is the most authoritative answer available.
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_HOST_PATH)) { $candidates += $env:DOTNET_HOST_PATH }
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) { $candidates += (Join-Path $env:DOTNET_ROOT $exeName) }
    # READ FROM THE ENVIRONMENT, DELIBERATELY, not from PowerShell's automatic $HOME. The automatic one
    # is fixed when the process starts, so it cannot be overridden in-process - which would make this
    # lookup untestable, and an untestable lookup is exactly how #4883 survived in the first place. The
    # automatic variable is kept as the last resort for a host that sets neither.
    $homeDir = if (-not [string]::IsNullOrWhiteSpace($env:HOME)) { $env:HOME }
               elseif (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) { $env:USERPROFILE }
               else { $HOME }
    if (-not [string]::IsNullOrWhiteSpace($homeDir)) { $candidates += (Join-Path (Join-Path $homeDir ".dotnet") $exeName) }
    foreach ($c in (Get-Command $exeName -CommandType Application -ErrorAction SilentlyContinue)) {
        $candidates += $c.Source
    }

    foreach ($c in $candidates) {
        if (Test-DotnetAnswers $c) { return $c }
    }
    return $null
}

<#
.SYNOPSIS
    The target frameworks MSBuild evaluates for ONE project, or an empty set when THAT PROJECT cannot be
    evaluated - which callers read as "cannot tell about this project", never as "it declares none".
    Takes the executable, so the lookup happens once per run rather than once per project.
#>
function Get-ProjectTargetFrameworks([string] $ProjectPath, [string] $DotnetExe) {
    if ([string]::IsNullOrWhiteSpace($ProjectPath) -or [string]::IsNullOrWhiteSpace($DotnetExe)) { return @() }

    # The gate's project list is written with Windows separators. Normalized so the lookup does not
    # depend on the host tolerating them. (PowerShell does not use the backslash as an escape; the
    # character code is used so the literal cannot be misread.)
    $resolved = $ProjectPath.Replace([string] [char] 92, [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $resolved)) { return @() }

    # Standard error is MERGED rather than discarded: when every project fails, the refusal below has to
    # be able to say WHY, and 2>$null threw exactly that evidence away.
    $raw = & $DotnetExe msbuild $resolved -getProperty:TargetFrameworks -getProperty:TargetFramework 2>&1
    if ($LASTEXITCODE -ne 0 -or $null -eq $raw) {
        $script:LastProjectQueryOutput = (($raw -join " ").Trim() -replace "\s+", " ")
        return @()
    }

    # Only the PARSE is guarded, and only because malformed output is a "cannot tell about this project"
    # rather than a fault: a blanket try/catch over the whole body is what made an unreadable answer, a
    # missing project and a dead kit look identical in the first place.
    try {
        $parsed = ($raw -join "`n") | ConvertFrom-Json
    } catch {
        return @()
    }

    $values = @($parsed.Properties.TargetFrameworks, $parsed.Properties.TargetFramework)
    return @($values |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Split(";") } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Trim() })
}

<#
.SYNOPSIS
    True when EVERY target framework the project evaluates to is a Windows one, so this operating system
    cannot run its tests. False when it has a portable target, and false when it cannot be evaluated -
    "cannot tell" runs it.
#>
function Test-ProjectIsWindowsOnly([string] $ProjectPath, [string] $DotnetExe) {
    $declared = Get-ProjectTargetFrameworks $ProjectPath $DotnetExe
    if ($declared.Count -eq 0) { return $false }   # cannot tell about this project - run it
    return -not ($declared | Where-Object { $_ -notmatch "-windows" })
}

<#
.SYNOPSIS
    The decision, in full: which suites are held back AND which projects could not be established, so the
    caller can record both. THROWS when no working kit can be found, and when every project in a list of
    two or more fails - see this file's description for why those are not answers.
#>
function Get-PlatformSuiteDecision([string[]] $Projects, [string] $RepoRoot, [string] $DotnetExe = $null) {
    $none = [pscustomobject]@{ HeldBack = @(); Unestablished = @(); Dotnet = $null }
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { return $none }
    if ($null -eq $Projects -or $Projects.Count -eq 0) { return $none }

    $exe = if ([string]::IsNullOrWhiteSpace($DotnetExe)) { Get-DotnetExecutable } else { $DotnetExe }
    if (-not (Test-DotnetAnswers $exe)) {
        throw "no working .NET software development kit could be found, so which suites this operating " +
              "system can run cannot be established. Looked at DOTNET_HOST_PATH, DOTNET_ROOT, ~/.dotnet " +
              "and the PATH; a candidate counts only if 'dotnet --version' answers, so a runtime-only " +
              "install is passed over. Set DOTNET_ROOT to a kit (on this fleet's Mac it is at ~/.dotnet) " +
              "and run again. Refusing rather than guessing: an empty answer here is indistinguishable " +
              "from 'this machine has no Windows-only projects', which would hold nothing back and look " +
              "correct (issue #4883)."
    }

    $held = @()
    $unestablished = @()
    foreach ($p in $Projects) {
        $declared = Get-ProjectTargetFrameworks (Join-Path $RepoRoot $p) $exe
        if ($declared.Count -eq 0) { $unestablished += $p; continue }
        if (-not ($declared | Where-Object { $_ -notmatch "-windows" })) { $held += $p }
    }

    # Two or more asked and not one answered: the instrument is broken, however well it probed a moment
    # ago. One project cannot be told apart from a project that is simply absent, so one is allowed.
    if ($Projects.Count -ge 2 -and $unestablished.Count -eq $Projects.Count) {
        $why = if ([string]::IsNullOrWhiteSpace($script:LastProjectQueryOutput)) { "(the query produced no output)" }
               else { $script:LastProjectQueryOutput.Substring(0, [Math]::Min(300, $script:LastProjectQueryOutput.Length)) }
        throw ("none of the $($Projects.Count) projects could be evaluated, using '$exe'. That is a broken " +
               "instrument rather than $($Projects.Count) unreadable projects, so this is a refusal and not " +
               "an answer of 'nothing is held back' (issue #4883). The last query said: " + $why)
    }

    return [pscustomobject]@{ HeldBack = @($held); Unestablished = @($unestablished); Dotnet = $exe }
}

<#
.SYNOPSIS
    Just the held-back suites, for callers that do not need the rest. Throws on the same two conditions.
#>
function Get-SuitesThisPlatformCannotRun([string[]] $Projects, [string] $RepoRoot, [string] $DotnetExe = $null) {
    return @((Get-PlatformSuiteDecision $Projects $RepoRoot $DotnetExe).HeldBack)
}
