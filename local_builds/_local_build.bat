@echo off
REM Builds the MAIN CC Director (cc-director.exe, no slot number) and installs the
REM Start Menu shortcut. Slots 1-4 are for development; this is the daily-driver.
powershell -ExecutionPolicy Bypass -File "%~dp0..\scripts\install-cc-director-main.ps1" %*
if %ERRORLEVEL% neq 0 (
    echo.
    echo BUILD FAILED - see errors above
    pause
    exit /b %ERRORLEVEL%
)
echo.
echo Exe location: %~dp0cc-director.exe
pause
