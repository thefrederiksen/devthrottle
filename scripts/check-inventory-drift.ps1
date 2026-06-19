#Requires -Version 5.1
<#
.SYNOPSIS
    Check docs/features/feature-inventory.yaml for drift against the source tree.

.DESCRIPTION
    The no-GUI half of the /document-features pipeline. It does NOT launch the app
    or take screenshots - it only verifies, deterministically, that the inventory
    still matches the code:

      1. Every "source:" path in the inventory must exist on disk. A documented
         feature whose implementing file was moved or deleted is drift and FAILS
         the check (exit 1).
      2. Every page referenced by the inventory's "page:" lines must exist.
      3. Informational only (never fails): top-level views/dialogs/pages that look
         user-facing but are not referenced by any inventory source path, so a human
         can decide whether they belong in the docs.

    Runs on Windows or Linux (pwsh). Used by .github/workflows/docs-drift.yml and
    by the /document-features skill's inventory pass.

.PARAMETER InventoryPath
    Path to feature-inventory.yaml. Defaults to the repo's docs/features one.
#>
param(
    [string]$InventoryPath
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $InventoryPath) { $InventoryPath = Join-Path $repoRoot "docs/features/feature-inventory.yaml" }
if (-not (Test-Path $InventoryPath)) { Write-Host "FAIL inventory not found: $InventoryPath"; exit 1 }

$text = Get-Content $InventoryPath -Raw

# --- 1. source: paths exist -------------------------------------------------
# Collect the list items under each "source:" block: subsequent "    - <path>" lines.
$sourcePaths = New-Object System.Collections.Generic.List[string]
$inSource = $false
foreach ($line in ($text -split "`n")) {
    if ($line -match '^\s*source:\s*$') { $inSource = $true; continue }
    if ($inSource) {
        if ($line -match '^\s*-\s*(.+?)\s*$') { $sourcePaths.Add($Matches[1].Trim()); continue }
        # a non-list, non-blank line ends the block
        if ($line -match '^\s*\S' -and $line -notmatch '^\s*-') { $inSource = $false }
    }
}

$missing = @()
foreach ($p in ($sourcePaths | Sort-Object -Unique)) {
    $full = Join-Path $repoRoot $p
    if (-not (Test-Path $full)) { $missing += $p }
}

# --- 2. page: targets exist -------------------------------------------------
$pages = [regex]::Matches($text, '(?m)^\s*page:\s*(\S+)\s*$') | ForEach-Object { $_.Groups[1].Value }
$missingPages = @()
foreach ($pg in ($pages | Sort-Object -Unique)) {
    if (-not (Test-Path (Join-Path $repoRoot $pg))) { $missingPages += $pg }
}

# --- 3. informational: candidate views not referenced -----------------------
$candidates = @()
$candidates += Get-ChildItem -Path (Join-Path $repoRoot "src/CcDirector.Avalonia/Controls") -Filter "*View.axaml" -Recurse -ErrorAction SilentlyContinue
$candidates += Get-ChildItem -Path (Join-Path $repoRoot "phone/CcDirectorClient") -Filter "*Page.xaml" -ErrorAction SilentlyContinue
$referenced = ($sourcePaths | ForEach-Object { Split-Path $_ -Leaf })
$unreferenced = @()
foreach ($c in $candidates) {
    if ($referenced -notcontains $c.Name) {
        $rel = $c.FullName.Substring($repoRoot.Length).TrimStart('\','/') -replace '\\','/'
        $unreferenced += $rel
    }
}

# --- Report -----------------------------------------------------------------
Write-Host "Inventory drift check: $InventoryPath"
Write-Host "  source paths checked: $($sourcePaths.Count)"
Write-Host "  pages checked:        $($pages.Count)"

if ($unreferenced.Count -gt 0) {
    Write-Host ""
    Write-Host "NOTE (informational) user-facing views not in the inventory:"
    $unreferenced | Sort-Object -Unique | ForEach-Object { Write-Host "  - $_" }
    Write-Host "  If any belong in the docs, run /document-features to add them."
}

$failed = $false
if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "FAIL inventory references source files that do not exist:"
    $missing | ForEach-Object { Write-Host "  - $_" }
    $failed = $true
}
if ($missingPages.Count -gt 0) {
    Write-Host ""
    Write-Host "FAIL inventory references pages that do not exist:"
    $missingPages | ForEach-Object { Write-Host "  - $_" }
    $failed = $true
}

if ($failed) {
    Write-Host ""
    Write-Host "Drift detected. Re-run /document-features to refresh the inventory and docs."
    exit 1
}

Write-Host ""
Write-Host "OK inventory is consistent with the source tree."
exit 0
