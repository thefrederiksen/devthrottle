# Proof harness for issue #580 - Director startup account gate.
# Launches the slot-13 test Director under three scenarios (no credential / credential+online /
# credential+offline), screenshots the resulting window, and extracts the gate decision log lines.
# This is a NON-session-creating test (it only exercises which window appears at startup and the
# startup log), so launching the slot exe directly is permitted. Only my own slot-13 exe is stopped.
#
# ASCII output only.

param(
    [string]$Exe = "D:\ReposFred\devthrottle-wt-580\local_builds\cc-director13.exe",
    [string]$OutDir = "D:\ReposFred\devthrottle-wt-580\docs\cencon\proof\issue-580"
)

$ErrorActionPreference = "Stop"
$SigningSecret = "test-signing-secret-for-issue-583-credential-store"

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

function New-TestJwt([datetime]$expiresUtc, [string]$secret) {
    function B64Url([byte[]]$b) {
        [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    }
    $headerJson = '{"alg":"HS256","typ":"JWT"}'
    $exp = [DateTimeOffset]::new($expiresUtc, [TimeSpan]::Zero).ToUnixTimeSeconds()
    $payloadJson = '{"sub":"test-user","exp":' + $exp + '}'
    $header = B64Url ([Text.Encoding]::UTF8.GetBytes($headerJson))
    $payload = B64Url ([Text.Encoding]::UTF8.GetBytes($payloadJson))
    $signingInput = "$header.$payload"
    $hmac = New-Object System.Security.Cryptography.HMACSHA256
    $hmac.Key = [Text.Encoding]::UTF8.GetBytes($secret)
    $sig = B64Url ($hmac.ComputeHash([Text.Encoding]::ASCII.GetBytes($signingInput)))
    return "$signingInput.$sig"
}

function Capture-Screenshot([string]$path) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Stop-SlotDirector([string]$exePath) {
    Get-Process -Name "cc-director13" -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -eq $exePath
    } | ForEach-Object {
        Write-Host "[proof] stopping slot Director PID $($_.Id) ($($_.Path))"
        Stop-Process -Id $_.Id -Force
    }
}

function Run-Scenario {
    param(
        [string]$Name,
        [string]$Root,
        [hashtable]$Env,
        [string]$ShotPath
    )
    Write-Host "=== Scenario: $Name ==="

    # Clean isolated profile root so each scenario starts from a known state.
    if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
    New-Item -ItemType Directory -Force -Path $Root | Out-Null

    Stop-SlotDirector $Exe

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.WorkingDirectory = Split-Path $Exe
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables["CC_DIRECTOR_ROOT"] = $Root
    foreach ($k in $Env.Keys) { $psi.EnvironmentVariables[$k] = $Env[$k] }

    $startUtc = (Get-Date).ToUniversalTime()
    $proc = [System.Diagnostics.Process]::Start($psi)
    Write-Host "[proof] launched PID $($proc.Id), root=$Root"

    # Give the splash + gate decision + window time to appear and render.
    Start-Sleep -Seconds 14
    Capture-Screenshot $ShotPath
    Write-Host "[proof] screenshot -> $ShotPath"

    $logDir = Join-Path $Root "logs\director"
    $logFile = Get-ChildItem -Path $logDir -Filter "director-*-$($proc.Id).log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1

    Stop-SlotDirector $Exe
    return [pscustomobject]@{ StartUtc = $startUtc; Pid = $proc.Id; LogFile = $(if ($logFile) { $logFile.FullName } else { "" }) }
}

$results = @()

# Scenario A: clean profile, NO stored credential -> gate screen, main window blocked.
$rootA = "$env:TEMP\cc-dt-580-A"
$rA = Run-Scenario -Name "no-credential" -Root $rootA -Env @{} `
    -ShotPath (Join-Path $OutDir "scenario-a-no-credential.png")
$results += [pscustomobject]@{ Scenario = "no-credential"; Root = $rootA; Result = $rA }

# Scenario B: stored test credential + networking enabled -> main window + background validation.
$rootB = "$env:TEMP\cc-dt-580-B"
$jwtB = New-TestJwt ((Get-Date).ToUniversalTime().AddHours(1)) $SigningSecret
$rB = Run-Scenario -Name "credential-online" -Root $rootB -Env @{
    "DEVTHROTTLE_JWT_SIGNING_SECRET" = $SigningSecret
    "DEVTHROTTLE_TEST_SEED_TOKEN"    = "$jwtB`ntest-refresh-token"
} -ShotPath (Join-Path $OutDir "scenario-b-credential-online.png")
$results += [pscustomobject]@{ Scenario = "credential-online"; Root = $rootB; Result = $rB }

# Scenario C: stored test credential + networking disabled -> main window.
# The BackendUnavailableTokenRefresher already makes no network call, so the running Director is
# offline-equivalent; we additionally use an expired token so the background refresh path is the
# offline path (refresh attempted, reported unavailable, cached credential kept).
$rootC = "$env:TEMP\cc-dt-580-C"
$jwtC = New-TestJwt ((Get-Date).ToUniversalTime().AddHours(-1)) $SigningSecret
$rC = Run-Scenario -Name "credential-offline" -Root $rootC -Env @{
    "DEVTHROTTLE_JWT_SIGNING_SECRET" = $SigningSecret
    "DEVTHROTTLE_TEST_SEED_TOKEN"    = "$jwtC`ntest-refresh-token"
} -ShotPath (Join-Path $OutDir "scenario-c-credential-offline.png")
$results += [pscustomobject]@{ Scenario = "credential-offline"; Root = $rootC; Result = $rC }

Write-Host ""
Write-Host "[proof] scenarios complete. Extracting gate log lines."

# Collect the relevant gate log lines per scenario into a single excerpt file for the report.
$excerpt = Join-Path $OutDir "director-log-excerpts.log"
"" | Set-Content -Path $excerpt -Encoding utf8
foreach ($r in $results) {
    $lf = $r.Result.LogFile
    Add-Content -Path $excerpt -Encoding utf8 -Value "===== Scenario: $($r.Scenario)  (PID $($r.Result.Pid), log: $lf) ====="
    if ($lf -and (Test-Path $lf)) {
        Get-Content $lf | Where-Object {
            $_ -match "AccountGatePolicy|AccountGateScreen|account gate|Main window shown|Startup blocked|DevThrottleAccountService|DevThrottleAccountFactory|BackendUnavailableTokenRefresher|MainWindow\] (?:Loaded|Shown)"
        } | ForEach-Object { Add-Content -Path $excerpt -Encoding utf8 -Value $_ }
    } else {
        Add-Content -Path $excerpt -Encoding utf8 -Value "(log file not found)"
    }
    Add-Content -Path $excerpt -Encoding utf8 -Value ""
}
Write-Host "[proof] log excerpts -> $excerpt"
$results | ForEach-Object { "{0,-20} PID={1} LOG={2}" -f $_.Scenario, $_.Result.Pid, $_.Result.LogFile } | Write-Host
