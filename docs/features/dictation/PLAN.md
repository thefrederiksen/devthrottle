# Dictation Library Plan (locked 2026-05-21)

## Decision summary

Build a dictation library as a C# sub-project of cc-director, embedded in
cc-director.exe and exposed via the existing ASP.NET embedded HTTP server.
The library powers both the desktop UI (in-process call) and HTML pages
(over a local WebSocket). OpenAI is the speech-to-text provider. Claude
Haiku does the post-transcription dictionary and style cleanup. No new
GitHub repo, no separate package, no multi-vendor abstraction.

## Non-negotiable features

1. Walkie-talkie streaming. Partial transcripts arrive while the user is
   still speaking. Target latency around 300 ms to first partial.
2. Updatable dictionary. User-editable YAML, hot-reloaded, applied two
   ways. As a soft prior via the OpenAI prompt parameter, and as a strong
   corrector via the Haiku cleanup pass with the term list in the system
   prompt.
3. Post-process cleanup. A small Claude model runs on the final transcript
   with the dictionary as context to repair mistakes and apply style.
4. Cross-platform consumers. Same library serves Windows desktop today,
   Mac desktop later, and any cc-director HTML pages.
5. Graceful offline. If the connection drops, capture audio to an in-memory
   ring buffer with disk spill for long sessions, then transcribe in batch
   when the connection returns.

## Architecture

- Location: `src/CcDirector.Core/Dictation/` (sub-project, mirrors the
  existing `Voice/` folder).
- Language: C#.
- Consumed by:
  - Desktop UI: direct in-process method calls.
  - HTML pages: new `/dictate` WebSocket endpoint on the existing embedded
    server in `CcDirector.ControlApi`.
- OpenAI API key stays inside cc-director.exe and never reaches the browser.
- Dictionary YAML lives on disk in one place. Both surfaces read the same
  file.
- Audio capture is platform-specific: NAudio on desktop, MediaRecorder in
  the browser. Both produce the same chunk format the library accepts.

```
                cc-director.exe
                ==========================================
                |                                        |
                |  C# code                               |
                |  ----------------------------------    |
                |  Avalonia + WPF UI                     |
                |  Session manager                       |
                |  Voice services (existing)             |
                |  [NEW] Dictation library               |
                |        DictionaryLoader                |
                |        OpenAI Realtime client          |
                |        AudioBuffer (ring + spill)      |
                |        CleanupOrchestrator (Haiku)     |
                |        DictationSession (facade)       |
                |                                        |
                |  ASP.NET embedded HTTP server          |
                |  ----------------------------------    |
                |  Existing endpoints                    |
                |  [NEW] /dictate WebSocket              |
                |                                        |
                ==========================================
                       ^                  ^
              direct method call   WebSocket on localhost
                       |                  |
                  Desktop UI         HTML pages
                                     (vanilla today,
                                      Blazor later if ever)
```

## Component breakdown

- **DictionaryLoader.** Parses the YAML. Watches the file for changes.
  Packs the keyterm list for OpenAI prompts and for the Haiku cleanup
  system prompt.
- **ProviderClient.** Speaks the OpenAI Realtime API over WebSocket.
  Streams audio chunks. Surfaces partial and final transcripts.
- **AudioBuffer.** Ring buffer with disk spill, used during offline
  windows. Drains to ProviderClient on reconnect.
- **CleanupOrchestrator.** Runs the final transcript through Haiku with
  the dictionary in the system prompt. Supports per-profile prompts
  (code mode, email mode, verbatim mode).
- **DictationSession.** High-level facade. Start, Stop, events for partial
  and final transcripts.
- **/dictate WebSocket handler.** Thin wrapper around DictationSession for
  HTML clients. No business logic, just protocol.

## Phased delivery

1. **Phase 0, proof of fact.** No code commitment. Record a 30 second
   clip of representative speech with company terms. Send it through
   OpenAI gpt-4o-transcribe both with and without the prompt parameter.
   Run both transcripts through a Haiku cleanup pass that knows the term
   list. Compare. Only proceed if the dictionary mechanism works
   reliably enough on your real voice and real terms.

2. **Phase 1, core library.** DictionaryLoader, ProviderClient,
   CleanupOrchestrator, DictationSession. Unit tested. No UI, no
   WebSocket endpoint yet. Verified via a small console test harness.

3. **Phase 2, desktop integration.** Wire DictationSession to NAudio
   capture. Add a global hotkey via the existing HookInstaller. Type
   into the focused window via SendInput. Tray app or in-app trigger,
   to be decided.

4. **Phase 3, HTML integration.** Add the `/dictate` WebSocket endpoint.
   Build a minimal client that captures audio with MediaRecorder, streams
   up, displays transcripts in a textbox. Drop it into one existing HTML
   page as the first consumer.

5. **Phase 4, offline buffering.** Add the ring buffer, the disk spill,
   the reconnect flush.

6. **Phase 5, Mac.** Port the platform-specific shell layer (mic,
   hotkey, output). Library code unchanged.

## Open items (still to decide)

- Hotkey choice for desktop (Right Alt, F13, Caps Lock, or other).
- Whether Phase 1 ships a standalone CLI harness or stays in-process for
  desktop only.
- Whether the Haiku cleanup runs on every utterance or only on longer
  ones, to keep costs down.
- The exact dictionary YAML schema (vocabulary, substitutions, profiles).

## Explicitly out of scope

- Multi-vendor speech provider abstraction. Single vendor: OpenAI.
- A standalone NuGet package or separate GitHub repo. Sub-project inside
  cc-director.
- Blazor migration of cc-director's HTML pages. Independent decision,
  no dependency.
- AWS Transcribe and AssemblyAI integrations. Revisit only if OpenAI plus
  Haiku cleanup proves insufficient on the dictionary problem.
- Voice command interpretation. The existing Voice services already
  handle intent parsing in a separate flow.
