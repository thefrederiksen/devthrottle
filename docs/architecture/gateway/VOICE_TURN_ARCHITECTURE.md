# Voice Turn Architecture

**Status:** IN PROGRESS (async submit/poll on Director, to be moved to Gateway)
**Date:** 2026-06-12
**Audience:** Anyone working on voice mode — the phone app, the Gateway, or the Director.

## Related documents

- [GATEWAY_DIRECTOR_RESPONSIBILITIES.md](GATEWAY_DIRECTOR_RESPONSIBILITIES.md)
- [../../plans/voice-mode-fidelity-and-logging.md](../../plans/voice-mode-fidelity-and-logging.md)

---

## Principle

The **Gateway** owns the voice-turn submit/poll interface. The phone only needs to
know the Gateway URL — it never addresses individual Directors directly for voice.
The **Director** owns the session and does the actual work (send text, poll Claude,
summarize, TTS). The Gateway drives the Director's existing SSE endpoint as a
background task and caches the result.

---

## Target flow

```
PHONE                      GATEWAY                        DIRECTOR
  |                            |                               |
  |  [1] Record audio          |                               |
  |                            |                               |
  |-- POST /sessions/{id}/ --->| create TurnJob                |
  |     voice-turn/submit      | look up Director for session  |
  |     Authorization: Bearer  |   via SessionOwnerCache       |
  |     {audio bytes}          | start background Task         |
  |<-- 202 {turn_id,           |                               |
  |         expires_at} -------|                               |
  |                            |                               |
  |   [phone is FREE]          | [BACKGROUND]:                 |
  |   can lose signal          |                               |
  |   can go in a tunnel       |-- POST /sessions/{id}/  ----->|
  |                            |     voice-turn (SSE)          |
  |                            |     {audio bytes forwarded}   |
  |                            |                               |
  |  [beep every 3s]           |  data: {stage:"transcribing"} |
  |                            |  Whisper --------------------------------> OpenAI
  |                            |  data: {stage:"transcript",   |
  |                            |         text:"..."}           |
  |                            |                               |
  |                            |  data: {stage:"waiting"}      |
  |                            |  (session busy, polling)      |
  |                            |                               |
  |                            |  data: {stage:"thinking"}     |
  |                            |  (Claude processing)          |
  |                            |                               |
  |                            |  data: {stage:"summarizing"}  |
  |                            |  Haiku summarize              |
  |                            |  TTS ------------------------------------> OpenAI
  |                            |                               |
  |                            |  data: {stage:"reply",        |
  |                            |         summary:"...",        |
  |                            |         audioBase64:"..."}    |
  |                            |                               |
  |                            | store in TurnJob (10 min TTL) |
  |                            |                               |
  |-- GET /sessions/{id}/ ---->|                               |
  |     voice-turn/{turn_id}   | (no Director call needed)     |
  |<-- 200 {stage:"reply",     |                               |
  |         summary:"...",     |                               |
  |         text:"...",        |                               |
  |         audioBase64:"..."} |                               |
  |                            |                               |
  |  show text on screen       |                               |
  |  play MP3 audio            |                               |
  |  stop thinking beep        |                               |
```

**Reconnect resilience:** if the phone loses signal after submit, it reconnects and
polls with the same turn_id. The Gateway has the cached result for 10 minutes.
The Director may have already finished — the phone collects the result immediately.

---

## Poll response stages

| stage | meaning | extra fields |
|---|---|---|
| `submitted` | job created, background task starting | — |
| `transcribing` | Whisper call in progress | — |
| `transcript` | audio transcribed | `transcript` |
| `waiting` | session busy, waiting to become ready | — |
| `thinking` | text sent, Claude processing | — |
| `summarizing` | turn complete, Haiku summarizing | — |
| `reply` | complete | `summary`, `text`, `audioBase64` |
| `error` | terminal failure | `message` |

---

## Where each piece lives

| Concern | Owner | Notes |
|---|---|---|
| Submit / poll API surface | **Gateway** | Phone calls this |
| TurnJobStore (job cache) | **Gateway** | 10-min TTL, survives phone reconnects |
| Auth (bearer token) | **Gateway** | Same token mechanism as other protected routes |
| Session-to-Director lookup | **Gateway** | `SessionOwnerCache` already exists |
| SSE voice-turn endpoint | **Director** | Gateway calls this in background |
| Transcription (Whisper) | **Director** | Via SSE endpoint |
| Send text to session | **Director** | Via SSE endpoint |
| Summarize (Haiku) | **Director** | Via SSE endpoint |
| TTS (OpenAI) | **Director** | Via SSE endpoint; audio returned in SSE reply event |
| Thinking beep (audio cue) | **Phone** | `AndroidThinkingCue`, plays while polling |

---

## Current state (2026-06-12)

The async submit/poll endpoints were placed on the **Director** in commit `044e1c3`
(branch `issue-347-voice-dictation-recording-indicator`). This was wrong — they
belong on the Gateway per the principle above. The Director's SSE endpoint
(`POST /sessions/{id}/voice-turn`) is correct and stays.

The phone app (deployed 2026-06-12) currently calls `session.TailnetEndpoint`
(the Director URL) for submit/poll. When the Gateway endpoints are live, this
changes to the Gateway URL.

---

## Known bugs (to fix before or alongside Gateway move)

### #366 — Stale transcript (critical)

`ReadLastAssistantText` reads the most recent assistant message in the JSONL
transcript after the turn completes. If the agent has not yet written its response
to the new utterance, this returns the **previous turn's output**.

**Fix:** snapshot the JSONL file byte offset immediately before calling
`session.SendTextAsync()`. After the turn completes, read only content that
appears **after** that offset.

File: `src/CcDirector.ControlApi/VoiceTurnHelpers.cs`

### #368 — Backtick content silently deleted

`CleanupForSpeech` strips the content inside backticks (e.g. `` `session-name` ``
becomes empty string). Session names, boolean values, and identifiers disappear
from the spoken summary.

**Fix:** strip the backtick markers but keep the enclosed text.

File: `src/CcDirector.Core/Voice/Services/ClaudeSummarizer.cs`

### #367 — Non-Latin script refused

The Haiku summarizer misidentifies valid non-Latin Unicode (Korean, Japanese,
Arabic, etc.) as encoding corruption and refuses to summarize, producing a refusal
message instead of the agent's reply.

**Fix:** remove encoding-validation logic from the prompt; instruct the model to
summarize faithfully in the source language.

File: `src/CcDirector.Core/Voice/Services/ClaudeSummarizer.cs`

---

## Implementation plan

### Phase 1 — Fix bugs (Director / Core)

1. **#366** `VoiceTurnHelpers.ProcessCoreAsync`: record JSONL offset before send,
   pass to `ReadNewAssistantText(session, offsetBefore)` after turn completes.
2. **#368** `ClaudeSummarizer.CleanupForSpeech`: change backtick regex to capture
   and re-emit inner text instead of deleting it.
3. **#367** `ClaudeSummarizer`: remove encoding guard from prompt and pre-processing.

### Phase 2 — Move async turn to Gateway

1. Add `TurnJobStore` to Gateway (move from `CcDirector.ControlApi` or copy).
2. Add `GatewayVoiceTurnEndpoint`:
   - `POST /sessions/{sid}/voice-turn/submit` — create job, find Director via
     `SessionOwnerCache`, fire background task that forwards audio to the
     Director's SSE endpoint and streams events back to update the job.
   - `GET /sessions/{sid}/voice-turn/{turnId}` — read job, return JSON.
3. Remove `VoiceTurnAsyncEndpoint` and `TurnJobStore` from `CcDirector.ControlApi`.
   The SSE endpoint (`VoiceTurnEndpoint`) stays on the Director.

### Phase 3 — Phone update

1. `TalkPage.RunVoiceTurnAsync`: change `session.TailnetEndpoint` → Gateway URL
   for `SubmitVoiceTurnAsync` and `PollVoiceTurnAsync`.
2. `VoiceConversation.SpeakTurnAsync`: same URL swap.
3. Build and deploy phone app.

### Phase 4 — Security (#369, flow:ready-dev)

1. Add bearer-token check to `POST .../voice-turn/submit` and
   `GET .../voice-turn/{turnId}` on the Gateway.
2. Phone already sends the token via `DirectorVoiceClient.NewClient()`
   (`Authorization: Bearer <token>`).
