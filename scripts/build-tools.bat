@echo off
REM Build all cc-director tools and copy to %LOCALAPPDATA%\cc-director\bin
REM Usage: scripts\build-tools.bat (from cc-director monorepo root)

setlocal enabledelayedexpansion

echo ============================================
echo Building all cc-director tools to cc-director\bin
echo ============================================
echo.

set "REPO_DIR=%~dp0.."
set "INSTALL_DIR=%LOCALAPPDATA%\cc-director\bin"
set "FAILED="
set "SUCCESS_COUNT=0"
set "FAIL_COUNT=0"

REM Create install directory if it doesn't exist
if not exist "%INSTALL_DIR%" (
    echo Creating %INSTALL_DIR%...
    mkdir "%INSTALL_DIR%"
)

REM ============================================
REM Python tools (built with PyInstaller)
REM Directory names use underscores, exe names use dashes
REM ============================================
set "PYTHON_TOOLS=cc-comm-queue cc-crawl4ai cc-devthrottle cc-docgen cc-excel cc-facebook cc-gmail cc-hardware cc-html cc-image cc-outlook cc-pdf cc-playwright cc-photos cc-powerpoint cc-reddit cc-transcribe cc-twitter cc-vault cc-video cc-voice cc-whisper cc-word cc-youtube cc-youtube-info"

for %%T in (%PYTHON_TOOLS%) do (
    echo.
    echo --------------------------------------------
    echo Building %%T...
    echo --------------------------------------------

    set "TOOL_DIR=%REPO_DIR%\tools\%%T"

    REM Convert underscore to dash for exe name (cc-outlook -> cc-outlook)
    set "EXE_NAME=%%T"
    set "EXE_NAME=!EXE_NAME:_=-!"

    if exist "!TOOL_DIR!\build.ps1" (
        pushd "!TOOL_DIR!"
        powershell -ExecutionPolicy Bypass -File build.ps1

        if !errorlevel! equ 0 (
            REM Copy exe to install directory
            if exist "dist\!EXE_NAME!.exe" (
                copy /Y "dist\!EXE_NAME!.exe" "%INSTALL_DIR%\" >nul
                echo [OK] !EXE_NAME!.exe copied to %INSTALL_DIR%
                set /a SUCCESS_COUNT+=1
            ) else (
                echo [FAIL] !EXE_NAME!.exe not found after build
                set "FAILED=!FAILED! %%T"
                set /a FAIL_COUNT+=1
            )
        ) else (
            echo [FAIL] Build failed for %%T
            set "FAILED=!FAILED! %%T"
            set /a FAIL_COUNT+=1
        )
        popd
    ) else (
        echo [SKIP] No build.ps1 found for %%T
    )
)

REM ============================================
REM Node.js tools (cc-browser)
REM ============================================
echo.
echo --------------------------------------------
echo Building cc-browser (Node.js)...
echo --------------------------------------------

set "BROWSER_SRC=%REPO_DIR%\tools\cc-browser"
set "BROWSER_DEST=%INSTALL_DIR%\_cc-browser"

if exist "%BROWSER_SRC%\build.ps1" (
    pushd "%BROWSER_SRC%"
    powershell -ExecutionPolicy Bypass -File build.ps1

    if !errorlevel! equ 0 (
        REM Remove old directory name if present (migration from cc-browser to _cc-browser)
        if exist "%INSTALL_DIR%\cc-browser" rmdir /S /Q "%INSTALL_DIR%\cc-browser"
        REM Create destination directory
        if not exist "%BROWSER_DEST%" mkdir "%BROWSER_DEST%"
        if not exist "%BROWSER_DEST%\src" mkdir "%BROWSER_DEST%\src"

        REM Copy built files from dist
        copy /Y "dist\package.json" "%BROWSER_DEST%\" >nul
        copy /Y "dist\src\*.mjs" "%BROWSER_DEST%\src\" >nul

        REM Copy extension and native-host directories
        if not exist "%BROWSER_DEST%\extension" mkdir "%BROWSER_DEST%\extension"
        xcopy /E /I /Q /Y "dist\extension" "%BROWSER_DEST%\extension" >nul
        if not exist "%BROWSER_DEST%\native-host" mkdir "%BROWSER_DEST%\native-host"
        xcopy /E /I /Q /Y "dist\native-host" "%BROWSER_DEST%\native-host" >nul

        REM Copy node_modules
        if exist "%BROWSER_DEST%\node_modules" rmdir /S /Q "%BROWSER_DEST%\node_modules"
        xcopy /E /I /Q /Y "dist\node_modules" "%BROWSER_DEST%\node_modules" >nul

        REM Create launcher scripts in install dir (.cmd for Windows, extensionless for Git Bash)
        echo @node "%%~dp0_cc-browser\src\cli.mjs" %%*> "%INSTALL_DIR%\cc-browser.cmd"
        > "%INSTALL_DIR%\cc-browser" (
            echo #^^!/bin/sh
            echo node "$(dirname "$0")/_cc-browser/src/cli.mjs" "$@"
        )

        echo [OK] cc-browser installed to %BROWSER_DEST%
        set /a SUCCESS_COUNT+=1
    ) else (
        echo [FAIL] Build failed for cc-browser
        set "FAILED=!FAILED! cc-browser"
        set /a FAIL_COUNT+=1
    )
    popd
) else (
    echo [SKIP] No build.ps1 found for cc-browser
)

REM ============================================
REM Node.js tools (cc-fox-browser)
REM ============================================
echo.
echo --------------------------------------------
echo Building cc-fox-browser (Node.js)...
echo --------------------------------------------

set "FOXBROWSER_SRC=%REPO_DIR%\tools\cc-fox-browser"
set "FOXBROWSER_DEST=%INSTALL_DIR%\_cc-fox-browser"

if exist "%FOXBROWSER_SRC%\build.ps1" (
    pushd "%FOXBROWSER_SRC%"
    powershell -ExecutionPolicy Bypass -File build.ps1

    if !errorlevel! equ 0 (
        REM Create destination directory
        if not exist "%FOXBROWSER_DEST%" mkdir "%FOXBROWSER_DEST%"
        if not exist "%FOXBROWSER_DEST%\src" mkdir "%FOXBROWSER_DEST%\src"

        REM Copy built files from dist
        copy /Y "dist\package.json" "%FOXBROWSER_DEST%\" >nul
        copy /Y "dist\src\*.mjs" "%FOXBROWSER_DEST%\src\" >nul

        REM Copy node_modules
        if exist "%FOXBROWSER_DEST%\node_modules" rmdir /S /Q "%FOXBROWSER_DEST%\node_modules"
        xcopy /E /I /Q /Y "dist\node_modules" "%FOXBROWSER_DEST%\node_modules" >nul

        REM Create launcher scripts in install dir (.cmd for Windows, extensionless for Git Bash)
        echo @node "%%~dp0_cc-fox-browser\src\cli.mjs" %%*> "%INSTALL_DIR%\cc-fox-browser.cmd"
        > "%INSTALL_DIR%\cc-fox-browser" (
            echo #^^!/bin/sh
            echo node "$(dirname "$0")/_cc-fox-browser/src/cli.mjs" "$@"
        )

        echo [OK] cc-fox-browser installed to %FOXBROWSER_DEST%
        set /a SUCCESS_COUNT+=1
    ) else (
        echo [FAIL] Build failed for cc-fox-browser
        set "FAILED=!FAILED! cc-fox-browser"
        set /a FAIL_COUNT+=1
    )
    popd
) else (
    echo [SKIP] No build.ps1 found for cc-fox-browser
)

REM ============================================
REM Node.js tools (cc-brandingrecommendations)
REM ============================================
echo.
echo --------------------------------------------
echo Building cc-brandingrecommendations (Node.js)...
echo --------------------------------------------

set "BRANDREC_SRC=%REPO_DIR%\tools\cc-brandingrecommendations"
set "BRANDREC_DEST=%INSTALL_DIR%\_cc-brandingrecommendations"

if exist "%BRANDREC_SRC%\build.ps1" (
    pushd "%BRANDREC_SRC%"
    powershell -ExecutionPolicy Bypass -File build.ps1

    if !errorlevel! equ 0 (
        REM Remove old directory name if present
        if exist "%INSTALL_DIR%\cc-brandingrecommendations" rmdir /S /Q "%INSTALL_DIR%\cc-brandingrecommendations"
        REM Create destination directory
        if not exist "%BRANDREC_DEST%" mkdir "%BRANDREC_DEST%"
        if not exist "%BRANDREC_DEST%\src" mkdir "%BRANDREC_DEST%\src"
        if not exist "%BRANDREC_DEST%\src\generators" mkdir "%BRANDREC_DEST%\src\generators"
        if not exist "%BRANDREC_DEST%\src\formatters" mkdir "%BRANDREC_DEST%\src\formatters"
        if not exist "%BRANDREC_DEST%\src\data" mkdir "%BRANDREC_DEST%\src\data"

        REM Copy built files from dist
        copy /Y "dist\package.json" "%BRANDREC_DEST%\" >nul
        copy /Y "dist\src\*.mjs" "%BRANDREC_DEST%\src\" >nul
        copy /Y "dist\src\generators\*.mjs" "%BRANDREC_DEST%\src\generators\" >nul
        copy /Y "dist\src\formatters\*.mjs" "%BRANDREC_DEST%\src\formatters\" >nul
        copy /Y "dist\src\data\*.mjs" "%BRANDREC_DEST%\src\data\" >nul

        REM Copy node_modules
        if exist "%BRANDREC_DEST%\node_modules" rmdir /S /Q "%BRANDREC_DEST%\node_modules"
        xcopy /E /I /Q /Y "dist\node_modules" "%BRANDREC_DEST%\node_modules" >nul

        REM Create launcher scripts in install dir (.cmd for Windows, extensionless for Git Bash)
        echo @node "%%~dp0_cc-brandingrecommendations\src\cli.mjs" %%*> "%INSTALL_DIR%\cc-brandingrecommendations.cmd"
        > "%INSTALL_DIR%\cc-brandingrecommendations" (
            echo #^^!/bin/sh
            echo node "$(dirname "$0")/_cc-brandingrecommendations/src/cli.mjs" "$@"
        )

        echo [OK] cc-brandingrecommendations installed to %BRANDREC_DEST%
        set /a SUCCESS_COUNT+=1
    ) else (
        echo [FAIL] Build failed for cc-brandingrecommendations
        set "FAILED=!FAILED! cc-brandingrecommendations"
        set /a FAIL_COUNT+=1
    )
    popd
) else (
    echo [SKIP] No build.ps1 found for cc-brandingrecommendations
)

REM ============================================
REM Node.js tools (cc-websiteaudit)
REM ============================================
echo.
echo --------------------------------------------
echo Building cc-websiteaudit (Node.js)...
echo --------------------------------------------

set "WSAUDIT_SRC=%REPO_DIR%\tools\cc-websiteaudit"
set "WSAUDIT_DEST=%INSTALL_DIR%\_cc-websiteaudit"

if exist "%WSAUDIT_SRC%\build.ps1" (
    pushd "%WSAUDIT_SRC%"
    powershell -ExecutionPolicy Bypass -File build.ps1

    if !errorlevel! equ 0 (
        REM Create destination directory
        if not exist "%WSAUDIT_DEST%" mkdir "%WSAUDIT_DEST%"
        if not exist "%WSAUDIT_DEST%\src" mkdir "%WSAUDIT_DEST%\src"
        if not exist "%WSAUDIT_DEST%\src\analyzers" mkdir "%WSAUDIT_DEST%\src\analyzers"

        REM Copy built files from dist
        copy /Y "dist\package.json" "%WSAUDIT_DEST%\" >nul
        copy /Y "dist\src\*.mjs" "%WSAUDIT_DEST%\src\" >nul
        copy /Y "dist\src\analyzers\*.mjs" "%WSAUDIT_DEST%\src\analyzers\" >nul

        REM Copy node_modules
        if exist "%WSAUDIT_DEST%\node_modules" rmdir /S /Q "%WSAUDIT_DEST%\node_modules"
        xcopy /E /I /Q /Y "dist\node_modules" "%WSAUDIT_DEST%\node_modules" >nul

        REM Create launcher scripts in install dir (.cmd for Windows, extensionless for Git Bash)
        echo @node "%%~dp0_cc-websiteaudit\src\cli.mjs" %%*> "%INSTALL_DIR%\cc-websiteaudit.cmd"
        > "%INSTALL_DIR%\cc-websiteaudit" (
            echo #^^!/bin/sh
            echo node "$(dirname "$0")/_cc-websiteaudit/src/cli.mjs" "$@"
        )

        echo [OK] cc-websiteaudit installed to %WSAUDIT_DEST%
        set /a SUCCESS_COUNT+=1
    ) else (
        echo [FAIL] Build failed for cc-websiteaudit
        set "FAILED=!FAILED! cc-websiteaudit"
        set /a FAIL_COUNT+=1
    )
    popd
) else (
    echo [SKIP] No build.ps1 found for cc-websiteaudit
)

REM ============================================
REM .NET tools (cc-click, cc-trisight)
REM ============================================
echo.
echo --------------------------------------------
echo Building cc-click (.NET)...
echo --------------------------------------------

set "CCCLICK_SRC=%REPO_DIR%\tools\cc-click"
set "CCCLICK_DEST=%INSTALL_DIR%\_cc-click"

if exist "%CCCLICK_SRC%\cc-click.slnx" (
    pushd "%CCCLICK_SRC%"
    dotnet publish -c Release -o "%CCCLICK_DEST%"

    if !errorlevel! equ 0 (
        REM Remove old directory name if present
        if exist "%INSTALL_DIR%\cc-click" rmdir /S /Q "%INSTALL_DIR%\cc-click"
        REM Create launcher scripts (.cmd for Windows, extensionless for Git Bash)
        echo @"%%~dp0_cc-click\cc-click.exe" %%*> "%INSTALL_DIR%\cc-click.cmd"
        > "%INSTALL_DIR%\cc-click" (
            echo #^^!/bin/sh
            echo "$(dirname "$0")/_cc-click/cc-click.exe" "$@"
        )
        echo [OK] cc-click installed to %CCCLICK_DEST%
        set /a SUCCESS_COUNT+=1
    ) else (
        echo [FAIL] Build failed for cc-click
        set "FAILED=!FAILED! cc-click"
        set /a FAIL_COUNT+=1
    )
    popd
) else (
    echo [SKIP] No cc-click.slnx found
)

echo.
echo --------------------------------------------
echo Building cc-trisight (.NET)...
echo --------------------------------------------

set "TRISIGHT_SRC=%REPO_DIR%\tools\cc-trisight"
set "TRISIGHT_DEST=%INSTALL_DIR%\_cc-trisight"

if exist "%TRISIGHT_SRC%\cc-trisight.slnx" (
    pushd "%TRISIGHT_SRC%"
    dotnet publish -c Release -o "%TRISIGHT_DEST%"

    if !errorlevel! equ 0 (
        REM Remove old directory name if present
        if exist "%INSTALL_DIR%\cc-trisight" rmdir /S /Q "%INSTALL_DIR%\cc-trisight"
        REM Create launcher scripts (.cmd for Windows, extensionless for Git Bash)
        echo @"%%~dp0_cc-trisight\cc-trisight.exe" %%*> "%INSTALL_DIR%\cc-trisight.cmd"
        > "%INSTALL_DIR%\cc-trisight" (
            echo #^^!/bin/sh
            echo "$(dirname "$0")/_cc-trisight/cc-trisight.exe" "$@"
        )
        echo [OK] cc-trisight installed to %TRISIGHT_DEST%
        set /a SUCCESS_COUNT+=1
    ) else (
        echo [FAIL] Build failed for cc-trisight
        set "FAILED=!FAILED! cc-trisight"
        set /a FAIL_COUNT+=1
    )
    popd
) else (
    echo [SKIP] No cc-trisight.slnx found
)

echo.
echo --------------------------------------------
echo Building cc-computer (.NET)...
echo --------------------------------------------

set "CCCOMPUTER_SRC=%REPO_DIR%\tools\cc-computer"
set "CCCOMPUTER_DEST=%INSTALL_DIR%\_cc-computer"

if exist "%CCCOMPUTER_SRC%\cc-computer.slnx" (
    pushd "%CCCOMPUTER_SRC%"
    dotnet publish ComputerApp -c Release -o "%CCCOMPUTER_DEST%"

    if !errorlevel! equ 0 (
        REM Remove old directory name if present
        if exist "%INSTALL_DIR%\cc-computer" rmdir /S /Q "%INSTALL_DIR%\cc-computer"
        REM Create launcher scripts for CLI and GUI modes (.cmd for Windows, extensionless for Git Bash)
        echo @"%%~dp0_cc-computer\cc-computer.exe" --cli %%*> "%INSTALL_DIR%\cc-computer.cmd"
        echo @"%%~dp0_cc-computer\cc-computer.exe" %%*> "%INSTALL_DIR%\cc-computer-gui.cmd"
        > "%INSTALL_DIR%\cc-computer" (
            echo #^^!/bin/sh
            echo "$(dirname "$0")/_cc-computer/cc-computer.exe" --cli "$@"
        )
        > "%INSTALL_DIR%\cc-computer-gui" (
            echo #^^!/bin/sh
            echo "$(dirname "$0")/_cc-computer/cc-computer.exe" "$@"
        )
        echo [OK] cc-computer installed to %CCCOMPUTER_DEST%
        set /a SUCCESS_COUNT+=1
    ) else (
        echo [FAIL] Build failed for cc-computer
        set "FAILED=!FAILED! cc-computer"
        set /a FAIL_COUNT+=1
    )
    popd
) else (
    echo [SKIP] No cc-computer.slnx found
)

REM ============================================
REM Copy documentation
REM ============================================
echo.
echo --------------------------------------------
echo Copying documentation...
echo --------------------------------------------

if exist "%REPO_DIR%\docs\CC_TOOLS.md" (
    copy /Y "%REPO_DIR%\docs\CC_TOOLS.md" "%INSTALL_DIR%\" >nul
    echo [OK] CC_TOOLS.md copied to %INSTALL_DIR%
) else (
    echo [SKIP] docs\CC_TOOLS.md not found
)

REM Copy helper batch files
if exist "%REPO_DIR%\tools\cc-gmail\cc-gmail_auth.bat" (
    copy /Y "%REPO_DIR%\tools\cc-gmail\cc-gmail_auth.bat" "%INSTALL_DIR%\" >nul
    echo [OK] cc-gmail_auth.bat copied to %INSTALL_DIR%
)

:summary
echo.
echo ============================================
echo Build Summary
echo ============================================
echo Successful: %SUCCESS_COUNT%
echo Failed: %FAIL_COUNT%
if defined FAILED (
    echo Failed tools:%FAILED%
    exit /b 1
) else (
    echo All tools built successfully!
    echo.
    echo Executables installed to: %INSTALL_DIR%
    echo.
    echo Run scripts\install-tools.bat to add %INSTALL_DIR% to your PATH
)
