# Three Tabs on Desktop + Phone: Terminal / Wingman / Voice

Status: working spec (agreed 2026-05-26)

## Goal

Bring the same three tabs to BOTH the desktop (Avalonia) and the phone (MAUI)
client, each operating on a single selected session:

  TERMINAL  ---------->  WINGMAN  ---------->  VOICE
  raw machine output     clean readable        spoken
  (real / mirrored TUI)  (wingman-interpreted) (eyes-free)

| Tab      | What it is                     | Input                                   | Output                                            |
|----------|--------------------------------|-----------------------------------------|---------------------------------------------------|
| Terminal | The raw session                | desktop: full keyboard; phone: buttons  | live raw terminal (desktop real / phone mirror)   |
| Wingman  | Clean, wingman-interpreted out | type or Speak -> agent                  | text                                              |
| Voice    | Car/desk, eyes-free            | Ask Agent (bigger) / Ask Wingman PTT    | spoken                                            |

Decisions locked with the user:
- Same three tabs on both apps. Desktop gains Voice + Wingman tabs (Terminal exists).
- Voice tab: two push-to-talk buttons. Ask Agent is a bit bigger than Ask Wingman.
  Ask Agent -> the working agent (does work). Ask Wingman -> read-only observer.
- Wingman tab is a clean output terminal with help from the wingman: the readable,
  de-noised version of session activity plus plain-language wingman annotations. You
  can type OR Speak to the agent from here; replies render as TEXT (no TTS).
- Voice is wanted on the desktop too (easier to test; voice mode usable on the PC).

## Guiding principle

The Director (desktop) owns the session and all backend logic in CcDirector.Core +
Control API. Both UIs are thin: desktop calls Core in-process, the phone calls the
SAME endpoints over HTTPS/Tailscale. We add UI, not new backends, except one cleanup
port and possibly one small key-input endpoint.

## Desktop target layout

```
+----------+--------------------------------------------------+----------+
| SESSIONS | [ Terminal ] [ Wingman ] [ Voice ] [ SrcControl ]| RIGHT    |
|          |--------------------------------------------------| PANEL    |
|  pi    * |                                                  | (screens,|
|  api     |     < selected tab content for THIS session >    |  queue,  |
|  ...     |                                                  |  director|
+----------+--------------------------------------------------+----------+
```

Terminal + Source Control already live in the left tab bar; add Wingman and Voice
beside them. The existing right-panel Wingman state widget folds into the new tab.

## Phone target layout

```
  pick a session (roster)  ->  three bottom tabs operate on it:

   +-----------------------------+
   |        < tab content >      |
   +-----------------------------+
   | [Terminal] [Wingman] [Voice]|   <- bottom TabBar
   +-----------------------------+
```

The existing offline recorder screen stays as its own thing (out of scope here).

## Tab mockups

Voice (both apps):
```
   +-----------------------------------+
   | Voice  -  pi            (green)   |
   |        status: Ready              |
   |   +---------------------------+   |
   |   |        ASK AGENT          |   |  <- bigger (primary)
   |   |      (hold to talk)       |   |
   |   +---------------------------+   |
   |   +-----------------------+       |
   |   |     Ask Wingman       |       |  <- a bit smaller
   |   +-----------------------+       |
   |   You said:  "add a test"         |
   |   Reply:     "done, added..."     |
   +-----------------------------------+
```

Wingman (both apps):
```
   +-------------------------------------------+
   | Wingman  -  pi                            |
   |-------------------------------------------|
   |  Agent edited main.cs and added a test.   |
   |  Tests: 12 passed.                        |
   |  Wingman: now waiting for you to confirm  |
   |  the migration step.                      |
   |  > you: run the migration                 |
   |  Agent: running... done.                  |
   |-------------------------------------------|
   | [ type a message... ]   [ Speak ] [ Send ]|
   +-------------------------------------------+
     Clean text, no raw terminal noise.
     Wingman annotates. Speak/type -> agent. Replies = text.
```

Terminal (phone = read-only mirror; desktop unchanged):
```
   +-----------------------------------+
   | Terminal  -  pi          (live)   |
   |-----------------------------------|
   | > Claude is editing main.cs ...   |
   | > Running tests... 12 passed      |
   | (waiting for your input)          |
   |-----------------------------------|
   | [ type / send ........ ] [ Send ] |
   | [Enter] [Esc] [Stop] [^][v][<][>] |
   +-----------------------------------+
```

## Workstreams

### B. Backend (Core / Control API) - small, shared
- B1. Cleanup port. DONE (2026-05-26). The voice-talk path (VoiceService) now routes
  the transcript verbatim (WingmanService no longer rewords or strips fillers) and
  then runs it through the SHARED CleanupOrchestrator with the live dictation
  dictionary - the same engine desktop dictation uses. So the Voice tab stops
  rewording and finally honors the dictionary. Routing (agent/wingman + wake-phrase
  strip) preserved. Core builds clean; 117 Wingman+Voice unit tests pass.
  NOTE: there are three cleanup paths - A dictation (/dictate), B recording (/ingest),
  C voice talk (/voice/utterance). This fixed C. Path B has a SEPARATE pre-existing
  bug (frozen dictionary at Gateway startup) tracked in
  docs/problems/voice-dictionary-not-applied-on-mobile.md - not addressed here.
- B2. Endpoint audit. DONE. All needs exist: /buffer?since= (terminal),
  /prompt /interrupt /escape (controls), /turns /summary /wingman/explain (clean
  output), /voice/utterance /chat /wingman/ask /tts (voice).
- B3. Raw-key endpoint. NOT NEEDED. POST /sessions/{sid}/prompt with AppendEnter=false
  already writes raw bytes to the PTY, so arrows (ESC[A/B/C/D), Tab, and Enter all go
  through existing endpoints; Esc=/escape, Ctrl+C=/interrupt. No server change for the
  phone Terminal tab.

### D. Desktop (Avalonia)
- D1. Tab shell. DONE (2026-05-26): Voice + Wingman tabs added to the left tab bar,
  wired through SwitchLeftTab.
- D2. Voice tab. IN PROGRESS.
  - Slice 1 DONE (2026-05-26): new Controls/VoiceView (Ask Agent bigger + Ask Wingman,
    status + transcript/reply). Push-to-talk reuses the in-process SpeakService
    dictation engine. Ask Agent sends the transcript to the active session via the
    existing SendPrompt path. Avalonia builds clean (warnings-as-errors).
  - Slice 2 DONE (2026-05-26): new Voice/DesktopTtsPlayer (reuses Core TtsService for
    OpenAI mp3, plays via NAudio; one player, a new reply cuts off the old). Ask
    Wingman now answers in-process via WingmanService.AnswerViaSessionAsync over the
    session's full cleaned terminal (AnsiCleaner) and speaks the answer. TTS stops when
    leaving the Voice tab. Avalonia builds clean.
  - Slice 3 DONE (2026-05-26): Ask Agent now follows the turn (watches the session's
    ActivityState - waits for Working to start, then to end) and speaks the reply via
    the shared ChatService poll path (DisplayText + ear-friendly Summary), reusing the
    same spoken-summary the phone uses. No double-send (native SendPrompt still does
    the send; we only follow + speak). Avalonia builds clean.
  - D2 (desktop Voice tab) FUNCTIONALLY COMPLETE: both Ask Agent (spoken reply) and
    Ask Wingman (spoken answer) work. Minor polish still open: update VoiceView session
    name on session switch (currently updates on tab switch); cancel an in-flight
    follow when the user leaves the tab or starts a new turn.
- D3. Wingman tab. IN PROGRESS.
  - Slice 1 DONE (2026-05-26): added a Wingman tab hosting the existing CleanView
    (moved out of the retired legacy Agent panel - referenced by name, so the
    Attach/Detach lifecycle is unchanged) plus a [Speak] [Send] bar. Speak reuses the
    in-process SpeakDialog; Send routes through the normal SendPrompt path so the
    prompt + reply render in the CleanView the tab hosts (replies are text). Avalonia
    builds clean.
  - Slice 2 DONE (2026-05-26): the "help from the wingman" layer - a wingman annotation
    banner atop the clean output giving a plain-language read (what just happened / what
    it is waiting on) via the in-process AnswerViaSessionAsync. Auto-refreshes when a
    turn ends while the tab is open and when the tab is opened on a new session (skips
    if the note already matches, so toggling tabs doesn't fire repeated calls); manual
    Refresh too. Latest call wins. Avalonia builds clean.
  - D3 (desktop Wingman tab) FUNCTIONALLY COMPLETE: clean output + Speak/Send to agent
    + wingman annotation layer.

### P. Phone (MAUI)
- P1. 3-tab shell. After a session is picked, host Terminal/Wingman/Voice in a bottom
  TabBar, all bound to that session.
- P2. Voice tab. Refactor TalkPage's single-session panel into the Voice tab: Ask
  Agent (bigger) + Ask Wingman, spoken replies. Mostly exists.
- P3. Wingman tab. New clean-output view: poll /turns + /wingman/explain, render
  readable text, [Speak]/[Send] -> agent, replies as TEXT (no TTS). Reuses
  DirectorVoiceClient.
- P4. Terminal tab. Read-only mirror: poll /buffer?since=, render monospace +
  scrollback, control buttons (Send/Enter/Esc/Stop/arrows) -> /prompt, /escape,
  /interrupt, /keys (B3).

## Phasing (desktop-first; testable on the PC)
- Phase 0 - DONE (2026-05-26). B1 cleanup port shipped; B2 audit confirmed; B3 not needed.
- Phase 1 - DONE (2026-05-26). Desktop tab shell + Voice tab: Ask Agent (spoken reply)
  + Ask Wingman (spoken answer) both working in-process. Voice mode usable on the PC.
  (Wingman TAB itself is Phase 2; this is the Voice tab.)
- Phase 2 - DONE (2026-05-26). Desktop Wingman tab: clean output + Speak/Send to agent
  + wingman annotation banner. The desktop now has all three tabs (Terminal existing,
  Voice, Wingman).
- Phases 3-5 - DONE (2026-05-26). Phone 3-tab shell + Voice + Wingman + Terminal tabs
  all implemented inside TalkPage as a segmented switcher (Voice | Wingman | Terminal)
  on the selected session. MAUI Android builds clean (0 errors). On-device E2E is
  still the user's step (cannot run MAUI on a device from here).
  - P1/P2 Voice: existing talk panel; big "Ask Agent" + smaller "Ask Wingman", spoken.
  - P3 Wingman: wingman note (ExplainAsync) + clean conversation text (GetTurnsTextAsync)
    + [Speak][Send] to agent, text replies (SendChatAsync/PollChatAsync, no TTS).
  - P4 Terminal: read-only mirror polling GET /buffer?raw=true&since= (~1s while
    visible, bounded scrollback), control buttons Send/Enter/Esc/Stop/arrows via
    /prompt + /escape + /interrupt. New DirectorVoiceClient methods: GetBufferAsync,
    SendKeysAsync, SendEscapeAsync, SendInterruptAsync, GetTurnsTextAsync.
- Phase 6 - Polish: live status light, keep-awake, car ergonomics, on-device pass.

## Risks / invariants
- Raw keys (B3) is the only possible new backend endpoint.
- Terminal sacred rule: the desktop Terminal tab stays untouched (real ConPTY, no
  interception). The phone mirror is read-only by construction (respects the
  wingman-read-only invariant).
- Wingman annotation cadence: render clean turns continuously; generate the wingman's
  plain-language note per-turn lazily, so it does not spam model calls.
