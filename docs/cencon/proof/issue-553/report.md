# Issue 553 - Proof: reliable /m voice playback (truthful triangle, mobile autoplay, yellow-until-ready)

Branch: voice/553-reliability

This note records the code changes and the automated-test evidence. On-device confirmation
(tap-to-play audio on a physical phone, gateway-restart durability of the triangle) requires a
manual run and is listed at the end - this agent cannot drive a physical phone.

## Summary of changes

### A. Gesture-safe playback (mobile autoplay) - src/CcDirector.Cockpit/wwwroot/m/m.js
- The list play triangle handler now calls a new startGesturePlayback(sid) SYNCHRONOUSLY inside the
  tap, before any await: it sets audioEl.src to the session voice-audio URL and calls audioEl.play()
  right there. This keeps the tap user-gesture alive, which iOS/Android require for play() to be
  allowed. The element is unlocked and begins as soon as the bytes arrive.
- The in-session Play button path (play()) does the same when the audio is not yet buffered: it
  starts gesture playback first, then drives the buffered-ready UI. An already-buffered tap plays
  (or stops if already speaking) as before.
- waitPlayable no longer resets src + load() when the element already points at the same URL (the
  gesture already started it), so it does not tear down the in-flight, gesture-authorized playback.
  preparePlaybackUrl detects gesture playback already in progress and does not restart it; it only
  flips the button to ready/speaking once buffered.

### B. Do not gate playback behind menu detection - m.js openVoice
- openVoice now starts BOTH fetches in parallel: /wingman/voice (cached voice) and /wingman/menu
  (which can take up to 90 s). It surfaces the cached voice (and begins playback when autoPlay was
  requested) the moment the voice fetch resolves, without waiting on the menu call. A menu, when
  actually present, still wins the on-screen surface afterwards.

### C. Truthful triangle: one readiness signal + durable audio
- The per-session ready audio cache is now PERSISTED to disk: under a voice-audio folder next to
  voice-sessions.json (CcStorage.Root()), each ready session writes <sid>.mp3 (audio) + <sid>.json
  (spoken/reply/AtUtc metadata). On startup LoadReadyAudio restores _ready so HasVoice/ReadySessionIds
  survive a gateway restart.
- A session is loaded ready only when BOTH its metadata and a non-empty audio file are present (the
  "if anything fails, remove the triangle" rule extends to a half-written/missing cache).
- OnSessionWorking (a new turn) now also deletes the durable audio, so a 5s-stale list row cannot
  point at audio that is gone (which 404s on /audio). StoreSpokenAsync writes the durable cache on a
  successful synthesis only; when TtsAsync returns null/empty the session is NOT in _ready, so no
  triangle shows.
- The /audio 404 path and the list 5s reconcile against /wingman/voice/ready (= ReadySessionIds)
  remain authoritative and are unchanged.

### D. Voice color state machine: yellow until audio ready
- SessionDto gains VoiceGenerating and VoiceAudioReady. The Gateway aggregator (GatewayEndpoints)
  stamps them from WingmanVoiceService.IsGenerating / HasVoice (wired in GatewayHost via the new
  voiceAudioReadyFor parameter).
- SessionOrdering.EffectiveColor gains IsVoicePreparing: a voice-mode session whose raw color is red
  and that is WaitingForInput/WaitingForPerm holds YELLOW while VoiceGenerating or not VoiceAudioReady,
  and only goes RED once audio is ready. It never shows red/needs-you in voice mode before audio.
- m.js effColor mirrors the same rule using the new DTO fields.
- SessionStatusWingman.VoiceColorFor (Core) encodes the pure rule, unit-tested once next to the rest
  of the color mapping.

## Files changed

- src/CcDirector.Cockpit/wwwroot/m/m.js  (A, B, D-mirror; version comment v19)
- src/CcDirector.Cockpit/wwwroot/m/index.html  (cache-bust v=21)
- src/CcDirector.Cockpit/wwwroot/m/sw.js  (cache name + shell v=21)
- src/CcDirector.Gateway/Wingman/WingmanVoiceService.cs  (durable audio cache; StoreReady seam)
- src/CcDirector.Gateway.Contracts/SessionDto.cs  (VoiceGenerating, VoiceAudioReady)
- src/CcDirector.Gateway.Contracts/SessionOrdering.cs  (IsVoicePreparing + EffectiveColor fold)
- src/CcDirector.Gateway/Api/GatewayEndpoints.cs  (stamp the two DTO fields; voiceAudioReadyFor param)
- src/CcDirector.Gateway/GatewayHost.cs  (wire voiceAudioReadyFor: HasVoice)
- src/CcDirector.Core/Wingman/SessionStatusWingman.cs  (VoiceColorFor pure rule)
- src/CcDirector.Core.Tests/Wingman/SessionStatusWingmanTests.cs  (5 color-rule tests)
- src/CcDirector.Gateway.Tests/SessionOrderingTests.cs  (6 EffectiveColor voice tests)
- src/CcDirector.Gateway.Tests/WingmanVoiceServiceTests.cs  (4 persistence/readiness tests)

## Automated-test evidence

Build: dotnet build cc-director.sln -p:WarningsNotAsErrors=NU1903 -> Build succeeded, 0 Warnings, 0
Errors. (The NU1903 SQLite NuGet-audit advisory is a local-only restore warning unrelated to this
change; no csproj change was committed for it.)

Core tests (CcDirector.Core.Tests): Passed 2107, Failed 0, Skipped 4. New color-rule cases pass:
- VoiceMode_waiting_and_not_ready_is_yellow
- VoiceMode_waiting_and_generating_is_yellow_even_if_audio_ready
- VoiceMode_waiting_and_audio_ready_is_red
- VoiceMode_while_working_is_unchanged_blue
- NonVoice_session_color_is_unchanged

Gateway tests (CcDirector.Gateway.Tests): Passed 837, Failed 1, Skipped 0. New cases pass:
- SessionOrderingTests: EffectiveColor_VoiceWaiting_NoAudio_IsYellow,
  EffectiveColor_VoiceWaiting_Generating_IsYellow_EvenWithStaleAudio,
  EffectiveColor_VoiceWaiting_AudioReady_IsRed, EffectiveColor_VoiceWorking_StaysBlue,
  EffectiveColor_NonVoiceWaiting_NoAudio_StaysRed, Classify_VoiceWaitingNoAudio_IsActive_NotNeedsYou
- WingmanVoiceServiceTests: StoreSpokenAsync_WithFailingTts_DoesNotMarkReady,
  StoreSpokenAsync_WithEmptySpoken_DoesNotMarkReady, ReadyAudio_PersistsAndReloadsAcrossRestart,
  OnSessionWorking_DeletesDurableAudio

The single Gateway failure is DictationEndpointTests.FullPipeline_transcribes_phase0_clip2_with_realtime_provider
(expected at least 1 partial transcript, got 0). It is a pre-existing, environment-dependent realtime
speech-to-text WebSocket integration test that requires a live transcription provider to emit partial
frames. It is entirely outside the voice-playback / triangle / color surface this issue touches -
none of the changed files are on the dictation path.

## On-device checks a human must do to fully verify

A physical phone is required for the gesture-autoplay and restart-durability acceptance criteria.

1. Tap-to-play from the list (mobile autoplay): on a phone open /m, pick a voice-ready session
   (triangle showing), tap the triangle ONCE. Expected: the session opens AND audio begins playing
   without a second tap.
2. In-session Play button: open a voice-ready session by tapping the row body (no autoplay), tap the
   big Play button once. Expected: it reaches Tap to listen / starts speaking and plays on that
   single tap.
3. Truthful triangle on a TTS failure: remove the OpenAI key from the gateway vault (or force
   synthesis to fail), run a turn on a voice session. Expected: NO triangle on the list and the
   in-session view does not claim it is playable. Restore the key, run a turn: the triangle appears
   and tapping it plays.
4. Yellow-until-ready color: send a turn to a voice-mode session and watch its dot on /m. Expected:
   it stays YELLOW while the wingman is generating / before audio is ready, and only turns RED once
   audio is available to play. It must never go red in voice mode before audio is ready.
5. Gateway-restart durability: with a session that has ready voice (triangle showing), restart the
   gateway. Expected: the triangle does NOT disappear-then-reappear-empty; after restart, tapping the
   triangle plays the previously-ready audio.
