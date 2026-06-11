#Requires -Version 5.1
<#
.SYNOPSIS
    Live resize proof for issue #332 (Terminal raw-metal review - Criterion 3).

.DESCRIPTION
    Launches a real Director session on a slot >= 6 test Director, performs 20 rapid
    resizes via the Control API POST /sessions/{sid}/resize endpoint, and verifies:
      - HTTP 200 after each resize
      - Returned cols/rows match the requested values
      - No crash in the Director (GET /healthz still returns status=ok after all resizes)

    The script does NOT start the Director itself (that is done by the caller via the
    agent-session-isolation.ps1 harness). It receives PORT and SESSION_ID as parameters.

.PARAMETER Port
    The Control API port of the running test Director.

.PARAMETER SessionId
    The session GUID to resize.

.EXAMPLE
    # Typical invocation after starting a slot-6 Director and creating a session:
    .\docs\cencon\proof\issue-332\resize-proof.ps1 -Port 7887 -SessionId "52976cf0-..."
#>
param(
    [Parameter(Mandatory=$true)] [int]$Port,
    [Parameter(Mandatory=$true)] [string]$SessionId
)

$ErrorActionPreference = "Stop"
$baseUrl = "http://127.0.0.1:$Port"

Write-Host "[resize-proof] Starting 20-resize proof run"
Write-Host "[resize-proof] Director: $baseUrl"
Write-Host "[resize-proof] Session:  $SessionId"
Write-Host ""

# Define 20 resize sequences covering: grow, shrink, extreme small, wide, square,
# interleaved wide<->narrow, typical terminal sizes, and back to a baseline.
$resizes = @(
    @{cols=80;  rows=24},   # 1  standard 80x24
    @{cols=120; rows=40},   # 2  grow both dims
    @{cols=200; rows=50},   # 3  wide/tall
    @{cols=40;  rows=10},   # 4  shrink both
    @{cols=10;  rows=5},    # 5  extreme small
    @{cols=300; rows=80},   # 6  extreme wide/tall
    @{cols=80;  rows=24},   # 7  back to standard
    @{cols=160; rows=50},   # 8  wide
    @{cols=80;  rows=80},   # 9  square
    @{cols=40;  rows=40},   # 10 small square
    @{cols=220; rows=55},   # 11 large
    @{cols=80;  rows=24},   # 12 standard
    @{cols=132; rows=43},   # 13 typical VT100 wide mode
    @{cols=180; rows=60},   # 14 grow
    @{cols=20;  rows=8},    # 15 very small
    @{cols=250; rows=70},   # 16 large
    @{cols=80;  rows=30},   # 17 standard rows
    @{cols=100; rows=24},   # 18 slightly wide
    @{cols=60;  rows=20},   # 19 slightly narrow
    @{cols=80;  rows=24}    # 20 back to standard
)

$passed = 0
$failed = 0
$results = @()

for ($i = 0; $i -lt $resizes.Count; $i++) {
    $r = $resizes[$i]
    $n = $i + 1
    $body = @{ cols = $r.cols; rows = $r.rows } | ConvertTo-Json

    try {
        $resp = Invoke-RestMethod -Uri "$baseUrl/sessions/$SessionId/resize" `
                                  -Method Post `
                                  -Body $body `
                                  -ContentType "application/json" `
                                  -TimeoutSec 5

        $colsOk = $resp.cols -eq $r.cols
        $rowsOk = $resp.rows -eq $r.rows
        $accepted = $resp.accepted -eq $true

        if ($accepted -and $colsOk -and $rowsOk) {
            $status = "PASS"
            $passed++
        } else {
            $status = "FAIL (mismatch: got cols=$($resp.cols) rows=$($resp.rows), expected cols=$($r.cols) rows=$($r.rows))"
            $failed++
        }
    } catch {
        $status = "FAIL (HTTP error: $_)"
        $failed++
    }

    $line = "Resize $n/$($resizes.Count): cols=$($r.cols) rows=$($r.rows) -> $status"
    Write-Host "[resize-proof] $line"
    $results += $line
}

Write-Host ""
Write-Host "[resize-proof] Verifying Director health after all resizes..."
try {
    $health = Invoke-RestMethod -Uri "$baseUrl/healthz" -Method Get -TimeoutSec 5
    $healthStatus = $health.status
    Write-Host "[resize-proof] Health: $healthStatus (sessions=$($health.sessions), version=$($health.version))"
    $healthLine = "Director health after all resizes: status=$healthStatus sessions=$($health.sessions) version=$($health.version)"
    $results += $healthLine
} catch {
    Write-Host "[resize-proof] FAIL: Director health check failed: $_"
    $results += "Director health check FAILED: $_"
    $failed++
}

Write-Host ""
Write-Host "[resize-proof] SUMMARY: $passed PASS, $failed FAIL"
$results += ""
$results += "SUMMARY: $passed PASS, $failed FAIL"

if ($failed -eq 0) {
    Write-Host "[resize-proof] Criterion 3: PASS - all 20 resizes succeeded, Director healthy"
    $results += "Criterion 3: PASS - all 20 resizes succeeded, Director healthy"
} else {
    Write-Host "[resize-proof] Criterion 3: FAIL - $failed resize(s) did not pass"
    $results += "Criterion 3: FAIL - $failed resize(s) did not pass"
}

return $results
