# Plan: Centralize accounts, sign-in, and telemetry on the Gateway

**Status:** Proposed (planning)
**Date:** 2026-06-22
**Owner:** Soren

## Goal

Invert today's model. Right now **each Director** signs in (browser loopback to
`devthrottle.com`), stores its own credential, and posts telemetry straight to the cloud; the
**Gateway** is only an internal fleet coordinator with no account concept and no cloud egress.

We want the **Gateway to become the account authority and the single egress to the outside world.**
Directors become purely internal clients: they connect to a gateway, and they report events
(startup, sign-in, usage) **through** the gateway, which is the only component that talks to
`devthrottle.com`.

> One line: **the Gateway is the outside world. Directors only ever talk to the Gateway.**

## Decisions (locked)

1. **A gateway is mandatory for every install** — including a solo user on one laptop (they install
   the Gateway role locally: gateway + director on one box). The account **always** lives on the
   gateway.
2. **Forced sign-in happens at the Gateway's first launch** (not inside the installer). The installer
   just installs; the gateway prompts sign-in on first run via the browser loopback flow.
3. **When the gateway is unreachable, Directors run degraded on a cached okay** — they keep working if
   previously authorized, revalidate when the gateway returns, and queue telemetry meanwhile.
4. **The telemetry consent (opt-out) toggle is centralized on the gateway** — one fleet-wide setting,
   managed where the account lives.

## Current state (grounded)

| Concern | Today | File(s) |
|---|---|---|
| Account / sign-in | Director-side; browser loopback to `devthrottle.com/signin` | `CcDirector.Core/Account/FirstRunLoginCoordinator.cs`, `AccountGatePolicy.cs`, `DevThrottleAccountService.cs` |
| Credential | Per-machine DPAPI blob | `WindowsProtectedTokenStore.cs` → `%LOCALAPPDATA%\cc-director\config\director\devthrottle-credential.bin` |
| Startup gate | Blocks Director until a local credential exists | `AccountGatePolicy.Decide()`, `CcDirector.Avalonia/App.axaml.cs` |
| Login telemetry | Director → `devthrottle.com/api/v1/telemetry/login` directly | `DevThrottleLoginTelemetryReporter.cs` |
| Startup telemetry | **None** | — |
| Gateway | Tray app + Kestrel; no account, no cloud egress; Directors register/heartbeat/pair | `CcDirector.Gateway/`, `CcDirector.GatewayApp/`, `Api/GatewayEndpoints.cs`, `Api/DeviceEnrollmentEndpoint.cs` |
| Director↔Gateway | `POST /directors/register`, heartbeats, device pairing codes, optional `gateway.token` | `CcDirector.ControlApi/GatewayClient.cs`, `GatewayConfig.cs` |
| Installer | 5-step wizard; Workstation vs Gateway role; **no sign-in** | `tools/cc-director-setup/`, `tools/cc-director-setup-engine/InstallRole.cs` |
| Consent toggle | Per-machine, Director Settings → Account | `TelemetrySettings.cs`, `SettingsDialog.axaml` |

## Target architecture

```
                         ┌─────────────────────────────────────────┐
   devthrottle.com  ◄────┤  GATEWAY  (the only thing facing cloud)  │
   /signin               │  - holds the DevThrottle account token   │
   /api/v1/telemetry/*   │  - browser sign-in on first launch       │
                         │  - token refresh                         │
                         │  - telemetry relay + queue               │
                         │  - centralized consent toggle            │
                         └───────▲───────────────▲──────────────────┘
                                 │ device key     │ device key
                        report events       report events
                                 │                │
                        ┌────────┴───┐     ┌──────┴─────┐
                        │ Director A │     │ Director B │  (no cloud calls,
                        │ (reports   │     │ (reports   │   no local account)
                        │  startup,  │     │  startup,  │
                        │  usage)    │     │  usage)    │
                        └────────────┘     └────────────┘
```

- **Directors never call `devthrottle.com`.** They authenticate to the gateway with the existing
  per-device key and POST events to gateway telemetry endpoints.
- **The gateway** owns the credential, does sign-in/refresh, attaches the account Bearer token, and
  forwards events to the cloud. It queues events while the cloud is unreachable.

---

## Phased plan

Phased to de-risk: Phase 1 makes the gateway the egress **without** moving auth (shippable on its
own). Phase 2 moves the account onto the gateway. Phase 3 makes the gateway mandatory and relocates
the installer/settings/consent surfaces.

### Phase 1 — Gateway becomes the telemetry egress (no auth move yet)

**Outcome:** Directors stop calling `devthrottle.com` directly; the gateway relays to the cloud.
Auth still lives on the Director for now (the Director hands its token to the gateway per request —
a transitional step Phase 2 removes).

- **Gateway** — add inbound telemetry endpoints, authenticated by the existing gateway token/device
  key, that forward to the cloud:
  - `POST /telemetry/login` → forwards to `devthrottle.com/api/v1/telemetry/login`
  - `POST /telemetry/director-startup` → forwards to the backend startup endpoint *(backend
    dependency — see Open questions)*
  - A small **outbound queue** so a cloud outage retries rather than drops.
  - Files: new `CcDirector.Gateway/Api/TelemetryEndpoints.cs`, a `CloudTelemetryForwarder`.
- **Director** — re-point `DevThrottleLoginTelemetryReporter` from `devthrottle.com` to the
  configured gateway; add a **startup event** fired on launch (best-effort, fire-and-forget, never
  blocks). Files: `DevThrottleLoginTelemetryReporter.cs` (retarget), new startup reporter call in
  `App.axaml.cs`.
- **Acceptance:** with a gateway configured, a Director login and a Director startup both appear in
  the backend, having transited the gateway; no Director makes a direct `devthrottle.com` call
  (verify by network trace / logs). Cloud-down → events queue and flush on recovery.

### Phase 2 — Move the account and sign-in onto the Gateway

**Outcome:** the gateway holds the credential and is the sole holder of the cloud token. Directors
stop holding any credential.

- **Gateway** — gains a credential service (relocate `DevThrottleAccountService`,
  `WindowsProtectedTokenStore`, `JwtAccessTokenValidator`, refresh, and `FirstRunLoginCoordinator`
  into the gateway). On **first launch with no credential**, the gateway runs the browser loopback
  sign-in (tray/Cockpit surface) and stores the token (DPAPI, gateway-host user). It now forwards
  telemetry with **its own** token; Directors no longer send tokens.
- **Director** — remove the browser sign-in path. The startup gate becomes **"am I connected to a
  gateway that is signed in?"**: the Director asks the gateway `GET /account/status`; **degraded
  mode** (decision #3) caches the last okay with a timestamp so a gateway outage doesn't stop work,
  and revalidates on reconnect. Drop the per-Director credential blob.
- **Token refresh** (currently stubbed `BackendUnavailableTokenRefresher`) becomes the gateway's job.
- **Acceptance:** a freshly installed gateway prompts sign-in once and stores the token; Directors
  with no local credential run as long as the gateway reports signed-in; pulling the gateway offline
  leaves Directors running (degraded) and they recover on reconnect.

### Phase 3 — Mandatory gateway, installer, settings & consent relocation, cleanup

**Outcome:** the model is fully centralized and the old per-Director surfaces are gone.

- **Installer** — every install ensures a gateway: a **solo install creates a local Gateway role**
  (gateway + director on one box); a **Workstation install requires pairing** to a gateway (URL +
  pairing code) before finishing. No sign-in in the installer itself (decision #2). Files:
  `tools/cc-director-setup/`, `InstallRole.cs`, `InstalledRoleDetector.cs`.
- **Director gate** — the gateway connection becomes **mandatory** (replace the optional gateway URL +
  onboarding with a required pairing gate). Files: `OnboardingModel.cs`, `App.axaml.cs`,
  `AccountGateScreen` repurposed to a "connect to your gateway" screen.
- **Settings / UI** — move the **Account tab** (identity, logout, consent toggle) to the gateway
  (Cockpit / gateway settings). The Director shows read-only "Connected to gateway X, signed in as
  <account>". Files: `SettingsDialog.axaml(.cs)`, Cockpit settings.
- **Consent** — centralize the opt-out on the gateway (decision #4); Directors read it from the
  gateway and gate usage telemetry accordingly. The **first-run consent screen** moves to the gateway
  first-launch (shown once), not per-Director.
- **Cleanup** — remove the Director-side credential blob, the direct `devthrottle.com` calls, and the
  per-Director "sign in to DevThrottle" path.
- **Acceptance:** a clean install ends with a signed-in gateway and a Director that reached its main
  window via gateway pairing alone; the username/identity and consent toggle exist only on the
  gateway; no per-Director credential file is created.

---

## Impact on recent work (not wasted — relocated)

The per-Director pieces we just built **move to the gateway** rather than being deleted:
- `FirstRunLoginCoordinator` + `LoopbackLoginListener` (browser sign-in) → gateway first-launch.
- `DevThrottleAccountService` + token store + validator → gateway.
- The redesigned **sign-in screen** → becomes the gateway's sign-in surface; the Director's gate
  becomes "connect to gateway."
- The **first-run consent screen** + `TelemetrySettings` → centralized on the gateway.
- `DevThrottleLoginTelemetryReporter` → retargeted at the gateway (Phase 1), then the gateway's own
  cloud forwarder (Phase 2).
- The **local dev sign-in tool** (`tools/devthrottle-dev-signin`) still applies — it now stands in
  for the cloud the *gateway* talks to.

## Open questions / dependencies (mostly backend)

- **Backend startup-event endpoint:** `/api/v1/telemetry/login` exists; is there (or can the backend
  add) a Director-startup event endpoint, or do we model startup as another `source`/event on the
  existing endpoint? (Coordinate with `devthrottle_internal`.)
- **Token refresh endpoint:** still stubbed; Phase 2 needs the real refresh exchange, now owned by the
  gateway.
- **Gateway sign-in UX:** the gateway is a tray app — confirm the sign-in surface (tray menu action +
  status in Cockpit) and how a not-signed-in gateway communicates that to Directors.
- **Migration:** existing installs have per-Director credentials and gateway-optional configs. Define
  the upgrade path (ignore local blobs once a signed-in gateway is present; prompt to pair).
- **Solo install footprint:** confirm the solo "local Gateway role" is acceptable (an always-on tray
  gateway on every single-user machine).

## Risks

- **Single point of failure / outage blast radius** — mitigated by degraded-on-cached (decision #3)
  and the telemetry queue, but a gateway that can't sign in blocks *new* installs from completing.
- **Larger gateway surface** — the gateway gains auth + secrets + cloud egress; raises its security
  bar (token at rest, who can reach its endpoints).
- **Migration friction** for existing per-Director installs.

## Suggested sequencing

Phase 1 first (independently valuable, low risk, no auth change), then Phase 2, then Phase 3. Each
phase ends shippable. Detailed work-item breakdown to follow once this plan is approved.
