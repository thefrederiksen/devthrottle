# Gateway Voice Turn API

Async voice-to-agent round-trip over a resilient, chunked-upload pipeline.

**Version:** 1.0  
**Date:** 2026-06-11  
**HTML version (with flow diagram):** [gateway-voice-turn-api.html](gateway-voice-turn-api.html)

---

## Overview

A voice turn lets a client (phone, web app, car) speak to a running agent session and hear a spoken
reply. The pipeline is designed to survive poor network conditions: audio is uploaded in resumable
chunks and all processing happens on the Gateway after the upload is complete. The client submits
audio and waits; a status poll tells it when audio is ready to play.

**Design principle:** The client does one thing -- capture audio and play audio. Everything else
(transcription, routing, agent response, TTS synthesis) happens on the Gateway, making the same
REST surface reusable by any client.

---

## End-to-End Flow

```
CLIENT                     GATEWAY (7878)                DIRECTOR / SESSION
  |                             |                               |
  |-- POST /voice-turn -------->|                               |
  |<-- { turnId, state } -------|                               |
  |                             |                               |
  |-- PUT .../chunk/0 --------->|  (store chunk, SHA256 guard)  |
  |-- PUT .../chunk/1 --------->|                               |
  |   (retry any chunk on       |                               |
  |    signal drop)             |                               |
  |-- POST .../complete ------->|                               |
  |<-- 202 Accepted ------------|                               |
  |                             |-- Whisper transcription       |
  |                             |-- state: transcribing         |
  |-- GET .../status ---------->|                               |
  |<-- { state: thinking } -----|-- text to session ----------->|
  |                             |                               |-- Claude processes
  |-- GET .../status ---------->|<-- agent reply text ----------|
  |<-- { state: synthesizing } -|                               |
  |                             |-- Haiku summarize             |
  |                             |-- OpenAI TTS -> audio/mpeg    |
  |                             |-- state: ready                |
  |-- GET .../status ---------->|                               |
  |<-- { state: ready } --------|                               |
  |-- GET .../audio ----------->|                               |
  |<-- audio/mpeg --------------|                               |
  |  [play to user]             |                               |
```

**Text shortcut:** Include `"text"` in `POST /voice-turn` to skip upload and transcription entirely.
The turn goes straight to `thinking`. This is the primary path for the test harness and web apps.

---

## Turn States

| State         | Meaning                                              |
|---------------|------------------------------------------------------|
| `registered`  | Turn ID allocated, no chunks yet                     |
| `uploading`   | Chunks being accepted                                |
| `transcribing`| Audio assembled, Whisper running                    |
| `thinking`    | Text forwarded to agent session, waiting for reply   |
| `synthesizing`| Reply being summarized and converted to speech       |
| `ready`       | Audio available for download (terminal)              |
| `error`       | Pipeline failed at some stage (terminal)             |

---

## REST API Reference

Base URL: `http://localhost:7878`  
Auth header: `X-Director-Token: {token}` on all requests.

---

### POST /voice-turn

Register a new voice turn. Include `"text"` to skip audio upload entirely.

**Request:**
```json
{
  "sessionId":  "d3f1a2b4-...",
  "directorId": "ce7e7143-...",  // optional -- Gateway locates it if omitted
  "text":       "what are you working on?"  // optional -- skips audio phases
}
```

**Response 201:**
```json
{
  "turnId": "a1b2c3d4e5f6...",
  "state":  "registered"   // or "thinking" if text shortcut used
}
```

**Errors:** 400 (missing sessionId), 404 (session not found on any Director).

---

### PUT /voice-turn/{turnId}/chunk/{index}

Upload one audio chunk. Zero-indexed. Idempotent on matching SHA256.

**Headers:**
- `Content-Type: application/octet-stream`
- `X-Chunk-Sha256: {hex}` (optional integrity guard)

**Body:** Raw audio bytes.

**Responses:** 204 (accepted), 409 (SHA256 mismatch), 422 (turn not accepting chunks).

---

### POST /voice-turn/{turnId}/complete

Mark upload done. Gateway validates all chunks present and starts the pipeline.

**Request:**
```json
{
  "totalChunks": 6,
  "mime": "audio/webm"
}
```

**Responses:** 202 (pipeline started), 409 (missing chunks: `{ "missingChunks": [2, 5] }`).

---

### GET /voice-turn/{turnId}/status

Poll progress. Recommended interval: 1-2 seconds.

**Response 200:**
```json
{
  "turnId":       "a1b2c3d4...",
  "state":        "thinking",
  "transcript":   "what are you working on?",  // set after transcription
  "summary":      null,                         // set after synthesis
  "errorMessage": null,
  "errorStage":   null,                         // "transcription" | "routing" | "agent" | "tts"
  "createdAt":    "2026-06-11T20:30:00Z",
  "updatedAt":    "2026-06-11T20:30:14Z"
}
```

---

### GET /voice-turn/{turnId}/audio

Download synthesized response MP3. Only available when state is `ready`.

**Responses:** 200 `audio/mpeg`, 425 (not ready yet), 410 (turn in error state).

---

### DELETE /voice-turn/{turnId}

Remove stored chunks and audio. Optional -- Gateway auto-purges turns older than 24 hours.

**Responses:** 204 (deleted), 422 (turn still in progress).

---

### GET /voice-turns

List all recent turns (last 24 hours).

---

## What Needs to Be Built

### Existing (Director ControlAPI)
- `POST /sessions/{id}/voice-turn` -- SSE walkie-talkie (audio or text in, streamed + base64 audio out)
- `POST /voice/utterance` + chunks -- resumable utterance upload on Director
- `TtsService`, `ClaudeSummarizer` -- pipeline components

### Existing (Gateway)
- `POST /ingest/recording` + chunks -- same chunked-upload pattern to copy

### New (Gateway)
- `POST /voice-turn`, `PUT /voice-turn/.../chunk/{n}`, `POST /voice-turn/.../complete`
- `GET /voice-turn/.../status`, `GET /voice-turn/.../audio`, `DELETE /voice-turn/...`
- `VoiceTurnService` (Gateway) -- background pipeline job
- Director routing -- Gateway looks up which registered Director owns the sessionId

**Implementation shortcut:** The pipeline logic already exists in `VoiceTurnEndpoint.cs`.
The new Gateway `VoiceTurnService` is the same steps (Whisper -> Director relay -> summarize -> TTS)
as a background job. The chunked upload is a copy of `RecordingIngestService` (same SHA256 guard,
same idempotent design).

---

## Test Harness

See the companion script: [../../scripts/test-voice-turn.ps1](../../scripts/test-voice-turn.ps1)

### Quick text-mode test (PowerShell)

```powershell
$GW      = "http://localhost:7878"
$TOKEN   = "your-gateway-token"
$SESSION = "d3f1a2b4-..."
$H       = @{ "X-Director-Token" = $TOKEN; "Content-Type" = "application/json" }

# 1. Register with text
$turn = Invoke-RestMethod -Uri "$GW/voice-turn" -Method POST -Headers $H `
          -Body (@{ sessionId = $SESSION; text = "What are you working on? One sentence." } | ConvertTo-Json)
$tid = $turn.turnId

# 2. Poll
do {
    Start-Sleep 2
    $s = Invoke-RestMethod -Uri "$GW/voice-turn/$tid/status" -Method GET -Headers $H
    Write-Host "state: $($s.state)"
} while ($s.state -notin "ready","error")

# 3. Play
if ($s.state -eq "ready") {
    Invoke-WebRequest -Uri "$GW/voice-turn/$tid/audio" `
        -Headers @{ "X-Director-Token" = $TOKEN } -OutFile reply.mp3
    Start-Process reply.mp3
}
```
