# The S5 screenshots (devthrottle_internal#2304). Needs playwright-core installed beside these files (npm i playwright-core)
# and the Playwright Chromium in %LOCALAPPDATA%\ms-playwright. Foreground: start the stand-in Gateway and the Cockpit dev server, capture S5 per role, stop both.
$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = (Resolve-Path (Join-Path $here '..')).Path
New-Item -ItemType Directory -Force $out | Out-Null
$stub = Start-Process node -ArgumentList "`"$here\stub-gateway.mjs`"" -PassThru -NoNewWindow
$env:COCKPIT_PROXY_TARGET = 'http://127.0.0.1:5299'
$vite = Start-Process cmd -ArgumentList '/c','npx vite --port 5317 --strictPort --host 127.0.0.1' -WorkingDirectory (Resolve-Path (Join-Path $here '..\..\..\..\apps\cockpit')).Path -PassThru -NoNewWindow
try {
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        try { Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5317/ -TimeoutSec 3 | Out-Null; break } catch { Start-Sleep -Seconds 2 }
    }
    Push-Location $here
    cmd /c "node shoot.mjs $out 2>&1"
    Pop-Location
}
finally {
    foreach ($p in @($vite, $stub)) { if ($p -and -not $p.HasExited) { & taskkill /PID $p.Id /T /F | Out-Null } }
    Write-Output "servers stopped"
}

