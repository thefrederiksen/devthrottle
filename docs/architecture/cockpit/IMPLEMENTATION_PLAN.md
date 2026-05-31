# The Cockpit - Implementation Plan (all phases)

**Status:** PLANNED
**Date:** 2026-05-31
**Audience:** Anyone implementing the Cockpit. Pairs with [COCKPIT_DESIGN.md](COCKPIT_DESIGN.md) (the architecture) - this doc is the ordered work.

## Related documents

- [COCKPIT_DESIGN.md](COCKPIT_DESIGN.md) - architecture, topology, hosting
- `src/CcDirector.Cockpit/` - the live code this plan continues
- `playground/wingman-briefing/` - the prototype the design formalizes

---

## Where we are (status snapshot, 2026-05-31)

The MVP is **already largely built** in `src/CcDirector.Cockpit/` (Blazor Server, references `Gateway.Contracts` only):

- **Left rail** (`SessionRail`) - fleet-wide sessions from the Gateway's `GET /sessions`, 2s poll.
- **Terminal** (`TerminalPane` + `cockpit-terminal.js`) - xterm.js on a direct `wss://` to the Director's `/sessions/{sid}/stream`, with reconnect + column-mirroring.
- **Composer** - Speak (browser Web Speech API), Send, Queue, Interrupt, Escape.
- **Screenshots** - drag/drop or pick -> uploaded through the Cockpit server to the Director's `/upload-image`.
- **Queue** - full panel (list / enqueue / send-now / remove).

Every Director endpoint the Cockpit calls **exists today** (`/prompt`, `/queue` + item ops, `/interrupt`, `/escape`, `/upload-image`, `/stream`). Nothing is assuming a phantom API.

**Recent progress (parallel session):**
- **Registration reachability is fixed in code.** `ControlApi/GatewayClient.cs` `ResolveTailnetEndpoint()` now advertises `https://{magicdns}:{ownPort}` (matching the Gateway's `TailscaleServeProvisioner`), demoting the stale shared `:7882` override to a fallback. Verified end-to-end on a slot-5 test Director (`https://<machine>.<tailnet>.ts.net:7887`, healthz 200 through Serve, aggregated by the Gateway, shown in the Cockpit rail). The user's 3 live Directors still error until they are **re-launched** with this build (do NOT restart them out from under their sessions).
- **Director queue REST exists** (`/sessions/{sid}/queue` GET/POST/DELETE + `/queue/{itemId}/send`), wired to `Session.PromptQueue`.
- **A raw-PTY write primitive already exists:** `session.SendInput(bytes)`. This is the hook the interactive terminal needs.

**Two things still NOT true:**
1. **Not verified E2E against the live fleet** - the terminal path is proven on a test Director, but the user's live Directors need re-launching with the registration fix first.
2. **The terminal is read-only** (`disableStdin: true`) - a decision we are now **reversing**: the user must be able to type into the terminal.

---

## Locked decisions

- **One Cockpit**, Blazor Server, hosted by Kestrel next to the Gateway, browser-accessed over the tailnet. (See COCKPIT_DESIGN.)
- **Reads aggregate through the Gateway; writes + the terminal stream go direct to the owning Director.**
- **The terminal is INTERACTIVE.** The user can type into it. This reverses the code's current `disableStdin: true`. The composer stays as a convenience for long prompts, dictation, queue, and screenshots - but the terminal itself is fully usable.
- **Cockpit stays thin (Contracts-only) until Phase 3.** The smarts (`WingmanService`, `SummaryBuilder`, `RecapGenerator`) move in only when the awareness layer is built.
- **Speak = browser Web Speech API for now.** Routing through cc-director's dictation pipeline (dictionary + verbatim) is deferred.
- **The Gateway becomes an always-on Windows service.** Headless Kestrel on `:7878`, auto-start at boot, survives logout/RDP. The **Director is never a service** (ConPTY/`claude.exe` needs an interactive session; Session 0 isolation breaks it). "Ensure a Director is running" = a watchdog that triggers the **Task Scheduler** launch (not `CreateProcessAsUser`); cross-machine supervision is local per-machine, not central.
- **The Cockpit is its own process next to the Gateway service** (not hosted inside it), so the UI can restart without bouncing the always-on Gateway. This is why the two tracks below are independent.

---

## Immediate work runs as two parallel tracks (two agents)

The immediate work touches **disjoint code**, so two agents can run concurrently:

- **Track A - Gateway -> always-on service** (Gateway code only: host, install, the probe breaker).
- **Track B - Final Director build + interactive Cockpit terminal** (Director endpoints + Cockpit). This is the "Phase 0" work below.

They converge before the E2E verification (a fresh Director registered with the now-service Gateway, driven from the Cockpit). Phases 1-5 follow once both tracks land.

---

## Track A - Gateway becomes an always-on service

**Goal:** the Gateway runs headless and always-on - auto-starts at boot, survives logout/RDP, auto-restarts on crash - so the fleet front door is reachable from your phone/browser regardless of interactive session state.

- **A1. Re-host `GatewayHost` as a service.** Run it under a Generic Host with `Microsoft.Extensions.Hosting.WindowsServices` (`UseWindowsService()`); Kestrel binds `0.0.0.0:7878`. Today it lives in `CcDirector.GatewayApp` (an Avalonia tray that hosts `GatewayHost` in-process) - decide between a new `CcDirector.GatewayService` host project vs a `--service` entry mode on the existing one.
- **A2. Install / lifecycle.** Idempotent install/uninstall/start (`sc.exe create` / `New-Service`), Automatic start type, an account that can bind the port and reach the tailnet.
- **A3. Tray app's fate.** Keep `CcDirector.GatewayApp` as an optional *status* tray that talks to the service, or retire it. The service owns the lifecycle either way. (Soft decision.)
- **A4. Probe circuit-breaker** (Gateway-side; **already in flight in the cockpit session**). Back off Directors that fail their probe (alive-but-unreachable at a stale endpoint) instead of eating a 2s timeout every poll. Naturally belongs to the Gateway agent.
- **A5. Don't break local-only.** Keep the filesystem-watch discovery path for deployments with no configured gateway URL.
- **Watchdog (follow-up, not blocking):** the service can trigger the Task Scheduler task to relaunch a *local* missing Director. Cross-machine machines self-supervise via a logon-triggered scheduled task; central remote-spawn stays deferred.

**Done:** the Gateway runs as a service, auto-starts at boot, survives logout; Cockpit and Directors talk to it unchanged.

---

## Track B - the "final Director build" principle

**Every Director rebuild kills its sessions.** So all Director-side changes must land in ONE build that we launch once and then never rebuild. Adding a Director endpoint later costs you a session-killing restart. Therefore Track B front-loads *every* Director-side change we can foresee, then cuts the build, then we drive everything else from the Cockpit (which restarts freely).

### Already on the Director - no change needed

sessions list / create / kill, **rename** (`PATCH /sessions/{sid}`), terminal stream (WS), buffer, prompt, interrupt, escape, upload-image, git, turn-summaries (read + generate), the whole wingman set (`/wingman`, `/wingman/ask`, `/explain`, `/goal`), recap, handover, `/dictate` (WS), `/tts`, and the `voice-mode` / `hold` / `wingman-enabled` toggles. So briefing, the turn rail, screenshots, rename, and **real OpenAI dictation** all already work against the current endpoints.

### Director build manifest (bake all of these in before launch)

| # | Change | Status | Note |
|---|---|---|---|
| 1 | **Registration fix** - advertise `https://{magicdns}:{port}` | done in code | reachable over Tailscale Serve |
| 2 | **Queue REST** - `GET/POST/DELETE /sessions/{sid}/queue`, `…/{id}/send` | done in code | wired to `Session.PromptQueue` |
| 3 | **Terminal input channel** - stream WS forwards client bytes -> `session.SendInput` | **DONE + tested** | interactive terminal |
| 4 | **Queue auto-drain on ready** - sends next queued item on Idle (gated by OnHold; never on WaitingForInput) | **DONE + tested** | Queue = auto-send when ready |
| 5 | **PTY resize** (`POST /sessions/{sid}/resize`) + unchanged-size guard | **DONE + tested** | repaint-loop-safe |
| 6 | **Full REST coverage** - git writes, workspaces/history, scheduler, relink | **DONE + tested** | desktop parity; never rebuild |

Decision: ship the final Director with **#1-#6**. **Status: all implemented + tested (18/18), build 0 warnings.** Report: [../../features/cockpit-final-build/REPORT.html](../../features/cockpit-final-build/REPORT.html). Remaining: Cockpit `disableStdin:false`+`onData`->WS, cut the build, live E2E, roll to Mac-mini + Windows-2.

---

## Track B / Phase 0 - Final Director build + interactive Cockpit terminal  [IMMEDIATE]

**Goal:** cut one final Director build with the full manifest, launch a fresh controllable Director, and drive a real session end-to-end from the Cockpit.

- **B1. Finish the Director build manifest** - implement #3 (terminal input channel) and #4 (queue auto-drain); #1 and #2 are already coded. This is the only Director code left before launch.
- **B2. Cockpit interactive terminal** - pairs with #3: `cockpit-terminal.js` set `disableStdin: false`; wire xterm `onData` -> WS send. **Keep PTY-resize OFF** (column-mirror only) until manifest #5 is done deliberately.
- **B3. Launch the fresh Director** (with the final build) and **verify E2E**: see output, type directly into the terminal, Send + Queue (auto-drains), drop a screenshot, Interrupt + Escape. (Re-launch the user's live Directors with this build too; do NOT restart them out from under their sessions.)
- Open sub-decision: Tailscale Serve strategy when several Directors share one machine (per-port vs path-based) - confirm per-port `tailscale serve --https={port}` scales to N Directors.

*(The Gateway probe circuit-breaker that protects against the stale-`:7882` Directors is **Track A / A4**, not Track B.)*

**Done (both tracks converge):** the Gateway runs as a service; a fresh Director runs the final build; a human drives a real session end-to-end from the Cockpit, including typing into the terminal, with no further Director rebuilds needed.

---

## Phase 1 - MVP finish + hardening

**Goal:** the built MVP is solid against real multi-Director data.

- Confirm composer, queue CRUD, screenshots, reconnect, and error/empty states under load.
- Selection UX; keyboard focus into the terminal on select.
- Status dots / needs-you correctness against live data (not just the error path).

**Done:** MVP reliable on a real fleet.

---

## Phase 2 - Operate without the desktop (lifecycle + settings)

**Goal:** the line you cross to stop opening the desktop for everyday work. All REST-backed, low risk.

- **New session** - pick machine/Director + repo + agent -> `POST /sessions` (direct, with a small "on which Director?" step).
- **Kill** session; **rename** (`PATCH /sessions/{sid}`).
- **Settings view** - `GET /settings` (read) -> `PUT /settings` (edit), with live re-apply.

**Done:** start / stop / rename / configure sessions entirely from the Cockpit.

---

## Phase 3 - Awareness / wingman (consume the Director's existing endpoints)

**Goal:** at-a-glance awareness and briefings without opening each session.

**Reconciliation:** the Director is *not* actually dumb today - it already exposes the whole wingman surface (`/wingman`, `/wingman/ask`, `/explain`, `/goal`), `recap`, `turn-summaries`, `/tts`, and `/dictate` over REST. So this phase is mostly **the Cockpit consuming those endpoints**, not hosting `WingmanService` itself. The Cockpit can stay Contracts-only + REST; no `CcDirector.Core` reference is required to ship awareness. (Moving the smarts physically into the Cockpit per the original "dumb runner" vision stays a *future option*, not a prerequisite.)

- **Recap / "what's happening"** panel - calls the Director's recap/explain; async, never blocks the live view (mind the ~90s opus latency).
- **Turn-summary right rail** (the session's arc) from `/turn-summaries`.
- **Needs-you prominence + notifications.**

**Done:** the awareness layer the prototype sketched, driven from the Director's existing REST surface.

---

## Phase 4 - Power tools

**Goal:** the heavier desktop features.

- **Source-control / git diff** view.
- **Workspaces / history.**
- **Handover** and **fan-out** across sessions.

**Done:** the remaining day-to-day desktop capabilities live in the Cockpit.

---

## Phase 5 - Voice + retire the desktop

**Goal:** finish the job.

- Full **voice mode** (optionally route Speak through the gateway dictation pipeline for dictionary + verbatim parity).
- Sweep any remaining desktop-only features into the Cockpit.
- **Deprecate the in-Director terminal + opinionated UI.**

**Done:** the desktop app is no longer needed.

---

## Cross-cutting concerns / risks

- **Director reachability/Serve is the gating dependency** for everything terminal-related (Phase 0a). Nothing downstream is verifiable until it's fixed.
- **Interactive terminal:** never resize the PTY from the Cockpit (repaint loop); keystroke input is the safe part.
- **Opus latency (~90s)** for briefings - async enrichment only, never block the live view.
- **Multiple viewers of one PTY** (desktop owner + Cockpit): a read mirror is fine; if both write, keystrokes interleave at the PTY - acceptable, it's one user.

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-05-31 | claude (cc-director assistant) | Initial plan. Status snapshot of the built MVP; reversed the read-only-terminal decision to interactive; phased Phase 0 (unblock + interactive terminal) through Phase 5 (retire the desktop). |
