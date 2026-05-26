# Voice Unification + Voice-Mode Flag - Implementation Plan

Status: DRAFT for review (2026-05-25)

This plan covers two related but separable efforts:

- Workstream A - Consolidate the voice backend so the desktop, HTML, and Android
  clients all run through ONE shared pipeline (one STT, one cleanup, one summarizer,
  one log). No two implementations of the same logic.
- Workstream B - Make "voice mode" a real, durable, session-level flag that every
  client reads and writes through one source of truth, with explicit walkie-talkie
  (start/stop) semantics. No auto-detection.

The two can ship independently. Workstream B is smaller, higher daily-leverage, and
mostly promotes machinery that already exists. Workstream A is the larger
architectural consolidation and primarily touches the desktop path.

---

## 1. North Star

One backend voice pipeline. The HTML page and the Android app are thin clients over
the same Control API; the desktop in-app voice draws from the same Core services
rather than its own parallel stack. The only legitimate per-client difference is
transport and the fact that the Android app can keep a turn alive in the background.
Eventually the HTML voice page can be retired without losing any backend code,
because there is no backend code unique to it.

A session is explicitly flagged as "in voice mode" (walkie-talkie). That flag is
authoritative session state, persisted, on the wire, and rendered by every client,
so a session never silently flickers between typed and spoken interaction.

---

## 2. Current State (verified)

### 2a. What is already shared

HTML voice and the Android app are already ~95% the same backend:

- Endpoints: `POST /voice/utterance`, `PUT /voice/utterance/{id}/chunk/{index}`,
  `POST /voice/utterance/{id}/complete`, then `POST /chat` (with `Voice=true`,
  and `PollOnly=true` for polling).
- Transcription + cleanup: `VoiceUtteranceService.CompleteAsync`
  (src/CcDirector.Core/Voice/VoiceUtteranceService.cs:99) ->
  `VoiceService.TranscribeAndCleanAsync` (src/CcDirector.Core/Voice/VoiceService.cs:137)
  -> `WingmanService.CleanVoiceTranscriptAsync`.
- Spoken summary: `ChatService.HandleAsync`
  (src/CcDirector.ControlApi/Chat/ChatService.cs:47) ->
  `BuildSpokenSummaryAsync` (ChatService.cs:281) ->
  `ClaudeSummarizer.SummarizeAsync` (src/CcDirector.Core/Voice/Services/ClaudeSummarizer.cs:78),
  the fidelity-first summarizer from issue #141.
- Turn logging: `VoiceTurnLog` (src/CcDirector.Core/Voice/VoiceTurnLog.cs).

The Android-specific code is only: the transport client
(phone/CcDirectorClient/Voice/DirectorVoiceClient.cs), native TTS
(Platforms/Android/AndroidTextToSpeech.cs), the background foreground service
(Platforms/Android/VoiceForegroundService.cs), and the multi-session conductor
(Voice/VoiceConversation.cs).

### 2b. What diverges - the desktop voice path

The desktop in-app voice (`src/CcDirector.Avalonia/Voice/`) is the real outlier:

- Different STT: `SpeakService` (SpeakService.cs:51) builds an in-process dictation
  pipeline on the OpenAI realtime/streaming provider, NOT the batched Whisper path
  the other two share.
- Different cleanup: it runs `CleanupOrchestrator` with `DictionaryLoader(watch:true)`,
  a separate pipeline from `WingmanService.CleanVoiceTranscriptAsync`.
- Input-only: the desktop speaks nothing back. No `ClaudeSummarizer`, no `VoiceTurnLog`.

### 2c. Known bug to fold in

The Gateway/mobile transcriber is constructed once with `watch:false`
(src/CcDirector.Core/Recording/OpenAiRecordingTranscriber.cs:44), so dictionary
edits never reach mobile transcription, while the desktop's `watch:true` does pick
them up. Documented in docs/problems/voice-dictionary-not-applied-on-mobile.md.
Consolidating STT/cleanup is the natural moment to kill this divergence.

### 2d. The flag already half-exists

- `Session.ViewMode` (MobileViewMode enum: Off/Text/Voice) at
  src/CcDirector.Core/Sessions/Session.cs:283, with derived `VoiceMode` (line 297)
  and `MobileMode` (line 290) bools.
- Working toggle endpoint `POST /sessions/{sid}/voice-mode`
  (src/CcDirector.ControlApi/ControlEndpoints.cs:221) flips ViewMode and returns the
  derived bools. The HTML page already calls it (session-view.html:1343).

Gaps:
1. Ephemeral - `ViewMode` is explicitly "what a remote viewer is looking at right
   now," not durable session intent (Session.cs:281 comment). Not in PersistedSession
   (SessionStateStore.cs:10), so it dies on Director restart.
2. Not on the wire - `SessionDto` (src/CcDirector.Gateway.Contracts/SessionDto.cs:7)
   has no voice-mode field, so desktop tiles and the Android roster never learn it.
3. Not rendered on desktop - `SessionViewModel`
   (src/CcDirector.Avalonia/SessionViewModel.cs:11) exposes no voice-mode property and
   the tile/prompt UI has no treatment for it.
4. Semantic mismatch - viewer-centric ("I'm viewing this in voice on my phone") vs
   the session-centric flag we want ("this session is a walkie-talkie for everyone").

---

## 3. DECISION TO CONFIRM (blocks Workstream B)

How to model the session-level flag, given the existing viewer-centric `ViewMode`:

- Option 1 (recommended): Add a distinct, durable, session-level `VoiceMode` intent
  flag on the session, owned by SessionManager and persisted. Keep `ViewMode` as the
  per-viewer ephemeral view if it is still needed elsewhere, but the new flag is the
  single authoritative "this session is a walkie-talkie" truth all clients read/write.
  Cleanest separation; avoids conflating "who is looking" with "what mode the session
  is in." Aligns with the single-source-of-truth, no-derived-substitute rule.
- Option 2: Redefine `ViewMode` itself to be session-level and persisted, and have
  each viewer simply follow it. Less new surface, but it overloads a field that today
  means something else and risks subtle regressions in the HTML/wingman-explain path
  that already reads it.

Recommendation: Option 1. Everything below assumes it. If you prefer Option 2, only
the storage/owner details in Phase B1 change; the client work is the same.

Second decision (desktop UX while flag is on): does the desktop prompt box go into a
visible "voice mode" state (de-emphasized keyboard, walkie-talkie affordance), or do
we just show a badge and leave the terminal fully usable? Plan assumes: badge on the
tile always, plus a prompt-box state on the focused session. Confirm.

---

## 4. Workstream B - Voice-Mode Flag

### Phase B1 - Authoritative session flag + persistence
- Add `bool VoiceMode` (intent) to `Session` (Core/Sessions/Session.cs), owned and
  mutated only via SessionManager, with an `OnVoiceModeChanged` event mirroring the
  existing `OnStatusColorChanged`/`OnActivityStateChanged` pattern (SessionManager.cs).
- Add the field to `PersistedSession` (Core/Sessions/SessionStateStore.cs:10) and
  save on change (same path rename uses: MainWindow catches the event and calls
  SessionStateStore.Save, MainWindow.axaml.cs ~895). Restore on load.
- Repurpose/replace the existing `POST /sessions/{sid}/voice-mode` endpoint
  (ControlEndpoints.cs:221) to mutate the new flag through SessionManager rather than
  setting `ViewMode` directly. Keep the request/response shape (`{ enabled }` ->
  `{ voiceMode }`) so the HTML client keeps working.
- Tests: SessionManager_SetVoiceMode_PersistsAndRaisesEvent; round-trip through
  SessionStateStore; endpoint toggles flag and returns it.

### Phase B2 - Put the flag on the wire
- Add `bool VoiceMode` to `SessionDto` (Gateway.Contracts/SessionDto.cs:7).
- Populate it in the Map function (ControlEndpoints.cs:1459).
- Gateway aggregator (Gateway/Api/GatewayEndpoints.cs:189) passes it through
  unchanged (it already forwards the Control API SessionDto list).
- Tests: Map_CopiesVoiceMode; Gateway roster contract test asserts the field survives
  fan-out (extend the existing CcDirector.Gateway roster test).

### Phase B3 - Desktop rendering
- Add `VoiceModeEnabled` to `SessionViewModel` (Avalonia/SessionViewModel.cs:11),
  reading `Session.VoiceMode` and subscribing to `OnVoiceModeChanged` (same pattern as
  the other status subscriptions at SessionViewModel.cs:48).
- Tile badge: add a voice-mode indicator to the session tile in MainWindow.axaml
  bound to `VoiceModeEnabled`. Follow docs/VisualStyle.md.
- Prompt-box state (focused session): bind the prompt input styling to
  `VoiceModeEnabled` so it visibly reads as walkie-talkie when on. Exact treatment per
  the Decision-2 answer.
- A desktop control to toggle the flag (button on the tile or session header) calling
  the same Control API path used by clients, so the desktop is not a special case.
- Tests: SessionViewModel_VoiceModeChanged_RaisesPropertyChanged.

### Phase B4 - HTML + Android read the flag
- HTML: it already POSTs to /voice-mode; update it to also reflect the authoritative
  flag from the session read (GET /sessions/{sid}) so its UI matches when the flag is
  flipped from another client.
- Android: the roster already parses SessionDto; surface VoiceMode in the session row
  and have the conductor treat flag state as the truth for which sessions are
  walkie-talkie. (DirectorVoiceClient / VoiceConversation.)
- Tests: extend the Android roster contract test to assert the field is parsed.

---

## 5. Workstream A - Backend Voice Consolidation

Goal: the desktop voice path stops using its own STT + cleanup and draws from the
same Core services the HTML/Android path uses, so there is exactly one implementation
of each concern.

### Phase A1 - Extract a single voice pipeline seam in Core
- Define one Core-level entry the three clients share for "audio bytes (or stream) in
  -> cleaned transcript out," wrapping the existing
  `VoiceService.TranscribeAndCleanAsync` so cleanup + dictionary handling live in one
  place. The realtime-vs-batched STT difference becomes an implementation detail
  behind this seam, not a second cleanup pipeline.
- Reuse the unused `IStreamingSpeechToText` interface the Explore pass found rather
  than inventing a new abstraction.
- No client behavior change yet; this is the seam other phases plug into.

### Phase A2 - Fix the dictionary divergence at the seam
- Make the shared cleanup use one dictionary source with consistent hot-reload, fixing
  OpenAiRecordingTranscriber.cs:44 (`watch:false`) as part of routing through the
  shared seam. This closes docs/problems/voice-dictionary-not-applied-on-mobile.md.
- Tests: editing the dictionary file is reflected by the shared seam without a Gateway
  restart (the desktop watch:true behavior becomes the single behavior).

### Phase A3 - Move desktop voice onto the shared pipeline
- Re-point `SpeakService` (Avalonia/Voice/SpeakService.cs:51) at the shared seam for
  cleanup so it no longer owns a parallel `CleanupOrchestrator` flow. Keep the desktop
  realtime STT as the streaming implementation behind the seam if low latency matters
  there; the cleanup + dictionary + logging become shared.
- If/when the desktop is meant to speak summaries back (currently input-only), it uses
  the same `ClaudeSummarizer` + `VoiceTurnLog` rather than a new path. Confirm whether
  desktop spoken output is in scope; if not, leave input-only but on the shared seam.

### Phase A4 - One logging story
- Ensure every client's turn (including desktop, if it gains output) writes through
  `VoiceTurnLog` so the fidelity comparison log is uniform across clients.

### Phase A5 - Retire-readiness for HTML
- Confirm no backend logic is unique to the HTML page (after A1-A4 it should not be).
- Document that the HTML voice page is now a pure thin client and can be removed
  whenever the Android app fully replaces it, with zero backend impact.

---

## 6. Sequencing and Dependencies

- Workstream B is independent of A and delivers the daily-felt improvement; do it
  first: B1 -> B2 -> B3 -> B4.
- Workstream A can proceed in parallel or after: A1 -> A2 -> A3 -> A4 -> A5.
- A2 (dictionary fix) is the one item with standalone user value and could be pulled
  forward if mobile transcription quality is hurting now.
- Nothing in A blocks B; the flag does not depend on the pipeline shape.

---

## 7. Testing Strategy

- Unit tests per phase as listed (Arrange-Act-Assert, Method_Scenario_Result naming).
- Contract tests: Gateway roster must carry VoiceMode end to end; Android roster parse.
- Manual end-to-end on the real surfaces (not stand-ins), per project rule:
  - Desktop: toggle voice mode on a session, confirm badge + prompt state + persistence
    across a Director restart.
  - HTML: toggle from the phone browser, confirm desktop tile reflects it live.
  - Android: confirm roster shows the flag and the conductor honors it.
  - Dictionary: edit a term, confirm mobile transcription picks it up without restart.
- Deliver a short HTML report (cc-html boardroom) with clean screenshots for the
  end-to-end runs.

---

## 8. Risks and Notes

- Semantic repurpose of the voice-mode endpoint: the HTML page relies on the current
  shape; keep the request/response identical when re-pointing it at the new flag.
- ViewMode still feeds the wingman-explain/briefing path (ControlEndpoints around the
  explain endpoint). If we add a separate flag (Option 1), verify we are not breaking
  any place that currently infers intent from ViewMode.
- Desktop STT latency: do not regress the desktop's realtime feel by forcing it onto
  batched STT; only the cleanup/dictionary/logging must be shared, not necessarily the
  STT transport.
- No fallbacks: if the shared seam cannot transcribe/clean, fail explicitly; do not
  silently drop back to a per-client path.
- Remote access stays HTTPS-only via Tailscale; no new plain-HTTP surface.

---

## 9. Open Questions for Soren

1. Decision 1: separate durable `VoiceMode` flag (Option 1, recommended) vs redefine
   `ViewMode` (Option 2)?
2. Decision 2: while voice mode is on, does the desktop prompt box enter a distinct
   voice state, or just a tile badge with the terminal left fully usable?
3. Is desktop spoken OUTPUT in scope (currently input-only), or do we keep desktop as
   dictation-in only while consolidating its backend?
4. Priority order: ship Workstream B first (recommended), or pull the dictionary fix
   (A2) forward because mobile transcription quality is hurting now?
