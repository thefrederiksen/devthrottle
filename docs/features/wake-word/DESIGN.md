# Wake-Word Test Dialog - Design

Status: building (2026-05-31). Entry point: CC Director Settings dialog.

## Goal and non-goals

**Goal:** a desktop sandbox to prove and tune the "wingman / wingman send /
wingman cancel" hands-free grammar in isolation, with every stage of the
pipeline visible.

**Non-goals (deliberately):**

- It does NOT send anything to a live session. It emits the assembled prompt
  into an on-screen textbox only. (Per the project rule: testing wake words
  against real sessions risks injecting garbage into one of the user's live
  Directors.)
- No on-device wake engine. No production always-on listening. Those come AFTER
  the grammar is trusted here.

## Why a test screen first

1. The hard part is detection, and it needs tuning in isolation: false wakes,
   missed wakes, and segmentation (does Whisper reliably hear "wingman send" vs
   "wingman, send it"? where does the captured prompt end?). That can't be tuned
   buried inside session routing.
2. Safety. The emit target is a textbox, not a session - the safety boundary is
   made visible.
3. A truthful debug surface: mic level, rolling transcript, what each chunk was
   classified as, current state, the assembled prompt. No fallbacks - every
   ignored control phrase is shown with its reason, never silently swallowed.
4. It decouples a reusable component. Once `WakeWordEngine` is proven, the same
   component drops into the phone Talk page and the desktop session view - they
   just swap "emit to textbox" for "send to session".

## Architecture - two pieces

### (a) `WakeWordEngine` (CcDirector.Core/Voice/WakeWordEngine.cs)

Pure, testable. No microphone, no Avalonia. Consumes transcript text, emits
state events. Knows nothing about audio or UI, so it is unit-tested with
scripted strings and reused later unchanged.

```
Feed(cumulativeTranscript)  ->  emits:  WakeDetected
                                        BodyUpdated(currentPrompt)
                                        Committed(finalPrompt)
                                        Cancelled
                                        ControlIgnored(reason)
Flush()  // settle the held trailing token on a speech pause / stop
Reset()     // back to Idle for a new listen session
```

**Input contract.** `OnPartial` from `OpenAiRealtimeProvider` delivers the
CUMULATIVE running transcript (it appends deltas to one StringBuilder and never
resets mid-session). So `Feed` receives a monotonically growing string. The
engine computes the delta against the last snapshot; if a snapshot is ever
non-monotonic (defensive), it re-baselines instead of losing the body.

### (b) `WakeWordTestDialog` (CcDirector.Avalonia/Voice/WakeWordTestDialog.axaml)

The harness. Reuses `SpeakService` exactly as `SpeakDialog` does (continuous
mic, equalizer, RMS, `OnPartial`), pipes `OnPartial` into the engine, and
renders the engine's events into debug panels. The only difference from
`SpeakDialog`: it never stops listening on its own, and `Committed` writes to a
textbox instead of returning a result.

## State machine

```
        +----------------------------------------+
        |                                         |
   [IDLE] --"wingman"--> [CAPTURING] --"wingman send"--> emit prompt --+
        ^                    |  ^                                       |
        |                    |  |  (more speech) body += words          |
        |                    |  +---------------------------------------+
        |                    |
        +--"wingman cancel"--+   (discard body)
```

- **IDLE** - mic live, transcript scanned, but only a leading `wingman` matters.
  All other speech is ignored (shown greyed in the detector log so you can see
  it was heard but not captured).
- **CAPTURING** - everything after the wake word accumulates into the prompt
  body, live-previewed.
- **"wingman send"** - terminal token. Body (text between the opening `wingman`
  and the closing `wingman send`) is the prompt. Emit it, reset to IDLE.
- **"wingman cancel"** - terminal token. Discard body, reset to IDLE.

## Detection algorithm (this is what is under test)

Because the control words live inside the same continuous transcript ("wingman
fix the login bug wingman send" in one breath), the engine treats the three
phrases as delimiters in a rolling, normalized buffer:

1. **Normalize** each token: lowercase, strip surrounding punctuation. Whitespace
   splits tokens. ("Wingman," / "wing man" / "Wingman send." mostly absorbed.)
2. **Hold the trailing token.** The engine never acts on the final settled token
   unless `Flush()` is called, because "wingman" alone could become "wingman
   send" when the next delta arrives. This is the streaming-disambiguation rule.
3. **Scan settled tokens** left to right:
   - Idle + `wingman` followed by a non-verb -> WakeDetected, body starts after.
   - Idle + `wingman send`/`wingman cancel` -> ControlIgnored (nothing to send).
   - Capturing + `wingman send` -> Committed(body so far).
   - Capturing + `wingman cancel` -> Cancelled.
   - Capturing + any other word -> appended to the body.
4. **Consume** processed leading tokens so the buffer does not grow unbounded;
   retain the body region while capturing and the held trailing token always.

### Pauses and `Flush()`

The held-trailing-token rule means a phrase ending in "...wingman send" will not
commit until either another token arrives OR `Flush()` is called. The dialog
runs a debounce timer: after ~800 ms with no new partial (the natural pause
after you say "wingman send"), it calls `Flush()`, which settles the held
token and lets the commit fire. A lone trailing "wingman" at finalize is treated
as a bare wake (Idle) or as body text (Capturing).

### Edge cases the harness exists to measure (never silently swallowed)

- `wingman send` / `wingman cancel` while IDLE (no body) -> ControlIgnored.
- "wingman" occurring legitimately inside dictation ("tell the wingman to wait")
  -> shown in the log so you can decide if the wake word needs to be rarer.
- Mistranscriptions ("we got men") -> visible in raw vs normalized panels;
  informs whether fuzzy matching is worth adding later.
- Stuttered commit ("wingman, uh, send") -> reveals whether tokens still match.

Fuzzy/approximate wake matching is intentionally NOT in v1: the engine is
deterministic so it is unit-testable, and the harness is what tells us whether
fuzzy matching is actually needed.

## UI layout (dark theme, consistent with SpeakDialog)

```
+--------------------------------------------------------------+
|  Wake-Word Test                              [ IDLE ]        |
|  Wake word: [ wingman          ]  [ Start ] [ Stop ] [Clear] |
|  |||  ||||| ||  |   (equalizer bars + level hint, reused)     |
+--------------------------------------------------------------+
|  RAW TRANSCRIPT (rolling)        | DETECTOR LOG (timestamped) |
+--------------------------------------------------------------+
|  CAPTURED PROMPT (live body)     | EMITTED OUTPUT (on commit) |
+--------------------------------------------------------------+
|  [ Inject test text ____________ ] [Feed]                    |
+--------------------------------------------------------------+
```

- State badge: grey IDLE / red CAPTURING (reuses SpeakDialog color constants).
- Raw vs detector-log side by side: what was heard vs what was classified.
- Captured prompt updates live; emitted output is the textbox standing in for
  "the session" (the safety boundary).
- Inject-text + Feed: drive the engine with typed transcripts, no mic needed -
  fast iteration and exact reproduction of a misfire.
- Configurable wake word so "wingman" vs "hey wingman" can be A/B-ed without a
  rebuild.

## Entry point

A "Wake-Word Test" button on the CC Director Settings dialog. It resolves
`AgentOptions` from the running App (same path the Wingman Speak button uses) so
the dialog can construct a `SpeakService`. If no OpenAI key is configured, the
button explains that instead of failing silently.

## Verification

- **Unit tests** (`WakeWordEngineTests`) - scripted transcript sequences cover
  every transition and every edge case above. Pure strings, no audio,
  deterministic. This is where correctness is proven.
- **Live dialog** - real-mic confirmation of the grammar and the re-trigger /
  reset behavior that only shows up with the actual streaming provider.

## The one trade-off

Reusing `SpeakService` means the harness holds an always-open realtime
transcription connection while listening (continuous OpenAI usage). Correct for
a test screen and the fastest path. If always-on listening later ships in the
product, that continuous cost is the reason to add a cheap LOCAL wake-word
detector in front of the expensive realtime stream - a future decision, not part
of this harness.
