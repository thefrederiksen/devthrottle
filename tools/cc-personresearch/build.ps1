# Build script for cc-personresearch
# Creates standalone executable using PyInstaller

$ErrorActionPreference = "Stop"

Write-Host "Building cc-personresearch..." -ForegroundColor Cyan

# Ensure we're in the right directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# Create/activate virtual environment
if (-not (Test-Path "venv")) {
    Write-Host "Creating virtual environment..." -ForegroundColor Yellow
    python -m venv venv
}

# Activate venv
. .\venv\Scripts\Activate.ps1

# Install dependencies
Write-Host "Installing dependencies..." -ForegroundColor Yellow
pip install -r requirements.txt
pip install pyinstaller

# Build executable
Write-Host "Building executable..." -ForegroundColor Yellow
pyinstaller cc-personresearch.spec --clean

# Check result
$exePath = "dist\cc-personresearch.exe"
if (Test-Path $exePath) {
    $size = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
    Write-Host "SUCCESS: Built $exePath ($size MB)" -ForegroundColor Green

    # Copy to %LOCALAPPDATA%\cc-director\bin (create the dir first; it may not exist on a clean build host)
    $binDir = "$env:LOCALAPPDATA\cc-director\bin"
    New-Item -ItemType Directory -Force -Path $binDir | Out-Null
    $targetPath = "$binDir\cc-personresearch.exe"
    Write-Host "Copying to $targetPath..." -ForegroundColor Yellow
    Copy-Item $exePath $targetPath -Force
    Write-Host "SUCCESS: Copied to $targetPath" -ForegroundColor Green
} else {
    Write-Host "ERROR: Build failed - executable not found" -ForegroundColor Red
    exit 1
}
