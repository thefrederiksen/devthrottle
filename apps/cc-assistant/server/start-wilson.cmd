@echo off
rem Starts Wilson's service. Used by the "Wilson" scheduled task at logon, and fine by hand.
rem
rem The Groq key comes from cc-secrets (entry groq-api-key), which supplies it to Wilson alone as
rem GROQ_API_KEY. cc-secrets hands back the service's output only when it exits, so Wilson writes
rem its own running log to %LOCALAPPDATA%\wilson\service.log; this script appends the start, whatever
rem cc-secrets returns at exit (including a startup failure), and the exit code to the same file.

setlocal
cd /d "%~dp0.."
if not exist "%LOCALAPPDATA%\wilson" mkdir "%LOCALAPPDATA%\wilson"
echo %date% %time% starting Wilson from %cd% >> "%LOCALAPPDATA%\wilson\service.log"
cc-secrets run groq-api-key --timeout 0 -- node server\wilson.mjs >> "%LOCALAPPDATA%\wilson\service.log" 2>&1
echo %date% %time% Wilson exited with %errorlevel% >> "%LOCALAPPDATA%\wilson\service.log"
endlocal
