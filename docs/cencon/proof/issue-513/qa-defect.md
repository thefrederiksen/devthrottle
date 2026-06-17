## QA Agent - DEFECT (flow:qa-failed)

Independent verification in the **running app** found a reproducible defect that defeats the
issue's primary goal. The unit tests pass, but the live end-to-end path does NOT meet acceptance
criterion 1.

### Defect: live DevThrottle dictation still sends `gpt-4o-transcribe`, not `whisper-large-v3`

**Acceptance criterion 1 (the contract):**
> With the Gateway in DevThrottle mode + a valid `dt_` key, dictation in a Director produces a
> transcript by calling the batch `/audio/transcriptions` on devthrottle.com **with
> `whisper-large-v3`**.

**Expected:** in DevThrottle mode the running Director resolves
`transport=batch, model=whisper-large-v3, baseUrl=devthrottle.com` and the batch provider is
constructed with `whisper-large-v3`.

**Actual (running slot-11 Director built from this PR branch, DevThrottle mode, attached to a
Gateway):**
```
[DictationEndpoint] routing: mode=devthrottle transport=batch model=gpt-4o-transcribe baseUrl=https://devthrottle.com/api/v1
[OpenAiTranscriptionProvider] StartAsync: prompt_len=126, model=gpt-4o-transcribe
```
The transport was correctly fixed to `batch` and the URL is `devthrottle.com`, but the **model is
`gpt-4o-transcribe`** - the exact model the issue documents the proxy rejects with
404 `model_not_found`. So a real dictation in DevThrottle mode would still hit the proxy with the
broken model. This is precisely the `transport=batch + wrong model` combination the issue set out to
eliminate ("fixing the model alone is insufficient" - here the transport was fixed and the model was
left broken).

### Root cause (so the Developer Agent can act directly)

The Director is attached to a Gateway, so the model comes from the Gateway's
`GET /transcription/routing` response. The live Gateway (build `0.9.12+1ffa669`, current `main`,
predates this PR) returns:
```json
{"mode":"devthrottle","baseUrl":"https://devthrottle.com/api/v1","model":"gpt-4o-transcribe","key":"dt_live_<REDACTED>"}
```
- No `transport` field (older Gateway).
- `model` = `gpt-4o-transcribe` (stale shared default).

In `OpenAiKeyResolver.ResolveEndpointFromGatewayAsync` the code **derives `transport` from the
authoritative mode** when the Gateway omits it (good - "the transport is a pure function of the
mode"), but it **takes the Gateway's `model` field verbatim** and applies no equivalent correction.
The DevThrottle model is equally a pure function of the mode
(`TranscriptionEndpointResolver.DevThrottleModel = "whisper-large-v3"`), yet a stale/mismatched
model from an older Gateway is trusted, producing the internally inconsistent
`transport=batch + model=gpt-4o-transcribe` target.

The resolver self-heals the transport against a pre-#513 Gateway but not the model, leaving the live
deployment broken. (Either: derive/validate the model from the mode the same way transport is when
the Gateway's model is inconsistent with the mode's transport; or otherwise guarantee the batch path
never carries a non-Groq model. The exact approach is the developer's call - QA reports the defect,
not the fix.)

### Steps to reproduce

1. Director config `transcription_mode=devthrottle`, attached to a Gateway whose
   `/transcription/routing` omits `transport` and returns `model=gpt-4o-transcribe` (i.e. any
   Gateway not yet rebuilt with this PR - including the one currently deployed).
2. Build this PR branch, launch the Director, open a `/dictate` WebSocket and send
   `{"type":"start"}`.
3. Observe the Director log: `routing: ... transport=batch model=gpt-4o-transcribe` and the batch
   provider starting with `model=gpt-4o-transcribe`.

### What passed (for completeness)

- Clean build: `dotnet build cc-director.sln` -> 0 Warning(s) / 0 Error(s).
- Targeted unit tests green: Gateway 8/8, Core 70/70 (transport routing, pipeline selection,
  null-cleanup). The in-process proof test passes because it boots THIS PR's routing endpoint, which
  does serve `whisper-large-v3`; it does not exercise the Director-against-an-older-Gateway path that
  fails live.
- Regression sweep CLEAN: every test failing on the branch (Gateway 5, Core 2) fails **identically**
  on clean `main` (`1ffa669`) - all pre-existing (live-OpenAI-key DictationEndpoint tests, ambient
  `CC_DIRECTOR_ROOT` VaultGateway tests, perf TerminalThroughput, NoCrossMachineLoopbackGuard
  allowlist). The branch adds net-new passing tests and introduces zero new failures.

### Evidence (committed under docs/cencon/proof/issue-513/)

- `qa-live-director-routing.txt` - the running Director's routing + provider log lines.
- `qa-gateway-routing-response.json` - the live Gateway routing response (key redacted).

Bouncing to the Developer Agent. The transport routing is correct; the model is not honored
provider-correctly on the live on-Gateway path, so acceptance criterion 1 is not met in the running
app.
