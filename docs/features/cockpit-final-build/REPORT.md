# Final Director Build (#3-#6) - Implementation Report

**Date:** 2026-05-31
**Status:** IMPLEMENTED + TESTED (not committed)
**Scope:** the four remaining Director-side changes from [the build checklist](../../architecture/cockpit/BUILD_CHECKLIST.md), so the Director exposes the whole desktop surface and never needs rebuilding once launched.

## Summary

| | Result |
|---|---|
| Features implemented | #3 interactive terminal, #4 queue auto-drain, #5 PTY resize, #6 full REST surface |
| Build | ControlApi + Core + Avalonia: **0 warnings, 0 errors** |
| New tests | **18 / 18 passing** (9 Core behavior, 9 endpoint wiring) |

---

## #3 - Interactive terminal

The terminal already accepts input today: the Cockpit wires xterm `onData` -> `POST /sessions/{sid}/prompt {appendEnter:false}` -> `Session.SendInput`, so typing (incl. Esc/Ctrl+C/arrows/slash-UI) works with no Director change.

This change makes `/sessions/{sid}/stream` **bidirectional** as well - it now forwards inbound WS frames to `Session.SendInput`. It is a **kept, ready capability**: the per-keystroke REST path is chatty/laggy for fast + arrow typing, and with the bidirectional stream baked into the final build the Cockpit can later switch to direct `ws.send` for snappy input **without a Director rebuild**.

- **Changed:** `TerminalStreamEndpoint.cs` (`ForwardClientInputAsync` replaces the drain-only loop).
- **Test:** `SendInput_forwards_raw_bytes_to_the_backend` - an Up-arrow escape sequence reaches the backend verbatim (the path both routes use).

## #4 - Queue auto-drain on ready

The queue now means "auto-send when Claude is ready". When a session returns to **Idle** (and isn't **OnHold**), the next queued prompt is sent automatically, FIFO, one per Idle transition. It never drains on **WaitingForInput** (a queued prompt must not answer Claude's own question).

- **Changed:** `Session.cs` (`TryDrainQueue`, hooked into `SetActivityState`; send is scheduled off-stack to avoid re-entrancy).
- **Tests:** `Queue_auto_drains_one_item_when_session_goes_idle`, `Queue_does_not_drain_when_on_hold`, `Queue_does_not_drain_on_waiting_for_input`.

## #5 - PTY resize

New `POST /sessions/{sid}/resize {cols,rows}` so the Cockpit terminal can use the full window width. `Session.Resize` now **no-ops on an unchanged size** - the guard against a chatty client feeding a repaint storm (the Wingman repaint-loop invariant).

- **Changed:** `ControlEndpoints.cs` (new route), `Session.cs` (unchanged-size guard), `ResizeRequest` DTO.
- **Tests:** `Resize_changed_size_calls_backend_unchanged_is_noop`, `Resize_rejects_nonpositive_dimensions`, `Resize_404_for_unknown_session`.

## #6 - Full REST coverage of the desktop surface

Every remaining desktop action now has an endpoint, so no later phase forces a rebuild.

- **Git writes** (`/sessions/{sid}/git/stage|unstage|discard|commit`) via a new `GitWriteService` that shells git in the repo. Reads stay on `GET /git`.
- **Workspaces / history** (`GET /workspaces`, `GET /workspaces/{slug}`, `GET /history`) wrapping the desktop's `WorkspaceStore` / `SessionHistoryStore`.
- **Scheduler** (`GET /scheduler`, `POST /scheduler/{name}/run`) wrapping `SchedulerService` (resolved lazily; returns 503 when a Director has no scheduler).
- **Relink** (`POST /sessions/{sid}/relink`) wrapping `SessionManager.RelinkClaudeSession`.
- **Changed/added:** `GitWriteService.cs`, `WorkspacesEndpoint.cs`, `SchedulerEndpoint.cs`, git+relink routes in `ControlEndpoints.cs`, scheduler accessor plumbed through `ControlApiHost` + `App.axaml.cs`.
- **Tests:** real-repo `GitWriteServiceTests` (stage/commit/discard/rejections) + endpoint wiring (`Git_stage_404...`, `Scheduler_get/run_returns_503...`, `Workspaces_list_returns_200`, `History_list_returns_200`, `Relink_...`).

---

## Test evidence

### Core behavior (9 passed)

```
Passed SessionInteractiveTests.SendInput_forwards_raw_bytes_to_the_backend
Passed SessionInteractiveTests.Resize_changed_size_calls_backend_unchanged_is_noop
Passed SessionInteractiveTests.Queue_auto_drains_one_item_when_session_goes_idle
Passed SessionInteractiveTests.Queue_does_not_drain_when_on_hold
Passed SessionInteractiveTests.Queue_does_not_drain_on_waiting_for_input
Passed GitWriteServiceTests.Stage_then_commit_creates_a_commit
Passed GitWriteServiceTests.Discard_reverts_an_unstaged_change_to_a_tracked_file
Passed GitWriteServiceTests.Commit_with_empty_message_is_rejected
Passed GitWriteServiceTests.Discard_with_no_paths_is_rejected
```

### Endpoint wiring (9 passed)

```
Passed DirectorSurfaceEndpointTests.Resize_rejects_nonpositive_dimensions
Passed DirectorSurfaceEndpointTests.Resize_404_for_unknown_session
Passed DirectorSurfaceEndpointTests.Relink_rejects_empty_claude_session_id
Passed DirectorSurfaceEndpointTests.Relink_404_for_unknown_session
Passed DirectorSurfaceEndpointTests.Git_stage_404_for_unknown_session
Passed DirectorSurfaceEndpointTests.Scheduler_get_returns_503_when_absent
Passed DirectorSurfaceEndpointTests.Scheduler_run_returns_503_when_absent
Passed DirectorSurfaceEndpointTests.Workspaces_list_returns_200
Passed DirectorSurfaceEndpointTests.History_list_returns_200
```

---

## What's left before launch

- This is **code complete + unit/endpoint tested**, not yet exercised against a live Director (the full interactive terminal needs the Cockpit `disableStdin:false` pair + a running session).
- Cut the final Director build, launch it, and run the live E2E from the Cockpit (type into the terminal, queue auto-fires, resize fills width, git/workspaces/scheduler/relink respond).
- Then roll the same build to the Mac-mini + Windows-2 and update the handover.
