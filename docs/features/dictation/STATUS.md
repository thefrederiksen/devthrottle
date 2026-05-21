# Dictation Library: STATUS

Last updated: 2026-05-21 (Speak button shipped in both desktop and browser; in-process NAudio capture on desktop; auto-insert on both surfaces). Only remaining work is global-hotkey/SendInput-into-foreign-windows (the original Phase 2) and Mac (Phase 5); both hardware-bound.

## Cleanup model switch (2026-05-21)

Real-world dictation showed Haiku cleanup taking 10-20 seconds per
transcript. Investigation: 90% of that latency was process-spawn
overhead from shelling out to `claude --print --model haiku` (Node
startup + Claude Code init + the actual model call + teardown). The
*actual model latency* was only 1-3 seconds.

**Fix:** `CleanupOrchestrator` now calls
`https://api.openai.com/v1/chat/completions` directly using the same
`HttpClient` we already use for transcription. Default model
`gpt-4o-mini` (cheap, fast, plenty smart for the task). Configurable
via `AgentOptions.DictationCleanupModel` so you can swap to
`gpt-4.1-nano` or any newer nano-class model when available without a
code change. The same vocabulary + known-mistranscription prompt is
delivered as the system message; the raw transcript is the user
message.

**Trade-off:** uses OpenAI tokens (pay per call) instead of your
Claude Code flat-fee subscription. At <$0.001 per cleanup for 200-char
transcripts, this is pennies a month even for heavy use.

Expected end-to-end latencies on a typical sentence:
- Stop -> raw transcript ready: ~2 seconds (OpenAI batch or Realtime)
- Raw -> cleaned: ~1 second (gpt-4o-mini)
- Total stop -> cleaned: **~3 seconds**, vs ~12-22 seconds with the
  old Haiku CLI path.

## Phase 0: PASS

The proof-of-fact experiment in `docs/features/dictation/phase0/` validated
that OpenAI `gpt-4o-transcribe` with the prompt parameter, followed by a
Claude Haiku cleanup pass that has the vocabulary and known mistranscription
patterns in its system prompt, recovers every expected company term across
the test clips (9 of 9). See `REPORT.md` and `transcripts.json` for the data.

Iterations are preserved:

- `REPORT_v1.md` / `transcripts_v1.json`: conservative cleanup prompt. 8/9.
  Left `Contui` alone.
- `REPORT_v2.md` / `transcripts_v2.json`: over-aggressive cleanup prompt. 8/9.
  Substituted `Contui` with the wrong glossary term (`CenCon`).
- `REPORT.md` / `transcripts.json` (v3): vocabulary plus known
  mistranscription patterns in natural language. 9/9. Final approach.

## Phase 1: COMPLETE

The core library lives in `src/CcDirector.Core/Dictation/`:

```
Dictation/
├── Models/
│   ├── Dictionary.cs            DictationDictionary, DictationProfile records
│   └── TranscriptResult.cs      TranscriptResult, PartialTranscript records
├── Providers/
│   ├── IDictationProvider.cs    Transport-agnostic STT interface
│   └── OpenAiTranscriptionProvider.cs   Batch impl via /v1/audio/transcriptions
├── DictionaryLoader.cs          YAML loader + FileSystemWatcher hot reload
├── AudioBuffer.cs               In-memory ring buffer with overflow policy
├── CleanupOrchestrator.cs       Claude Haiku side-call following SupervisorService pattern
└── DictationSession.cs          High-level facade wiring everything together
```

The session API is:

```csharp
await using var session = new DictationSession(dictionary, provider, cleanup);
session.OnPartial += text => /* update UI */;
await session.StartAsync(profile: "default");
await session.PushAudioAsync(chunk);
TranscriptResult result = await session.StopAsync();
// result.CleanedTranscript carries the final text, raw is preserved for debugging
```

### Test coverage

- 41 unit tests in `src/CcDirector.Core.Tests/Dictation/` covering
  DictionaryLoader (13), CleanupOrchestrator (11), AudioBuffer (10),
  DictationSession (7). All pass.
- End-to-end smoke test via the console harness exercises the whole
  pipeline against the real OpenAI API and the real Claude Haiku CLI
  using the three Phase 0 audio clips. All three produce clean
  transcripts with company terms intact.

### Console harness

`playground/dictation-harness/` builds to `cc-dictate-harness.exe`. Usage:

```
cc-dictate-harness                          # uses Phase 0 clip2 by default
cc-dictate-harness path/to/audio.mp3
cc-dictate-harness audio.mp3 dict.yaml --profile code
```

It loads a YAML dictionary, transcribes the audio via the Phase 1
batch provider, runs the cleanup pass, and prints raw vs cleaned text.
Defaults to a generated `sample-dictionary.yaml` next to the executable
on first run.

### Architectural deviations from PLAN.md

PLAN.md called for the Realtime API (streaming partials over WebSocket)
for `ProviderClient`. Phase 1 ships a batch provider against
`/v1/audio/transcriptions` instead. Rationale:

- The pipeline is identical end to end whether the transport is batch or
  streaming. The dictionary, the cleanup pass, and the session facade
  are unaffected.
- Streaming partials are only consumed by the HTML walkie-talkie UI
  arriving in Phase 3. There is no Phase 1 consumer.
- `IDictationProvider` is the seam. The streaming variant
  (`OpenAiRealtimeProvider`) lands in Phase 3 behind the same interface
  with zero changes to `DictationSession`.

`OnPartial` fires once just before `StopAsync` returns in the batch
provider so consumers wired to partials still get one update.

## Phase 3: COMPLETE

The browser-facing surface is in place:

- **`/dictate` WebSocket endpoint** lives in
  `src/CcDirector.ControlApi/DictationEndpoint.cs`. Wire protocol is
  documented in the file header. It accepts JSON control frames
  (`start`, `stop`, `abort`) and opaque binary audio frames, and emits
  `ready`, `started`, `partial`, `transcribing`, `final`, and `error`
  frames. Localhost-only. Honors existing `ControlApiHost` auth.
- **`/dictate.html` page** lives in
  `src/CcDirector.ControlApi/Web/dictate.html` and is embedded the same
  way as `session-view.html`. Captures with `MediaRecorder`, streams to
  the WebSocket, displays raw vs cleaned transcript and a live log.
- **`AgentOptions.DictationDictionaryPath`** added with a sensible
  default of `%LOCALAPPDATA%/cc-director/dictation/dictionary.yaml`.
  Missing file means "no vocabulary bias, no cleanup glossary" without
  breaking the pipeline.
- **Three integration tests** in
  `src/CcDirector.Gateway.Tests/DictationEndpointTests.cs`. Two pure
  protocol checks plus one full end-to-end roundtrip: client opens a
  WebSocket, sends Phase 0 `clip2.mp3` in 4 KB chunks, asserts the
  cleaned transcript contains `ConPTY`, `Soren Frederiksen`, and
  `Avalonia`. End-to-end test self-skips when `OPENAI_API_KEY` is not
  set so CI without credentials still passes.

### How to try it

1. Make sure `cc-director.exe` is running (it owns port 7879).
2. Optional: drop a dictionary at
   `%LOCALAPPDATA%/cc-director/dictation/dictionary.yaml`. A reasonable
   starter exists at `playground/dictation-harness/sample-dictionary.yaml`
   you can copy.
3. Open `http://localhost:7879/dictate.html` in a browser.
4. Click "Start recording", grant microphone permission, speak.
5. Click "Stop". The page shows the raw and cleaned transcripts.

### What is NOT yet done (deliberate)

- **True streaming partials.** Phase 3 ships the batch provider behind
  the WebSocket: audio is buffered server-side and transcribed in one
  shot when the client sends `stop`. The Realtime API variant
  (`OpenAiRealtimeProvider`) for word-by-word partials is a drop-in
  upgrade behind the same `IDictationProvider` interface and can be
  added later without changing the wire protocol.
- **Integration with `session-view.html`.** The dedicated `/dictate.html`
  page exists for end-to-end validation. Wiring dictation into the
  existing session view as a button or tab is a UX follow-up.

## Phase 4: COMPLETE

The offline-resilience pieces are in place:

- **AudioBuffer disk spill.** When the in-memory cap is exceeded, oldest
  chunks are written to disk under the configured spill directory as
  numbered files (atomic write via temp + rename) instead of being
  dropped. `DrainAll` reads disk-spilled chunks back in original order
  before the in-memory chunks. `Clear`/`Dispose` cleans up. New `Spilled`
  flag tracks whether disk spill ever fired (separate from `Overflowed`
  which now only fires when spill is disabled).
- **ConnectionState observable on DictationSession.** New
  `Models/ConnectionState.cs` (Idle, Connected, Buffering, Reconnecting,
  Failed). Session exposes `State` and `OnStateChanged`. Each state
  transition is logged.
- **Buffer-on-failure path.** `PushAudioAsync` wraps the provider call.
  On a transient error (`HttpRequestException`, `WebSocketException`,
  `SocketException`, `IOException`, `TimeoutException`,
  `TaskCanceledException`), the chunk is routed to the AudioBuffer and
  the session moves to `Buffering`. Subsequent pushes stay in the
  buffer. Programming-error exceptions are deliberately NOT caught so
  bugs stay visible.
- **`TryReconnectAsync`.** Drains the buffer through the provider. On
  partial failure, remaining chunks are re-buffered for the next attempt
  and the session stays in `Buffering`. On success the session returns
  to `Connected`.
- **`StopAsync` from `Buffering`** automatically calls
  `TryReconnectAsync` once before stopping the provider. If reconnect
  fails, the buffered audio is left behind and the
  `TranscriptResult.CleanupFailureReason` records how many chunks were
  abandoned.

### Test coverage

- 17 AudioBuffer tests (+ 7 new for disk spill: spill-on-overflow,
  many-chunk ordering, drain deletes spill files, Clear cleans disk,
  Dispose cleans disk, idempotent Dispose, post-Dispose ops throw).
- 13 DictationSession tests (+ 6 new for Phase 4): state lifecycle,
  buffer-on-transient-failure, full reconnect drain, partial drain
  failure rebuffering, Stop-from-Buffering with successful reconnect,
  Stop-from-Buffering with failed reconnect recording the reason.
- Phase 3 endpoint integration tests still pass (API contract intact).

## Post-Phase-4: streaming provider + endpoint polish (also COMPLETE)

After Phase 4 landed, the follow-ups that the original status doc
listed as "still ahead" have all shipped:

- **OpenAiRealtimeProvider** lives at
  `src/CcDirector.Core/Dictation/Providers/OpenAiRealtimeProvider.cs`.
  Talks to the OpenAI Realtime GA API
  (`wss://api.openai.com/v1/realtime?intent=transcription`). Streams
  PCM16 at 24 kHz, surfaces partial transcripts as the model emits
  deltas, commits and waits for the completed event on stop. The Beta
  API is dead (OpenAI rejects `OpenAI-Beta: realtime=v1` with
  `beta_api_shape_disabled`); the GA shape uses `session.update` with
  `session.type=transcription` and the audio config nested under
  `session.audio.input.format`. Server VAD is explicitly disabled
  because walkie-talkie use commits manually.

- **Connection-state frames** on the `/dictate` wire protocol.
  Whenever `DictationSession.OnStateChanged` fires, the endpoint sends
  `{"type":"state","value":"connected|buffering|reconnecting|failed|idle"}`.
  The HTML page renders a "connection" pill that flips color on each
  transition.

- **Disk-spill wiring in the endpoint.** Each `/dictate` connection
  now gets its own buffer under
  `%LOCALAPPDATA%/cc-director/dictation/buffer/<guid>/`. The session
  owns the buffer's lifetime; spill files clean up on dispose.

- **Provider selection by mode.** The client's start frame carries
  `{"mode":"batch"|"streaming"}`. Batch routes through
  `OpenAiTranscriptionProvider` (works with any audio container the
  transcription endpoint accepts; default for `MediaRecorder` /
  webm-opus browsers). Streaming routes through
  `OpenAiRealtimeProvider` (requires PCM16 mono at 24 kHz).

- **Browser PCM16 capture via AudioWorklet.** `dictate.html` got a
  mode picker. When set to "streaming" the page opens an AudioContext
  at 24 kHz, loads
  `/dictate-worklet.js` (a new embedded resource), and pipes the mic
  through a `pcm16-writer` worklet that posts Int16 buffers to the
  main thread for WebSocket transmission. No webm decode is needed on
  the server because the browser delivers the right format.

### Test coverage

- **68 Dictation unit tests** in `src/CcDirector.Core.Tests/Dictation/`:
  - 13 DictionaryLoader
  - 11 CleanupOrchestrator
  - 17 AudioBuffer (incl. disk spill)
  - 13 DictationSession (incl. state/buffer/reconnect)
  - 12 OpenAiRealtimeProtocol (build + parse)
  - 2 OpenAiRealtimeProvider integration (smoke + real Phase 0 audio)
- **5 /dictate endpoint integration tests** in
  `src/CcDirector.Gateway.Tests/DictationEndpointTests.cs`:
  - dictate.html served
  - dictate-worklet.js served
  - non-WS GET returns 400
  - full pipeline batch mode against real OpenAI + Haiku
  - full pipeline streaming mode against real OpenAI Realtime API
    (drives PCM through the endpoint, confirms partials arrive, asserts
    cleaned transcript contains expected company terms)

All 73 tests pass. End-to-end paths exercised against real APIs (gated
on `OPENAI_API_KEY` so CI without credentials still passes).

## What you (Soren) need to do for Phase 2

Phase 2 is the desktop microphone integration. It requires live testing
on your hardware, which is why the goal doc reserved it for you.

Concrete next steps when you are ready:

1. Decide the global hotkey (Right Alt, F13, Caps Lock, etc.) and the
   activation style (hold-to-talk vs toggle).
2. Wire a microphone capture loop using the existing NAudio code in
   `src/CcDirector.Core/Voice/` and feed chunks into
   `DictationSession.PushAudioAsync`.
3. Pipe the resulting `TranscriptResult.CleanedTranscript` to the focused
   window via `SendInput` (Win32) or clipboard paste.
4. Decide whether the dictation runs as a tray app
   (`cc-dictate.exe`, separate process) or as a feature inside the
   existing Manager UI. The library does not care.

## Desktop integration (2026-05-21, COMPLETE)

The Speak button is wired into the cc-director Avalonia window and
runs entirely in-process. No browser, no localhost roundtrip.

- **Green Speak button** in `MainWindow.axaml` next to Send / Queue /
  Handover.
- **`MicAudioCapture`** (NAudio `WaveInEvent` at 24 kHz mono PCM16,
  RMS energy event for the equalizer level meter) lives in
  `src/CcDirector.Avalonia/Voice/`.
- **`SpeakService`** wires `MicAudioCapture` chunks through
  `DictationSession` with the in-process `OpenAiRealtimeProvider` +
  `CleanupOrchestrator` + offline `AudioBuffer`. All in the same
  Avalonia process; the `/dictate` WebSocket is not used by the
  desktop surface.
- **`SpeakDialog`** is a small modal with the equalizer (driven by
  real mic level), a big timer, and a single button (Stop →
  Wait → auto-close). No "Use it" confirmation. After cleanup the
  dialog closes itself and `MainWindow` inserts the cleaned text at
  the caret in PromptInput, adding whitespace separators where
  needed.
- NAudio is a Windows-only dependency. Mac will need a per-platform
  `IMicCapture` (deferred to Phase 5).

## Browser session-view integration (2026-05-21, COMPLETE)

`session-view.html` now has the same Speak button next to Send.
Clicking it opens an in-page overlay (same look as the desktop
dialog: equalizer + timer + Stop). Cleaned text auto-inserts at the
caret in the session's `#prompt` input on completion. No "Use it"
confirmation. Esc cancels.

- Reusable JS module **`/dictate-client.js`** (new embedded resource)
  exposes `window.ccDictate.start({ onResult, onCancel, profile })`.
  Handles the overlay UI, AudioWorklet PCM16 capture, WebSocket
  protocol, level meter, and stage transitions. Any page in the
  Director can drop it in.
- `session-view.html` includes the script and binds the Speak button
  click to `window.ccDictate.start({ onResult: insertAtCaret })`.
- Same `/dictate` WebSocket backend as before. Same dictation
  library. Zero new server-side logic.

## What's still ahead (only hardware-bound work)

- **Original Phase 2: global hotkey + SendInput.** "Press the
  hotkey anywhere in Windows, talk, words appear in whatever window
  has focus." This goes beyond cc-director — it would replace
  Windows Dictation across the OS. Requires Win32 keyboard hook
  setup + SendInput plumbing + live keyboard testing.
- **Phase 5: Mac.** AVAudioEngine for capture, Accessibility APIs
  for typing into the focused window. Library code is
  platform-agnostic; only the shell layer changes.
- **Optional polish:** Speak button parity in `chat.html` and
  `manager.html` if you decide those pages need it. The
  `dictate-client.js` module is ready to drop in there too.

Everything that does not require live hardware is done. The
dictation library is usable from inside cc-director on both
surfaces.
