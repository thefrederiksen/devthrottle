## Problem / Value

The phone app currently holds a persistent SSE connection to the Director for the full duration of a voice turn (up to 60-120 seconds: transcription + Claude processing + TTS). If the phone loses signal mid-turn the connection drops and the user receives no response. Reconnecting restarts the entire turn from scratch.

The Gateway is the correct owner of the async voice-turn interface, per the architecture documented in `docs/architecture/gateway/VOICE_TURN_ARCHITECTURE.md`. The phone submits once and gets a `turn_id`; the Gateway processes in the background by driving the Director's existing SSE endpoint; the phone polls at its convenience. Reconnects cost nothing -- the result is cached for 10 minutes.

The Director already has the SSE `POST /sessions/{sid}/voice-turn` endpoint which does the real work (transcription, Claude, summarization, TTS). It stays on the Director unchanged. What this issue adds is the Gateway's async submit/poll layer on top of it.

## Scope

**In:**
- `src/CcDirector.Gateway/Voice/GatewayTurnJobStore.cs` -- in-memory job store, 10-minute TTL, keyed by UUID `turn_id`; thread-safe; expiry checked on read
- `src/CcDirector.Gateway/Api/GatewayVoiceTurnEndpoint.cs` -- two routes:
  - `POST /sessions/{sid}/voice-turn/submit` -- create TurnJob, find Director via `SessionOwnerCache`, fire background `Task` that POSTs to the Director's SSE endpoint (forwarding audio/text), streams stage events back to update the TurnJob in-memory; return 202 `{ "turn_id": "...", "expires_at": "..." }`
  - `GET /sessions/{sid}/voice-turn/{turnId}` -- read TurnJob, return 200 `{ "stage": "...", ... }` with all fields from the most recent SSE event; 404 if unknown or expired
- Wire into `GatewayEndpoints.Map()` and `GatewayHost` (register `GatewayTurnJobStore` as singleton, call `GatewayVoiceTurnEndpoint.Map()`)
- `src/CcDirector.Gateway.Tests/GatewayVoiceTurnAsyncTests.cs` -- integration tests using real `ControlApiHost` + real `GatewayHost` on ephemeral ports (follow pattern in `WingmanAskForwardingTests.cs`); use `QuickIdleBackend` to avoid live Claude/OpenAI calls
- HTML proof report at `docs/cencon/proof/voice-turn-gateway/report.html` -- all tests listed with PASS/FAIL + a manual `curl` trace of submit->202->poll->reply

**Out:**
- Bearer-token auth on the new endpoints (issue #369, already filed, depends on this)
- Phone URL swap (separate issue)
- Director async endpoint removal (separate issue)
- Bug fixes in `VoiceTurnEndpoint.cs` or `ClaudeSummarizer.cs` (issues #366, #367, #368)
- Changes to the Director's SSE `POST /sessions/{sid}/voice-turn` endpoint

## Acceptance Criteria

- [ ] `POST /sessions/{unknown-guid}/voice-turn/submit` returns 404 JSON `{ "error": "session not found" }`
- [ ] `POST /sessions/not-a-guid/voice-turn/submit` returns 400 JSON `{ "error": "invalid session id format" }`
- [ ] `POST /sessions/{valid-idle-session-id}/voice-turn/submit` returns 202 JSON with `turn_id` (UUID string) and `expires_at` (ISO 8601 UTC string)
- [ ] `GET /sessions/{sid}/voice-turn/{unknown-turn-id}` returns 404 JSON
- [ ] `GET /sessions/{sid}/voice-turn/{valid-turn-id}` while background task is in-flight returns 200 with `stage` one of: `submitted`, `transcribing`, `transcript`, `waiting`, `thinking`, `summarizing`
- [ ] `GET /sessions/{sid}/voice-turn/{valid-turn-id}` after background task completes returns 200 with `stage: "reply"`, non-null `summary` field, non-null `audioBase64` field (may be empty string when no TTS key; field must be present)
- [ ] After TTL expires (simulated via a test helper that sets job creation time to 11 minutes ago), `GET .../voice-turn/{turnId}` returns 404
- [ ] When the Director is not reachable (not registered in the Gateway's DirectorRegistry), submit returns 404 or 503; poll of any job for that Director eventually returns `{ "stage": "error", "message": "..." }`
- [ ] `dotnet build cc-director.sln` succeeds with 0 errors
- [ ] `dotnet test --filter GatewayVoiceTurnAsync` passes -- all tests green
- [ ] HTML proof report committed to `docs/cencon/proof/voice-turn-gateway/report.html` showing each test by name with result

## Affected Containers

- `CcDirector.Gateway`
- `CcDirector.Gateway.Tests`

## Proof Target

`docs/cencon/proof/voice-turn-gateway/report.html` showing: (a) all integration test names with PASS/FAIL, (b) a `curl` or HttpClient trace of the submit -> 202 -> poll (waiting) -> poll (reply) sequence run against a real Gateway + Director slot 5.

## Assumptions

- `SessionOwnerCache` is already accessible inside `GatewayHost` (it was introduced in #372). If it is not yet wired into `GatewayEndpoints.Map()`, this issue includes wiring it.
- The Director's SSE endpoint (`POST /sessions/{sid}/voice-turn`) is callable from the Gateway over loopback using the Director's registered `DirectorAddress`. This is confirmed by the existing `WingmanAskForwardingTests` pattern.
- The Gateway's background `Task` uses a short-lived `HttpClient` to drive the SSE stream from the Director; it does not need to share the Gateway's existing `DirectorEndpointClient`.
- The `GatewayTurnJobStore` is a simple in-memory dictionary with a timer or lazy-expiry check on read. Persistent storage across Gateway restarts is out of scope.
