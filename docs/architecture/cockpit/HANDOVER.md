# Cockpit / Gateway / Director - Handover

**Status:** ACTIVE handover
**Date:** 2026-05-31
**For:** the new agent implementing the **Cockpit** and the **Gateway features**, now that the Director is final, committed, and built.

---

## TL;DR (read this first)

We are building **one Cockpit** (Blazor Server, opened in a browser, hosted next to the Gateway) that drives **every** Claude session across the tailnet. **Directors become dumb, long-lived runners** that own the session PTYs. The point is that the UI (Cockpit) restarts freely while **the Directors - and their live sessions - never get killed by our iteration.**

**Why this doc exists:** the Director is now **final, committed (`2c12a04` code, `ba839f6` docs), and built** at `local_builds\cc-director.exe`. Its whole desktop capability surface is exposed over REST, so we should not need to rebuild a Director again. The remaining work is two bodies: **the Cockpit** (the UI) and **the Gateway features** (service, key vault, settings tiers). This doc hands the new agent the current state and how to implement both.

**The golden rule (still holds):** **ALL Director-side changes go into the build BEFORE launch.** After launch, only the Cockpit and the Gateway change - both restart freely without touching a single running session.

**Read the detail in:**
- Architecture -> [COCKPIT_DESIGN.md](COCKPIT_DESIGN.md) (+ `cockpit-topology.png`)
- Phased plan -> [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md)
- What shipped in the Director -> [the final-build report](../../features/cockpit-final-build/REPORT.html)
- Gateway features to build -> [GATEWAY_KEY_VAULT.md](../gateway/GATEWAY_KEY_VAULT.md), [SETTINGS_OWNERSHIP.md](../gateway/SETTINGS_OWNERSHIP.md)
- This doc = current state + the new agent's mission + cross-machine rollout.

---

## Current status (2026-05-31) - what's DONE

- **Director: final, committed, built.** `#3-#6` (interactive terminal, queue auto-drain, resize, full REST surface: git writes, workspaces/history, scheduler, relink) are implemented + tested (18/18, 0 warnings) and committed (`2c12a04`). The **main `local_builds\cc-director.exe` is built** and ready to launch as the new stable main.
- **Gateway (committed, on `origin/main`):** registration fix (`https://{magicdns}:{port}`), queue REST, a probe **circuit-breaker** for alive-but-unreachable Directors, and Cockpit supervision. The legacy NSSM `cc_director` service was **removed** (slate clear for a real service).
- **Cockpit app** (`src/CcDirector.Cockpit/`, Blazor Server, `Gateway.Contracts` only): left rail, direct-to-Director terminal (typeable - input via `onData`->`POST /prompt {appendEnter:false}`), composer (Speak/Send/Queue/Interrupt/Esc), screenshot upload, queue panel. Verified against a test Director (terminal renders, Send round-trips).

**Everything above is committed and pushed-ready** (local `main` is 2 commits ahead of `origin` at handoff - push to share).

---

## The Director REST surface (the gate is MET)

The Director now exposes the **entire** desktop capability surface over REST. The Cockpit builds against this and needs no further Director change. Inventory (`src/CcDirector.ControlApi/`):

**Already present (no change needed):**
- **Lifecycle:** `GET /sessions`, `GET /sessions/{sid}`, `POST /sessions` (create), `POST /sessions/github`, `DELETE /sessions/{sid}` (kill), `PATCH /sessions/{sid}` (rename), `GET /repos`
- **Terminal / IO:** `GET /sessions/{sid}/stream` (WS - now **bidirectional**, #3), `POST /sessions/{sid}/resize` (#5), `/buffer`, `/buffer/html`, `POST /prompt`, `/interrupt`, `/escape`, `/upload-image`, queue (`GET/POST /queue`, `DELETE /queue/{id}`, `POST /queue/{id}/send`, **auto-drains** #4)
- **Awareness / wingman:** `/wingman`, `/wingman/ask`, `/wingman/act`, `/wingman/explain`, `/wingman/goal`, `/turns`, `/summary`, `/turn-summaries` (GET+POST), `/recap` (GET+POST), `/handover-context`, `POST /handover`, `/git`, `/state-vote`, `/rule-violations`, `/recovery-prompt`
- **Voice / dictation:** `/dictate` (WS), `/dictate/recovered`, `/tts`, `/tts/status`, `/voice/command|status|utterance`, `/chat`
- **Toggles:** `/voice-mode`, `/mobile-mode`, `/hold`, `/wingman-enabled`
- **Settings:** `GET/PUT /settings`, `/settings/detect/*`, `/settings/test/gateway`
- **Tools:** `/tools`, `/tools/{name}`, `/tools/{name}/test`, `/tools/test`
- **Fan-out / ops:** `/fanout-local`, `/healthz`, `/shutdown`, `/file`, `/sessions/{sid}/view`

**This means Phases 2-5 of the Cockpit roadmap (lifecycle, settings, awareness/wingman, git, handover, voice/dictation) all map onto endpoints that ALREADY EXIST - they need no Director change.** That is exactly the reassurance you wanted.

**The only Director-side gaps - decide each BEFORE cutting the build:**

| Gap | Needed for | Status |
|---|---|---|
| **#3 Terminal input** - typing already works via REST (`onData`->`/prompt`); `/stream` is now also bidirectional as a kept lower-latency capability | typing into the terminal (Esc/Ctrl+C/arrows/slash-UI) | **DONE + tested** |
| **#4 Queue auto-drain** - sends the next queued item when the session goes Idle (gated by OnHold; never on WaitingForInput) | Queue meaning "auto-send when ready" not a holding list | **DONE + tested** |
| **#5 PTY resize** (`POST /sessions/{sid}/resize`) - with the unchanged-size repaint-loop guard | full-width terminal at any window size | **DONE + tested** |
| **#6 Full REST coverage** - git writes (stage/unstage/discard/commit), workspaces/history, scheduler (view/run), session relink | never rebuilding for any later phase | **DONE + tested** |

**Gate status: MET.** #3-#6 implemented, tested (18/18), committed, and the main is built. Report: [REPORT.html](../../features/cockpit-final-build/REPORT.html).

---

## New agent: your mission

The Director is done. Your work is two streams - both safe (they never rebuild a Director, so live sessions survive your iteration):

### Stream 1 - Implement the Cockpit (follow [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md))

- **Done:** left rail, live terminal (typeable), composer, queue, screenshots.
- **Phase 2 - operate without the desktop:** New session / kill / rename UI + a Settings view (`POST /sessions`, `DELETE`, `PATCH`, `GET/PUT /settings` - all exist).
- **Phase 3 - awareness:** consume the Director's existing `/wingman`, `/explain`, `/recap`, `/turn-summaries` over REST (no Core reference needed). Recap panel, turn-summary rail, needs-you/notifications.
- **Phase 4 - power tools:** git view using the new `/git/stage|unstage|discard|commit`; workspaces/history (`/workspaces`, `/history`); scheduler (`/scheduler`); relink (`/relink`).
- **Phase 5 - voice**, then deprecate the desktop terminal.
- **Optional perf win:** switch terminal input from per-keystroke REST to direct `ws.send` over the now-bidirectional `/stream` (#3 capability is already in the build - no Director change).

### Stream 2 - Build the Gateway features

- **Gateway -> Windows service** (Track A): re-host `GatewayHost` under `UseWindowsService()`, install/autostart, survives logout/RDP. The `cc_director` NSSM service is already removed, so the name is free. Recommend a dedicated `CcDirector.GatewayService` host; the tray becomes optional.
- **Key Vault** - central API keys handed out to Directors on demand. Spec + diagram: [GATEWAY_KEY_VAULT.md](../gateway/GATEWAY_KEY_VAULT.md).
- **Settings tiers** - classify settings into Gateway-central vs Director-local as they come up. Framework: [SETTINGS_OWNERSHIP.md](../gateway/SETTINGS_OWNERSHIP.md).

---

## Launch + cross-machine rollout

Goal: the **same final Director build** running on **this Windows box, the Mac-mini, and Windows-2**, all registered with the Gateway service, all controllable from the one Cockpit.

1. **The main build is done** - `local_builds\cc-director.exe` (committed code `2c12a04`).
2. **Relaunch locally:** launch the fresh main and move your live sessions onto it deliberately (never kill a Director out from under a session you want to keep).
3. **Mac-mini:** build the **Mac** Director (.NET; uses the `UnixPty` backend, not ConPty - confirm `docs/plan-mac-support.md` maturity). Set `gatewayUrl`, ensure Tailscale Serve, autostart at login, launch.
4. **Windows-2:** same final Windows build as the main box; set `gatewayUrl`, Tailscale Serve, logon autostart, launch.
5. **Per machine, confirm:** the Director registers `https://{magicdns}:{port}`, appears in the Cockpit rail, and a session's terminal drives end-to-end (output + typing + Send/Queue + screenshot + Interrupt/Esc).

**Tailscale Serve note:** each Director needs its own reachable `https` endpoint. Confirm per-port `tailscale serve --https={port} http://localhost:{port}` scales when several Directors share one machine.

**Director lifecycle on each machine:** the Director is **never a service** (ConPTY needs an interactive session). It autostarts at **logon** via a scheduled task with restart-on-failure; the always-on Gateway service supervises/observes but does not launch remote Directors.

---

## After launch - safe to iterate WITHOUT touching Directors

- The whole Cockpit roadmap (Phases 1-5) runs against the **existing** Director REST surface. Building it does **not** require Director rebuilds.
- The **Gateway is a service** and can be restarted without killing any session.
- The **Cockpit** restarts/redeploys freely (it owns no session state).

So after the final Director launch, all further work is on the Cockpit and Gateway - sessions stay alive.

---

## Hard rules / gotchas

- **Never rebuild or restart a Director out from under live sessions.** Relaunch only deliberately, with the final build.
- **Don't resize the PTY from the Cockpit** unless #5 is implemented and done carefully (the wingman repaint-loop invariant).
- **Everything over Tailscale; no localhost, ever.** No-tailnet Director -> skip HTTP registration (local-only), never advertise loopback.
- **Director must NOT be a service** (Session 0 isolation breaks ConPTY). **Gateway IS a service.** **Cockpit is neither** - it's a browser-accessed web app next to the Gateway.

---

## Open decisions

1. **Gateway service host:** a new `CcDirector.GatewayService` project vs a `--service` mode on the existing `CcDirector.GatewayApp` tray. (Recommend a dedicated service host; the tray becomes optional.)
2. **Key Vault at-rest format** and **per-setting tier classification** - deferred until each is built (see the gateway docs).

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-05-31 | claude (cc-director assistant) | Initial handover. Verified Director REST inventory; defined the launch gate; cross-machine rollout to Mac-mini + Windows-2. |
| 2026-05-31 | claude (cc-director assistant) | **Director final + committed (`2c12a04`/`ba839f6`) + main built.** Reframed from launch-gate to the new agent's mission: Stream 1 implement the Cockpit (phases), Stream 2 build the Gateway features (service, key vault, settings tiers). |
