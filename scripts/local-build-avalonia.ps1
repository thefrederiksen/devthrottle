#Requires -Version 5.1
<#
.SYNOPSIS
    Builds CC Director (Avalonia) locally.

.DESCRIPTION
    Publishes CC Director Avalonia as a single-file executable. Framework-dependent by
    default (~5-10 MB, requires .NET 10 runtime). Pass -SelfContained for a
    standalone build (~150+ MB).

.PARAMETER SelfContained
    Build as self-contained (no .NET runtime required on target machine).

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.EXAMPLE
    .\scripts\local-build-avalonia.ps1
    .\scripts\local-build-avalonia.ps1 -SelfContained
#>
param(
    [switch]$SelfContained,
    [string]$Configuration = "Release",
    [string]$Slot = ""
)

$ErrorActionPreference = "Stop"

# The MAIN cc-director is no longer built locally - it is INSTALLED from a GitHub release into
# %LOCALAPPDATA%\cc-director\app (see docs/install/windows-install-prompt.md), so auto-update can
# replace it in place. This script only builds dev/test SLOT builds now. Refuse a main (no -Slot)
# build so nobody accidentally recreates a competing local_builds\cc-director.exe.
if (-not $Slot) {
    Write-Error "Building the main cc-director.exe is disabled. The main app is INSTALLED from a release (docs/install/windows-install-prompt.md). Pass -Slot <n> to build a dev/test slot, e.g. -Slot 5."
    exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\CcDirector.Avalonia\CcDirector.Avalonia.csproj"
$corePath = Join-Path $repoRoot "src\CcDirector.Core\CcDirector.Core.csproj"

# Read version from csproj
[xml]$csproj = Get-Content $projectPath
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) {
    Write-Error "Could not read <Version> from $projectPath"
    exit 1
}

Write-Host "Building CC Director Avalonia v$version ($Configuration)" -ForegroundColor Cyan

$selfContainedFlag = if ($SelfContained) { "true" } else { "false" }

if ($SelfContained) {
    Write-Host "  Mode: Self-contained" -ForegroundColor Yellow
} else {
    Write-Host "  Mode: Framework-dependent (.NET 10 runtime required)" -ForegroundColor Yellow
}

# Step 0: Clean
Write-Host "  Cleaning previous build..." -ForegroundColor Gray
& dotnet clean $projectPath -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet clean failed with exit code $LASTEXITCODE"
    exit 1
}

# Step 1: Pre-build Core dependency
Write-Host "  Pre-building Core dependency..." -ForegroundColor Gray
& dotnet build $corePath -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Error "Core pre-build failed with exit code $LASTEXITCODE"
    exit 1
}

# Step 2: Build Avalonia project with RID
Write-Host "  Building Avalonia project..." -ForegroundColor Gray
& dotnet build $projectPath -c $Configuration -r win-x64 --self-contained $selfContainedFlag --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Error "Avalonia build failed with exit code $LASTEXITCODE"
    exit 1
}

# Step 3: Publish
$msbuildArgs = @(
    "msbuild", $projectPath,
    "-t:Publish",
    "-p:Configuration=$Configuration",
    "-p:RuntimeIdentifier=win-x64",
    "-p:SelfContained=$selfContainedFlag",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:NoBuild=true"
)

if ($SelfContained) {
    $msbuildArgs += "-p:EnableCompressionInSingleFile=true"
}

Write-Host "  Publishing..." -ForegroundColor Gray
& dotnet @msbuildArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed with exit code $LASTEXITCODE"
    exit 1
}

# Locate published output
$publishDir = Join-Path $repoRoot "src\CcDirector.Avalonia\bin\$Configuration\net10.0\win-x64\publish"
$exePath = Join-Path $publishDir "cc-director.exe"

if (-not (Test-Path $exePath)) {
    Write-Error "Published exe not found at $exePath"
    exit 1
}

# Copy to local_builds directory
$releasesDir = Join-Path $repoRoot "local_builds"
if (-not (Test-Path $releasesDir)) {
    New-Item -ItemType Directory -Path $releasesDir | Out-Null
}
$exeName = "cc-director$Slot.exe"   # always slotted; main builds are refused above
$destPath = Join-Path $releasesDir $exeName
Copy-Item $exePath $destPath -Force

$exeSize = (Get-Item $destPath).Length / 1MB
Write-Host ""
Write-Host "Build complete: $([math]::Round($exeSize, 1)) MB" -ForegroundColor Green
Write-Host "  $destPath" -ForegroundColor Green
