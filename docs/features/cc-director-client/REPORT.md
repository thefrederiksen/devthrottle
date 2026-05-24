% CC Director Client - Build Report
% GitHub issue #138
% 2026-05-24

# CC Director Client

A native Android voice client for talking to Claude Code sessions hands-busy
(driving, walking). Built as a verbatim clone of the CC Recorder app and then
extended across four phases. The recorder stays the daily driver and was left
untouched; the new app is a separate package that installs alongside it.

This report records what was built and what was verified for each phase.

---

## Summary

| Item | Result |
|------|--------|
| App | CC Director Client (`com.ccdirector.client`) |
| Source | `phone/CcDirectorClient` (clone of `phone/CcRecorder`) |
| Solution | `phone/CcDirectorClient.slnx` (app + test project) |
| Android build | Clean, 0 errors (net10.0-android) |
| Signed APK | `com.ccdirector.client-Signed.apk` (~16.9 MB) |
| Unit tests | 28 passing (net10.0, off-device) |
| Live contract | Gateway `GET /sessions` shape verified against the running gateway |
| Recorder | `phone/CcRecorder` byte-for-byte unchanged |

Phase commits on `main`:

```
7befef2  Phase 1 - clone CC Recorder to CC Director Client
7305381  Phase 2 - single-session voice
fb262f6  Phase 3 - background audio + ducking
451e226  Phase 4 - all-sessions conductor
```

---

## Architecture

The app is a smart client; no new gateway endpoints were needed.

- Roster from the Gateway: `GET /sessions` returns every session across all
  Directors, each stamped with the owning Director's Tailnet base URL
  (`tailnetEndpoint`) and authoritative `statusColor`.
- Per-session voice round-trip goes directly to the owning Director's Control
  API using that base URL: `POST /voice/utterance` -> `PUT .../chunk/0` ->
  `POST .../complete` (transcribe) -> `POST /chat` (follow the turn) -> speak the
  reply with native Android text-to-speech.
- Recap for the conductor: `GET/POST /sessions/{sid}/recap`.
- Reply audio is synthesized on-device (no `/tts` network fetch) so it stays
  reliable backgrounded and on weak signal.

Pure logic (roster parsing, the needs-you filter, the chat turn state machine,
and the conductor queue) lives in dependency-free classes under
`phone/CcDirectorClient/Voice` so it is unit tested off-device; the test project
link-includes those files as a single source of truth.

---

## Phase 1 - Clone the app (no behavior change)

Copied `phone/CcRecorder` to `phone/CcDirectorClient` verbatim, then renamed:

- namespace `CcRecorder` -> `CcDirectorClient` (and `RootNamespace`)
- `ApplicationTitle` -> "CC Director Client"
- `ApplicationId` -> `com.ccdirector.client`

Added `phone/CcDirectorClient.slnx`. The recorder (`com.ccdirector.recorder`) is
untouched, so both apps install at once with distinct package ids.

Evidence:

- Android build clean, 0 errors (33 inherited recorder warnings only).
- Produced `com.ccdirector.client.apk` and `com.ccdirector.client-Signed.apk`.
- No `CcRecorder` identifiers remain in the clone; no edits to `phone/CcRecorder`.

## Phase 2 - Single-session voice

New Talk screen (AppShell now hosts Talk + the cloned Recorder as tabs):

- Lists sessions from `GET /sessions` with a status dot (green/blue/yellow/red).
- Pick a session: push Talk to capture, push Send to stop and run the round-trip
  (`/voice/utterance` upload -> transcribe -> `/chat` -> native TTS reply).
- Long turns are followed by polling `/chat` (`PollOnly`), with an occasional
  spoken progress note.
- The screen is kept awake (Waze-style) while it is in front.

Voice layer: `GatewayClient`, `DirectorVoiceClient`, `VoiceConversation`,
`RosterParser`, `SessionInfo`, `SessionFilter`, `ChatTurnResult`, `ClientLog`;
Android `AndroidUtteranceRecorder` and `AndroidTextToSpeech`.

Evidence:

- Android build clean, 0 errors.
- 28 unit tests passing (roster parsing, needs-you filter, chat status mapping,
  conductor queue).
- Live Gateway `GET /sessions` returns exactly the fields `RosterParser` maps
  (`sessionId`, `name`, `statusColor`, `activityState`, `tailnetEndpoint`, ...),
  and `tailnetEndpoint` correctly carries the per-Director port.

## Phase 3 - Background audio + ducking

- `VoiceForegroundService` (foreground service typed `microphone|mediaPlayback`)
  keeps the round-trip and the spoken reply alive when the app is backgrounded or
  the screen is off. This is the fix for the web voice page's "problem fetching"
  failure when an agent finishes while another app is in front.
- The Talk screen starts the service right after the microphone permission is
  granted (the required order on Android 14+) and stops it on leaving.
- Music ducks under the voice: `AndroidTextToSpeech` requests transient audio
  focus (`GainTransientMayDuck`) while speaking and abandons it after, so music
  dips then restores rather than stopping.
- Added the `FOREGROUND_SERVICE_MEDIA_PLAYBACK` permission.

Evidence:

- Android build clean, 0 errors.

## Phase 4 - All-sessions conductor

"Talk to all sessions that need me":

- Polls the roster and filters to sessions whose authoritative `statusColor` is
  "red" (needs the user - question, error, or permission). Working (blue), idle
  or clean (green), and soft-warning (yellow) sessions are deliberately skipped:
  no "still working" roll-call.
- Rotates one at a time. For each it speaks the session name, then the recap,
  then the answer/question, then waits.
- The user either push-to-talk replies to that session or presses Next. Nothing
  auto-advances. Next refreshes the roster first so resolved sessions drop out,
  then advances round-robin; the cursor stays on the same session across
  background refreshes.

The needs-you classification uses the wingman's authoritative `statusColor`
verbatim rather than re-deriving it, so the conductor agrees with what the
desktop and wingman show.

Evidence:

- Android build clean, 0 errors.
- Conductor queue behavior (filtering, round-robin advance, cursor preservation,
  clamping when the current session resolves) covered by unit tests.

---

## Verification scope

Verified in this build:

- All four phases build clean for `net10.0-android` (0 errors).
- 28 unit tests passing for the dependency-free voice logic.
- A signed APK is produced with the distinct `com.ccdirector.client` package id.
- The live Gateway `GET /sessions` payload matches the client's roster contract.

Left to on-device validation (the phone, as with the recorder):

- The end-to-end spoken round-trip against a live session in the car, and the
  backgrounded-with-music-ducking behavior, are validated on the physical device.
  The pieces they depend on (roster contract, build, foreground service wiring,
  audio-focus request, native TTS path) are in place and build clean.

---

## Out of scope (follow-up)

- Hands-free start (wake word) and end-word ("over") to replace the buttons.
- A server-side gateway voice conductor (only if client-side orchestration proves
  insufficient).
- iOS build (the recorder is Android-only today).
