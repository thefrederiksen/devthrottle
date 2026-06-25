# Build cc-ask executable
# Usage: .\build.ps1

$ErrorActionPreference = "Stop"

Write-Host "Building cc-ask..." -ForegroundColor Cyan

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
pyinstaller --clean --noconfirm cc-ask.spec

if (Test-Path "dist\cc-ask.exe") {
    $size = (Get-Item "dist\cc-ask.exe").Length / 1MB
    Write-Host "[OK] Built dist\cc-ask.exe ($([math]::Round($size, 1)) MB)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "[FAIL] Build failed - cc-ask.exe not found" -ForegroundColor Red
    exit 1
}
