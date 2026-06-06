# Director Self-Serve: every Director owns its own Tailscale Serve front door

**Status:** Workstreams 1-5 + 7 IMPLEMENTED 2026-06-06 (uncommitted). Remaining: WS6
(installer preflight + public docs), rollout to SORENLAPTOP, live acceptance pass.
**Issue:** #197 (SORENLAPTOP permanently unreachable)
**Decision:** Option A chosen by Soren 2026-06-06. Reverse-tunnel (Option B) explicitly rejected for now.

Implemented surface:
- `src/CcDirector.Core/Network/TailscaleCli.cs` (shared CLI runner + cross-process serve mutex)
- `src/CcDirector.ControlApi/TailscaleServeSelfProvisioner.cs` (Director owns its serve mapping)
- `src/CcDirector.ControlApi/GatewayClient.cs` (EndpointVerifier gate + ProbeAdvertisedEndpointAsync)
- `src/CcDirector.ControlApi/ControlApiHost.cs` (wiring: provision -> verify -> register; off on graceful stop)
- `src/CcDirector.Gateway/Tailscale/TailscaleServeProvisioner.cs` (uses shared mutex-serialized CLI)
- `src/CcDirector.Gateway/Discovery/DirectorRegistry.cs` (WasEverReachable + circuit-open log noise fix)
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` (actionable never-answered message)
- Tests: TailscaleCliTests (Core.Tests), TailscaleServeSelfProvisionerTests,
  GatewayClientVerifyTests, DirectorEverReachableTests (Gateway.Tests)

---

## Problem recap (proven in #197)

There are two channels between a Director and the Gateway:

1. **Outbound announcements** (Director -> Gateway): register, heartbeat every 15 s,
   doorbell. These always work - it is why SORENLAPTOP shows up in the Cockpit at all.
2. **Inbound work** (Gateway/Cockpit -> Director): list sessions, read buffers, submit
   prompts, screenshots, kill/rename, and the Cockpit's DIRECT terminal WebSocket
   (wss://<magicdns>:<port>/sessions/{sid}/stream). The Director is dumb metal exposing
   REST; the server genuinely must reach it.

The Director binds Kestrel to **loopback only** (locked security design). The only
inbound path is a `tailscale serve --https=<port>` mapping **on the Director's own
machine**. The only component that creates such mappings is the **Gateway's**
TailscaleServeProvisioner, which since #179 (correctly) maps only LOCAL Directors.
A Workstation-role machine has no Gateway, so **no component ever opens its inbound
door**. The Director advertises `https://<magicdns>:<port>` - an endpoint that is dead
by construction - and the Gateway flaps through a register/timeout/evict/410/re-register
loop every ~3.5 minutes forever.

This is not a version issue. No build has ever provisioned serve on a Gateway-less
machine.

## Target end state (the deployment story)

On ANY new Windows machine:

1. Install Tailscale, log into the tailnet. (MagicDNS + HTTPS certs are tailnet-level
   settings and are already enabled.)
2. Install cc-director (Workstation role).
3. Set gateway.url (Settings page or config.json).

Done. The Director provisions its own serve mapping, verifies its advertised URL
actually answers, registers, and its sessions appear in the Cockpit with a live
terminal. Zero manual tailscale commands, ever. If anything is missing (no tailscale,
not logged in, serve fails), the Director surfaces ONE precise, actionable error and
does NOT register a dead endpoint. No fallbacks.

---

## Workstream 1: Director-side serve self-provisioning

New class `TailscaleServeSelfProvisioner` (location: `CcDirector.ControlApi`, next to
GatewayClient; it is part of the Director's remote-reachability concern, not the
Gateway's).

Lifecycle, wired into ControlApiHost startup after PortAllocator has assigned the
port and Kestrel is listening:

- **Assert:** run `tailscale serve --bg --https=<port> http://localhost:<port>` for the
  Director's own allocated port. Idempotent.
- **Re-assert on a timer (~5 min):** the serve table is known to lose entries with no
  cc-director process removing them (#179 evidence: the 443 front door vanished in
  production). Same self-healing rationale as the Gateway's reconcile.
- **Graceful shutdown:** `tailscale serve --https=<port> off` for the own port only.
  Crash recovery needs nothing special: ports are stable per slot (PortAllocator), so
  the next startup re-asserts the same mapping. A crashed Director's leftover mapping
  proxies to a dead loopback port until then - harmless (connection refused locally,
  Gateway circuit opens) and self-corrects on restart. The Gateway's existing orphan
  sweep does NOT run on remote machines; accept the bounded leak (max the 7879-7898
  range) rather than build a second sweeper. Document this.
- **Failure = loud stop, not fallback:** if tailscale.exe is absent, the daemon is not
  running, the node is logged out, or the serve command errors: log the exact CLI
  output, expose the failure on the Director's status (see Workstream 5), and make
  GatewayClient SKIP registration (extend the existing "no tailnet endpoint to
  advertise; staying local-only" path with the specific reason). The Director keeps
  working locally - that is the truthful state.

Implementation notes:

- Reuse the RunTailscale pattern from the Gateway provisioner (15 s process timeout,
  stdout+stderr capture). Extract the shared CLI runner into CcDirector.Core (e.g.
  `Core/Network/TailscaleCli.cs`) so Gateway and Director use one implementation; keep
  a test seam (Func delegate) exactly like `GatewayClient.MagicDnsResolver`.
- The serve mapping must exist BEFORE the first /directors/register POST. Order in
  ControlApiHost: bind Kestrel -> self-provision serve -> verify (Workstream 3) ->
  GatewayClient.Start().

## Workstream 2: cross-process tailscale CLI lock

`tailscale serve` is a full-config read-modify-write. After this change, on the
Gateway machine these can race: the Gateway provisioner (443 front door + sweep),
and up to 5+ local Directors each asserting their own port. Concurrent writers can
clobber each other's mappings - this is plausibly the actual mechanism behind #179's
"entries vanish with nothing removing them".

- All serve-mutating CLI calls (serve on/off; status reads can stay lock-free) go
  through ONE **named OS mutex** (e.g. `Global\cc-director-tailscale-serve`) shared
  across all cc-director processes on the machine. Lives in the shared TailscaleCli
  class from Workstream 1.
- Both the Gateway provisioner and the new Director self-provisioner adopt it.
- Mutex hold time is bounded by the existing 15 s CLI timeout; take the mutex with a
  timeout (e.g. 30 s) and treat failure-to-acquire as a failed CLI call (log, retry on
  the next reconcile tick).

## Workstream 3: verify-before-advertise

After asserting the mapping, the Director probes its OWN advertised URL from the
outside-in: `GET https://<magicdns>:<port>/healthz` (real HTTPS through the serve
front door, not loopback).

- Retry with backoff for up to ~60 s: the FIRST serve on a node can trigger Lets
  Encrypt cert issuance, which takes seconds.
- Only when the probe answers does GatewayClient register. The "register a lie, flap
  forever" failure mode becomes impossible.
- On verify failure after retries: same loud-stop path as Workstream 1 (precise error
  on the machine that can act on it, no registration).
- Re-verify opportunistically on the re-assert timer; if verification regresses,
  unregister is NOT needed (the Gateway's circuit/evict already handles a
  went-dark endpoint) but log the regression locally.

## Workstream 4: shrink the Gateway provisioner to one owner per mapping

- Directors own their own port mappings (new builds). The Gateway provisioner keeps:
  the 443 front door, and the orphan sweep / per-Director mapping for LOCAL Directors
  as a back-compat layer for old-build Directors during transition.
- The sweep must NOT remove a mapping a live local new-build Director just asserted -
  current logic already keys on "live local Director ports", which stays correct since
  desired state is identical whether the Gateway or the Director asserted it. Verify
  with a test.
- Once the whole fleet is on self-serving builds, optionally retire the Gateway's
  per-Director mapping duty (follow-up, not this change).

## Workstream 5: honest diagnostics end to end

- **Director side:** Director status (served HTML + /healthz payload + Settings) shows
  remote reachability state: OK (verified <when>) / FAILED (<exact reason: no
  tailscale.exe | logged out | serve error text | verify timeout>).
- **Gateway/Cockpit side:** replace the generic "unreachable (timeout; cooling down)"
  with actionable truth. Distinguish two cases in the machineErrors envelope:
  - never answered since registration -> "SORENLAPTOP registered but its endpoint never
    answered - check Tailscale Serve / Director log on that machine"
  - was reachable, went dark -> current timeout wording is fine.
  The registry already has FirstUnreachableAt and RecordReachable; add a
  "was ever reachable" bit per entry.
- **Log-noise fix (found in #197):** DirectorRegistry.RecordUnreachable logs
  "circuit OPEN after N failures" on EVERY failure >= threshold; log only on the
  closed->open transition.

## Workstream 6: installer / setup preflight

- Workstation-role setup (and the Settings gateway-connect flow) gains a Tailscale
  preflight: tailscale.exe present -> daemon running -> logged in -> MagicDNS name
  resolves. Each failure produces the exact remediation step (winget install
  tailscale.Tailscale / tailscale up / login). Detection only - the installer does not
  install Tailscale itself (explicit user action, keep the installer honest).
- Docs: docs/public deployment page gets the 3-step story (Tailscale -> cc-director ->
  gateway.url) and the troubleshooting table keyed off the Workstream 5 statuses.

## Workstream 7: tests + rollout

Tests:
- Unit: self-provisioner lifecycle via the CLI seam (assert on start, re-assert on
  timer, off on shutdown, loud-stop on CLI failure); GatewayClient skips registration
  until verify passes; registry "was ever reachable" wording; mutex serialization
  (two fake callers, interleaving).
- Integration (manual/QA): the laptop is the real-world fixture. Acceptance = the
  target end state above, plus: kill tailscale on the laptop -> Director logs the
  precise error and stops registering; restart tailscale -> self-heals within one
  reconcile tick.

Rollout:
1. Land behind normal release (no flag needed - on machines where the Gateway already
   maps local Directors the command is idempotent and now mutex-serialized).
2. Release vNext; laptop updates via auto-update (or fresh install - we have access).
3. Verify acceptance on SORENLAPTOP; then remove the manual serve mapping if one was
   added as an interim workaround, and watch one full day of Gateway logs for flap
   loops (there should be none).
4. Follow-up issue: retire Gateway per-Director mapping once fleet is current.

## Explicitly rejected

- **Reverse tunnel (Option B):** all traffic multiplexed over one outbound WebSocket.
  Architecturally clean ("pure ping-in") but re-plumbs every fan-out call AND the
  direct Cockpit->Director terminal stream through a Gateway relay - a large rework
  that discards what Tailscale already provides. Documented as the long-term option
  if non-tailnet machines ever must join the fleet.
- **Binding Kestrel to the Tailscale interface / advertising http://100.x.y.z:port:**
  violates the locked loopback-only + HTTPS-via-serve trust design and additionally
  hits the Windows inbound firewall.
- **Gateway provisioning remote machines:** impossible by mechanism - a serve mapping
  proxies to localhost on the machine that runs the CLI.
