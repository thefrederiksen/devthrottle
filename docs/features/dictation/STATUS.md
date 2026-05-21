# Dictation Library: STATUS

Last updated: 2026-05-21 (Phase 3 complete)

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

## What's still ahead (no work yet)

- **Phase 4.** Add disk spill to `AudioBuffer` under
  `%LOCALAPPDATA%/cc-director/dictation/buffer/`. Add a connection-state
  observable. Write the disconnect-mid-stream integration test the goal
  doc calls for.
- **Phase 5.** Mac shell port. Library code is platform-agnostic; only
  the audio capture and output layers change.
- **Streaming partials follow-up.** Implement
  `OpenAiRealtimeProvider` against the OpenAI Realtime WebSocket API.
  Behind the same `IDictationProvider` interface; `DictationSession`
  and the `/dictate` wire protocol are already shaped for it.
