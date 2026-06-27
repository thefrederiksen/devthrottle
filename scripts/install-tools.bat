@echo off
REM Add %LOCALAPPDATA%\cc-director\bin to user PATH (one-time setup)
REM Usage: scripts\install.bat

setlocal

set "INSTALL_DIR=%LOCALAPPDATA%\cc-director\bin"

echo ============================================
echo cc-director PATH Installation
echo ============================================
echo.

REM Create install directory if it doesn't exist
if not exist "%INSTALL_DIR%" (
    echo Creating %INSTALL_DIR%...
    mkdir "%INSTALL_DIR%"
)

REM Check if already in PATH
echo %PATH% | findstr /I /C:"%INSTALL_DIR%" >nul
if %errorlevel% equ 0 (
    echo [OK] %INSTALL_DIR% is already in PATH
    goto :done
)

REM Add to user PATH using PowerShell
echo Adding %INSTALL_DIR% to user PATH...

powershell -Command "$p=[Environment]::GetEnvironmentVariable('Path','User'); $oldLegacy='C:\cc-tools'; $oldBin=$env:LOCALAPPDATA+'\cc-tools\bin'; $new=$env:LOCALAPPDATA+'\cc-director\bin'; $parts=$p -split ';' | Where-Object {$_ -ne $oldLegacy -and $_ -ne $oldBin -and $_ -ne ''}; if($parts -notcontains $new){$parts+=$new}; [Environment]::SetEnvironmentVariable('Path',($parts -join ';'),'User'); Write-Host '[OK] PATH updated: removed legacy cc-tools entries, added' $new"

if %errorlevel% neq 0 (
    echo [FAIL] Could not add to PATH
    echo Please add %INSTALL_DIR% to your PATH manually
    exit /b 1
)

:done
echo.
echo ============================================
echo Installation Complete
echo ============================================
echo.
echo IMPORTANT: Open a NEW terminal for PATH changes to take effect.
echo.
echo Available tools:
echo.
echo Python tools (compiled executables):
if exist "%INSTALL_DIR%\cc-crawl4ai.exe" echo   - cc-crawl4ai
if exist "%INSTALL_DIR%\cc-devthrottle.exe" echo   - cc-devthrottle
if exist "%INSTALL_DIR%\cc-gmail.exe" echo   - cc-gmail
if exist "%INSTALL_DIR%\cc-image.exe" echo   - cc-image
if exist "%INSTALL_DIR%\cc-html.exe" echo   - cc-html
if exist "%INSTALL_DIR%\cc-pdf.exe" echo   - cc-pdf
if exist "%INSTALL_DIR%\cc-word.exe" echo   - cc-word
if exist "%INSTALL_DIR%\cc-outlook.exe" echo   - cc-outlook
if exist "%INSTALL_DIR%\cc-reddit.exe" echo   - cc-reddit
if exist "%INSTALL_DIR%\cc-transcribe.exe" echo   - cc-transcribe
if exist "%INSTALL_DIR%\cc-vault.exe" echo   - cc-vault
if exist "%INSTALL_DIR%\cc-video.exe" echo   - cc-video
if exist "%INSTALL_DIR%\cc-voice.exe" echo   - cc-voice
if exist "%INSTALL_DIR%\cc-whisper.exe" echo   - cc-whisper
if exist "%INSTALL_DIR%\cc-youtube-info.exe" echo   - cc-youtube-info
if exist "%INSTALL_DIR%\cc-hardware.exe" echo   - cc-hardware
if exist "%INSTALL_DIR%\cc-photos.exe" echo   - cc-photos
if exist "%INSTALL_DIR%\cc-comm-queue.exe" echo   - cc-comm-queue
echo.
echo Node.js tools (with .cmd + Git Bash launchers):
if exist "%INSTALL_DIR%\cc-browser.cmd" echo   - cc-browser
if exist "%INSTALL_DIR%\cc-brandingrecommendations.cmd" echo   - cc-brandingrecommendations
echo.
echo .NET tools (with .cmd + Git Bash launchers):
if exist "%INSTALL_DIR%\cc-click.cmd" echo   - cc-click
if exist "%INSTALL_DIR%\cc-trisight.cmd" echo   - cc-trisight
if exist "%INSTALL_DIR%\cc-computer.cmd" echo   - cc-computer
echo.
echo All tools work in CMD, PowerShell, and Git Bash (used by Claude Code).
echo Run any tool with --help for usage info.
