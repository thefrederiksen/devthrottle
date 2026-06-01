# Build script for cc-docgen executable
# Usage: .\build.ps1

$ErrorActionPreference = "Stop"

Write-Host "Building cc-docgen executable..." -ForegroundColor Cyan

# Check for Python
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
    Write-Host "ERROR: Python not found. Please install Python 3.11 or later." -ForegroundColor Red
    exit 1
}

# Create virtual environment if it doesn't exist
if (-not (Test-Path "venv")) {
    Write-Host "Creating virtual environment..." -ForegroundColor Yellow
    python -m venv venv
}

# Activate virtual environment
Write-Host "Activating virtual environment..." -ForegroundColor Yellow
& .\venv\Scripts\Activate.ps1

# Install dependencies
Write-Host "Installing dependencies..." -ForegroundColor Yellow
pip install -e ".[dev]"

# Build with PyInstaller
Write-Host "Building executable with PyInstaller..." -ForegroundColor Yellow
pyinstaller cc-docgen.spec --clean --noconfirm

# Check result
$exePath = "dist\cc-docgen.exe"
if (Test-Path $exePath) {
    $size = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
    Write-Host "SUCCESS: Built $exePath ($size MB)" -ForegroundColor Green

    # Copy to %LOCALAPPDATA%\cc-director\bin (create the dir first; it may not exist on a clean build host)
    $binDir = "$env:LOCALAPPDATA\cc-director\bin"
    New-Item -ItemType Directory -Force -Path $binDir | Out-Null
    $targetPath = "$binDir\cc-docgen.exe"
    Write-Host "Copying to $targetPath..." -ForegroundColor Yellow
    Copy-Item $exePath $targetPath -Force
    Write-Host "SUCCESS: Copied to $targetPath" -ForegroundColor Green
} else {
    Write-Host "ERROR: Build failed - executable not found" -ForegroundColor Red
    exit 1
}
