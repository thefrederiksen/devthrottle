<#
.SYNOPSIS
    Test harness for the Gateway Voice Turn API.
    Tests the full voice-to-agent pipeline without a phone.

.DESCRIPTION
    Two modes:
      -Mode text   (default) -- send text directly, skip audio upload + Whisper
      -Mode audio  -- upload a local audio file in chunks, run full Whisper pipeline

    The script:
      1. Finds a running Director and lists live sessions (or uses -SessionId)
      2. Registers a voice turn
      3. Uploads audio (if -Mode audio)
      4. Polls for completion with a progress bar
      5. Downloads the response MP3
      6. Plays it and prints the spoken summary
      7. Cleans up the turn

.PARAMETER GatewayUrl
    Gateway base URL. Default: http://localhost:7878

.PARAMETER Token
    Gateway auth token. If omitted, reads from config.json.

.PARAMETER SessionId
    Target session GUID. If omitted, the script lists available sessions and prompts.

.PARAMETER Question
    The text to ask the agent (text mode). Default: "What are you currently working on?"

.PARAMETER AudioFile
    Path to audio file (audio mode). Supports: m4a, mp3, wav, webm, ogg.

.PARAMETER Mode
    "text" (default) or "audio".

.PARAMETER ChunkSize
    Chunk size in bytes for audio mode. Default: 131072 (128 KB).

.PARAMETER PollInterval
    Seconds between status polls. Default: 2.

.PARAMETER OutFile
    Path to save the response MP3. Default: $env:TEMP\voice-turn-reply.mp3

.PARAMETER NoPlay
    Do not auto-play the response audio.

.EXAMPLE
    .\test-voice-turn.ps1
    # Text mode, prompts for session selection

.EXAMPLE
    .\test-voice-turn.ps1 -SessionId "d3f1a2b4-..." -Question "Summarize what you did today"

.EXAMPLE
    .\test-voice-turn.ps1 -Mode audio -AudioFile "D:\test\question.m4a" -SessionId "d3f1a2b4-..."
#>
param(
    [string]$GatewayUrl   = "http://localhost:7878",
    [string]$Token        = "",
    [string]$SessionId    = "",
    [string]$Question     = "What are you currently working on? Please answer in one or two sentences.",
    [string]$AudioFile    = "",
    [ValidateSet("text","audio")][string]$Mode = "text",
    [int]   $ChunkSize    = 131072,
    [int]   $PollInterval = 2,
    [string]$OutFile      = "$env:TEMP\voice-turn-reply.mp3",
    [switch]$NoPlay
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---- helpers ----------------------------------------------------------------

function Write-Step([string]$msg) {
    Write-Host "[*] $msg" -ForegroundColor Cyan
}

function Write-Ok([string]$msg) {
    Write-Host "[OK] $msg" -ForegroundColor Green
}

function Write-Fail([string]$msg) {
    Write-Host "[FAIL] $msg" -ForegroundColor Red
}

function Get-Sha256([byte[]]$bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-","").ToLower()
}

# ---- resolve token ----------------------------------------------------------

if (-not $Token) {
    $cfg = "$env:LOCALAPPDATA\cc-director\config\config.json"
    if (Test-Path $cfg) {
        $json = Get-Content $cfg -Raw | ConvertFrom-Json
        if ($json.PSObject.Properties["gatewayToken"]) {
            $Token = $json.gatewayToken
        }
    }
}

if (-not $Token) {
    $Token = Read-Host "Gateway auth token"
}

$H_JSON = @{
    "X-Director-Token" = $Token
    "Content-Type"     = "application/json"
}
$H_BIN = @{
    "X-Director-Token" = $Token
    "Content-Type"     = "application/octet-stream"
}

# ---- resolve session --------------------------------------------------------

if (-not $SessionId) {
    Write-Step "Looking for running Directors..."

    # Find instance files
    $instanceDir = "$env:LOCALAPPDATA\cc-director\config\director\instances"
    $instances = @()
    if (Test-Path $instanceDir) {
        Get-ChildItem $instanceDir -Filter "*.json" | ForEach-Object {
            try {
                $inst = Get-Content $_.FullName -Raw | ConvertFrom-Json
                if ($inst.port) { $instances += $inst }
            } catch {}
        }
    }

    $allSessions = @()
    foreach ($inst in $instances) {
        try {
            $sessions = Invoke-RestMethod -Uri "http://127.0.0.1:$($inst.port)/sessions" -TimeoutSec 3
            foreach ($s in $sessions) {
                $allSessions += [PSCustomObject]@{
                    Id         = $s.id
                    Name       = $s.name
                    State      = $s.activityState
                    DirectorId = $inst.id
                    Port       = $inst.port
                }
            }
        } catch {}
    }

    if ($allSessions.Count -eq 0) {
        Write-Fail "No sessions found. Make sure a Director is running."
        exit 1
    }

    Write-Host ""
    Write-Host "Available sessions:" -ForegroundColor Yellow
    for ($i = 0; $i -lt $allSessions.Count; $i++) {
        $s = $allSessions[$i]
        Write-Host "  [$($i+1)] $($s.Name) -- $($s.State) -- $($s.Id.Substring(0,8))..."
    }
    Write-Host ""
    $pick = Read-Host "Select session (1-$($allSessions.Count))"
    $picked = $allSessions[[int]$pick - 1]
    $SessionId = $picked.Id
    Write-Host "Using session: $($picked.Name) ($SessionId)" -ForegroundColor Gray
}

# ---- validate audio mode ----------------------------------------------------

if ($Mode -eq "audio") {
    if (-not $AudioFile) {
        $AudioFile = Read-Host "Path to audio file"
    }
    if (-not (Test-Path $AudioFile)) {
        Write-Fail "Audio file not found: $AudioFile"
        exit 1
    }
}

# ---- STEP 1: Register turn --------------------------------------------------

Write-Step "Registering voice turn..."

if ($Mode -eq "text") {
    $body = @{ sessionId = $SessionId; text = $Question } | ConvertTo-Json
} else {
    $body = @{ sessionId = $SessionId } | ConvertTo-Json
}

$turn = Invoke-RestMethod -Uri "$GatewayUrl/voice-turn" -Method POST -Headers $H_JSON -Body $body
$tid  = $turn.turnId
Write-Ok "Turn registered: $tid  state: $($turn.state)"

# ---- STEP 2: Upload chunks (audio mode only) --------------------------------

if ($Mode -eq "audio") {
    Write-Step "Uploading audio in chunks from $AudioFile..."

    $audioBytes = [System.IO.File]::ReadAllBytes($AudioFile)
    $total      = [Math]::Ceiling($audioBytes.Length / $ChunkSize)
    Write-Host "  File: $($audioBytes.Length) bytes -> $total chunks"

    for ($i = 0; $i -lt $total; $i++) {
        $start = $i * $ChunkSize
        $len   = [Math]::Min($ChunkSize, $audioBytes.Length - $start)
        $chunk = $audioBytes[$start..($start + $len - 1)]
        $sha   = Get-Sha256 $chunk

        $h = $H_BIN + @{ "X-Chunk-Sha256" = $sha }
        Invoke-RestMethod -Uri "$GatewayUrl/voice-turn/$tid/chunk/$i" -Method PUT -Headers $h -Body $chunk | Out-Null
        Write-Host "  chunk $($i+1)/$total  ($len bytes)" -ForegroundColor Gray
    }

    Write-Ok "All $total chunks uploaded"

    # -- Complete
    Write-Step "Marking upload complete..."
    $ext  = [System.IO.Path]::GetExtension($AudioFile).TrimStart(".").ToLower()
    $mime = @{
        m4a  = "audio/mp4"
        mp3  = "audio/mpeg"
        wav  = "audio/wav"
        webm = "audio/webm"
        ogg  = "audio/ogg"
        aac  = "audio/aac"
        flac = "audio/flac"
    }[$ext]
    if (-not $mime) { $mime = "application/octet-stream" }

    $cBody = @{ totalChunks = $total; mime = $mime } | ConvertTo-Json
    Invoke-RestMethod -Uri "$GatewayUrl/voice-turn/$tid/complete" -Method POST `
        -Headers $H_JSON -Body $cBody | Out-Null
    Write-Ok "Pipeline started (202)"
}

# ---- STEP 3: Poll status ----------------------------------------------------

Write-Step "Waiting for response..."

$lastState = ""
$startTime = Get-Date

:poll while ($true) {
    Start-Sleep -Seconds $PollInterval
    $status = Invoke-RestMethod -Uri "$GatewayUrl/voice-turn/$tid/status" `
                  -Method GET -Headers $H_JSON

    $elapsed = [int]((Get-Date) - $startTime).TotalSeconds

    if ($status.state -ne $lastState) {
        $stateLabel = switch ($status.state) {
            "transcribing" { "Transcribing audio..." }
            "thinking"     { "Agent thinking..." }
            "synthesizing" { "Synthesizing speech..." }
            "ready"        { "Ready!" }
            "error"        { "ERROR" }
            default        { $status.state }
        }
        Write-Host "  [${elapsed}s] $stateLabel" -ForegroundColor Yellow
        if ($status.transcript -and $status.state -ne $lastState) {
            Write-Host "  Transcript: $($status.transcript)" -ForegroundColor Gray
        }
        $lastState = $status.state
    }

    if ($status.state -eq "error") {
        Write-Fail "Pipeline failed at stage '$($status.errorStage)': $($status.errorMessage)"
        exit 1
    }

    if ($status.state -eq "ready") {
        break poll
    }

    if ($elapsed -gt 180) {
        Write-Fail "Timed out waiting for turn to complete after ${elapsed}s"
        exit 1
    }
}

$totalSec = [int]((Get-Date) - $startTime).TotalSeconds
Write-Ok "Turn complete in ${totalSec}s"

if ($status.summary) {
    Write-Host ""
    Write-Host "Spoken reply:" -ForegroundColor Green
    Write-Host "  $($status.summary)"
    Write-Host ""
}

# ---- STEP 4: Download audio -------------------------------------------------

Write-Step "Downloading response audio..."

$dlH = @{ "X-Director-Token" = $Token }
Invoke-WebRequest -Uri "$GatewayUrl/voice-turn/$tid/audio" -Headers $dlH -OutFile $OutFile
$sizekb = [int]((Get-Item $OutFile).Length / 1024)
Write-Ok "Audio saved to $OutFile ($sizekb KB)"

# ---- STEP 5: Play -----------------------------------------------------------

if (-not $NoPlay) {
    Write-Step "Playing response..."
    Start-Process $OutFile
}

# ---- STEP 6: Cleanup --------------------------------------------------------

try {
    Invoke-RestMethod -Uri "$GatewayUrl/voice-turn/$tid" -Method DELETE -Headers $H_JSON | Out-Null
    Write-Ok "Turn cleaned up"
} catch {
    Write-Host "[WARN] Could not delete turn: $_" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
