# The Cockpit - Design (TARGET / PLANNED v1)

**Status:** PLANNED
**Date:** 2026-05-31
**Audience:** Anyone building the Cockpit, or deciding where a session-driving feature should live going forward.

## Related documents

- `cockpit-topology.d2` / `cockpit-topology.png` - the three-lane topology (read first)
- [../gateway/GATEWAY_DIRECTOR_ARCHITECTURE.md](../gateway/GATEWAY_DIRECTOR_ARCHITECTURE.md) - CURRENT Gateway/Director split this builds on
- [../gateway/GATEWAY_SESSION_VIEW_PLAN.md](../gateway/GATEWAY_SESSION_VIEW_PLAN.md) - the shipped fleet-wide `GET /sessions` aggregation the Cockpit consumes
- [../wingman/SESSION_VIEW_MERGE_PLAN.md](../wingman/SESSION_VIEW_MERGE_PLAN.md) - the wingman/agent-feed view the Cockpit replaces
- `playground/wingman-briefing/PLAN-v1-cockpit.md` + `cockpit.html` - the working prototype this doc formalizes

---

## Topology at a glance

On the left is the **outside view** you interact with: the Cockpit (your window) and the always-on Gateway it reads from. On the right, **inside the Tailscale network**, are the runner machines - each a Director that owns the live session PTYs. The Cockpit reads the fleet directory from the Gateway, then reaches straight into the network to each owning Director for the live terminal and prompts. The Gateway never carries terminal traffic, and there is no Director on the Gateway's host - it is purely the front door.

![Cockpit fleet topology: outside view (Cockpit + Gateway) reaching into the Tailscale network of Director runners](cockpit-topology.png)

---

## 1. The idea in one sentence

> **The Cockpit is the single place you drive every Claude session on your tailnet. Directors become dumb, long-lived runners that own the PTY and nothing opinionated. The thing you restart constantly (the Cockpit) is no longer the thing that owns sessions - so sessions never die.**

Everything below is a consequence of that sentence.

## 2. Why this exists (the driver)

The PTY wrapping `claude.exe` is the running session. Today the opinionated UI - the rich `session-view.html`, the Avalonia terminal tab, the wingman views - lives **inside or beside** the process that owns that PTY. So iterating on a UI feature means restarting `cc-director`, which kills every session. That is disruptive and lossy, and there is no clean session-restart story.

The Cockpit fixes this **structurally, not by discipline**: it owns no session state, reaches the PTY only over a network stream, and can be rebuilt and relaunched all day without touching a single running session.

A secondary win: today **three** frontends reimplement the same UI (Avalonia desktop XAML, Director web HTML/JS, Gateway web HTML/JS), sharing only Core C# over REST. The Cockpit consolidates toward one.

## 3. Scope of v1

- **Desktop-first.** Built for a large screen (desktop, possibly a large tablet, but a desktop-class experience). The existing Android app is a **separate track** handled later. Prove it on a large screen first.
- **Tech is locked: Blazor Server.** Chosen so the view tier is C# and reuses `CcDirector.Core` + `CcDirector.Gateway.Contracts` directly (no JS re-modeling of DTOs). Note: "share with the desktop" realistically means **share Core C#**, not views - Avalonia XAML cannot be reused by any web tier.
- **Directors keep working as they do today** until the Cockpit is proven. Then their opinionated UI is retired and they shrink to runners. No big-bang switch.

Out of scope for v1: mobile/phone layout, replacing the Avalonia desktop app, multi-user identity, remote Director spawn.

## 4. How it connects (the phone book + direct dial)

There are only two ideas in the topology, and the diagram above shows both:

### 4.1 The Gateway is a phone book, used once

The Cockpit can't know, on its own, every machine on the tailnet or which one is running which session. The Gateway already solves that: every Director registers with it, so the Gateway can answer **"which sessions exist across the whole fleet, and which machine owns each one"** (`GET /sessions`, `GET /directors`). The Cockpit asks the Gateway that question and gets back a list, each entry carrying its home Director's address (`tailnetEndpoint`).

That is the Gateway's *entire* role for the Cockpit: discovery and read-aggregation. **It never carries terminal traffic and is never in the write path.** It's a switchboard that tells you the number to dial, not the line you talk over.

### 4.2 Everything real is dialed direct to the owning Director

Once the Cockpit knows a session lives on, say, Mac-mini, it talks to **that Director directly**:

- **Live terminal** - the xterm.js view opens a WebSocket straight to the Director's `/sessions/{sid}/stream`. This high-frequency byte stream goes machine-to-machine over the tailnet and **never passes through the Gateway**. That is why the remoted terminal is as fast as opening the Director's own page today - it answers your terminal-latency concern directly.
- **Prompts, interrupt, escape, rename** - REST calls straight to the owning Director.

So: **one call to the Gateway to find sessions; direct calls to each Director to use them.** This matches the already-shipped principle - *reads aggregate through the Gateway, writes/streams go direct-to-Director.*

## 5. "Why is there a Cockpit *server*?" (the Blazor Server model)

This caused confusion in the first draft, so to be explicit: **the Cockpit is one app you launch, not a client plus a separate server you deploy.**

Blazor Server simply means the app's C# logic runs in an in-process web host, and the window you see is its view, connected over a local (loopback) SignalR link. You double-click one exe; everything below is internal:

- The **C# side** is where the smarts live - `WingmanService` (opus briefing), `SummaryBuilder` (the turn rail), `RecapGenerator` - reused directly from `CcDirector.Core`. This is the concrete meaning of "dumb runners": the enrichment the external harness (`playground/wingman-briefing/`) does today from outside the Director becomes a permanent, built-in part of the Cockpit.
- The **view side** is the rendered window (xterm.js terminal + the panels).

We chose Blazor Server precisely so that smart layer is **C# we share with the rest of the stack** instead of re-implemented in JavaScript. The terminal is the one thing that deliberately bypasses this internal link - its bytes go from the window straight to the Director (section 4.2), not through the C# render channel, so the terminal stays fast.

The briefing is **async enrichment layered over the live view** - it catches up; you never wait on opus to see reality. The live terminal and the cheap fleet reads are always current regardless of briefing latency.

## 6. Project shape

`CcDirector.Cockpit` - a Blazor Server project referencing:

- `CcDirector.Core` - `WingmanService`, `SummaryBuilder`, `RecapGenerator`, and the agent/session domain types.
- `CcDirector.Gateway.Contracts` - `SessionDto`, `DirectorDto`, and the fleet-wide field set (`MachineName`, `TailnetEndpoint`, `ViewUrl`, `LastActivityAt`).
- A typed REST client to Directors and the Gateway (lift/extract from the Gateway's existing `DirectorEndpointClient`).

### Components (from the prototype `cockpit.html`, formalized as Razor components)

```
+--------------------------------------------------------------+------------------+
|  Header: SessionPicker (fleet-wide) | branch | dirty | state |  TurnRail        |
+--------------------------------------------------------------+  one card per    |
|  TerminalPane  (xterm.js, DIRECT WS to the owning Director) |  turn, newest    |
|                                                              |  highlighted;    |
|                                                              |  the arc of the  |
+--------------------------------------------------------------+  session at a    |
|  BriefingDock  (opus enrichment, async, never blocks)        |  glance          |
|   Claude is asking: "..."  Claude recommends: "..."  Wingman |                  |
+--------------------------------------------------------------+                  |
|  ReplyBar  [ reply ............... ] [Send]                  |                  |
|            [Yes proceed] [Use B] ...   [Interrupt] [Esc]     |                  |
+--------------------------------------------------------------+------------------+
```

- `SessionPicker` - fed by Gateway `GET /sessions`; selecting a session targets every pane below it.
- `TerminalPane` - JS-interop wrapper around xterm.js; opens the direct WebSocket to the owning Director.
- `BriefingDock` - renders the latest `WingmanService` briefing for the selected session.
- `ReplyBar` - writes (prompt / interrupt / escape) direct to the owning Director.
- `TurnRail` - renders `GET /sessions/{sid}/turn-summaries`, newest highlighted.

## 7. Hosting (the one open fork)

Blazor Server is a server process plus SignalR to a browser. "Desktop app" can mean two things:

| | (i) Local host (RECOMMENDED for v1) | (ii) Always-on host |
|---|---|---|
| Where the Cockpit server runs | On the user's desktop machine, wrapped in a WebView2 shell (Photino.NET or a tiny WPF/WinForms WebView2 host) - a real window/exe | On the always-on box, co-located with the Gateway (possibly hosted by the GatewayApp tray) |
| Accessed from | The local shell window | Any machine's browser |
| UI latency (SignalR) | Loopback - zero | Crosses the tailnet per interaction (usually fine, occasionally noticeable) |
| Cockpits per fleet | One per machine | One shared |

**Recommendation: (i) for v1.** It gives a true desktop-app feel, loopback-fast UI, and a single exe, while Lane A and Lane C still reach Directors anywhere on the tailnet. It also preserves the Cockpit's restartability - rebuild and relaunch all day, never touch a PTY. The connection lanes are identical under (ii), so moving to a from-anywhere model later is a small change, not a rewrite. Revisit (ii) when the phone/from-anywhere story comes up.

## 8. What this buys you, restated

The PTY lives only on the Director and is reached by a direct WebSocket. The Cockpit owns no session state. Kill it, rebuild it, relaunch it as often as you like; every session keeps running untouched. The "never restart the session" property becomes structural.

## 9. Build order (by risk, front-loading the uncertain part)

1. **Scaffold `CcDirector.Cockpit`** (Blazor Server) referencing `Core` + `Gateway.Contracts`; WebView2 shell per hosting option (i).
2. **TerminalPane first (Lane A).** xterm.js JS-interop component, direct WebSocket to a live Director's `/sessions/{sid}/stream`. This is the only genuinely uncertain piece - does a remoted terminal feel good enough to abandon the in-Director one? Learn that cheaply, before building anything pretty.
3. **ReplyBar (Lane C).** Prove a human can answer a real pending question from the Cockpit.
4. **SessionPicker (Lane B).** Fleet-wide list from the Gateway's `GET /sessions`.
5. **TurnRail.** From `/sessions/{sid}/turn-summaries`.
6. **BriefingDock.** `WingmanService` opus enrichment, async, never blocking the live view.
7. **Prove on a real desktop session with a real pending question** before declaring it works.

## 10. Open questions

1. **WebView2 shell choice** - Photino.NET vs a minimal WPF/WinForms WebView2 host. Both are small; pick when scaffolding.
2. **Director-side trims** - which opinionated routes/pages get retired, and when. Keep a barebones raw-terminal page on the Director as an emergency fallback?
3. **Reuse vs extract** - does the Director REST client get extracted from `CcDirector.Gateway` into a shared library both Gateway and Cockpit reference, or copied?
4. **Briefing ownership** - does the Cockpit server run opus side-calls itself, or call a Director endpoint that does? v1 leans Cockpit-server-side to keep Directors dumb.

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-05-31 | claude (cc-director assistant) | Initial PLANNED design. Locks Blazor Server, desktop-first, the three connection lanes, smarts-in-Cockpit, and the local-host recommendation. Formalizes the `playground/wingman-briefing` prototype. |
