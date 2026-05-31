# Cockpit / Gateway / Director - Handover

**Status:** ACTIVE handover
**Date:** 2026-05-31
**For:** a fresh cc-director session picking this up after the current building sessions are killed.

---

## TL;DR (read this first)

We are building **one Cockpit** (Blazor Server, opened in a browser, hosted next to the Gateway) that drives **every** Claude session across the tailnet. **Directors become dumb, long-lived runners** that own the session PTYs. The point is that the UI (Cockpit) restarts freely while **the Directors - and their live sessions - never get killed by our iteration.**

**Why this doc exists:** we are about to **kill the current building sessions and relaunch the Directors on a final build.** Rebuilding a Director kills its sessions, so we must get the Director *completely right once*, launch it, and then never rebuild it. This doc is the source of truth so (a) the final Director build is provably complete, and (b) a fresh agent can continue without us.

**The golden rule:** **ALL Director-side changes go into the final build BEFORE launch.** After launch, only the Cockpit and the Gateway change (both restart freely without touching sessions).

**Read the detail in:**
- Architecture -> [COCKPIT_DESIGN.md](COCKPIT_DESIGN.md) (+ `cockpit-topology.png`)
- Phased plan -> [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md)
- This doc = current status + the launch gate + cross-machine rollout.

---

## Current status (2026-05-31)

Two parallel tracks (see the plan):

- **Track A - Gateway becomes a Windows service.** Being finished by the cockpit agent: re-host `GatewayHost` under `UseWindowsService()`, install/autostart, and a probe circuit-breaker for alive-but-unreachable Directors. The **legacy NSSM `cc_director` service has been removed** (verified: `sc`/CIM/registry all clear), so the name and slate are free for the new service.
- **Track B - final Director build + interactive Cockpit terminal.** Registration fix (`https://{magicdns}:{port}`) and queue REST are **done in code**. Outstanding: terminal **input** channel (#3) and queue **auto-drain** (#4).
- **Cockpit app** (`src/CcDirector.Cockpit/`, Blazor Server, references `Gateway.Contracts` only): left rail, direct-to-Director terminal, composer (Speak/Send/Queue/Interrupt/Esc), screenshot upload, queue panel. **Verified** against a slot-5 test Director: terminal renders and Send round-trips over the tailnet.

**Nothing is committed to git.** All of the above is working-tree only.

---

## THE LAUNCH GATE - the final Director build must be provably complete

This is the most important section. The Director's REST surface is **already almost everything the whole Cockpit roadmap needs.** Verified inventory (`src/CcDirector.ControlApi/`):

**Already present (no change needed):**
- **Lifecycle:** `GET /sessions`, `GET /sessions/{sid}`, `POST /sessions` (create), `POST /sessions/github`, `DELETE /sessions/{sid}` (kill), `PATCH /sessions/{sid}` (rename), `GET /repos`
- **Terminal / IO:** `GET /sessions/{sid}/stream` (WS - **output-only today**), `/buffer`, `/buffer/html`, `POST /prompt`, `/interrupt`, `/escape`, `/upload-image`, queue (`GET/POST /queue`, `DELETE /queue/{id}`, `POST /queue/{id}/send`)
- **Awareness / wingman:** `/wingman`, `/wingman/ask`, `/wingman/act`, `/wingman/explain`, `/wingman/goal`, `/turns`, `/summary`, `/turn-summaries` (GET+POST), `/recap` (GET+POST), `/handover-context`, `POST /handover`, `/git`, `/state-vote`, `/rule-violations`, `/recovery-prompt`
- **Voice / dictation:** `/dictate` (WS), `/dictate/recovered`, `/tts`, `/tts/status`, `/voice/command|status|utterance`, `/chat`
- **Toggles:** `/voice-mode`, `/mobile-mode`, `/hold`, `/wingman-enabled`
- **Settings:** `GET/PUT /settings`, `/settings/detect/*`, `/settings/test/gateway`
- **Tools:** `/tools`, `/tools/{name}`, `/tools/{name}/test`, `/tools/test`
- **Fan-out / ops:** `/fanout-local`, `/healthz`, `/shutdown`, `/file`, `/sessions/{sid}/view`

**This means Phases 2-5 of the Cockpit roadmap (lifecycle, settings, awareness/wingman, git, handover, voice/dictation) all map onto endpoints that ALREADY EXIST - they need no Director change.** That is exactly the reassurance you wanted.

**The only Director-side gaps - decide each BEFORE cutting the build:**

| Gap | Needed for | Verdict |
|---|---|---|
| **#3 Terminal input** - make `/sessions/{sid}/stream` bidirectional (client key bytes -> `session.SendInput`, which already exists) | typing into the terminal (Esc/Ctrl+C/arrows/slash-UI) | **MUST ADD** - the interactive terminal you required |
| **#4 Queue auto-drain** - "send next queued item when the session goes idle" moves into the Director/Core | Queue meaning "auto-send when ready" not a holding list | **MUST ADD** |
| **#5 PTY resize** (`POST /sessions/{sid}/resize`) | full-width terminal at any window size | **INCLUDE NOW (decided 2026-05-31).** The one careful piece (the wingman repaint-loop invariant) - do it deliberately, but it's in the final build so we never rebuild for it. |
| **#6 Full REST coverage of the desktop surface** | never rebuilding for any later phase | **INCLUDE NOW (decided 2026-05-31 - "endpoints for everything").** Gaps vs `src/CcDirector.Avalonia`: **workspaces/history**, **source-control write actions** (stage/commit/discard - `/git` is read-only), **scheduler** (view/run), **session relink**. The build agent confirms the exhaustive list against the desktop code. |

**Gate condition (a fresh agent must confirm before launch):** #3, #4, #5, and #6 are implemented - i.e. the Director exposes the **entire desktop capability surface** over REST. Once that's true, the final build is complete and **we should not need to rebuild a Director again.**

---

## Launch + cross-machine rollout

Goal: the **same final Director build** running on **this Windows box, the Mac-mini, and Windows-2**, all registered with the Gateway service, all controllable from the one Cockpit.

1. **Cut the final Director build** (with #3, #4, and the chosen optionals).
2. **Relaunch locally:** kill the current building sessions, launch the fresh Director(s) on the final build. (Relaunch deliberately; never kill a Director out from under a session you want to keep.)
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

## Open decisions to settle before launch

1. ~~Include #5 (PTY resize)?~~ **DECIDED: yes.**
2. ~~Workspaces/history now or later?~~ **DECIDED: endpoints for everything now (#6).**
3. **Track A:** new `CcDirector.GatewayService` host project vs a `--service` mode on the existing `CcDirector.GatewayApp` tray. (Recommend a dedicated service host; tray becomes optional.)

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-05-31 | claude (cc-director assistant) | Initial handover. Verified Director REST inventory; defined the launch gate (only #3/#4 required, #5 + workspaces/history to decide); cross-machine rollout to Mac-mini + Windows-2. |
