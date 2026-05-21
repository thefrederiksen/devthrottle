# Gateway-as-Directory — Implementation Plan (Phase 1)

**Status:** IN PROGRESS
**Date:** 2026-05-21
**Tracking issue:** [#123](https://github.com/thefrederiksen/cc-director/issues/123)
**Design doc:** [GATEWAY_DIRECTOR_TARGET.md](GATEWAY_DIRECTOR_TARGET.md)

This document is the concrete implementation plan for the Phase-1 pivot described in the target doc: the Gateway becomes a thin "receptionist" that lists running Directors and deeplinks users to each Director's own manager UI. It does not proxy session traffic anymore.

The why and the shape are in the target doc. This doc is just the slices and where each one touches the code.

---

## Goal in one sentence

Allow a user to point a browser at the Gateway, see every live Director on the fleet, and click through to any Director's own manager UI without the Gateway ever proxying a session call.

---

## Out of scope (deferred)

Same list as section 6 of the target doc:

- Aggregated session list / fan-out / cross-Director orchestration on the Gateway
- Live event hub from Director through Gateway to browser
- Per-user identity
- Remote Director spawn
- Migration of the embedded Avalonia ManagerView away from the proxy routes (it will keep using the legacy `/sessions/*` routes during the transition - see "Backward compatibility" below)

---

## Slices

### Slice 1 — Persist Director identity

**Why:** Today the Director mints a fresh GUID every startup, so the Gateway sees "the same Director" as a new entry after every restart. Persisting the ID once and reading it forever makes the registry stable.

**Touches:**
- New: `src/CcDirector.ControlApi/DirectorIdStore.cs` (static helper that reads/writes `%LOCALAPPDATA%\cc-director\config\director\director-id.txt`).
- Edit: `src/CcDirector.ControlApi/ControlApiHost.cs` — replace `Guid.NewGuid().ToString()` in the `DirectorId` initializer with a `DirectorIdStore.LoadOrCreate()` call.

**Tests:**
- `DirectorIdStoreTests` — write-once-and-reuse semantics, valid GUID format, file path correctness.

---

### Slice 2 — Register / heartbeat / unregister on the Gateway

**Why:** Without HTTP registration, the Gateway can only discover same-machine Directors via the filesystem-watch path. HTTP registration gives us cross-machine reach.

**Touches:**
- New endpoints in `src/CcDirector.Gateway/Api/GatewayEndpoints.cs`:
  - `POST /directors/register` — body is `DirectorRegistrationRequest { directorId, tailnetEndpoint, machineName, user, version }`. Adds or refreshes the entry. Auth-gated.
  - `POST /directors/{id}/heartbeat` — refreshes `LastSeen`. Auth-gated.
  - `DELETE /directors/{id}/registration` — graceful unregister. Auth-gated.
- New: `src/CcDirector.Gateway.Contracts/DirectorRegistrationRequest.cs`.
- Edit: `src/CcDirector.Gateway/Discovery/DirectorRegistry.cs` — add `Upsert(DirectorDto)`, `Heartbeat(string id)`, `Remove(string id)`. Add heartbeat-based TTL sweep (60s default) alongside the existing file-gone / PID-dead sweeps.
- Edit: `DirectorDto` — add `Source` (`"file" | "http"`) to disambiguate the two paths in logs.

**Tests:**
- Posting register adds an entry. Posting heartbeat updates `LastSeen`. Deleting registration removes the entry. Stale entry (no heartbeat in 60s) is swept.

---

### Slice 3 — `GatewayClient` on the Director

**Why:** The Director needs to announce itself to the Gateway and keep heartbeating. Today nothing on the Director knows about the Gateway.

**Touches:**
- New: `src/CcDirector.ControlApi/GatewayClient.cs` — reads `gatewayUrl` and `gatewayToken` from a new config helper. If unset, no-op (local-only deploys keep working exactly as today). If set, on `Start()`:
  - Computes the Director's tailnet endpoint (best-effort: Tailscale interface address, fall back to `$"http://{Environment.MachineName}:{port}"`).
  - POSTs `/directors/register`.
  - Starts a 15s heartbeat timer.
  - Retries with exponential backoff on transient failure.
  On `Stop()`: DELETE the registration (best-effort, swallow errors).
- New: `src/CcDirector.Core/Configuration/GatewayConfig.cs` — read-only DTO + loader that reads `gatewayUrl` and `gatewayToken` from `%LOCALAPPDATA%\cc-director\config\config.json` under a `gateway` block. Missing keys = local-only.
- Edit: `ControlApiHost` — instantiate and start `GatewayClient` after Kestrel is listening; stop it on `StopAsync`.

**Tests:**
- `GatewayClientTests` — boots a fake Gateway (test server), confirms register + heartbeat + unregister are sent. No-op when `gatewayUrl` is unset. Backoff path.

---

### Slice 4 — Directory page on the Gateway

**Why:** The whole user-facing point of Phase 1.

**Touches:**
- New: `src/CcDirector.Gateway/Web/directory.html` — simple list: one row per Director, columns for machine, user, version, last-seen, with an "Open Director" anchor pointing at `dto.ControlEndpoint` (or `dto.TailnetEndpoint` if present). Polls `/directors` every 3s. Auth-gated (cookie or bearer).
- Edit: `GET /` in `GatewayEndpoints.cs` — serve `directory.html` instead of `manager.html`.
- Keep the existing `manager.html` reachable at `GET /legacy-manager` during the transition for the embedded Avalonia ManagerView. It will be removed once `ManagerView` migrates to per-Director direct calls (out of scope for this PR).

**Tests:**
- The new `/` returns the directory HTML and contains an anchor to a registered Director's endpoint.

---

### Slice 5 — Smoke test and doc update

- Local smoke: run a Gateway and a Director on the same box, with `gatewayUrl=http://localhost:7878` configured for the Director. Confirm the Director shows up in the directory and the "Open Director" link opens the Director's `manager.html` on its allocated port.
- Update `GATEWAY_DIRECTOR_TARGET.md` Status from `PLANNED` to `Phase 1 implemented` once merged.

---

## Backward compatibility

We deliberately do **not** rip out the proxy routes from `GatewayEndpoints.cs` in this PR. The embedded Avalonia `ManagerView` (`src/CcDirector.Avalonia/Controls/ManagerView/ManagerView.axaml.cs`) currently calls the Gateway proxy for `/sessions`, `/sessions/{sid}/prompt`, etc. Removing those routes would break the embedded view in the same change.

Plan:
- Phase 1 (this PR): additive. New endpoints, persistent ID, directory page at `/`. Old `manager.html` remains reachable at `/legacy-manager` and the proxy routes remain functional, marked deprecated in comments.
- Phase 1.1 (follow-up): migrate `ManagerView` to call the Director's own Control API directly per `DirectorDto.ControlEndpoint`, removing the dependency on Gateway proxy routes.
- Phase 1.2 (follow-up): delete the proxy routes and `/legacy-manager`. This matches Step 4 of the target doc exactly, just deferred to a separate PR for safer review.

This sequencing keeps each PR reviewable and shippable on its own.

---

## File-by-file change list

| File | Change |
|---|---|
| `src/CcDirector.ControlApi/DirectorIdStore.cs` | NEW — load-or-create persistent Director GUID |
| `src/CcDirector.ControlApi/GatewayClient.cs` | NEW — register / heartbeat / unregister against Gateway |
| `src/CcDirector.ControlApi/ControlApiHost.cs` | EDIT — use `DirectorIdStore`, start/stop `GatewayClient` |
| `src/CcDirector.Core/Configuration/GatewayConfig.cs` | NEW — read `gateway.url` / `gateway.token` from config.json |
| `src/CcDirector.Gateway.Contracts/DirectorRegistrationRequest.cs` | NEW — POST /directors/register body |
| `src/CcDirector.Gateway.Contracts/DirectorDto.cs` | EDIT — add `Source` ("file" or "http"), `TailnetEndpoint` |
| `src/CcDirector.Gateway/Discovery/DirectorRegistry.cs` | EDIT — `Upsert` / `Heartbeat` / `Remove`, heartbeat TTL sweep |
| `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` | EDIT — new register/heartbeat/unregister endpoints, `/` serves directory, old route at `/legacy-manager` |
| `src/CcDirector.Gateway/Web/directory.html` | NEW — directory page |
| `src/CcDirector.Core.Tests/DirectorIdStoreTests.cs` | NEW |
| `src/CcDirector.Gateway.Tests/DirectorRegistrationTests.cs` | NEW |
| `src/CcDirector.Gateway.Tests/GatewayClientTests.cs` | NEW |

---

## Done criteria

1. New Director starts up with `gatewayUrl` configured -> Gateway directory shows it within 5 seconds.
2. Restart the Director -> same row updates in place (same `directorId`), not a new one.
3. Kill the Director -> row disappears from the directory within 60 seconds (heartbeat TTL).
4. "Open Director" anchor opens the Director's own `manager.html` and the session interactions there work as today.
5. No `gatewayUrl` configured -> Director boots normally, no errors, file-watch discovery on the same box still works.
6. All existing tests still pass. New tests for the slices above all pass.
