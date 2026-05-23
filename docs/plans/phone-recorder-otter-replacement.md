# Phone Voice Recorder -> Gateway Transcription (Otter replacement)

## Status: IMPLEMENTED (server + app), pending hardware verification

Owner: Soren. Designed and implemented 2026-05-23.

Implementation report (screenshots + test instructions):
`docs/features/phone-recorder/REPORT.html`. Server pipeline and Android app
are built and tested; the three hardware-bound checks (multi-hour
crash-safety on a real device, tailnet end-to-end upload, APK sideload) are
listed in the report as the remaining human steps. Changes are uncommitted.

Hand this document to a dev agent. It contains a single clear goal, the
decisions already made (with rationale), the wire contract, and phased
milestones that are each independently testable. Where a decision is still
open it is called out explicitly under "Open questions" - do not silently
pick one.

---

## Goal (one sentence)

Build a standalone Android app that records audio fully offline and
crash-safe for multi-hour sessions, then uploads the recording plus the
notes typed during it to the always-on CC Director Gateway, which
transcribes it through the existing dictation pipeline and files the
cleaned transcript into the vault.

If you only read one paragraph, read that one. Everything below serves it.

---

## The split (memorize this)

- **Phone = dumb, reliable, offline recorder + note-taker.** It captures
  audio to local storage and never depends on a network during recording.
  It does NOT transcribe.
- **Gateway = upload target + transcription + vault storage.** All the
  heavy work happens server-side, reusing code that already ships today.

This split is the whole architecture. Resist the temptation to transcribe
on the phone or to record through the Gateway.

---

## What already exists (REUSE, do not rebuild)

The server-side transcription path is mature and tested. The phone app is
the only genuinely new surface; the Gateway needs one new ingest endpoint
that feeds existing code.

| Component | Path | Reuse for |
|---|---|---|
| `DictationSession` facade | `src/CcDirector.Core/Dictation/DictationSession.cs` | Drive transcription of each uploaded chunk |
| `OpenAiTranscriptionProvider` (batch) | `src/CcDirector.Core/Dictation/Providers/OpenAiTranscriptionProvider.cs` | Transcribe a finalized audio file via `/v1/audio/transcriptions`. This is the right provider for file ingest (NOT the realtime one). |
| `CleanupOrchestrator` | `src/CcDirector.Core/Dictation/CleanupOrchestrator.cs` | gpt-4o-mini cleanup pass with vocabulary + known-mistranscription glossary |
| `DictionaryLoader` + dictionary YAML | `src/CcDirector.Core/Dictation/` | Company-term vocabulary bias (ConPTY, Avalonia, mindzie, etc.) |
| `AudioBuffer` (disk spill) | `src/CcDirector.Core/Dictation/AudioBuffer.cs` | Reference for the crash-safe / chunked philosophy; the phone mirrors this on-device |
| Gateway endpoint pattern | `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` | Where the new `/ingest/*` routes are mapped |
| Gateway auth (token) + Tailscale Serve | `src/CcDirector.Gateway/Tailscale/`, `docs/plans/phase1-https-via-tailscale.md` | The HTTPS-only remote path the phone uploads over |
| `cc-vault docs` CLI | on PATH | File the final transcript as a vault document |

Read `docs/features/dictation/STATUS.md` before touching the server side.
It documents the full pipeline, the OpenAI 25 MB file limit, the cleanup
model choice, and the wire protocol of the existing `/dictate` WebSocket.

---

## Key decisions (already made, with rationale)

These are decided. Implement them as written unless Soren overrides.

### 1. Native Android app, built with .NET MAUI

A PWA / browser recorder is rejected for this use case. Browser
`MediaRecorder` is unreliable for multi-hour capture: the OS reclaims
backgrounded tabs and recording stops when the screen locks. The user's
hard requirement is "a 2-3 hour call must never lose audio," and only a
**native Android foreground service** delivers that (the OS will not kill
a foreground-service process, and recording survives screen lock).

Toolchain: **.NET MAUI** (C#), to match the existing codebase and let the
phone app share DTO/contract types with the Gateway. The reliability comes
from the Android foreground service + recording API, which is
platform-specific code regardless of MAUI vs Kotlin; MAUI keeps the UI,
upload, and contract logic in the same language as everything else.

> Fallback only if MAUI's foreground-audio-service integration proves
> genuinely unworkable during Milestone 1: drop to native Kotlin. Record
> the reason in this doc before switching. Do not switch for taste.

### 2. Rolling finalized chunks, not one giant file

The phone records into **rolling ~1-minute segments**, each independently
finalized to disk (`.m4a` AAC, mono, ~64 kbps). One minute is chosen for
maximum crash safety: a crash or kill loses at most ~60 seconds. This
single decision solves four problems at once:

1. **Crash safety.** A crash loses at most the current open segment (~1
   min), and even partial PCM/AAC is usually salvageable. Prior finalized
   segments are safe on disk.
2. **OpenAI 25 MB limit.** The batch transcription endpoint caps files at
   25 MB. A 1-minute mono AAC chunk is well under 1 MB - always far under
   the limit. A single 3-hour file would not be transcribable in one shot.
   Note the trade-off of 1-min segments: a 3-hour call produces ~180
   chunks = ~180 transcription API calls + 180 files to manage. This is
   accepted in exchange for the tighter crash-loss bound.
3. **Resumable / early upload.** Each finalized chunk can be uploaded as
   soon as it closes (if online) or batched at the end (if the call was
   offline). Upload is idempotent per chunk index.
4. **Progressive transcription.** The Gateway can start transcribing
   chunk 0 while the call is still going.

Use Android `AudioRecord` -> raw PCM with the app doing segment rotation,
or `MediaRecorder` with `setNextOutputFile` for clean segment handoff.
Prefer whichever gives a finalized, independently-decodable file per
segment. Verify a mid-recording kill leaves prior segments playable.

### 3. Notes are timestamped markers in a sidecar manifest

While recording, the user types notes. Each note is stored with the
millisecond offset from recording start. Notes live in a per-recording
manifest JSON (schema below), uploaded alongside the audio, and are
attached to the final vault document.

### 4. Upload over Tailscale HTTPS only

The Gateway is HTTPS-only by construction (Kestrel on loopback, Tailscale
Serve as the only remote path - see the HTTPS memory rule and
`phase1-https-via-tailscale.md`). The phone is on the same tailnet and
uploads to the Gateway's Tailscale HTTPS hostname. No plain-HTTP path, no
degraded mode. Auth uses the existing Gateway token (the same token the
web UI uses), supplied by the phone as a bearer header.

### 5. Final transcript filed into a dedicated vault collection, with the audio attached

After all chunks transcribe and the per-chunk texts are concatenated (with
time offsets) and run through the cleanup pass, the result is filed into a
**dedicated transcripts collection/catalog in the vault** (NOT mixed into
the general `docs` library). The original audio is **stored inside the
vault itself as an attachment** on the transcript record, so audio and
transcript travel together in vault backups.

The dedicated collection does not exist yet. As the first server task,
inspect the current `cc-vault` schema (`cc-vault docs`, `cc-vault catalog`,
`cc-vault library`) and define a `transcripts` collection: fields for
title, recorded date, duration, notes (timestamped list), raw transcript,
cleaned transcript, and an audio attachment. If `cc-vault` cannot express
a new collection or attach binary audio, STOP and report the gap rather
than shoehorning it into `docs` (per the "tool gaps - stop and report"
rule). Do not silently fall back to the general docs library; that path
was explicitly rejected.

### 6. Start recording instantly; rename later

Tapping record begins capture immediately with a default title like
`Recording 2026-05-23 09:34`. The user can rename during or after the
session. No title prompt blocks the start of capture - missing the first
seconds of a call while typing a title is unacceptable for the use case.

### 7. Distribution: sideloaded signed APK

The dev agent produces a signed APK that Soren copies to the phone and
installs (with "install unknown apps" enabled). No Play Store account, no
review cycle. Updates = build and install a new APK. This is a personal
single-user tool; the Play Store internal-track path was considered and
rejected as unnecessary overhead.

---

## Architecture

### A. Phone app ("CC Recorder", Android, .NET MAUI)

Components:

- **RecordingService** (Android foreground service). Owns the mic, rotates
  segments every ~5 min, writes each finalized segment + updates the
  manifest. Survives screen lock and backgrounding. Shows a persistent
  notification with elapsed time and a stop action.
- **Local store.** Per-recording directory on device:
  ```
  /recordings/<recordingId>/
    manifest.json
    0000.m4a
    0001.m4a
    ...
  ```
- **Notes UI.** Big record button, running timer, a text box to add a
  timestamped note at any moment, and a list of notes added so far.
- **Upload worker.** When connectivity is available, uploads unsent chunks
  (idempotent by index) and finally calls "complete". Resumable: it can
  upload progressively during the call or all at once afterward. Uses
  Android `WorkManager` so uploads survive app exit.
- **Library screen.** List of past recordings with state (Recording /
  Local only / Uploading / Transcribing / Filed) and a manual
  "Upload now" / "Retry" action.

The phone never deletes a local recording until the Gateway confirms the
transcript is filed in the vault (status `filed`).

### B. Manifest schema (sidecar JSON, lives on phone and is uploaded)

```json
{
  "recordingId": "uuid-v4 generated on phone",
  "title": "user-entered, or 'Recording <local datetime>' default",
  "deviceId": "stable phone id",
  "startedAt": "2026-05-23T09:34:00Z",
  "endedAt": "2026-05-23T12:10:00Z",
  "sampleRateHz": 16000,
  "channels": 1,
  "codec": "aac-m4a",
  "chunks": [
    { "index": 0, "file": "0000.m4a", "startMs": 0,     "durationMs": 60000, "bytes": 482345, "sha256": "..." },
    { "index": 1, "file": "0001.m4a", "startMs": 60000, "durationMs": 60000, "bytes": 478012, "sha256": "..." }
  ],
  "notes": [
    { "tMs": 124000, "text": "Discussed Q3 pricing" },
    { "tMs": 845000, "text": "ACTION: send proposal to Dave" }
  ]
}
```

### C. Gateway ingest contract (NEW endpoints in `GatewayEndpoints.cs`)

All require the Gateway bearer token. All are idempotent so the phone can
safely retry.

| Method + path | Body | Behavior |
|---|---|---|
| `POST /ingest/recording` | manifest header fields (id, title, device, startedAt) | Register a recording. Idempotent on `recordingId`. Returns the recording resource. |
| `PUT /ingest/recording/{id}/chunk/{index}` | raw audio bytes + `X-Chunk-Sha256` header | Store one finalized chunk. Verify sha. Idempotent: re-PUT of same index+sha is a no-op 200. May enqueue transcription of this chunk immediately. |
| `POST /ingest/recording/{id}/complete` | full manifest (with notes + endedAt) | Mark upload done. Triggers final assembly: ensure all chunks transcribed, concatenate with time offsets, run cleanup pass, file to vault. |
| `GET /ingest/recording/{id}/status` | - | Returns `{ state, chunksReceived, chunksTotal, chunksTranscribed, vaultDocId? }`. States: `receiving`, `transcribing`, `cleaning`, `filed`, `error`. |

Server-side storage of received chunks + manifest:
`%LOCALAPPDATA%/cc-director/recordings/<recordingId>/` (mirrors the
existing dictation buffer convention).

### D. Server pipeline (reuses existing code)

1. Each received chunk -> `OpenAiTranscriptionProvider` (batch) with the
   loaded dictionary as vocabulary bias -> per-chunk raw transcript stored
   next to the chunk.
2. On `complete`: concatenate per-chunk transcripts in index order,
   prefixing each with its `startMs` offset for a rough timeline.
3. Run the concatenated raw text through `CleanupOrchestrator`
   (gpt-4o-mini, vocabulary + known-mistranscription glossary).
4. File into the dedicated vault transcripts collection (see Decision 5):
   title, recorded date, duration, notes (rendered as a timestamped list),
   cleaned transcript as the body, raw transcript as metadata, and the
   original audio stored as a vault attachment.
5. Set status `filed` with the vault document id. Phone sees this on its
   next status poll and may then delete its local copy.

Reuse `DictationSession` if it cleanly drives file-by-file batch
transcription; if the facade is too streaming-oriented for N independent
files, call `OpenAiTranscriptionProvider` + `CleanupOrchestrator` directly
(both are public, transport-agnostic seams). Do not duplicate the OpenAI
HTTP logic.

---

## Milestones (each independently shippable + testable)

### Milestone 1 - Reliable offline recorder (phone only, no Gateway)
Foreground-service recording with rolling finalized 5-min segments + notes
+ on-device library. **Acceptance:** record a 3-hour session with screen
locked and app backgrounded; kill the app mid-segment; confirm all prior
segments are on disk, playable, and the manifest is intact. No data loss.

### Milestone 2 - Gateway ingest endpoints (server only, no phone)
Implement `/ingest/*` routes + on-disk chunk storage + status. **Acceptance:**
integration test (in `CcDirector.Gateway.Tests`) PUTs synthetic chunks,
asserts idempotency on re-PUT, asserts sha mismatch is rejected, asserts
status transitions. No transcription yet.

### Milestone 3 - Server transcription + vault filing
Wire received chunks through `OpenAiTranscriptionProvider` +
`CleanupOrchestrator`, concatenate, file via `cc-vault docs`. **Acceptance:**
end-to-end test (gated on `OPENAI_API_KEY`, like the existing dictation
tests) feeds the Phase 0 audio clips as chunks and asserts the filed vault
document contains the expected company terms.

### Milestone 4 - Phone upload worker (end to end)
WorkManager-based resumable upload (progressive during call + batch after),
plus the library state machine and "delete local only after `filed`."
**Acceptance:** real phone records a short session, app shows
Recording -> Uploading -> Transcribing -> Filed, and the transcript appears
in the vault. Then repeat with the phone in airplane mode during recording
and online only afterward.

### Milestone 5 - Polish
Configurable segment length and bitrate, recording rename, retry/error
surfacing, optional auto-upload-on-wifi-only, manifest versioning.

---

## Testing strategy

- **Server:** unit + integration tests in
  `src/CcDirector.Gateway.Tests/` mirroring the existing
  `DictationEndpointTests.cs`. Gate real-OpenAI tests on `OPENAI_API_KEY`
  so credential-less CI still passes.
- **Phone:** the crash-safety acceptance test in Milestone 1 is the
  highest-value test - automate the "kill mid-recording, assert prior
  segments intact" check.
- **End to end:** the Milestone 4 airplane-mode-during-recording run is
  the canonical proof the Otter use case works.

---

## Decisions resolved (no open questions)

Soren answered all five open questions on 2026-05-23. They are now folded
into the Decisions section above and recorded here for traceability:

1. **Vault destination:** dedicated transcripts collection/catalog, NOT
   the general docs library. Dev agent defines the collection. (Decision 5)
2. **Source audio retention:** keep the original audio inside the vault
   itself as an attachment, so it travels with backups. (Decision 5)
3. **Segment length:** 1 minute, for maximum crash safety. (Decision 2)
4. **Title flow:** start recording instantly, rename later. (Decision 6)
5. **Distribution:** sideloaded signed APK. (Decision 7)

The only thing the dev agent must still discover (not decide) is the exact
`cc-vault` mechanism for a new collection + binary attachment - and if
that capability is missing, STOP and report it rather than working around
it.

---

## Out of scope (explicitly NOT in this plan)

- On-device transcription (the phone never transcribes).
- iOS. Android only for now.
- Real-time / live transcription during the call (the existing `/dictate`
  streaming path already covers live dictation on desktop/browser; this
  feature is record-now, transcribe-later).
- Speaker diarization. If wanted later, it is a server-side enhancement to
  the pipeline, not a phone change.
- Replacing or modifying the existing desktop/browser Speak feature.

---

## Why not just extend the existing browser recorder

The browser `/dictate` path already exists and works for live dictation,
so it is tempting to make it a PWA and call it done. It is rejected here
because the core requirement is reliable multi-hour offline capture that
survives screen lock and backgrounding, which browser `MediaRecorder`
cannot guarantee. The browser path stays as-is for live desktop dictation;
this plan adds a separate, purpose-built offline capture surface that
hands off to the same server-side transcription pipeline.
