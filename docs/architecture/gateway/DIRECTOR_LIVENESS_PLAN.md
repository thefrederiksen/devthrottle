# Gateway Director Liveness & Unreachability (PLAN + build)

**Status:** BUILT 2026-05-31 (Gateway compiles 0 warnings; breaker unit-tested). Needs the running
Gateway to be relaunched on this build to take effect (a Gateway restart loses no sessions - Directors
re-register via 410 within ~15s).
**Problem:** the Cockpit shows Directors stuck at "unreachable (timeout)" indefinitely, and every fleet
read re-probes them, so a dead or mis-registered Director keeps costing a timeout on every poll.

## Two distinct failure modes (both must be handled)

1. **Process gone / shut down improperly.** The Director was killed (no graceful
   `DELETE /directors/{id}/registration`), so it simply stops heartbeating.
2. **Alive but unreachable.** The Director process is up and heartbeating fine, but its advertised
   control endpoint does not answer (stale/wrong `tailnetEndpoint`, a dropped Tailscale Serve
   mapping, a half-dead Kestrel). **This is what the two dangling Directors in the screenshot are** -
   two long-lived Directors, heartbeating every ~10s, but advertising an old unreachable port.

A heartbeat-only expiry handles (1) but NOT (2): a heartbeating-but-unreachable Director never expires.
So we need both a heartbeat TTL *and* a reachability circuit-breaker.

## Design

### Layer 1 - Heartbeat TTL (already exists, keep)
- Director `POST /directors/{id}/heartbeat` every **15s** (`GatewayClient.HeartbeatInterval`).
- `DirectorRegistry.SweepStale` (every 30s) drops any HTTP-registered Director whose `LastSeen` is older
  than `HttpHeartbeatTimeout` (**60s** = 4 missed beats). Graceful `DELETE` removes immediately.
- Covers mode (1). No change needed beyond keeping it.

### Layer 2 - Reachability circuit-breaker (NEW)
Per-Director breaker state in `DirectorRegistry` (NOT on the wire `DirectorDto`):
- The `/sessions` fan-out, after each per-Director probe (2s timeout), reports the outcome:
  - **success** -> `RecordReachable(id)`: clear the breaker.
  - **failure** -> `RecordUnreachable(id, error)`: `ConsecutiveFailures++`, remember the error and the
    first-unreachable time.
- After `MaxConsecutiveFailures` (**3**) consecutive failures, open the breaker: set
  `CooldownUntil = now + UnreachableCooldown` (**30s**). While open, `ShouldProbe(id)` returns false, so
  the fan-out **skips** that Director entirely (no 2s timeout) and reports it in `machineErrors` as
  "unreachable (cooling down)". After the cooldown one probe is allowed; success resets, failure re-opens.
- **Eviction:** if a Director stays unreachable for `UnreachableEvictAfter` (**3 min**), `SweepStale`
  removes it from the registry even though it is still heartbeating - "alive but its control endpoint has
  been dead too long; stop carrying it." If the process is genuinely alive it self-heals: its next
  heartbeat gets `410 Gone`, and its `GatewayClient` re-registers (existing behaviour), which resets the
  breaker via `Upsert`. A Director that fixed its endpoint (e.g. restarted on the new build) therefore
  reappears within ~15s; one that is wedged stays gone.

### Resets
- `Upsert` (a fresh `POST /register`, possibly with a corrected endpoint) clears the breaker for that id.
- `Remove` / sweep eviction clears the breaker too (no leak).
- A heartbeat does **not** reset the breaker: it proves the Director can reach the Gateway, not that the
  Gateway can reach the Director's control endpoint. Only a successful fan-out proves reachability.

## Why this fixes the screenshot
The two dangling Directors are alive+unreachable. The breaker stops the per-poll 2s timeout within 3 polls
(they move to a 30s skip), and they are evicted after 3 minutes. When the operator restarts them on the build
with the registration fix, they re-register with the correct `https://<magicdns>:<port>`, the breaker
resets on `Upsert`, the next fan-out succeeds, and they show their sessions.

## Touch points
- `src/CcDirector.Gateway/Discovery/DirectorRegistry.cs` - breaker state, `ShouldProbe` / `RecordReachable`
  / `RecordUnreachable` / `LastUnreachableError`, eviction in `SweepStale`, reset in `Upsert`/`Remove`.
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` `GET /sessions` - skip cooled-down Directors, record
  each probe outcome.
- Per-Director probe timeout stays 2s (`DirectorEndpointClient`).
