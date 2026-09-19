# Throwaway measurement, READ-ONLY. Not product code.
# Question: of the packages in C:\Windows\Installer, how many does Windows still point at?
# Windows records, per installed product and per applied patch, the cached file it will need
# to repair or uninstall it (the LocalPackage value). A cached file nothing points at is an orphan.

$ErrorActionPreference = 'Stop'
$dir = 'C:\Windows\Installer'

$referenced = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$userData = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData'
$productKeys = 0; $patchKeys = 0
foreach ($sid in Get-ChildItem $userData) {
    $products = Join-Path $sid.PSPath 'Products'
    if (Test-Path $products) {
        foreach ($p in Get-ChildItem $products) {
            $productKeys++
            $ip = Join-Path $p.PSPath 'InstallProperties'
            if (Test-Path $ip) {
                $v = (Get-ItemProperty $ip).LocalPackage
                if ($v) { [void]$referenced.Add($v) }
            }
        }
    }
    $patches = Join-Path $sid.PSPath 'Patches'
    if (Test-Path $patches) {
        foreach ($p in Get-ChildItem $patches) {
            $patchKeys++
            $v = (Get-ItemProperty $p.PSPath).LocalPackage
            if ($v) { [void]$referenced.Add($v) }
        }
    }
}

$files = Get-ChildItem $dir -File -Force | Where-Object { $_.Extension -in '.msi', '.msp' }
$refExisting = 0
foreach ($r in $referenced) { if (Test-Path -LiteralPath $r) { $refExisting++ } }

"CONTROLS (an instrument that found nothing proves nothing):"
"  product keys read: $productKeys   patch keys read: $patchKeys"
"  distinct packages Windows points at: $($referenced.Count)   of which exist on disk: $refExisting"
"  package files found in the folder: $($files.Count)"
if ($referenced.Count -eq 0 -or $refExisting -eq 0 -or $files.Count -eq 0) {
    "BROKEN INSTRUMENT: one side of the comparison is empty. No conclusion can be drawn."
    exit 2
}

$kept = @(); $orphans = @()
foreach ($f in $files) { if ($referenced.Contains($f.FullName)) { $kept += $f } else { $orphans += $f } }

"RESULT:"
"  still needed (pointed at):  {0,5} files  {1,8:N2} GB" -f $kept.Count, (($kept | Measure-Object Length -Sum).Sum / 1GB)
"  orphans (nothing points):   {0,5} files  {1,8:N2} GB" -f $orphans.Count, (($orphans | Measure-Object Length -Sum).Sum / 1GB)
"  ten largest orphans:"
$orphans | Sort-Object Length -Descending | Select-Object -First 10 | ForEach-Object {
    "    {0,8:N1} MB  {1}  last written {2:yyyy-MM-dd}" -f ($_.Length / 1MB), $_.Name, $_.LastWriteTime
}

"OTHER CONTENT OF THE FOLDER (not judged by this script):"
Get-ChildItem $dir -Directory -Force | ForEach-Object {
    $s = (Get-ChildItem $_.FullName -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    [pscustomobject]@{ Name = $_.Name; GB = $s / 1GB }
} | Sort-Object GB -Descending | Select-Object -First 5 | ForEach-Object { "    {0,8:N2} GB  {1}" -f $_.GB, $_.Name }
