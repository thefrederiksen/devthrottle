<#
.SYNOPSIS
    Build the CC Director Python tools bundle: one shared-venv payload that replaces the
    ~26 individual PyInstaller tool exes.

.DESCRIPTION
    Produces two release assets under -OutputDir:

      cc-python-win-x64.zip       - a relocatable python-build-standalone CPython (the runtime
                                    the installer creates the shared venv from).
      cc-tools-pyenv-win-x64.zip  - wheelhouse/ (de-duped dependency wheels + the core tool
                                    wheels), requirements.lock, and tools-manifest.json.

    Core is an explicit allowlist: a tool ships ONLY when it has "ship": true in tools/registry.json.
    Non-core tools stay in the repo (buildable for dev) but never enter the bundle. There is no
    longer a core/extras split.

    The installer (PythonToolsInstaller) extracts the python, runs `python -m venv`, then
    `pip install --no-index --find-links wheelhouse -r requirements.lock <tools>` fully offline,
    and writes bin\cc-<tool>.cmd shims from tools-manifest.json.

    Requires: uv (https://astral.sh) and Python 3 on PATH. Windows x64.

.PARAMETER OutputDir
    Where the two .zip assets are written. Default: dist/python-bundle.

.PARAMETER PyVersion
    The CPython minor version to bundle and target wheels for. Default: 3.12.
#>
[CmdletBinding()]
param(
    [string]$OutputDir = "dist/python-bundle",
    [string]$PyVersion = "3.12"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Step($msg) { Write-Host "[build-python-bundle] $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Error "[build-python-bundle] $msg"; exit 1 }

# Run a native exe capturing all output (incl. stderr) as plain strings and the exit code,
# WITHOUT tripping PowerShell 5.1's "native stderr -> terminating error" behavior.
function Invoke-Native {
    param([Parameter(Mandatory)][string]$Exe, [string[]]$Args, [switch]$Quiet)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $output = & $Exe @Args 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    if (-not $Quiet) { $output | ForEach-Object { Write-Host "  $_" } }
    return [pscustomobject]@{ Code = $code; Output = @($output) }
}

foreach ($exe in @("uv", "python")) {
    if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) {
        Fail "$exe not found on PATH. Install it before running this script."
    }
}

$work = Join-Path $repoRoot "build/python-bundle"
$wheelhouse = Join-Path $work "wheelhouse"
$pyStage = Join-Path $work "python"
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $wheelhouse | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# ---- 1. Select the SHIPPED Python tools from the registry ------------------------------
# Core is an explicit allowlist: a tool ships only when it has "ship": true in tools/registry.json.
# Non-core tools stay in the repo (buildable for dev) but never enter the bundle.
Step "reading tools/registry.json"
$registry = Get-Content (Join-Path $repoRoot "tools/registry.json") -Raw | ConvertFrom-Json
$pyTools = @($registry.tools | Where-Object { $_.type -eq "python" -and $_.ship -eq $true -and $_.name -ne "cc-director-setup" })
if ($pyTools.Count -eq 0) { Fail "no shipped python tools found in registry (set `"ship`": true on core tools)" }
Step "selected $($pyTools.Count) shipped python tools: $(($pyTools | ForEach-Object { $_.name }) -join ', ')"

function ToolDir($t) {
    $dir = if ($t.PSObject.Properties.Name -contains "source_dir" -and $t.source_dir) { $t.source_dir } else { $t.name }
    Join-Path $repoRoot "tools/$dir"
}
$toolDirs = $pyTools | ForEach-Object { ToolDir $_ }
$sharedDirs = @("tools/cc_shared", "tools/cc_storage") | ForEach-Object { Join-Path $repoRoot $_ }

# ---- 2. Build every tool + shared-lib wheel into the wheelhouse ------------------------
Step "building tool + shared-lib wheels"
foreach ($d in @($sharedDirs + $toolDirs)) {
    if (-not (Test-Path (Join-Path $d "pyproject.toml"))) { Fail "missing pyproject.toml in $d" }
    # Clean stale build/egg-info so setuptools never re-includes a leftover 'src' package.
    Remove-Item -Recurse -Force (Join-Path $d "build"), (Join-Path $d "*.egg-info") -ErrorAction SilentlyContinue
    $r = Invoke-Native uv @("build", "--wheel", $d, "-o", $wheelhouse) -Quiet
    if ($r.Code -ne 0) { Fail "wheel build failed for $d`n$($r.Output -join "`n")" }
}

# ---- 3. Build the THIRD-PARTY requirement set, then resolve a pinned lock --------------
# Critical: never feed our cc-* distribution NAMES to the resolver - several collide with
# unrelated PyPI packages (e.g. a real "cc-vault" exists). We resolve only third-party deps
# (parsed from the tools' pyprojects, excluding every cc-* name) so PyPI is never consulted
# for our packages. Inter-tool deps (cc-photos -> cc-vault) are satisfied at install time
# from the wheelhouse with --no-index.
Step "collecting third-party dependencies from tool pyprojects"
$collect = Join-Path $work "collect_thirdparty.py"
@'
import tomllib, glob, os, re, sys, json
registry = json.load(open("tools/registry.json"))
ship = {t["name"] for t in registry["tools"] if t.get("type")=="python" and t.get("ship") and t["name"]!="cc-director-setup"}
allcc = {t["name"] for t in registry["tools"]}     # every cc-* name (any type)
ours = {"cc-shared","cc-storage"} | allcc          # our own dists never come from PyPI
norm = lambda r: re.split(r"[<>=!~ \[]", r.strip(), 1)[0].lower().replace("_","-")
reqs=set()
def add(pp, extras=()):
    d=tomllib.load(open(pp,"rb")); proj=d.get("project",{})
    for r in (proj.get("dependencies") or []):
        if norm(r) not in ours: reqs.add(r.strip())
    opt=proj.get("optional-dependencies") or {}
    for e in extras:
        for r in (opt.get(e) or []):
            if norm(r) not in ours: reqs.add(r.strip())
for pp in glob.glob("tools/cc-*/pyproject.toml"):
    name=os.path.basename(os.path.dirname(pp))
    if name not in ship: continue                  # only shipped tools' deps enter the wheelhouse
    add(pp, extras=("full",) if name=="cc-vault" else ())   # cc-vault ships converters under [full]
add("tools/cc_shared/pyproject.toml"); add("tools/cc_storage/pyproject.toml")
open(sys.argv[1],"w",encoding="utf-8").write("\n".join(sorted(reqs))+"\n")
print(f"{len(reqs)} third-party requirement lines")
'@ | Set-Content $collect -Encoding utf8
$tpIn = Join-Path $work "thirdparty.in"
$r = Invoke-Native python @($collect, $tpIn)
if ($r.Code -ne 0) { Fail "collecting third-party deps failed`n$($r.Output -join "`n")" }

Step "compiling pinned lock (python $PyVersion / windows)"
$lock = Join-Path $work "requirements.lock"
$r = Invoke-Native uv @("pip", "compile", $tpIn,
    "--python-version", $PyVersion, "--python-platform", "x86_64-pc-windows-msvc",
    "--no-annotate", "--no-header", "-o", $lock)
if ($r.Code -ne 0) { Fail "combined lock did not resolve - dependency conflict across tools (see plan contingency)" }

$thirdParty = Get-Content $lock | Where-Object { $_ -match "==" }
$tpFile = Join-Path $work "download.txt"
$thirdParty | Set-Content $tpFile -Encoding utf8
Step "locked third-party deps: $($thirdParty.Count)"

# ---- 4. Fill the wheelhouse with the third-party closure (binary wheels) ---------------
Step "downloading binary wheels for the locked third-party deps"
# Some pure-python deps are sdist-only on PyPI (e.g. GPUtil); --only-binary rejects them,
# so download what we can, then build universal wheels for the stragglers.
$r = Invoke-Native python @("-m", "pip", "download", "--only-binary=:all:",
    "--python-version", "312", "--platform", "win_amd64", "-r", $tpFile, "-d", $wheelhouse) -Quiet
if ($r.Code -ne 0) {
    $missing = @($r.Output | Select-String "No matching distribution found for (\S+)" | ForEach-Object { $_.Matches[0].Groups[1].Value })
    if ($missing.Count -eq 0) { Fail "pip download failed for a non-sdist reason:`n$($r.Output -join "`n")" }
    Step "sdist-only packages need a built wheel: $($missing -join ', ')"
    $names = $missing | ForEach-Object { ($_ -split "==")[0] }
    (Get-Content $tpFile | Where-Object { $names -notcontains (($_ -split "==")[0]) }) | Set-Content $tpFile -Encoding utf8
    $r = Invoke-Native python @("-m", "pip", "download", "--only-binary=:all:",
        "--python-version", "312", "--platform", "win_amd64", "-r", $tpFile, "-d", $wheelhouse) -Quiet
    if ($r.Code -ne 0) { Fail "pip download failed after filtering sdist-only packages" }
    foreach ($m in $missing) {
        $r = Invoke-Native python @("-m", "pip", "wheel", $m, "--no-deps", "-w", $wheelhouse) -Quiet
        if ($r.Code -ne 0) { Fail "could not build a wheel for sdist-only package $m" }
    }
}

# ---- 5. Stage the python-build-standalone CPython --------------------------------------
Step "provisioning python-build-standalone $PyVersion via uv"
$r = Invoke-Native uv @("python", "install", $PyVersion) -Quiet
if ($r.Code -ne 0) { Fail "uv python install $PyVersion failed" }
$r = Invoke-Native uv @("python", "find", $PyVersion) -Quiet
$pyExe = (@($r.Output) | ForEach-Object { $_.Trim() } | Where-Object { $_ -match 'python\.exe$' } | Select-Object -Last 1)
if (-not $pyExe -or -not (Test-Path $pyExe)) { Fail "could not locate the provisioned python $PyVersion (got: $($r.Output -join '|'))" }
$pyRoot = Split-Path -Parent $pyExe
$r = Invoke-Native $pyExe @("-c", "import platform;print(platform.python_version())") -Quiet
$exactVer = (@($r.Output) | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\d+\.\d+\.\d+' } | Select-Object -First 1)
Step "bundling CPython $exactVer from $pyRoot"
Copy-Item -Recurse -Force $pyRoot $pyStage

# ---- 6. Write the tools manifest -------------------------------------------------------
Step "writing tools-manifest.json"
# Product version lives in Directory.Build.props (single source, see docs/architecture/VERSIONING.md)
$bundleVersion = (Get-Content (Join-Path $repoRoot "Directory.Build.props") -Raw | Select-String "<Version>(.*?)</Version>").Matches[0].Groups[1].Value
function ToolEntries($tools) {
    foreach ($t in $tools) {
        $pp = Get-Content (Join-Path (ToolDir $t) "pyproject.toml") -Raw
        $scriptsBlock = ""
        if ($pp -match "(?s)\[project\.scripts\](.*?)(\r?\n\[|\z)") { $scriptsBlock = $Matches[1] }
        $scripts = [regex]::Matches($scriptsBlock, '(?m)^\s*([\w-]+)\s*=') | ForEach-Object { $_.Groups[1].Value }
        # cc-vault's document converters are under its [full] extra; install with that extra selected.
        $dist = if ($t.name -eq "cc-vault") { "cc-vault[full]" } else { $t.name }
        [pscustomobject]@{ id = $t.name; dist = $dist; scripts = @($scripts) }
    }
}
$manifestPath = Join-Path $work "tools-manifest.json"
[pscustomobject]@{ bundleVersion = $bundleVersion; pythonVersion = $exactVer; tools = @(ToolEntries $pyTools) } |
    ConvertTo-Json -Depth 6 | Set-Content $manifestPath -Encoding utf8

# ---- 7. Zip the two assets --------------------------------------------------------------
Step "packaging assets into $OutputDir"
$pyZip = Join-Path $OutputDir "cc-python-win-x64.zip"
$toolsZip = Join-Path $OutputDir "cc-tools-pyenv-win-x64.zip"
Remove-Item -Force $pyZip, $toolsZip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $pyStage "*") -DestinationPath $pyZip -CompressionLevel Optimal
Compress-Archive -Path $wheelhouse, $lock, $manifestPath -DestinationPath $toolsZip -CompressionLevel Optimal

$pyMB = [math]::Round((Get-Item $pyZip).Length / 1MB, 1)
$toolsMB = [math]::Round((Get-Item $toolsZip).Length / 1MB, 1)
$wheelCount = (Get-ChildItem $wheelhouse -Filter *.whl).Count
Step "DONE. python=$pyMB MB, tools-pyenv=$toolsMB MB ($wheelCount wheels)"
Step "assets: $pyZip ; $toolsZip"
