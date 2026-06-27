#Requires -Version 5.1
<#
.SYNOPSIS
    Builds all cc-director tools (Python, Node.js, .NET) and collects artifacts.

.DESCRIPTION
    Orchestrates building every tool in the tools/ directory by calling each
    tool's build.ps1 script. Collects output executables into a single
    artifacts directory for release packaging.

    Supports three modes:
    - Default: builds and copies to %LOCALAPPDATA%\cc-director\bin (local dev)
    - -ArtifactsDir: builds and copies to specified directory (CI/release)
    - Individual tool: -Tool cc-markdown (build one tool only)

.PARAMETER ArtifactsDir
    Directory to collect built artifacts. If not specified, uses
    %LOCALAPPDATA%\cc-director\bin (local development mode).

.PARAMETER Tool
    Build a single tool by name (e.g., cc-markdown). If not specified,
    builds all tools.

.PARAMETER SkipPython
    Skip building Python tools.

.PARAMETER SkipNode
    Skip building Node.js tools.

.PARAMETER SkipDotnet
    Skip building .NET tools.

.EXAMPLE
    .\scripts\build-all-tools.ps1
    .\scripts\build-all-tools.ps1 -ArtifactsDir artifacts
    .\scripts\build-all-tools.ps1 -Tool cc-markdown
#>
param(
    [string]$ArtifactsDir,
    [string]$Tool,
    [switch]$SkipPython,
    [switch]$SkipNode,
    [switch]$SkipDotnet
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsDir = Join-Path $repoRoot "tools"

# Determine output directory
if ($ArtifactsDir) {
    if (-not [System.IO.Path]::IsPathRooted($ArtifactsDir)) {
        $ArtifactsDir = Join-Path $repoRoot $ArtifactsDir
    }
} else {
    $ArtifactsDir = Join-Path $env:LOCALAPPDATA "cc-director\bin"
}

if (-not (Test-Path $ArtifactsDir)) {
    New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Building cc-director tools" -ForegroundColor Cyan
Write-Host "Output: $ArtifactsDir" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# Tool definitions
$pythonTools = @(
    "cc-comm-queue",
    "cc-crawl4ai",
    "cc-devthrottle",
    "cc-docgen",
    "cc-excel",
    "cc-gmail",
    "cc-hardware",
    "cc-html",
    "cc-image",
    "cc-outlook",
    "cc-pdf",
    "cc-playwright",
    "cc-photos",
    "cc-powerpoint",
    "cc-reddit",
    "cc-transcribe",
    "cc-vault",
    "cc-video",
    "cc-voice",
    "cc-whisper",
    "cc-word",
    "cc-youtube-info"
)

$nodeTools = @(
    "cc-browser",
    "cc-brandingrecommendations",
    "cc-websiteaudit"
)

$dotnetTools = @(
    @{ Name = "cc-click"; Solution = "cc-click.slnx"; Project = $null },
    @{ Name = "cc-trisight"; Solution = "cc-trisight.slnx"; Project = $null },
    @{ Name = "cc-computer"; Solution = "cc-computer.slnx"; Project = "ComputerApp" }
)

$successCount = 0
$failCount = 0
$failedTools = @()

function Build-PythonTool {
    param([string]$toolName)

    $toolDir = Join-Path $toolsDir $toolName
    $buildScript = Join-Path $toolDir "build.ps1"

    if (-not (Test-Path $buildScript)) {
        Write-Host "  [SKIP] No build.ps1 found" -ForegroundColor Yellow
        return $true
    }

    Push-Location $toolDir
    try {
        & powershell -ExecutionPolicy Bypass -File build.ps1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  [FAIL] Build failed" -ForegroundColor Red
            return $false
        }

        $exeName = "$toolName.exe"

        $exePath = Join-Path $toolDir "dist\$exeName"
        if (Test-Path $exePath) {
            Copy-Item $exePath (Join-Path $ArtifactsDir $exeName) -Force
            Write-Host "  [OK] $exeName" -ForegroundColor Green
            return $true
        } else {
            Write-Host "  [FAIL] $exeName not found after build" -ForegroundColor Red
            return $false
        }
    } finally {
        Pop-Location
    }
}

function Build-NodeTool {
    param([string]$toolName)

    $toolDir = Join-Path $toolsDir $toolName
    $buildScript = Join-Path $toolDir "build.ps1"

    if (-not (Test-Path $buildScript)) {
        Write-Host "  [SKIP] No build.ps1 found" -ForegroundColor Yellow
        return $true
    }

    Push-Location $toolDir
    try {
        & powershell -ExecutionPolicy Bypass -File build.ps1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  [FAIL] Build failed" -ForegroundColor Red
            return $false
        }

        # Node tools produce a dist/ folder; copy entire dist to artifacts
        $distDir = Join-Path $toolDir "dist"
        $destDir = Join-Path $ArtifactsDir "_$toolName"

        if (Test-Path $distDir) {
            if (Test-Path $destDir) {
                Remove-Item $destDir -Recurse -Force
            }
            Copy-Item $distDir $destDir -Recurse -Force
            Write-Host "  [OK] $toolName -> _$toolName/" -ForegroundColor Green
            return $true
        } else {
            Write-Host "  [FAIL] dist/ not found after build" -ForegroundColor Red
            return $false
        }
    } finally {
        Pop-Location
    }
}

function Build-DotnetTool {
    param([hashtable]$tool)

    $toolDir = Join-Path $toolsDir $tool.Name
    $destDir = Join-Path $ArtifactsDir "_$($tool.Name)"

    if (-not (Test-Path (Join-Path $toolDir $tool.Solution))) {
        Write-Host "  [SKIP] $($tool.Solution) not found" -ForegroundColor Yellow
        return $true
    }

    Push-Location $toolDir
    try {
        $publishArgs = @("publish", "-c", "Release", "-o", $destDir)
        if ($tool.Project) {
            $publishArgs = @("publish", $tool.Project, "-c", "Release", "-o", $destDir)
        }

        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  [FAIL] Build failed" -ForegroundColor Red
            return $false
        }

        Write-Host "  [OK] $($tool.Name) -> _$($tool.Name)/" -ForegroundColor Green
        return $true
    } finally {
        Pop-Location
    }
}

# Filter to single tool if specified
if ($Tool) {
    if ($Tool -in $pythonTools) {
        $pythonTools = @($Tool)
        $nodeTools = @()
        $dotnetTools = @()
    } elseif ($Tool -in $nodeTools) {
        $pythonTools = @()
        $nodeTools = @($Tool)
        $dotnetTools = @()
    } elseif ($Tool -in ($dotnetTools | ForEach-Object { $_.Name })) {
        $pythonTools = @()
        $nodeTools = @()
        $dotnetTools = $dotnetTools | Where-Object { $_.Name -eq $Tool }
    } else {
        Write-Error "Unknown tool: $Tool"
        exit 1
    }
}

# Build Python tools
if (-not $SkipPython -and $pythonTools.Count -gt 0) {
    Write-Host ""
    Write-Host "--- Python Tools ($($pythonTools.Count)) ---" -ForegroundColor Cyan
    foreach ($tool in $pythonTools) {
        Write-Host ""
        Write-Host "Building $tool..." -ForegroundColor White
        if (Build-PythonTool $tool) {
            $successCount++
        } else {
            $failCount++
            $failedTools += $tool
        }
    }
}

# Build Node.js tools
if (-not $SkipNode -and $nodeTools.Count -gt 0) {
    Write-Host ""
    Write-Host "--- Node.js Tools ($($nodeTools.Count)) ---" -ForegroundColor Cyan
    foreach ($tool in $nodeTools) {
        Write-Host ""
        Write-Host "Building $tool..." -ForegroundColor White
        if (Build-NodeTool $tool) {
            $successCount++
        } else {
            $failCount++
            $failedTools += $tool
        }
    }
}

# Build .NET tools
if (-not $SkipDotnet -and $dotnetTools.Count -gt 0) {
    Write-Host ""
    Write-Host "--- .NET Tools ($($dotnetTools.Count)) ---" -ForegroundColor Cyan
    foreach ($tool in $dotnetTools) {
        Write-Host ""
        Write-Host "Building $($tool.Name)..." -ForegroundColor White
        if (Build-DotnetTool $tool) {
            $successCount++
        } else {
            $failCount++
            $failedTools += $tool.Name
        }
    }
}

# Copy documentation
$docsFile = Join-Path $repoRoot "docs\CC_TOOLS.md"
if (Test-Path $docsFile) {
    Copy-Item $docsFile (Join-Path $ArtifactsDir "CC_TOOLS.md") -Force
    Write-Host ""
    Write-Host "[OK] CC_TOOLS.md copied" -ForegroundColor Green
}

# Summary
Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Build Summary" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Successful: $successCount" -ForegroundColor Green
Write-Host "Failed:     $failCount" -ForegroundColor $(if ($failCount -gt 0) { "Red" } else { "Green" })

if ($failedTools.Count -gt 0) {
    Write-Host "Failed tools: $($failedTools -join ', ')" -ForegroundColor Red
    exit 1
} else {
    Write-Host ""
    Write-Host "All tools built successfully!" -ForegroundColor Green
    Write-Host "Output: $ArtifactsDir" -ForegroundColor Green
}
