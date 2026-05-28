# Wingman Auto-Explain Toggle + Yellow "translating" state

Source: voice transcript 2026-05-28 06:17 (`40c327af0a1c472693c6aab5fbe3e668`).

## The ask, in one paragraph

When a session finishes its turn and is about to flip Red ("needs you"), if Wingman
is enabled for the session, automatically run the Wingman "explain this session"
briefing. While that briefing is being generated, paint the dot **Yellow** so the
user can tell "the agent is done, but Wingman is still talking." Once the briefing
is ready (or skipped), flip to **Red**. Wingman is **on by default** per session.
A session with Wingman off shows only the Terminal tab and behaves exactly like
today (Blue -> Red, no Yellow detour).

## What's already there (so we know how small this is)

| Concern | Where it lives today | Reused |
|---|---|---|
| Cognitive state machine | `ActivityState` (Starting/Idle/Working/WaitingForInput/WaitingForPerm/Exited) | yes |
| Single-writer color | `SessionStatusWingman.ColorFromActivityState` -- Blue/Red/Unknown today, no Yellow emitted | extend to Yellow |
| 10s turn-end gate | `TerminalStateDetector.QuietThreshold = 10s` (NOTE: transcript said "5s"; real value is 10s) | yes |
| Auto-explain on turn-end | `ProactiveExplainService` already triggers on `WaitingForInput`, gated on `Session.MobileMode`, runs in background, caches via `Session.SetCachedExplain`, has in-flight guard | yes, re-gate it |
| Three tabs UI | Phone `TalkPage.xaml` has Voice / Wingman / Terminal already; desktop `session-view.html` has Voice + Terminal + others | yes, just hide-when-off |
| `VoiceMode` on the wire | `SessionDto.VoiceMode` (mirrors `Session.ViewMode == Voice`) | similar pattern |

Net effect: this is mostly a **flag, a color rule, and a re-gate** -- not a new engine.

## The new bits

1. **`Session.WingmanEnabled`** (`bool`, default `true`, durable per session).
2. **`SessionDto.WingmanEnabled`** on the wire so phone + desktop agree.
3. **Yellow status during briefing.** Owned by `SessionStatusWingman` (still the
   single writer). Sourced by a new `Session.IsExplaining` flag that
   `ProactiveExplainService` sets on entry and clears on exit (success, failure,
   or timeout).
4. **Re-gate `ProactiveExplainService`** from `session.MobileMode` to
   `session.WingmanEnabled` so it fires for every Wingman-on session regardless of
   how the user is connected.
5. **Tab visibility** keyed off `WingmanEnabled`: Wingman + Voice tabs hidden
   when off (Terminal only).

That's the whole feature surface. Implementation details (file names, test
names) come in a follow-up sub-plan.

---

## State machine

### Before (today)

```
                 +-----------+   bytes on PTY    +----------+
   session  ---> | Starting  | ----------------> | Working  |
                 +-----------+                   +----------+
                       |                              |
                       | (10s of silence)             |
                       v                              v
                 +------------------+   10s silent    |
                 | WaitingForInput  | <----------------
                 +------------------+

   color = { Working/Starting -> Blue, WaitingForInput/Perm/Idle -> Red }
   tabs  = { Voice, Wingman, Terminal (all always present) }
```

### After

```
                              +-----------+   bytes on PTY    +----------+
                session  ---> | Starting  | ----------------> | Working  |     <-- BLUE
                              +-----------+                   +----------+
                                                                    |
                                                                    | 10s silent
                                                                    v
                       WingmanEnabled?  ------- NO ------->  WaitingForInput   <-- RED
                              |
                             YES
                              |
                              v
              +--------------------------------+   <-- YELLOW
              | Explaining                     |   ProactiveExplainService
              | (IsExplaining=true, briefing   |   runs in background;
              |  generation in flight)         |   briefing cached on session
              +--------------------------------+
                              |
                       briefing done OR failed OR cancelled
                              v
                       WaitingForInput                       <-- RED
```

`Explaining` is **not** a new `ActivityState`. ActivityState stays owned by
`TerminalStateDetector` (bytes-only, mechanical). `Explaining` is a parallel
flag on the session; the color rule reads both:

```
color =
  if ActivityState in {Working, Starting}                       -> Blue
  if WingmanEnabled and IsExplaining and ActivityState==WFI/WFP -> Yellow
  if ActivityState in {WaitingForInput, WaitingForPerm, Idle}   -> Red
  if ActivityState == Exited                                    -> Unknown
```

This keeps `SessionStatusWingman` the single writer and keeps Yellow's source
(the in-flight briefing) explicit and falsifiable instead of derived.

---

## Tab layout

### Wingman ON (default)

```
+-------------------------------------------------------+
| [ Voice ] [ Wingman ] [ Terminal ]              [dot] |   <-- 3 tabs
+-------------------------------------------------------+
|                                                       |
|   Voice tab: walkie-talkie; explain/answer flow       |
|   Wingman tab: latest briefing + ask-the-wingman      |
|   Terminal tab: xterm.js view of the live PTY         |
|                                                       |
+-------------------------------------------------------+
```

### Wingman OFF

```
+-------------------------------------------------------+
| [ Terminal ]                                    [dot] |   <-- 1 tab
+-------------------------------------------------------+
|                                                       |
|   Terminal tab: xterm.js view of the live PTY         |
|   (No Voice. No Wingman. No briefings. No yellow.)    |
|                                                       |
+-------------------------------------------------------+
```

Identical layout rules on **Android (`TalkPage.xaml`)** and
**Desktop (`session-view.html`)**. Tabs are hidden, not greyed -- a Wingman-off
session looks like a plain terminal because that is what it is.

---

## End-to-end flow (Wingman ON)

```
  t=0       agent emits last byte of its turn
                          |
                          | (bytes still flowing -> Blue)
                          v
  t=0..10s  PTY is now silent. TerminalStateDetector arms the 10s timer.
                          |
                          v
  t=10s     QuietThreshold crosses. ActivityState: Working -> WaitingForInput.
                          |
                          | SessionStatusWingman sees the transition.
                          | WingmanEnabled? YES.
                          |   set Session.IsExplaining = true
                          |   color: Blue -> YELLOW ("Wingman is reading")
                          |
                          | ProactiveExplainService (already wired to the same
                          | transition) starts the briefing call:
                          |   - 600ms settle delay (existing SettleDelay)
                          |   - WingmanService.AnswerViaSessionAsync(BriefingQuestion)
                          |   - in-flight guard already there, reuse
                          v
  t=10s+    Briefing call returns. Session.SetCachedExplain(briefing).
                          |
                          | ProactiveExplainService clears IsExplaining.
                          | SessionStatusWingman recomputes color:
                          |   IsExplaining=false, ActivityState=WaitingForInput
                          |   -> RED ("needs you")
                          v
  t=...     User opens the Wingman tab; briefing is already cached, no spinner.
            Or the Voice tab speaks it.
```

If the briefing **fails or times out** (60s `ProcessTimeout`), `IsExplaining`
clears the same way and the session goes straight to Red. No half-yellow stuck
states.

If the user types into the Terminal tab while the dot is Yellow, that's a new
byte burst: `TerminalStateDetector` flips ActivityState back to Working, the
briefing is cancelled, color goes back to Blue. (Already free from the
existing in-flight guard semantics; just need to make sure the guard clears.)

## End-to-end flow (Wingman OFF)

```
  t=0       agent emits last byte of its turn
                          |
                          | (Blue while bytes flow)
                          v
  t=10s     QuietThreshold crosses. ActivityState: Working -> WaitingForInput.
                          |
                          | SessionStatusWingman sees the transition.
                          | WingmanEnabled? NO. -> RED immediately. Done.
                          v
            Terminal-only tab; no briefing, no voice, no yellow.
```

Cost model: a Wingman-off session pays **zero** Opus turns. Wingman-on pays
exactly one strong-model briefing per turn-end. The toggle is the bill.

---

## Where it lands per surface

### Server (`src/CcDirector.Core` + `src/CcDirector.ControlApi`)

- `Session.WingmanEnabled` (default `true`, persisted via `SessionStateStore`).
- `Session.IsExplaining` (transient, in-memory only).
- `SessionDto.WingmanEnabled` (Gateway contract).
- `SessionStatusWingman.ColorFromActivityState` -> extend to read `IsExplaining`
  and emit Yellow when applicable.
- `ProactiveExplainService`:
  - swap gate from `session.MobileMode` to `session.WingmanEnabled`
  - set `IsExplaining=true` before the call, clear in `finally`
- New endpoints:
  - `POST /sessions/{id}/wingman/enable`
  - `POST /sessions/{id}/wingman/disable`
  - (or one `PATCH` with `{ "wingmanEnabled": true|false }`)
- `WingmanService` unchanged (`BriefingQuestion` already does exactly what
  the user wants).

### Desktop (`src/CcDirector.Avalonia` + `src/CcDirector.ControlApi/Web/session-view.html`)

- Session tile: the dot already renders `StatusColor` verbatim. Once the server
  emits Yellow, the dot turns Yellow -- no client change needed except adding
  Yellow to the existing color palette.
- HTML session view (`session-view.html`):
  - Hide Voice + Wingman tabs when `wingmanEnabled === false`.
  - Yellow tooltip text: "Wingman is reading the session".
- Session-create flow: a toggle "Wingman" defaulting to ON, written into
  `Session.WingmanEnabled` at create-time.
- Per-session settings page: same toggle, can be flipped at any time.

### Android (`phone/CcDirectorClient`)

- `TalkPage.xaml`: hide the **Voice** and **Wingman** tab buttons (and switch
  the visible inline section to Terminal) when the session has
  `WingmanEnabled=false`.
- The session roster (`SessionInfo`/`RosterParser`) needs the new field on the
  DTO -- one wire-format add.
- FIFO conveyor (`FifoPage`): skip sessions whose `WingmanEnabled=false` --
  they cannot participate in the explain/answer conveyor by definition.
- Status dot widget: add Yellow to the existing palette.

### Gateway (`src/CcDirector.Gateway`)

- Aggregator: pass `WingmanEnabled` through from per-Director responses.
- No new endpoints; the toggle is a Director-local operation reached via the
  existing `/sessions/{id}` proxy.

---

## Timing summary (Android + desktop, both modes)

```
                                       t=0     t=10s      t=10s+briefing      t=...
                                        |       |              |               |
  PTY active (bytes flowing)            #####
  TerminalStateDetector arms idle               .              .
  ActivityState                         WORK    WFI            WFI             WFI
  Wingman ENABLED:
    IsExplaining                        false   true           false           false
    Session.StatusColor                 BLUE    YELLOW         RED             RED
    Briefing cache                      -       -              [briefing]      [briefing]
    Voice/Wingman/Terminal tabs visible Y/Y/Y   Y/Y/Y          Y/Y/Y           Y/Y/Y
  Wingman DISABLED:
    Session.StatusColor                 BLUE    RED            RED             RED
    Voice/Wingman/Terminal tabs visible N/N/Y   N/N/Y          N/N/Y           N/N/Y
```

Idle threshold stays at **10s** (existing `QuietThreshold`). Briefing duration
is whatever the strong-model call takes, hard-capped at the existing
`WingmanService.ProcessTimeout = 60s`. Typical observed briefing latency in
the FIFO experience is roughly 2--8s.

---

## What this plan does NOT do

- Does not add a new `ActivityState`. The byte-only state machine stays as is.
- Does not change `QuietThreshold`. (Transcript said 5s; real value is 10s.
  Tuning is a separate question if Soren wants it.)
- Does not change how the briefing prompt is built or what model it uses.
- Does not introduce per-user overrides or scheduling -- one toggle per session.
- Does not back-port Yellow to past sessions: starts fresh on the next turn.

## Order of work (when approved)

1. `Session.WingmanEnabled` + persistence + `SessionDto.WingmanEnabled`.
2. `Session.IsExplaining` + Yellow path in `SessionStatusWingman`.
3. Re-gate `ProactiveExplainService` from `MobileMode` to `WingmanEnabled`,
   set/clear `IsExplaining`.
4. REST endpoints to toggle + Gateway aggregator pass-through.
5. Desktop tile/view: render Yellow; hide Voice/Wingman tabs when off; add
   toggle to session create + per-session settings.
6. Android `TalkPage.xaml` + FIFO conveyor: same tab hiding + roster field +
   yellow dot.
7. Tests (existing test patterns):
   - `SessionStatusWingmanTests`: Yellow only when WingmanEnabled+IsExplaining.
   - `ProactiveExplainServiceTests`: fires when WingmanEnabled, no-ops when off.
   - Wire-format tests for the new DTO field.

Estimate: a day for the server bits + tests, a day for the two clients. The
heavy lifting (`ProactiveExplainService`, `WingmanService.BriefingQuestion`,
the 3-tab UI shell) is already shipped.
