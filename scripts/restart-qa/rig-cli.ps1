<#
.SYNOPSIS
  Run THIS worktree's cc-devthrottle command line against the isolated restart QA rig.

.DESCRIPTION
  The cc-devthrottle on this machine's PATH is the installed 2.8.1 tool and has no
  'director smart-restart', 'smart-restart-status' or 'restart-history' - phase 4 wrote those and
  they have not shipped. So the tree's own source is run instead, through the installed virtual
  environment, with a junction that puts it on the module path under the package name the tool
  expects. The Gateway address and key are the RIG's, never the machine's real ones.

  It prints the module file that actually answered, so that the tree's source having run - rather
  than an installed copy - is checked and not assumed.

.EXAMPLE
  scripts\restart-qa\rig-cli.ps1 director restart-history --count 3
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $CommandLine
)

$ErrorActionPreference = 'Stop'

$RigRoot   = 'C:\Users\soren\AppData\Local\cc-director-restart-qa-rig'
$TokenFile = Join-Path $RigRoot 'config\director\gateway-token.txt'
$TreeSrc   = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'tools\cc-devthrottle\src'
$Python    = Join-Path $env:LOCALAPPDATA 'cc-director\pyenv\Scripts\python.exe'

foreach ($needed in @($TokenFile, $TreeSrc, $Python)) {
    if (-not (Test-Path $needed)) {
        throw "rig-cli: $needed does not exist. Stand the rig up first: scripts\restart-qa-rig.ps1 up"
    }
}

$PkgRoot = Join-Path $env:TEMP 'restart-qa-rig-pkgroot'
$Link    = Join-Path $PkgRoot 'cc_devthrottle'
New-Item -ItemType Directory -Force -Path $PkgRoot | Out-Null
if (Test-Path $Link) {
    # A junction left over from another worktree points at the WRONG source. Replace it every time
    # rather than trusting it, because a stale one answers successfully with somebody else's code.
    cmd /c rmdir "$Link" | Out-Null
}
cmd /c mklink /J "$Link" "$TreeSrc" | Out-Null

$env:PYTHONPATH             = $PkgRoot
$env:CC_GATEWAY_URL         = 'http://127.0.0.1:7911'
$env:CC_GATEWAY_SESSION_KEY = (Get-Content $TokenFile -Raw).Trim()

& $Python -c "import cc_devthrottle.cli as c; print('[rig-cli] the source that answered: ' + c.__file__)"
& $Python -m cc_devthrottle.cli @CommandLine
exit $LASTEXITCODE
