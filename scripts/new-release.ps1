#Requires -Version 5.1
<#
.SYNOPSIS
    Bumps the product version, commits, tags, and pushes to trigger a GitHub Actions release.

.DESCRIPTION
    The product version lives in EXACTLY ONE file: Directory.Build.props at the
    repo root (see docs/architecture/VERSIONING.md). MSBuild stamps that version
    into every .NET binary in the release (Director, Gateway, Cockpit, setup
    wizards, setup CLI); all UIs read it from their assembly at runtime, so no
    other file needs to change.

    This script bumps Directory.Build.props, commits, creates the vX.Y.Z git tag,
    and pushes. The GitHub Actions release workflow builds and publishes the
    release for Windows and macOS; a workflow guard fails the release if the tag
    and Directory.Build.props ever disagree.

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

# --- The single version source ---
$propsPath = Join-Path $repoRoot "Directory.Build.props"
if (-not (Test-Path $propsPath)) {
    Write-Error "Directory.Build.props not found at $propsPath"
    exit 1
}

[xml]$props = Get-Content $propsPath
$currentVersion = $props.SelectSingleNode("//Version").InnerText
if (-not $currentVersion) {
    Write-Error "Could not read <Version> from $propsPath"
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

# --- Guard: no .csproj may carry its own <Version> (it would silently override the props file) ---
$strayVersions = Get-ChildItem $repoRoot -Recurse -Filter *.csproj |
    Where-Object { $_.FullName -notmatch '\\archived\\' } |
    Where-Object { (Get-Content $_.FullName -Raw) -match '<Version>' }
if ($strayVersions) {
    Write-Host ""
    Write-Host "ERROR: These .csproj files declare their own <Version>, which overrides Directory.Build.props:" -ForegroundColor Red
    $strayVersions | ForEach-Object { Write-Host "  - $($_.FullName)" -ForegroundColor Red }
    Write-Host "Remove the <Version> element(s); the props file is the single source of truth." -ForegroundColor Red
    exit 1
}

# --- Update the one file ---
Write-Host ""
Write-Host "Updating version to $newVersion..." -ForegroundColor Cyan
$props.SelectSingleNode("//Version").InnerText = $newVersion
$props.Save($propsPath)
Write-Host "  [+] $propsPath" -ForegroundColor Gray

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
Write-Host "File changed:" -ForegroundColor Yellow
Write-Host "  - Directory.Build.props (the single version source)"
Write-Host ""

$confirm = Read-Host "Commit, tag, and push? (Y/N)"
if ($confirm -ne 'Y' -and $confirm -ne 'y') {
    Write-Host ""
    Write-Host "Aborted. The file was updated but not committed." -ForegroundColor Yellow
    Write-Host "Run 'git checkout -- Directory.Build.props' to undo." -ForegroundColor Yellow
    exit 0
}

# --- Git operations ---
Write-Host ""
Write-Host "Committing..." -ForegroundColor Cyan
git -C $repoRoot add $propsPath
git -C $repoRoot commit -m "release: v$newVersion"

Write-Host "Tagging $tagName..." -ForegroundColor Cyan
git -C $repoRoot tag $tagName

Write-Host "Pushing to origin..." -ForegroundColor Cyan
git -C $repoRoot push origin main
git -C $repoRoot push origin $tagName

Write-Host ""
Write-Host "Done! Release $tagName pushed." -ForegroundColor Green
Write-Host "GitHub Actions: https://github.com/thefrederiksen/cc-director/actions" -ForegroundColor Cyan
