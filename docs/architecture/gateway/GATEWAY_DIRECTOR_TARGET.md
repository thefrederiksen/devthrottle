# Gateway / Director Architecture (TARGET - Phase 1)

**Status:** PLANNED
**Date:** 2026-05-19
**Audience:** Anyone implementing the cross-machine support. Diff against [GATEWAY_DIRECTOR_ARCHITECTURE.md](GATEWAY_DIRECTOR_ARCHITECTURE.md) (CURRENT) to see what changes.

## Related documents

- [GATEWAY_DIRECTOR_ARCHITECTURE.md](GATEWAY_DIRECTOR_ARCHITECTURE.md) - the CURRENT-state doc
- [GATEWAY_DIRECTOR_RESPONSIBILITIES.md](GATEWAY_DIRECTOR_RESPONSIBILITIES.md) - the feature-by-feature decision matrix
- `gateway-director-target-overview.d2` / `.png` - target topology (Phase 1)
- `gateway-director-target-detail.d2` / `.png` - same topology with feature lists per component
- `gateway-director-overview.d2` / `.png` - CURRENT topology, for comparison
- `gateway-director-detail.d2` / `.png` - CURRENT detail, for comparison

---

## 1. The shape

> **The Director is canonical. The Gateway is a thin receptionist.**

Office metaphor: the Gateway is the general manager at the front desk. Departments (Directors) are where work happens. When someone walks in and says "I want to talk to my agent on machine B," the receptionist points them at the door to Director B and gets out of the way. They do not do the work themselves.

Phase 1 commits to the simplest possible shape that gives us cross-machine reach. We deliberately defer everything that would make the Gateway "smart." Those features are sketched in section 6 ("Deferred to later phases") so we do not forget them, but they are out of scope for the work this doc is about.

### What the Phase 1 Gateway does

1. **Discovery.** Accepts inbound HTTP from every Director on the fleet (`POST /directors/register`, `POST /directors/{id}/heartbeat`, `DELETE /directors/{id}/registration`). Maintains an in-memory list of who's online.
2. **Directory page.** Serves `/` as an HTML directory page listing the registered Directors with their machine name, last-seen time, and a deeplink to each Director's own Manager UI.
3. **Auth.** Bearer / cookie protection for the directory page and the register endpoint.
4. **Health.** `/healthz`.

That's it. No proxying of session calls. No aggregated session list. No event hub. No fan-out. No cross-Director handover orchestration.

### What the Phase 1 Director does

1. **Everything it does today.** The Director's `manager.html` is unchanged in scope - it's the canonical place a user goes to talk to a session.
2. **PLUS register / heartbeat to the Gateway** if `gatewayUrl` is configured. On startup, the Director POSTs `/directors/register` to the Gateway with its tailnet endpoint, then heartbeats every 15 s, and DELETEs on graceful shutdown.
3. **Falls back to today's behavior** when `gatewayUrl` is unset. Local-only deployments keep working exactly as they do today.

---

## 2. End-to-end: how a user reaches a session

```
1. User points browser at:   https://<gateway-host>/
2. Gateway serves the directory page
3. User clicks "machine-B Director" in the list
4. Browser opens a new tab to:   http://<machine-b-tailnet>:7879/
5. Director B serves its own manager.html
6. User sends prompts, renames sessions, generates recaps, etc.,
   all by talking directly to Director B
7. Gateway never sees the session traffic
```

Compare to CURRENT:

```
1. User points browser at:   http://localhost:7878/
2. Gateway serves manager.html and aggregates /sessions across local Directors
3. Every action proxies through the Gateway to the owning Director
```

The Phase 1 model removes the proxy entirely. The simplification is intentional. We get cross-machine without committing to a Gateway-aggregator design.

---

## 3. Discovery (the only mechanism the Gateway needs)

### 3.1 Director registration

On startup the Director:

1. Reads `gatewayUrl` and `gatewayToken` from `cc-director.json`. If `gatewayUrl` is empty, skip steps 2-4.
2. Constructs its `tailnetEndpoint` - the URL the Gateway's browser users will deeplink to. This is the Director's Tailscale-reachable address plus its allocated port (e.g. `http://machine-b.tailnet.example:7879`).
3. POSTs to `<gatewayUrl>/directors/register` with `{ directorId, tailnetEndpoint, machineName, user, version }` and the bearer token.
4. Starts a heartbeat timer: every 15 s, POST to `<gatewayUrl>/directors/{id}/heartbeat`.

On graceful shutdown the Director DELETEs `<gatewayUrl>/directors/{id}/registration`.

### 3.2 Gateway-side registry

The Gateway keeps an in-memory `DirectorRegistry` keyed by `directorId`. Each entry stores the last `DirectorDto` payload plus a `LastHeartbeat` timestamp. A sweeper runs every 30 s and removes entries that have not heartbeat in the last 60 s.

The CURRENT filesystem-watch (`%LOCALAPPDATA%\cc-director\config\director\instances\`) is **kept** as a fallback path for same-machine deployments where no `gatewayUrl` is configured. Both code paths coexist.

### 3.3 Director identity stability

The Director needs a `directorId` that's the same across restarts so the Gateway can recognize "the same Director coming back." Today the Director generates a fresh GUID at every startup. We change it to persist the GUID once to `%LOCALAPPDATA%\cc-director\config\director\director-id.txt` and reuse it forever.

---

## 4. Directory page

`GET /` on the Gateway returns a simple HTML page:

- One row per registered Director
- Each row shows: machine name, user, version, last-seen, "Open Director" button
- "Open Director" is an anchor to the Director's `tailnetEndpoint`
- Optional filter / search box if there are many Directors
- Periodic refresh every few seconds OR a small SSE that emits `director.added` / `director.removed` (Phase 1 can poll; the SSE is a nice-to-have)

That's the whole UI surface of the Gateway in Phase 1.

---

## 5. Auth model

Today's `gateway-token.txt` shared-bearer model is sufficient for Phase 1.

- The Director uses the token for `POST /directors/register` and the heartbeat / unregister calls.
- The Gateway accepts cookie or bearer auth for the directory page (today's middleware).
- Per-Director or per-user identity is **out of scope** for Phase 1.

The Tailnet remains the trust boundary. If you can reach the Gateway over Tailscale, you have full access. We accept this for now.

---

## 6. Deferred to later phases

These were in the original (overambitious) TARGET design. We are deferring them. They are listed here so they don't get forgotten and so we don't accidentally implement them in Phase 1.

| Capability | Why deferred |
|---|---|
| Aggregated session list on the Gateway (`/sessions` across all Directors) | Requires the Gateway to track sessions, not just Directors. Big surface; we don't yet know if we want it as a list or as a chat. |
| Fan-out (`POST /fanout`) across Directors | Real cross-Director feature, but rarely needed in practice today. Add when concretely useful. |
| Cross-Director handover orchestration | Same. Belongs to whoever writes the "talk to all my agents" UI. |
| Live session events from Director to Gateway to browser | Today's 1.5 s polling is fine for now. Live events are needed only once we have an aggregated view that benefits from them. |
| Gateway-side proxy of session ops (`PATCH /sessions/{sid}`, `POST /sessions/{sid}/prompt`, etc.) | Removed entirely from the Gateway in Phase 1. Director is the only place these run. (The routes exist on the Director already - that's the canonical home.) |
| Gateway-side Manager UI for individual sessions | Phase 1 has no such UI. The Director's `manager.html` is the only UI. |
| Per-Director registration tokens | Shared token is enough for Phase 1. |
| Per-user identity | Out of scope. |
| Remote Director spawn (Process.Start on another machine) | Out of scope. Adding a machine to the fleet is a manual step in Phase 1. |

When any of these become concrete needs, design them then, in their own doc, with their own diagrams. Do not pre-build them.

---

## 7. Migration phases

Phase 1 is small enough to be one PR or one short series.

### Step 1: Persist Director identity

- Add `director-id.txt` write-once-and-reuse. Touches `ControlApiHost` / startup code.

### Step 2: Add register / heartbeat endpoints to the Gateway

- `POST /directors/register`, `POST /directors/{id}/heartbeat`, `DELETE /directors/{id}/registration`.
- Extend `DirectorRegistry` to accept HTTP-fed entries alongside the FSW-fed entries.
- Heartbeat-based TTL sweep (60 s default).

### Step 3: Add `GatewayClient` to the Director

- New class in `CcDirector.Core` or `CcDirector.ControlApi`.
- Reads `gatewayUrl` / `gatewayToken` from `cc-director.json`. If unset, no-op.
- Manages register / heartbeat / unregister lifecycle.
- Reconnect with exponential backoff if the Gateway is unreachable.

### Step 4: Replace the Gateway's Manager UI with a directory page

- Rewrite `Web/manager.html` on the Gateway side: directory only, no cards, no detail view, no send-prompt, no recap.
- Remove the proxy routes from `GatewayEndpoints.cs` (everything except `/healthz`, `/directors`, `/login`, `/logout`, `/directors/register`, `/directors/{id}/heartbeat`, `DELETE /directors/{id}/registration`).
- Keep the Director's `Web/manager.html` exactly as it is today. It IS the Manager UI.

### Step 5: Cross-machine smoke test

- Gateway on machine A. Director on machine B with `gatewayUrl` set to A.
- Browser hits A's URL, sees B in the directory, clicks through, sees B's sessions.
- Sends a prompt to a session on B - happens entirely Director-direct.

### Step 6: Update docs and ship

- Both file-watch and HTTP-register paths run in parallel forever (file-watch keeps local-only deployments working without config).

After Step 6, focus shifts entirely to the Director: the concrete feature gaps (waiting-for-input prominence, live current-session summary, terminal-question surfacing) and any other Director-side improvements. The Gateway stays small.

---

## 8. State ownership (diff vs. CURRENT section 5)

| What | Where | New vs. CURRENT? |
|---|---|---|
| In-memory registry of Directors | Gateway, fed by HTTP register **and** the existing filesystem watcher | **CHANGED** (HTTP path added; FSW path retained) |
| Director identity (`directorId`) | Persisted to `%LOCALAPPDATA%\cc-director\config\director\director-id.txt` | **NEW** (was a per-startup fresh GUID) |
| Session-level data on the Gateway | Nothing | **REMOVED** (Phase 1 has no `/sessions` route on the Gateway) |
| Everything else | Same as CURRENT | Unchanged |

The Gateway is genuinely small in Phase 1. The simplification is the point.

---

## 9. Open questions

1. **Heartbeat interval vs. drop deadline.** Recommend 15 s interval, 60 s deadline. Open: confirm or tune.
2. **Directory page polling vs. SSE.** Recommend poll every 5 s in Phase 1. SSE-when-things-change is a nice-to-have for Phase 2.
3. **What does the Director do when the Gateway is unreachable at startup?** Recommend: boot normally and retry-with-backoff in the background. Surface a status dot in the Avalonia UI.
4. **Should the Gateway also accept ad-hoc Director URLs (manual "add Director" form)?** Nice-to-have for testing. Optional.
5. **What happens when both a same-machine FSW-discovered Director and an HTTP-registered Director resolve to the same `directorId`?** De-dupe by `directorId`, prefer HTTP-registered entry (it has the tailnet endpoint, FSW only has loopback).

---

## 10. The diagrams

- `gateway-director-target-overview.png` - Phase 1 topology. Gateway is small (directory + register endpoint). Browser deeplinks to Director for everything else.
- `gateway-director-target-detail.png` - same topology with per-component feature lists. New boxes (`[NEW]`) are the `GatewayClient` on the Director side and the register / heartbeat / directory endpoints on the Gateway side.

To re-render:

```powershell
& "D:\Tools\d2\d2.exe" --theme=0 --layout=elk gateway-director-target-overview.d2 gateway-director-target-overview.png
& "D:\Tools\d2\d2.exe" --theme=0 --layout=elk gateway-director-target-detail.d2   gateway-director-target-detail.png
```

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-05-18 | claude (cc-director assistant) | Initial PLANNED design (overambitious: central Gateway with event hub, SSE, aggregated views). |
| 2026-05-19 | claude (cc-director assistant) | **Rescoped to Phase 1.** Gateway becomes a thin directory page + discovery. Everything Manager-UI-related stays on the Director. The big Gateway smarts (event hub, aggregated views, fan-out, cross-Director handover) moved to section 6 "Deferred to later phases." |
