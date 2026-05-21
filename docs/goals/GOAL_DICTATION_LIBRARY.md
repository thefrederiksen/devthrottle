# Goal: Build the cc-director dictation library

You are the implementing agent. Your job is to build the dictation library
described in `docs/features/dictation/PLAN.md`. Read that document first.
It is the source of truth for architecture, components, phases, and what
is out of scope. This document tells you HOW to execute that plan.

## Scope of this goal

Implement Phases 0, 1, 3, and 4 from PLAN.md. Do NOT attempt Phase 2
(desktop microphone integration) or Phase 5 (Mac). Those phases require
collaboration with the user, who must test the live microphone and the
hotkey behavior on each platform. You will stop and hand off when the
non-microphone phases are complete.

In order:
1. Phase 0, proof of fact. No code commitment.
2. Phase 1, core library (DictionaryLoader, ProviderClient, AudioBuffer
   skeleton, CleanupOrchestrator, DictationSession).
3. Phase 3, HTML integration via the `/dictate` WebSocket endpoint.
4. Phase 4, offline buffering (ring buffer + disk spill + reconnect flush).
5. Stop. Report. Hand off.

## Phase 0 is a gate

Before writing any production code, run this experiment and report the
result.

1. Generate three short test audio clips using OpenAI TTS (cc-director
   already has `TtsService` in `src/CcDirector.Core/Voice/`). Each clip
   should contain a different sentence that uses two or more of these
   terms: `mindzie`, `CenCon`, `ConPTY`, `cc-director`, `Avalonia`,
   `Soren Frederiksen`. Example sentence: "I sent the cc-director patch
   to mindzie before the CenCon review."

2. Transcribe each clip through the OpenAI Realtime API (gpt-4o-transcribe)
   three ways:
   - No prompt parameter, no cleanup.
   - With the term list packed into the prompt parameter, no cleanup.
   - With the term list in the prompt parameter, and then a Claude Haiku
     cleanup pass whose system prompt contains the term list as
     "known company terms, fix any obvious mistranscriptions."

3. For each clip and each variant, record the raw output. Save the audio
   files and the JSON of all transcripts under
   `docs/features/dictation/phase0/`.

4. Decide pass or fail. Pass means: the variant with prompt + Haiku
   cleanup gets every term correct on every clip. Fail means it does not.

5. If Phase 0 fails, STOP. Write a short report at
   `docs/features/dictation/phase0/REPORT.md` explaining what worked,
   what did not, and what you would change. Do not proceed to Phase 1
   without explicit user approval.

6. If Phase 0 passes, write a short pass report and proceed.

## Phase 1 implementation notes

- Location: `src/CcDirector.Core/Dictation/`. Mirror the folder structure
  of the existing `Voice/` directory.
- Components match PLAN.md: DictionaryLoader, ProviderClient,
  AudioBuffer (basic in-memory ring buffer is fine for Phase 1, disk
  spill comes in Phase 4), CleanupOrchestrator, DictationSession.
- DictationSession exposes Start, Stop, and events for partial and final
  transcripts. It accepts an audio chunk source (an
  `IAsyncEnumerable<byte[]>` or similar) so it can be driven from a file,
  a microphone, or a WebSocket.
- Reuse existing infrastructure where it makes sense: the OpenAI client
  patterns in `Voice/`, the Haiku call pattern in
  `Supervisor/SupervisorService.cs`, the logging pattern (`FileLog.Write`)
  used everywhere in cc-director.

## Phase 3 implementation notes

- Add the `/dictate` WebSocket endpoint in `CcDirector.ControlApi`
  alongside the existing endpoints in `ControlEndpoints.cs`.
- The endpoint accepts binary audio chunks from the client and emits
  JSON messages with partial and final transcripts.
- Authentication and origin checks follow the same pattern as the
  existing endpoints. Localhost only.
- Add a thin JavaScript client and drop it into one of the existing HTML
  pages (`session-view.html` is the obvious target) behind a feature
  flag or a hidden button. Capture audio with MediaRecorder, stream to
  `/dictate`, display the returned transcripts.

## Phase 4 implementation notes

- AudioBuffer gets a disk spill path under
  `%LOCALAPPDATA%/cc-director/dictation/buffer/`. Spill kicks in when
  the in-memory buffer exceeds a configurable threshold (default 60
  seconds of audio).
- On reconnect, the buffer drains to the ProviderClient in order.
- Add a simple connection-state observable so the UI can show "offline,
  buffering" vs "online, streaming."

## Testing strategy (no microphone)

You do not have access to a microphone. Test the library entirely with
audio files.

- Use OpenAI TTS to generate any test phrases you need. Save the
  generated audio files under `tests/Dictation/audio/` so the test
  corpus is reproducible.
- Write unit tests that load a file, feed chunks into DictationSession,
  and assert on the resulting transcript.
- Write integration tests that exercise the full pipeline against the
  real OpenAI Realtime API and the real Claude Haiku API. Mark these
  with a category so they can be excluded from a "no network" test run.
- Add at least one test per scenario:
  - Dictionary applied correctly on a clip with company terms.
  - Cleanup pass correctly repairs an intentionally messy transcript.
  - Offline buffering: simulate a disconnect mid-stream, reconnect,
    verify the full transcript is delivered.
  - Hot-reload: modify the dictionary YAML on disk, verify the next
    session uses the new terms without restart.

## API key handling

- The OpenAI API key is read from the same source cc-director already
  uses for the Voice services. Check `Voice/VoiceService.cs` and follow
  that pattern. Do not introduce a new mechanism.
- The Anthropic API key for Haiku cleanup follows the same convention
  used by `Supervisor/SupervisorService.cs`.
- Never commit keys. Never log keys. Never print keys to stdout or to
  log files.

## Coding standards

Follow `docs/CodingStyle.md` strictly. The CC Director rules apply
here:

- No fallback programming. Fix root causes. If something fails, raise
  clearly with an actionable message.
- Log every public method's entry, exit, and errors using
  `FileLog.Write` and the `[ClassName] MethodName: context` format.
- Try-catch only at entry points (event handlers, lifecycle hooks,
  external event subscriptions). Not in helpers.
- UI feedback rule does not apply here directly, but any cross-thread
  hand-off to the UI must use the existing dispatcher pattern when you
  reach the consumer code.
- No Unicode characters, no emojis, no special symbols anywhere in
  source or in any text output. ASCII only.

## When to stop and ask the user

Stop and ask the user in any of these situations:

- Phase 0 fails. Do not proceed.
- You need a non-trivial deviation from PLAN.md (different component
  shape, different dependency, change to the overall architecture).
- You hit an OpenAI or Anthropic API behavior that contradicts what
  PLAN.md assumes (for example, the Realtime API does not actually
  support the prompt parameter the way we expect).
- Something requires the user's microphone to verify. This is Phase 2
  territory.
- You are unsure whether a change is in scope. Ask first.

Do NOT stop for cosmetic decisions you can make yourself (naming,
internal method shapes, log message phrasing). Use judgment, follow
existing conventions in cc-director.

## Done definition

You are done with this goal when:

- Phase 0 has passed and the report is written.
- Phase 1 components are implemented with unit tests.
- Phase 3 `/dictate` endpoint is in place and the HTML client streams
  transcripts successfully end to end using TTS-generated audio
  injected through a test harness.
- Phase 4 offline buffering passes its integration test (disconnect
  mid-stream, reconnect, full transcript delivered).
- All tests pass. Build is clean.
- A short `docs/features/dictation/STATUS.md` document is written that
  lists what was built, where it lives, what tests cover it, and what
  the user must do for Phase 2 (microphone integration on desktop).

Then stop and report. Phase 2 is the user's collaboration loop.
