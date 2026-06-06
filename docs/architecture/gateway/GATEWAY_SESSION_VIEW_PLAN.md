# Gateway Session View — Implementation Plan (Phase 2)

**Status:** IMPLEMENTED 2026-05-21 (branch `feature/gateway-session-view`, tracking #124)
**Predecessor:** [GATEWAY_AS_DIRECTORY_PLAN.md](GATEWAY_AS_DIRECTORY_PLAN.md) (Phase 1)
**Design diff:** Promotes "Aggregated session list on the Gateway" from `GATEWAY_DIRECTOR_TARGET.md` section 6 (deferred) into a shipped Phase 2.

---

## Goal in one sentence

Make the Gateway's `/` show **every session across the fleet** in card or list view, with one-click deep-links into each session's existing UI on its owning Director, and expose the same data as a clean public `GET /sessions` API that Claude Code (or any other consumer) can call to see what's running fleet-wide.

---

## Principles (locked, do not relitigate mid-PR)

1. **Sessions are the unit, not machines.** Machine name is a group header, never a clickable destination. Phase 1 made the Gateway machine-centric; Phase 2 fixes that.
2. **Director stays canonical.** The Gateway aggregates **reads** only. Writes (prompts, renames, interrupts, deletes, new sessions) go direct from client to the owning Director using `tailnetEndpoint + sessionId`. No write-through proxy on the Gateway in Phase 2.
3. **The Gateway is the network-wide hub for Claude Code.** Anything Claude Code needs to know about what is running anywhere on the fleet, it asks the Gateway.

---

## Out of scope (deferred to Phase 2.1+)

- Writing to sessions through the Gateway (prompts, renames, deletes). Writes stay direct-to-Director.
- SSE / push updates from Director to Gateway to browser. 2s polling is fine for v1.
- Cross-fleet orchestration ("fan-out a prompt", "talk to all my agents"). Re-open only when a concrete consumer asks for it.
- Removing legacy proxy routes from `GatewayEndpoints.cs`. The embedded Avalonia `ManagerView` still calls them — they get deleted in a follow-up PR per Phase 1.2.

---

## Decisions recorded

- **Card density:** info-rich cards, 3 per row at desktop width. Show agent + repo + activity state + idle time on every card.
- **Exited sessions:** hidden by default. Filter bar carries a `Show exited (N)` toggle alongside the status pills. Stored in `localStorage.gw_show_exited`.
- **`LastActivityAt`:** new field, added in Slice 1 of this plan (not deferred). It drives the "Idle Xm" column and is the truthful "how stale is this session" signal that the UI needs.
- **`ViewUrl` form:** computed server-side as `{TailnetEndpoint}/sessions/{sid}/view` so clients don't redo the math.
- **HTML preview report:** standalone self-contained artifact lives at `docs/architecture/gateway/views-preview.html`. Shows both card view and list view stacked, with realistic mock data baked in. No JS polling, no auth. Used for visual review during development and as a static reference of what the views should look like.

---

## Slices

### Slice 1 — Add `LastActivityAt` on the Director side

**Why:** The only freshness signal exposed today is `CreatedAt`, which only tells the UI "started X minutes ago" — useless once a session is live for hours. We need "last produced output X minutes ago" to make the Idle column truthful.

**Touches:**
- Edit: the terminal buffer class that backs `Session.Buffer` (already exposes `TotalBytesWritten`). Add a `LastWriteAt` (UTC) timestamp updated on every write. No new allocations on the hot path — single `DateTime` field write.
- Edit: `src/CcDirector.Gateway.Contracts/SessionDto.cs` — add `LastActivityAt` (nullable UTC). Documented as "Most recent buffer write. Falls back to `CreatedAt` if the session has produced no output yet."
- Edit: `src/CcDirector.ControlApi/ControlEndpoints.cs` `Map()` (line 1109) — populate `LastActivityAt = s.Buffer?.LastWriteAt ?? s.CreatedAt.UtcDateTime`.

**Tests:**
- Buffer write updates `LastWriteAt`.
- `Map()` falls back to `CreatedAt` for a freshly created session with no output.
- Existing Director `/sessions` tests still pass.

---

### Slice 2 — Stamp fleet-only fields on `SessionDto` from the Gateway

**Why:** The UI needs machine name to render the group header, and a `ViewUrl` to build the "Open" anchor without doing endpoint math in JavaScript. The Claude Code consumer of `GET /sessions` needs the same things.

**Touches:**
- Edit: `src/CcDirector.Gateway.Contracts/SessionDto.cs` — add four optional fields, populated by the Gateway, empty in Director-local responses (same precedent as the existing `DirectorId` field):
  - `MachineName`
  - `User`
  - `TailnetEndpoint`
  - `ViewUrl`
- Edit: `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` `GET /sessions` (line 158) and `GET /sessions/{sid}` (line 183) — stamp the four fields from the owning `DirectorDto` after pulling sessions from each Director.

**Tests:**
- Aggregator stamps `MachineName`, `TailnetEndpoint`, `User`, `ViewUrl` on every returned session.
- `ViewUrl` is correctly formed (no double slashes, no missing port).
- Director-local `/sessions` response leaves the new fields empty (no regression on the per-Director API).

---

### Slice 3 — Robust parallel fan-out

**Why:** Today's fan-out (`GatewayEndpoints.cs:167`) is sequential and silently skips Directors that error. With multiple machines this becomes "the page mysteriously misses sessions when one box is slow." Graceful degradation, visible to the user.

**Touches:**
- Edit: `GatewayEndpoints.cs` `GET /sessions` — parallelize per-Director calls with `Task.WhenAll`; 2s timeout per Director. New response shape:
  ```json
  {
    "sessions": [ ...flat list... ],
    "machineErrors": [
      { "machineName": "...", "directorId": "...", "error": "timeout" }
    ]
  }
  ```
- Extend filter query params (some already exist):
  - `?statusColor=red|yellow|green|unknown` (NEW — drives the pill bar)
  - `?machine=MACHINE_A` (NEW)
  - `?includeExited=true|false` (NEW — default false; matches UI default)
  - `?agent=ClaudeCode|Pi|Codex|Gemini` (exists, keep)
  - `?state=Idle|Working|...` (exists, keep)
  - `?q=substring` (NEW — server-side substring against `Name` + `RepoPath`)

**Tests:**
- Two Directors registered, one returns sessions, one times out — response is 200 + first Director's sessions + a `machineErrors` entry for the timed-out one.
- Total wall-clock bounded by the per-Director timeout, not their sum.
- `includeExited=false` (and the default) filters out `ActivityState == "Exited"`.
- All filter params combine correctly.

---

### Slice 4 — Rewrite `directory.html` as a session-centric view

**Why:** The whole user-facing point of Phase 2.

**Touches:**
- Rewrite: `src/CcDirector.Gateway/Web/directory.html`. Keep the file name; `GET /` already routes to it.

**Layout:**

```
+----------------------------------------------------------------------------------+
|  CC DIRECTOR        7 sessions  *  3 red, 1 yellow         [ Cards | List ]      |
|                                                                       /api       |
+----------------------------------------------------------------------------------+
|  All  | Red(3) | Yellow(1) | Green(3)   [ ] Show exited (4)                      |
|  Agent: [All v]   Search: [_________]                                            |
+----------------------------------------------------------------------------------+
|                                                                                  |
|  MACHINE_A   *  alice  *  3 sessions                                           |
|  ------------------------------------------------------------------------------  |
|  +-------------------+  +-------------------+  +-------------------+             |
|  | (R) fix raw tab.. |  | (G) rename hotkey |  | (Y) (unnamed)     |             |
|  | ClaudeCode * cc-..|  | Pi * private/ass..|  | Codex * mindzieETL|             |
|  | Waiting for input |  | Idle              |  | Working           |             |
|  | 2m                |  | 18m               |  | 4s                |             |
|  | [ Open -> ]       |  | [ Open -> ]       |  | [ Open -> ]       |             |
|  +-------------------+  +-------------------+  +-------------------+             |
|                                                                                  |
|  LAPTOP_B  *  alice  *  unreachable (timeout)                                |
|  ------------------------------------------------------------------------------  |
+----------------------------------------------------------------------------------+
```

**Card content:**
- Status dot (color from `StatusColor`).
- `Name` (or "(unnamed)" placeholder).
- `Agent * <repo basename>`.
- Humanized `ActivityState` ("Waiting for input", "Working", "Idle", "Exited").
- Idle = `now - LastActivityAt`, humanized as `Xs / Xm / Xh / Xd`.
- `[Open ->]` anchor to `session.ViewUrl`, `target="_blank"`.

**Row content (list view):** `Dot · Agent · Name · Repo · Idle · [Open]`. Sorted within each machine: red, yellow, green, unknown, exited (the last only when `Show exited` is checked).

**Empty / error states:**
- 0 sessions visible due to filters: "No sessions match the current filters."
- 0 sessions registered anywhere on the fleet: "No sessions registered. A session will appear here when a Director starts one."
- A Director in `machineErrors`: render its machine header with an inline `(unreachable: <error>)` row so the machine doesn't silently disappear.

**Persistence:**
- View toggle `[Cards | List]` → `localStorage.gw_view`.
- Show-exited toggle → `localStorage.gw_show_exited`.
- Active status-pill / agent / search → ephemeral (reset on reload).

**Polling:**
- `fetch('/sessions?includeExited=…', { signal })` every 2s, with `AbortController` to cancel in-flight on next tick.
- On error: inline error banner at top, **do not** wipe the existing list.

---

### Slice 5 — Public API reference page at `GET /api`

**Why:** The user said "the Gateway also needs an API that Claude Code can talk to." `GET /sessions` is that API; give it a discoverable home so a developer landing on `/` knows where to look.

**Touches:**
- New: `src/CcDirector.Gateway/Web/api.html` — single-page reference:
  - `GET /sessions` (query params, response shape including `machineErrors`, example JSON).
  - `GET /sessions/{sid}`.
  - `GET /directors` (already shipped — note here for completeness).
  - `GET /healthz`.
  - Note: writes go direct-to-Director using `tailnetEndpoint` + session id. Example `curl` for sending a prompt directly to a Director.
- Edit: `GatewayEndpoints.cs` — map `GET /api` → `api.html`.
- Edit: `directory.html` header — `/api` link in the top-right.

**Tests:**
- `GET /api` returns 200 + HTML.

---

### Slice 6 — HTML preview report, tests, doc updates, smoke

**6a — HTML preview report:**
- New: `docs/architecture/gateway/views-preview.html` — single self-contained file. Shows both Card view and List view stacked on the same page (no toggle), with the same realistic mock data fed into both. No JS polling, no auth, no fetch. Pure HTML + inline CSS + a tiny initialization script that hydrates from a hardcoded `const SAMPLE = [...]` array so the file is faithful to the live UI without depending on the Gateway.
- Useful for visual review during development and as a static reference of what the views should look like at all times. Reviewers can open it directly in any browser.

**6b — Aggregation tests:**
- New: `src/CcDirector.Gateway.Tests/SessionsAggregationTests.cs` — covers Slices 2 and 3 (field stamping, parallel fan-out, timeout degradation, filter combinations).

**6c — Doc updates:**
- Update: `docs/architecture/gateway/GATEWAY_DIRECTOR_TARGET.md` — move "Aggregated session list on the Gateway" out of section 6 (deferred); add a Phase 2 entry above section 6 noting the read-aggregation shipped.
- Update: `docs/architecture/gateway/GATEWAY_DIRECTOR_RESPONSIBILITIES.md` if it duplicates the same matrix.

**6d — Smoke:**
- Two Directors registered (one local, one cross-machine over Tailnet), four sessions between them with varied `StatusColor`/`ActivityState`. Confirm: page renders correct cards, deep-links open the right session views directly on owning Directors, status pills narrow correctly, `Show exited` toggle works, killing one Director surfaces a machine error placeholder within ~5s.

---

## File-by-file change list

| File | Change |
|---|---|
| Terminal buffer class (the one backing `Session.Buffer`) | EDIT — add `LastWriteAt` timestamp updated on every write |
| `src/CcDirector.Gateway.Contracts/SessionDto.cs` | EDIT — add `LastActivityAt`, `MachineName`, `User`, `TailnetEndpoint`, `ViewUrl` |
| `src/CcDirector.ControlApi/ControlEndpoints.cs` (`Map()` at line 1109) | EDIT — populate `LastActivityAt` |
| `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` | EDIT — parallel fan-out + new fields + new filter params + `machineErrors` response + map `GET /api` |
| `src/CcDirector.Gateway/Web/directory.html` | REWRITE — session-centric cards/list view |
| `src/CcDirector.Gateway/Web/api.html` | NEW — public API reference page |
| `src/CcDirector.Gateway.Tests/SessionsAggregationTests.cs` | NEW — fan-out, timeout degradation, field stamping, filter combinations |
| `docs/architecture/gateway/views-preview.html` | NEW — standalone HTML preview report (both views, mock data) |
| `docs/architecture/gateway/GATEWAY_SESSION_VIEW_PLAN.md` | NEW — this doc |
| `docs/architecture/gateway/GATEWAY_DIRECTOR_TARGET.md` | EDIT — Phase 2 shipped note |

---

## Done criteria

1. `GET /sessions` on the Gateway returns every session across every registered Director, each carrying `MachineName`, `User`, `TailnetEndpoint`, `ViewUrl`, and `LastActivityAt`.
2. One slow / unreachable Director does not block or hide other Directors' sessions; it surfaces in `machineErrors`, and the UI shows a placeholder row under that machine header.
3. `/` renders both Cards and List views. The toggle persists across reloads.
4. Exited sessions are hidden by default; a `Show exited (N)` toggle reveals them. State persists.
5. Clicking "Open" on any session opens that session's `/sessions/{sid}/view` page directly on its owning Director (Gateway is not in the request path).
6. Filter pills (Red / Yellow / Green / All), agent dropdown, and search box narrow the visible set; `?statusColor=`, `?machine=`, `?includeExited=`, `?agent=`, `?state=`, `?q=` query params work for programmatic callers.
7. `GET /api` serves an HTML reference page with copyable examples for the public endpoints.
8. The legacy `/legacy-manager` route still serves the old manager page (no regression on the embedded Avalonia `ManagerView`).
9. All pre-existing tests still pass. New aggregation tests pass.

---

## Document history

| Date | Author | Change |
|---|---|---|
| 2026-05-21 | claude (cc-director assistant) | Initial PLANNED. Confirmed with Soren: info-rich cards 3/row, exited hidden by default with toggle, `LastActivityAt` added now. |
