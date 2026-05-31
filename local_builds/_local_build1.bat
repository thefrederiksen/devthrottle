@echo off
powershell -ExecutionPolicy Bypass -File "%~dp0..\scripts\local-build-avalonia.ps1" -Slot 1 %*
if %ERRORLEVEL% neq 0 (
    echo.
    echo BUILD FAILED - see errors above
    pause
    exit /b %ERRORLEVEL%
)
echo.
echo Exe location: %~dp0cc-director1.exe
pause
