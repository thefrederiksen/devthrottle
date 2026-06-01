# Cockpit QA Loop - Iteration 03 (adversarial edge cases + desktop parity)

**App version:** 0.3.5 (build `990dba3` + uncommitted Cockpit fixes)
**Report generated:** 2026-05-31 23:14 local
**Tested against:** Cockpit `http://localhost:7471` -> Gateway `127.0.0.1:7878` -> Directors `c99a103c` (`7884`) and `39aad623` (`7879`, older v0.3.2 build).
**Driver:** cc-playwright `cockpit-qa` (Brave, 1920x951) + Win32 PrintWindow capture of the desktop app.

---

## Edge cases swept (all PASS)

| Edge case | Result | Evidence / note |
|-----------|--------|-----------------|
| Multi-line composer - **Shift+Enter** inserts a newline, does NOT send | PASS | textarea value became `line one\nline two`; OnComposerKey only sends on Enter-without-Shift |
| **Cross-Director** terminal - select a session owned by the *other* Director (39aad623 / 7879) | PASS | `img/iter3/01-crossdirector-readonly.png` - live terminal streamed straight from 7879 (read-only, no interaction with the user's session) |
| **Stale-Director resilience** - 7879 is an older build missing `/queue` | PASS | Queue panel degraded gracefully to "QUEUE (0)"; no error surfaced |
| **Session switching** - swap between sessions/Directors | PASS | `img/iter3/02-back-to-scratch.png` - terminal pane (keyed by SessionId) disposes + reconnects cleanly, no ghost frames; composer resets on select |
| **Gateway-down resilience** (observed in Iter 01) | PASS | rail retained last-good roster, red banner shown, auto-recovered on next poll |

**New Cockpit bugs found this iteration: 0.**

---

## Desktop parity (the user's bar: desktop must be at least at par)

Captured the running desktop app (Avalonia, "CC Director -- Leader") via PrintWindow: `img/iter3/03-desktop-app.png`. It has the equivalent of every Cockpit capability, plus more:

| Capability | Cockpit | Desktop | Verdict |
|-----------|---------|---------|---------|
| Session rail (fleet, status dots) | yes | yes (left rail) | at par |
| Live terminal (type / Ctrl+C / Esc / arrows) | yes (xterm/WSS) | yes (native TerminalControl) | at par |
| Composer Speak / Send / Queue / Interrupt | yes | yes (same button set, bottom dock) | at par |
| Screenshots into the session | upload -> inject path | drag-drop onto prompt box -> path; image also opens in the right-panel viewer (the uploaded `qa-screenshot-test.png` is visible in the desktop capture) | at par |
| Right panel | screenshots + queue | tabbed (Clean / Wingman / document & image viewers) | desktop ahead |
| New / Kill / Rename / Settings | yes (Settings fixed this loop) | yes | at par |
| Awareness / recap / git, Fan-out, Handover | yes | yes (Wingman / Fifo / move-session) | at par |

**Verdict: the desktop is at-or-ahead of the Cockpit on every feature.** The Cockpit is the focused remote driver; the desktop is the full IDE-style app. No desktop gaps versus the Cockpit were found.

**One parity nicety (enhancement, not a defect):** the desktop accepts a drag-drop directly onto the prompt textbox; the Cockpit's drop target is the Screenshots panel. Both deliver the image to Claude. Adding composer-textarea drop to the Cockpit would match the desktop gesture exactly.

---

## Loop status

- **Iter 01:** 2 Cockpit bugs found (Settings endpoint; dictation cancel error).
- **Iter 02:** both fixed + verified live; New/Kill lifecycle verified; 0 new.
- **Iter 03:** edge cases + cross-Director + desktop parity; 0 new.

Two consecutive iterations with **0 new issues**. Iteration 04 does a code-level adversarial review (error/edge paths the happy-path tests don't reach) and revisits the one cosmetic observation (header action-buttons can overflow at narrow widths) before declaring the Cockpit clean.
