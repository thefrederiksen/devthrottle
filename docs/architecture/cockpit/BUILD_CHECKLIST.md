# Final Director Build - Checklist for #3-#6

**Status:** ACTIVE - implement ALL of these into the Director, then cut the build ONCE and launch.
**Date:** 2026-05-31
**Why:** rebuilding a Director kills its sessions. Everything here must be in before we launch the new main Director. See [HANDOVER.md](HANDOVER.md).

Verified-as-done already: **#1** registration fix, **#2** queue REST. The items below are NOT yet implemented (checked in code).

For #6, **mirror exactly what the desktop UI already does** - source the action set from `src/CcDirector.Avalonia` so the Cockpit reaches parity.

---

## #3 - Terminal input channel  (the interactive terminal)

- **File:** `src/CcDirector.ControlApi/TerminalStreamEndpoint.cs`
- **Now:** the WS `ReceiveAsync` loop only watches for `Close`; inbound frames are discarded.
- **Do:** on a **Binary** frame, write the bytes straight to the PTY via `session.SendInput(bytes)` (the primitive already exists). Keep applying output as today.
- **Cockpit pair (Track B):** `cockpit-terminal.js` set `disableStdin: false`; xterm `onData` -> `ws.send(bytes)`.
- **Done:** typing in the Cockpit terminal reaches Claude - text, Enter, **arrows**, Ctrl+C, Esc, double-Esc, the slash-command UI.

## #4 - Queue auto-drain on ready

- **Where:** Director/Core - `Session.PromptQueue` + the activity-state transition.
- **Do:** when a session becomes **Idle / WaitingForInput** and the queue is non-empty, automatically send the **next** item (FIFO, one at a time). Do **not** drain while `Working` or `OnHold`.
- **Endpoints:** unchanged (GET/POST/DELETE + `/send` already exist) - this is the auto-fire logic behind them.
- **Done:** enqueued items fire by themselves, in order, the moment Claude is ready - not a manual holding list.

## #5 - PTY resize

- **Add:** `POST /sessions/{sid}/resize` body `{ cols, rows }` -> resize the session's PTY backend.
- **Caution (hard):** resizing the PTY is the **wingman repaint-loop invariant** - a resize is an indirect write that can self-inject through the monitoring loop. Guard/debounce so a Cockpit resize does **not** trigger the wingman loop or a repaint storm.
- **Multi-viewer:** desktop + Cockpit may both size the same PTY; last-writer wins is acceptable (one user). Note it; don't over-engineer.
- **Done:** the Cockpit terminal fills the window width at any size; no repaint loop.

## #6 - Full REST coverage of the desktop surface  ("endpoints for everything")

Mirror the desktop. Confirm the exact action set against the cited Avalonia code before implementing.

- **6a. Source-control write actions** - `/sessions/{sid}/git` is read-only today.
  - Add the writes the desktop **Source Control** tab does (`SourceControlTabButton` / its view): e.g. `POST /sessions/{sid}/git/stage`, `/unstage`, `/discard`, `/commit {message}` (and push/pull **only if** the desktop does them).
- **6b. Workspaces / history** - mirror the desktop Workspaces dialog + history.
  - `GET /workspaces`, `GET /history` (read), plus any open/reopen action the desktop offers.
- **6c. Scheduler** - mirror the desktop **Scheduler** view (`BtnScheduler`).
  - `GET /scheduler` (registered runners + leader/lease status), `POST /scheduler/{name}/run` (run on demand).
- **6d. Session relink** - mirror the desktop **Relink** button (`BtnRelink`).
  - `POST /sessions/{sid}/relink` (re-attach the session to its Claude session).

**Done for #6:** every action a user can take in the desktop UI has a REST endpoint - nothing left that would force a later Director rebuild.

---

## Gate (confirm before cutting the build)

- [ ] #3 terminal input forwards to the PTY; typing works incl. arrows/Esc/Ctrl+C
- [ ] #4 queue auto-drains on Idle, FIFO, not while Working/OnHold
- [ ] #5 `/resize` exists and does not trigger the wingman repaint loop
- [ ] #6a git writes, #6b workspaces/history, #6c scheduler, #6d relink - all present and matched to the desktop
- [ ] Builds clean (0 warnings); existing tests pass
- [ ] Then: cut the build, launch the new main Director, verify E2E from the Cockpit
