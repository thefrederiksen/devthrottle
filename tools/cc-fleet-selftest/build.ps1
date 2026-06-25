# Build cc-fleet-selftest executable
# Usage: .\build.ps1

$ErrorActionPreference = "Stop"

Write-Host "Building cc-fleet-selftest..." -ForegroundColor Cyan

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

if (-not (Test-Path ".venv")) {
    Write-Host "Creating virtual environment..."
    python -m venv .venv
}

. .venv\Scripts\Activate.ps1

Write-Host "Installing dependencies..."
pip install --quiet --upgrade pip
pip install --quiet -r requirements.txt

Write-Host "Building executable..."
pyinstaller --clean --noconfirm cc-fleet-selftest.spec

if (Test-Path "dist\cc-fleet-selftest.exe") {
    $size = (Get-Item "dist\cc-fleet-selftest.exe").Length / 1MB
    Write-Host "[OK] Built dist\cc-fleet-selftest.exe ($([math]::Round($size, 1)) MB)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "[FAIL] Build failed - cc-fleet-selftest.exe not found" -ForegroundColor Red
    exit 1
}
