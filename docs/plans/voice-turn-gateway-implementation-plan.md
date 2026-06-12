# Voice Turn -> Gateway: Implementation Plan

**Date:** 2026-06-12
**Branch base:** `main` (start a new branch per issue)
**Architecture doc:** `docs/architecture/gateway/VOICE_TURN_ARCHITECTURE.md`

---

## Goal

Move the async voice-turn submit/poll interface from the Director to the Gateway. Fix three confirmed voice bugs in the Director's SSE endpoint. Add bearer-token auth to the new Gateway endpoints.

The phone currently holds a live SSE connection to the Director for the full duration of a voice turn (60-120 s). If it loses signal the turn is lost. The Gateway's submit/poll surface decouples the phone from the Director: submit once, poll at leisure, reconnect freely.

---

## Architecture (target state)

```
PHONE  -->  POST /sessions/{sid}/voice-turn/submit  -->  GATEWAY
                                                            |
                                                     (background Task)
                                                            |
                                                     POST /sessions/{sid}/voice-turn (SSE)
                                                            |
                                                         DIRECTOR
                                                   (transcribe, Claude, TTS)
                                                            |
                                                     GATEWAY caches result (10 min TTL)
                                                            |
PHONE  -->  GET /sessions/{sid}/voice-turn/{turnId}  -->  GATEWAY  -->  200 {stage, summary, audioBase64}
```

The Director's SSE endpoint (`POST /sessions/{sid}/voice-turn`) is **unchanged**. The Gateway calls it in a background task and caches the final reply event.

---

## Issue Sequence

Dependencies flow left to right. Issues with no arrow are independent.

```
#376 (Gateway endpoints + tests)
    --> #377 (Director cleanup)
    --> #378 (Phone URL swap)
    --> #369 (Auth)

#366 (stale transcript fix)   -- independent
#367 (non-Latin fix)          -- independent
#368 (backtick fix)           -- independent
```

**Recommended implementation order:**

1. #366, #367, #368 in parallel (small, self-contained, fixes code that will keep running)
2. #376 (the core Gateway work -- largest issue)
3. #377 + #378 in parallel (both depend on #376 merged)
4. #369 (auth, last -- depends on #376 merged)

---

## Issues

### #376 -- [Gateway] Add async voice-turn submit/poll endpoints and TurnJobStore

**Link:** https://github.com/thefrederiksen/cc-director/issues/376
**Labels:** flow:ready-dev, enhancement

**What to build:**

1. `src/CcDirector.Gateway/Voice/GatewayTurnJobStore.cs`
   - In-memory dictionary of `TurnJob` objects keyed by UUID `turn_id`
   - 10-minute TTL; expiry checked lazily on read
   - Thread-safe (ConcurrentDictionary or lock)
   - `TurnJob` fields: `TurnId`, `SessionId`, `Stage`, `Transcript`, `Summary`, `AudioBase64`, `ErrorMessage`, `CreatedAt`, `ExpiresAt`

2. `src/CcDirector.Gateway/Api/GatewayVoiceTurnEndpoint.cs`
   - `POST /sessions/{sid}/voice-turn/submit`
     - Validate `sid` is a valid Guid; return 400 if not
     - Look up session owner via `SessionOwnerCache`; return 404 if not found
     - Create `TurnJob` (stage = `submitted`), store in `GatewayTurnJobStore`
     - Fire background `Task` (not awaited): POST multipart/form-data (forwarding audio or JSON text) to the Director's `POST /sessions/{sid}/voice-turn` SSE endpoint; read SSE events and update the `TurnJob` stage/fields as each event arrives; on completion set stage to `reply` or `error`
     - Return 202 `{ "turn_id": "...", "expires_at": "..." }`
   - `GET /sessions/{sid}/voice-turn/{turnId}`
     - Look up by `turnId`; return 404 if unknown or expired
     - Return 200 `{ "stage": "...", "transcript": "...", "summary": "...", "audioBase64": "...", "message": "..." }` (null fields omitted or empty string is fine)

3. Wire into `GatewayEndpoints.Map()`:
   - Add `GatewayTurnJobStore store` parameter (singleton injected by `GatewayHost`)
   - Call `GatewayVoiceTurnEndpoint.Map(app, store, owners)`

4. Wire into `GatewayHost`:
   - Register `GatewayTurnJobStore` as a singleton
   - Pass it to `GatewayEndpoints.Map()`

5. `src/CcDirector.Gateway.Tests/GatewayVoiceTurnAsyncTests.cs`
   - Follow pattern in `WingmanAskForwardingTests.cs`: real `ControlApiHost` + real `GatewayHost` on ephemeral ports, isolated `_instancesDir`
   - Use `QuickIdleBackend` from `VoiceTurnEndpointTests.cs` so sessions go Idle instantly (no live Claude/OpenAI needed)
   - Tests to cover:
     1. `Submit_InvalidGuid_Returns400`
     2. `Submit_UnknownSession_Returns404`
     3. `Submit_ValidIdleSession_Returns202WithTurnId`
     4. `Poll_UnknownTurnId_Returns404`
     5. `Poll_ValidTurnId_WhileProcessing_ReturnsInProgressStage`
     6. `Poll_ValidTurnId_AfterCompletion_ReturnsReplyStage`
     7. `Poll_ValidTurnId_AfterTTLExpiry_Returns404` (inject old `CreatedAt` to simulate expiry)
     8. `Submit_SessionAlreadyExited_Returns404OrGone`
     9. `Poll_DirectorUnreachable_ReturnsErrorStage` (no Director registered)
     10. `Submit_TextBody_Returns202` (JSON `{ "text": "..." }` path, not multipart)
     11. `Poll_MultiplePolls_SameResult` (idempotent after completion)

6. HTML proof report at `docs/cencon/proof/voice-turn-gateway/report.html`
   - List all test names with PASS/FAIL
   - Include a `curl` trace of the submit -> poll -> reply sequence against a real slot-5 Director + Gateway

**Key files to read before implementing:**
- `src/CcDirector.Gateway.Tests/WingmanAskForwardingTests.cs` -- GatewayHost+Director test pattern
- `src/CcDirector.Gateway.Tests/VoiceTurnEndpointTests.cs` -- QuickIdleBackend, ReadSseStreamAsync helpers
- `src/CcDirector.ControlApi/VoiceTurnEndpoint.cs` -- SSE event format the background task must consume
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` -- how to wire new endpoints
- `src/CcDirector.Gateway/GatewayHost.cs` -- how to add a new singleton and pass to Map()
- `docs/architecture/gateway/VOICE_TURN_ARCHITECTURE.md` -- full flow diagram and stage table

---

### #377 -- [ControlApi] Remove temporary async voice-turn endpoints from Director

**Link:** https://github.com/thefrederiksen/cc-director/issues/377
**Labels:** flow:ready-dev, enhancement
**Depends on:** #376 merged

**What to do:**

- Delete `src/CcDirector.ControlApi/VoiceTurnAsyncEndpoint.cs` (if present on branch)
- Delete `src/CcDirector.ControlApi/TurnJobStore.cs` (if present on branch)
- In `src/CcDirector.ControlApi/ControlApiHost.cs`: remove singleton registration for `TurnJobStore` and the `VoiceTurnAsyncEndpoint.Map()` call; remove now-unused `using` directives
- Run `dotnet build cc-director.sln` -- must succeed with 0 errors
- Run `dotnet test --filter VoiceTurn` -- existing SSE endpoint tests must still pass
- Screenshot of successful build is the proof

**Do NOT touch:**
- `VoiceTurnEndpoint.cs` (SSE endpoint, stays on Director)
- `VoiceTurnHelpers.cs` (if present, stays on Director)

**Note:** These files are on branch `issue-347-voice-dictation-recording-indicator` (commit `044e1c3`). If they are not present in the working tree when this issue is picked up, close as no-op with a comment.

---

### #378 -- [Voice] Phone app: swap voice-turn URLs from Director to Gateway

**Link:** https://github.com/thefrederiksen/cc-director/issues/378
**Labels:** flow:ready-dev, enhancement
**Depends on:** #376 merged and deployed

**What to change:**

- `phone/CcDirectorClient/TalkPage.xaml.cs` -- `RunVoiceTurnAsync`: change the base URL for `SubmitVoiceTurnAsync` and `PollVoiceTurnAsync` from `session.TailnetEndpoint` (Director URL) to the Gateway URL
- `phone/CcDirectorClient/Voice/VoiceConversation.cs` -- `SpeakTurnAsync`: same URL swap
- `phone/CcDirectorClient/Voice/DirectorVoiceClient.cs` -- `NewClient()` or equivalent: ensure Gateway URL is used as `HttpClient.BaseAddress` for submit/poll calls

**Before implementing:** check whether the session DTO the phone receives includes a Gateway URL field. If not, that field must be added to the DTO and populated by the Gateway's sessions aggregation endpoint -- flag as a blocker if missing.

**After change:**
- `dotnet build` (MAUI Android) must succeed with 0 errors
- Deploy to test device via `scripts/deploy-phone.ps1`
- Live end-to-end test: voice turn succeeds with spoken reply received on phone
- Director log shows the voice turn request arriving from the Gateway IP (not from the phone IP directly)

---

### #366 -- [Voice] Fix stale transcript: voice-turn endpoint speaks previous-turn content

**Link:** https://github.com/thefrederiksen/cc-director/issues/366
**Labels:** flow:ready-dev, bug, voice-review
**Independent** (no dependency on Gateway issues)

**Root cause:** `ReadLastAssistantText` in `src/CcDirector.ControlApi/VoiceTurnEndpoint.cs` reads the most recent assistant message in the JSONL file after the turn poll loop exits. If the agent has not yet written its response to the current utterance, this returns the previous turn's output.

**Fix:**
- Immediately before calling `session.SendTextAsync()`, snapshot the JSONL file size: `var offsetBefore = File.Exists(jsonlPath) ? new FileInfo(jsonlPath).Length : 0L;`
- After the turn poll loop completes, pass `offsetBefore` to a new `ReadNewAssistantText(session, offsetBefore)` method
- `ReadNewAssistantText` reads only assistant messages whose byte position in the file is >= `offsetBefore`

**File:** `src/CcDirector.ControlApi/VoiceTurnEndpoint.cs` (and/or `VoiceTurnHelpers.cs` if that file exists on the branch)

**Test required:** `VoiceTurn_TwoConsecutiveTurns_SecondTurnSpeaksCurrentReply` -- submit two turns to the same session with `QuickIdleBackend`, assert the second reply stage does not contain content from the first turn.

---

### #367 -- [Voice] Fix non-Latin script refusal in ClaudeSummarizer

**Link:** https://github.com/thefrederiksen/cc-director/issues/367
**Labels:** flow:ready-dev, bug, voice-review
**Independent**

**Root cause:** The summarizer prompt or pre-processing in `src/CcDirector.Core/Voice/Services/ClaudeSummarizer.cs` flags non-ASCII code points as encoding corruption and instructs the model to refuse.

**Fix:**
- Remove any encoding-validation guard from `ClaudeSummarizer.cs` pre-processing
- Update the system/user prompt to explicitly allow non-Latin input: instruct the model to summarize faithfully in the source language without treating Unicode characters as errors

**File:** `src/CcDirector.Core/Voice/Services/ClaudeSummarizer.cs`

**Test required:** `ClaudeSummarizer_NonLatinInput_IsNotDropped` -- pass a Korean or Japanese string through `CleanupForSpeech`; assert the content is not empty or near-empty after processing.

---

### #368 -- [Voice] Fix backtick content deletion in ClaudeSummarizer CleanupForSpeech

**Link:** https://github.com/thefrederiksen/cc-director/issues/368
**Labels:** flow:ready-dev, bug, voice-review
**Independent**

**Root cause:** `CleanupForSpeech` in `src/CcDirector.Core/Voice/Services/ClaudeSummarizer.cs` (around line 261) strips the content inside backticks (e.g. `` `sessionName` `` becomes empty string). The fix is to strip the backtick markers only and keep the inner text.

**Fix:** Change the backtick regex replacement from deleting the match to emitting the captured group content. Example: replace `` `([^`]+)` `` -> `$1` instead of `""`.

Note: triple-backtick code blocks should still be stripped entirely (code blocks are not speakable).

**File:** `src/CcDirector.Core/Voice/Services/ClaudeSummarizer.cs`

**Tests required:**
- `ClaudeSummarizer_BacktickIdentifier_IsPreserved` -- `CleanupForSpeech("session \`my-session\` is active")` contains "my-session"
- `ClaudeSummarizer_TripleBacktickCodeBlock_IsStripped` -- triple-backtick blocks are removed entirely

---

### #369 -- Security: require API key on Gateway voice-turn endpoints

**Link:** https://github.com/thefrederiksen/cc-director/issues/369
**Labels:** flow:ready-dev (already filed)
**Depends on:** #376 merged

Add bearer-token check (same mechanism as other protected Gateway routes) to:
- `POST /sessions/{sid}/voice-turn/submit`
- `GET /sessions/{sid}/voice-turn/{turnId}`

Return 401 when token is missing or invalid. Phone already sends `Authorization: Bearer <token>` via `DirectorVoiceClient.NewClient()`.

---

## Key File Locations

| File | Purpose |
|------|---------|
| `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` | Where new Gateway routes are wired |
| `src/CcDirector.Gateway/GatewayHost.cs` | Singleton registration, passes to Map() |
| `src/CcDirector.Gateway/Discovery/SessionOwnerCache.cs` | Session-to-Director lookup |
| `src/CcDirector.ControlApi/VoiceTurnEndpoint.cs` | Director SSE endpoint (stays, don't break it) |
| `src/CcDirector.Core/Voice/Services/ClaudeSummarizer.cs` | Bug fixes #367 + #368 |
| `src/CcDirector.Gateway.Tests/WingmanAskForwardingTests.cs` | GatewayHost+Director test pattern |
| `src/CcDirector.Gateway.Tests/VoiceTurnEndpointTests.cs` | SSE test helpers to reuse |
| `src/CcDirector.Gateway.Tests/TestEnvironment.cs` | Assembly env (CC_TURNBRIEFS=0 etc.) |
| `phone/CcDirectorClient/Voice/DirectorVoiceClient.cs` | Phone voice client |
| `phone/CcDirectorClient/TalkPage.xaml.cs` | Phone voice UI |
| `docs/architecture/gateway/VOICE_TURN_ARCHITECTURE.md` | Target architecture + stage table |

---

## SSE Stage Reference

The Director's SSE endpoint emits these stages (phone polls for them at Gateway level):

| stage | meaning | extra fields |
|-------|---------|-------------|
| `submitted` | job created, background task starting | -- |
| `transcribing` | Whisper in progress | -- |
| `transcript` | audio transcribed | `text` |
| `waiting` | session busy, polling | -- |
| `thinking` | Claude processing | -- |
| `summarizing` | turn complete, summarizing | -- |
| `reply` | complete | `summary`, `audioBase64` |
| `error` | terminal failure | `message` |

---

## Build + Test Commands

```powershell
# Build
dotnet build src\cc-director.sln

# Run all Gateway tests
dotnet test src\CcDirector.Gateway.Tests

# Run only voice-turn tests
dotnet test src\CcDirector.Gateway.Tests --filter VoiceTurn

# Run ClaudeSummarizer tests (once added)
dotnet test src\CcDirector.Core.Tests --filter ClaudeSummarizer

# Build phone (MAUI Android)
dotnet build phone\CcDirectorClient\CcDirectorClient.csproj -f net10.0-android

# Deploy phone
powershell scripts\deploy-phone.ps1
```

---

## Proof Requirements (QA checklist)

Each issue requires a proof artifact committed to the PR branch:

| Issue | Proof artifact |
|-------|---------------|
| #376 | `docs/cencon/proof/voice-turn-gateway/report.html` with all tests PASS + curl trace |
| #377 | Screenshot of `dotnet build` showing 0 errors |
| #378 | Screenshot of phone with spoken reply + Gateway log showing submit from phone |
| #366 | `dotnet test` output showing `VoiceTurn_TwoConsecutiveTurns_*` PASS |
| #367 | `dotnet test` output showing `ClaudeSummarizer_NonLatinInput_*` PASS |
| #368 | `dotnet test` output showing backtick tests PASS |
| #369 | `curl` trace showing 401 without token, 202 with token |
