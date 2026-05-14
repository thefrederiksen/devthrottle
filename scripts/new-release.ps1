#Requires -Version 5.1
<#
.SYNOPSIS
    Bumps version, commits, tags, and pushes to trigger a GitHub Actions release.

.DESCRIPTION
    Updates the version in 5 locations (Avalonia csproj, Setup WPF csproj, Setup WPF XAML,
    Setup Avalonia csproj, Setup Avalonia AXAML), commits the changes, creates a git tag,
    and pushes to origin. The existing GitHub Actions workflow handles building and
    creating the GitHub Release for both Windows and macOS.

    Note: CcDirector.Avalonia is the cross-platform main app, published for win-x64 and
    osx-arm64. The legacy CcDirector.Wpf project was archived in commit c557e58c.

.EXAMPLE
    .\scripts\new-release.ps1
#>

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

# --- Check for uncommitted changes ---
$status = git -C $repoRoot status --porcelain
if ($status) {
    Write-Host ""
    Write-Host "ERROR: Working tree has uncommitted changes." -ForegroundColor Red
    Write-Host "Commit or stash your changes before running a release." -ForegroundColor Red
    Write-Host ""
    git -C $repoRoot status --short
    Write-Host ""
    exit 1
}

# --- Version file paths ---
$avaloniaCsproj   = Join-Path $repoRoot "src\CcDirector.Avalonia\CcDirector.Avalonia.csproj"
$setupCsproj      = Join-Path $repoRoot "tools\cc-director-setup\CcDirectorSetup.csproj"
$setupXaml        = Join-Path $repoRoot "tools\cc-director-setup\MainWindow.xaml"
$setupAvCsproj    = Join-Path $repoRoot "tools\cc-director-setup-avalonia\CcDirectorSetup.csproj"
$setupAvAxaml     = Join-Path $repoRoot "tools\cc-director-setup-avalonia\MainWindow.axaml"

# --- Read current version (from Avalonia, the canonical main app csproj) ---
[xml]$csproj = Get-Content $avaloniaCsproj
$currentVersion = $csproj.SelectSingleNode("//Version").InnerText
if (-not $currentVersion) {
    Write-Error "Could not read <Version> from $avaloniaCsproj"
    exit 1
}

Write-Host ""
Write-Host "Current version: $currentVersion" -ForegroundColor Cyan
$newVersion = Read-Host "New version (X.Y.Z or X.Y.Z-rcN)"

# --- Validate semver format ---
if ($newVersion -notmatch '^\d+\.\d+\.\d+(-rc\d+)?$') {
    Write-Error "Invalid version format: '$newVersion'. Expected X.Y.Z or X.Y.Z-rcN"
    exit 1
}

if ($newVersion -eq $currentVersion) {
    Write-Error "New version is the same as current version ($currentVersion)"
    exit 1
}

# --- Check tag doesn't already exist ---
$tagName = "v$newVersion"
$existingTag = git -C $repoRoot tag -l $tagName
if ($existingTag) {
    Write-Error "Tag $tagName already exists"
    exit 1
}

# --- Update files ---
Write-Host ""
Write-Host "Updating version to $newVersion..." -ForegroundColor Cyan

# 1. Avalonia csproj (main app, cross-platform)
[xml]$avaloniaXml = Get-Content $avaloniaCsproj
$avaloniaXml.SelectSingleNode("//Version").InnerText = $newVersion
$avaloniaXml.Save($avaloniaCsproj)
Write-Host "  [+] $avaloniaCsproj" -ForegroundColor Gray

# 2. Setup csproj (Windows)
[xml]$setupXml = Get-Content $setupCsproj
$setupXml.SelectSingleNode("//Version").InnerText = $newVersion
$setupXml.Save($setupCsproj)
Write-Host "  [+] $setupCsproj" -ForegroundColor Gray

# 3. Setup XAML (Windows -- replace version text like v1.2.0)
$xamlContent = Get-Content $setupXaml -Raw
$xamlContent = $xamlContent -replace 'Text="v[0-9]+\.[0-9]+\.[0-9]+(-rc[0-9]+)?"', "Text=`"v$newVersion`""
Set-Content $setupXaml $xamlContent -NoNewline
Write-Host "  [+] $setupXaml" -ForegroundColor Gray

# 4. Setup Avalonia csproj (macOS)
[xml]$setupAvXml = Get-Content $setupAvCsproj
$setupAvXml.SelectSingleNode("//Version").InnerText = $newVersion
$setupAvXml.Save($setupAvCsproj)
Write-Host "  [+] $setupAvCsproj" -ForegroundColor Gray

# 5. Setup Avalonia AXAML (macOS -- replace version text like v1.2.0)
$axamlContent = Get-Content $setupAvAxaml -Raw
$axamlContent = $axamlContent -replace 'Text="v[0-9]+\.[0-9]+\.[0-9]+(-rc[0-9]+)?"', "Text=`"v$newVersion`""
Set-Content $setupAvAxaml $axamlContent -NoNewline
Write-Host "  [+] $setupAvAxaml" -ForegroundColor Gray

# --- Determine pre-release ---
$isPreRelease = $newVersion -match '-rc\d+$'

# --- Summary ---
Write-Host ""
Write-Host "=== Release Summary ===" -ForegroundColor Yellow
Write-Host "  Version : $currentVersion -> $newVersion"
Write-Host "  Tag     : $tagName"
if ($isPreRelease) {
    Write-Host "  Type    : Pre-release" -ForegroundColor Yellow
} else {
    Write-Host "  Type    : Stable release" -ForegroundColor Green
}
Write-Host ""
Write-Host "Files changed:" -ForegroundColor Yellow
Write-Host "  Main app (Avalonia, cross-platform):"
Write-Host "  - src\CcDirector.Avalonia\CcDirector.Avalonia.csproj"
Write-Host "  Setup wizard (Windows, WPF):"
Write-Host "  - tools\cc-director-setup\CcDirectorSetup.csproj"
Write-Host "  - tools\cc-director-setup\MainWindow.xaml"
Write-Host "  Setup wizard (macOS, Avalonia):"
Write-Host "  - tools\cc-director-setup-avalonia\CcDirectorSetup.csproj"
Write-Host "  - tools\cc-director-setup-avalonia\MainWindow.axaml"
Write-Host ""

$confirm = Read-Host "Commit, tag, and push? (Y/N)"
if ($confirm -ne 'Y' -and $confirm -ne 'y') {
    Write-Host ""
    Write-Host "Aborted. Files were updated but not committed." -ForegroundColor Yellow
    Write-Host "Run 'git checkout -- .' to undo." -ForegroundColor Yellow
    exit 0
}

# --- Git operations ---
Write-Host ""
Write-Host "Committing..." -ForegroundColor Cyan
git -C $repoRoot add $avaloniaCsproj $setupCsproj $setupXaml $setupAvCsproj $setupAvAxaml
git -C $repoRoot commit -m "release: v$newVersion"

Write-Host "Tagging $tagName..." -ForegroundColor Cyan
git -C $repoRoot tag $tagName

Write-Host "Pushing to origin..." -ForegroundColor Cyan
git -C $repoRoot push origin main
git -C $repoRoot push origin $tagName

Write-Host ""
Write-Host "Done! Release $tagName pushed." -ForegroundColor Green
Write-Host "GitHub Actions: https://github.com/thefrederiksen/cc-director/actions" -ForegroundColor Cyan
