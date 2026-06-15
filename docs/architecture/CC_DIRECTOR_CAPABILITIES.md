# CC Director: Core Capabilities

**Status:** CURRENT (definition + grounded inventory, 2026-06-15)
**Audience:** Anyone deciding what cc-director must do as a standalone installed application, what is must-have vs. later, and what the user is allowed to expect when they power it up on a fresh machine.

## Related documents

- [DIRECTOR_DUMB_WRAPPER_TARGET.md](DIRECTOR_DUMB_WRAPPER_TARGET.md) - the boundary rule (Director = hands, Gateway = brain) and the Facts/Events/Verbs contract. THIS doc is the concrete "what must work" companion to that doc's "where does a feature belong."
- [gateway/GATEWAY_DIRECTOR_ARCHITECTURE.md](gateway/GATEWAY_DIRECTOR_ARCHITECTURE.md) - current Director/Gateway split and startup.
- [TARGET_IMPLEMENTATION_PLAN.md](TARGET_IMPLEMENTATION_PLAN.md) - phased execution plan.

---

## 1. What cc-director is

cc-director is the application we install on a machine to run a fleet of agents.

> **Multiple agents, one machine, one interface - with no setup pain.**

You run different CLI agents (Claude Code, and any other CLI agent) side by side in one tool, on your own machine, and you can see at a glance which ones are working and which are waiting for you. Getting there takes one Windows installer that hand-holds you the whole way - you never go set up tmux or wire anything together yourself.

The product is two layers:

- **The local part (this doc's two core features):** easy install, and multi-agent terminal running/handling. This is the winning combo and it must be rock-solid on its own.
- **The smart layer on top (the Gateway + Cockpit):** briefs, Wingman, dictation, voice. These require the Gateway and are deliberately kept *off* the Director so it stays dumb and rarely changes - which is what protects your long-running sessions across updates.

---

## 2. The two core features (the local winning combo)

```
==========================================================================
                  CC DIRECTOR  --  THE LOCAL PART
       "multiple agents, one machine, one interface -- with no setup pain"
==========================================================================

  +-------------------------------+   +-------------------------------+
  |  1. EASY INSTALL              |   |  2. MULTI-AGENT TERMINAL      |
  |     (Windows-first)           |   |     RUNNING & HANDLING        |
  +-------------------------------+   +-------------------------------+
  |  - One installer; a wizard    |   |  - Run MANY sessions at once  |
  |    guides every step          |   |  - Mix & match ANY CLI agents |
  |  - Nothing to set up yourself |   |    in ONE interface           |
  |    (no tmux, no plumbing)     |   |  - WORKING / WAITING status   |
  |  - Detects prerequisites,     |   |    per session (5s rule)      |
  |    hand-holds through them    |   |    <-- the differentiator     |
  |  - Ends by telling you plainly|   |  - Start / stop / restart /   |
  |    "you're ready -- it runs"  |   |    resume running sessions    |
  |  - Safe self-update that      |   |  - Sessions PERSIST: survive  |
  |    NEVER costs you a session  |   |    updates, restarts, crashes |
  +-------------------------------+   |  - Real terminal (paste, etc.)|
                                      |  - Drives each agent type     |
                                      |    (slash-cmd injection),     |
                                      |    weekly cadence             |
                                      +-------------------------------+

  ----------------------------------------------------------------------
  ON TOP (not local core):  Gateway + Cockpit add ALL the smarts
  briefs / wingman / dictation / voice  --  requires Gateway,
  big red corner when not connected.
  ----------------------------------------------------------------------

  footnote: yes, it replaces tmux for agent work -- but that's a
  comparison, not the pitch.
```

### 2.1 Easy install (Windows-first)

The on-ramp is a first-class feature, not an afterthought. **Windows-first.** One installer, and a wizard that hand-holds you to a running fleet.

- One installer; a wizard guides every step.
- Nothing to set up yourself - no tmux, no terminal multiplexer, no manual plumbing.
- Detects prerequisites and walks you through them rather than failing.
- **Ends by telling you plainly that you are ready** - "it will run." (See the readiness promise, section 8.)
- **Safe self-update that never costs you a session** - the app keeps itself current, and updating never kills a running agent session. This is the payoff of keeping the Director dumb. (See section 6.)

### 2.2 Multi-agent terminal running and handling

The engine. Run many agent sessions at once, of different kinds, in one interface, and always know their status.

- **Run MANY sessions at once.**
- **Mix and match ANY CLI agents** in one interface - Claude Code and any other CLI agent, side by side. The custody layer is agent-agnostic.
- **Working / Waiting status per session** - the differentiator nothing else gives you (section 5).
- **Session lifecycle** - start, stop, restart, and resume previously-running sessions.
- **Sessions persist** - they survive Director restarts, updates, and crashes (section 6).
- **A real terminal** - tmux-grade raw terminal, including paste (section 4).
- **Drives each agent type** - builds the launch command and injects the right slash-commands per agent. This is the one piece that changes, on a weekly cadence.

### 2.3 The must-have capability list (offline vs Gateway-required)

The concrete checklist. Items 1-8 ALWAYS work with no Gateway; item 9 requires the Gateway.

**Always works (offline, no Gateway):**

1. **Full terminal** - a real, tmux-grade terminal (keys, paste, scrollback, colors); panes deferred, probably never.
2. **Multi-agent sessions** - run many sessions at once, mixing any CLI agents, in one interface.
3. **Session control** - start, stop, restart, and resume sessions.
4. **Session state** - the two-state Working / Waiting signal (red after 5s silence).
5. **Local screenshots** - view and manage the screenshots on this machine (used constantly).
6. **Prompt queue** - the right-side queue panel.
7. **Copy Director info** - copy the Director's details from the top-right corner (id / name / endpoint).
8. **Copy session info** - copy each session's ID and name, to hand a session off to another session.

**Only with the Gateway connected:**

9. **Transcription / dictation** - off when offline; it needs the central dictionary and the API key, both of which come from the Gateway. (The full Gateway-required super-feature list is in section 7.)

---

## 3. The hard rule: the Gateway is required (the layer on top)

**We built this with the Gateway. We enforce it. There are no fallbacks.**

- The Gateway is a **requirement**, not an option. The main reason is keys: dictation/transcription and the other super-features run through the Gateway so you never set up API keys on every machine. One vault, one brain, every machine fungible.
- When the Gateway is **not** connected, the super-features are **simply unavailable** - not degraded, not faked, not approximated with a local fallback. They are off until the Gateway reconnects.
- The Director **must never block on the Gateway.** A missing or unreachable Gateway never stops the two core features from working.

### No nagging - just a red corner

When the Gateway is not connected, the Director does **not** throw dialogs, warnings, or modal popups in the user's face. It shows **one signal: a big red indicator in the bottom corner.** Connected = not red. Disconnected = big and red. That is the entire user-facing contract for Gateway state.

> CURRENT GAP: today the app surfaces large warnings / a dialog on Gateway disconnect. Replacing that with the silent red-corner indicator is a known fix (small, not yet done).

### What works with NO Gateway

Exactly the two core features, and nothing else:

- Terminal sessions run; multi-session works; the two-state status works.
- Full keystroke I/O, resize, scrollback, copy/paste (once paste is fixed - see section 4).
- Source Control (git) facts and the local diagnostics surface.
- Session persistence and crash recovery.

No briefs. No assessments. No dictation. No scheduled sends. No Wingman actions. No voice. Red corner. That is correct and intended.

---

## 4. Deep dive: the terminal must be a REAL terminal

A real terminal is table stakes under feature 2.2 - "multi-agent terminal running" only means something if each terminal is a genuine terminal. The bar is tmux: anything you can do in a normal terminal, you must be able to do here.

The following is grounded in the shipping Avalonia code (`src/CcDirector.Terminal.Avalonia/`), not aspiration.

### Works today (the floor that holds)

| Capability | Status | Where |
|---|---|---|
| ConPTY backend, byte I/O, resize | OK | `CcDirector.Core/ConPty/PseudoConsole.cs` |
| ANSI/VT parsing (xterm-checked) | OK | `CcDirector.Terminal.Core/AnsiParser.cs` |
| 16 / 256 / 24-bit truecolor | OK | AnsiParser + renderers |
| Wide-char / CJK / emoji / UTF-8 | OK | `CharWidth.cs` |
| Scroll regions, alt-screen, cursor save/restore | OK | AnsiParser |
| Scrollback (1000 lines) + wheel scroll | OK | TerminalControl |
| Text selection + copy (Ctrl+C w/ selection, Ctrl+Shift+C, right-click) | OK | TerminalControl |
| Special keys: arrows, F1-F12, Home/End/PgUp/PgDn, Ins/Del, Tab, Shift+Tab | OK | `MapKeyToBytes` |
| Ctrl+C / Ctrl+D / Ctrl+Z / Ctrl+L | OK | `MapKeyToBytes` |

### MUST-HAVE (locked)

These are non-negotiable for "a real terminal." User-stated, must ship.

- **[MUST] Plain Ctrl+V paste.** Today paste only binds to **Ctrl+Shift+V** and a right-click menu item; plain **Ctrl+V is not mapped** (`TerminalControl.cs` line ~1054 handles `ctrl && shift && Key.V` only). This is the "I can't paste" bug. Standard Ctrl+V must paste.
- **[MUST] Multi-session, always.** Many terminals at once, each isolated, never failing because of the Gateway. (Working today - listed here because it is load-bearing and must stay rock-solid.)
- **[MUST] Terminal parity overall.** The terminal is the floor the whole product stands on; its quality bar is higher than any convenience feature.

### Candidate parity backlog (NOT yet graded must-have vs. later)

These are confirmed gaps vs. a real terminal / tmux. They are listed for grading, one at a time, in a later session. None is committed yet. Tracked in GitHub issue [#449](https://github.com/thefrederiksen/cc-director/issues/449) (parked - not a current priority).

| # | Gap | Impact | Notes |
|---|---|---|---|
| B1 | Bracketed paste (`?2004`) | Multi-line / shell-meta paste can mangle or drop chars | Mode is parsed but ignored; paste is replayed as slow char-by-char keystrokes, not a real paste. Pairs naturally with the Ctrl+V must-fix. |
| B2 | Mouse reporting to the PTY | vim / htop / lazygit / tmux mouse modes do not work | Modes 1000-1006 parsed but "accepted without effect" (AnsiParser). |
| B3 | Alt / Ctrl+Alt key combos | Cannot send ESC-prefixed keys (e.g. Alt+X) | Not mapped in `MapKeyToBytes`. |
| B4 | Scrollback search / find | Cannot search the scrollback | No find facility. |
| B5 | Splits / panes | No tmux-style horizontal/vertical splits | One session per view today. |
| B6 | Configurable cursor shape (DECSCUSR) | Apps cannot signal bar/underline cursor | Fixed blinking block. |
| B7 | Consolidate the two terminal controls | Divergence risk | Avalonia has both `TerminalControl.cs` (has paste) and `TerminalView.cs` (copy only, no paste). Two code paths to keep in sync. |
| B8 | Scrollback persistence / export | Scrollback lost on restart; no save-to-file | In-memory only. |

---

## 5. Deep dive: the two-state activity signal (the differentiator)

The status is *why* feature 2.2 beats running raw terminals or tmux: nothing else tells you, per session, whether the agent is working or waiting for you.

The Director is allowed exactly **one** interpretation, and it is deliberately stupid. It always computes a binary activity state from raw terminal activity, and nothing else:

- **Working** - the moment anything changes in the terminal (any output / event).
- **Waiting for the user (red)** - after **5 seconds** of no terminal events / nothing changing.

Rules (DECIDED 2026-06-15):

- **Always on, never a fallback.** The Director computes and emits these two states all the time - Gateway connected or not. This is not a degraded-mode behavior; it is a permanent Director job.
- **Only two states, ever.** The Director never distinguishes done / stuck / awaiting-permission / needs-you. Those are richer judgments it is not allowed to make.
- **All smarts layer on top.** The Gateway + Cockpit interpret what the red actually means (done vs. stuck vs. needs-you), produce briefs and recommendations, and render the richer state when connected. The Director's two-state signal is the raw truth they build on.

Current code delta to implement this: `TerminalStateDetector` today uses a **10-second** silence threshold - this becomes **5 seconds**; `SessionStatusWingman.ColorFromActivityState` collapses to the two states (working / red-waiting). See the boundary discussion in [DIRECTOR_DUMB_WRAPPER_TARGET.md](DIRECTOR_DUMB_WRAPPER_TARGET.md) section 4.2.

---

## 6. Deep dive: session persistence and safe self-update

This is the co-headline under feature 2.2, and the whole reason the Director is kept dumb: **a running agent session must survive.**

- **Sessions persist across restarts and crashes.** A Director crash does not lose the roster; sessions can be recovered (the interrupted-sessions registry).
- **Resume previously-running sessions** rather than starting from scratch.
- **Safe self-update that never costs you a session.** The Director keeps itself current, but an update must never kill a running agent session. Because all the fast-moving smarts live in the Gateway/Cockpit, the Director changes rarely - and when it does (mainly the weekly agent-driver updates), the update path must preserve in-flight sessions.

> CURRENT GAP / RISK: the dumb-wrapper design is what *enables* this, but the update path must be hardened so a self-update provably preserves running sessions (a past auto-update left a machine unable to recover - the standing requirement is: update health-check + rollback, never a silent half-swap). This is a requirement, not yet a finished guarantee.

### 6.1 Self-healing tool install

The 30+ cc-* CLI tools install as one shared Python venv. A real field failure: an update resets the venv but never refills it, leaving the `bin\*.cmd` wrappers pointing at programs that no longer exist - a half-installed state that *looks* installed but fails on use, with no error surfaced. The Director must heal this on its own instead of waiting for a human to stumble on it. Four stages (tracked in issue #453):

1. **Detect** - a tool whose wrapper exists but whose backing exe does not, or a venv whose tool scripts are missing, is *broken* (distinct from *not installed*). The Director already has this signal: tool-presence resolves the real exe, not the wrapper.
2. **Surface, never skip silently** - the installer/updater must not quietly return when a tool bundle is missing or the venv is unhealthy; it records the broken state in the readiness surface, with the reason (bundle missing vs. install failed).
3. **Reinstall** - re-run the already-idempotent tool install, which rebuilds the venv and pip-installs the tools. *Hardened 2026-06-15:* the installer's "already installed" early-out now verifies the tool scripts are actually on disk, so simply re-running the installer repairs a stripped venv instead of trusting a stale version stamp.
4. **Delegate when it can't** - if the reinstall genuinely cannot succeed (the release lacks the bundle, the machine is offline, etc.), hand the problem to an agent with the exact diagnosis rather than looping.

This is the concrete first slice of the "it stays working without manual repair" promise under feature 2.1. Stage 3's early-out hardening shipped 2026-06-15; the detect / surface / delegate automation is the remaining work in #453.

---

## 7. Gateway-required super-features (the layer on top)

Everything below is **off when the red corner is showing.** No local fallback exists or will be built.

| Super-feature | Needs Gateway for | Off when disconnected |
|---|---|---|
| Dictation / transcription | API keys (the main reason the Gateway is mandatory) | Yes |
| Turn briefs / explain / recap | The Gateway brain | Yes |
| Wingman decisions / options | The Gateway brain | Yes |
| Voice (per-session + fleet) | Brain + event hub | Yes |
| Scheduled sends / comm queue policy | Gateway scheduler + vault | Yes |
| Shared key vault | The Gateway is the vault | Yes |

The Director only ever performs the **mechanical** half of these (capture audio bytes, inject keystrokes, dispatch a comm) on the Gateway's instruction. It never makes the judgment call. See the boundary rule in [DIRECTOR_DUMB_WRAPPER_TARGET.md](DIRECTOR_DUMB_WRAPPER_TARGET.md) section 3.

---

## 8. Readiness: "will it run, and is it connected?"

The promise is that on a fresh machine you know two things at a glance:

1. **Will it run?** - the install wizard ends by telling you plainly you are ready, and after that the two core features are up; you can open a session right now.
2. **Is it connected?** - the bottom-corner indicator. Not red = Gateway connected, super-features available. Big and red = the two core features only.

No third state to read, no dialog to dismiss. Two facts, one glance.

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-06-15 | Claude (with Soren) | Initial version: Gateway-required (no fallback, red-corner-not-dialog) locked; terminal-as-real-terminal with grounded parity checklist; must-have = Ctrl+V paste + multi-session + parity; candidate backlog B1-B8 ungraded. |
| 2026-06-15 | Claude (with Soren) | Restructured around the two core local features: (1) Easy install (Windows-first, wizard-guided, ends with a readiness verdict, safe self-update that never costs a session) and (2) Multi-agent terminal running/handling (many sessions, mix any CLI agents, Working/Waiting status as the differentiator, lifecycle/resume, persistence, real terminal, weekly agent-driver). tmux demoted to a footnote. Added section 6 (persistence + safe self-update). |
| 2026-06-15 | Claude (with Soren) | Added section 2.3, the concrete must-have capability checklist (offline items 1-8: full terminal, multi-agent sessions, session control, session state, local screenshots, prompt queue, copy Director info, copy session info; Gateway-required item 9: transcription/dictation). Captured two previously-missing capabilities: local screenshots and copy Director/session info. |
