# Auto-Update - Implementation Scope

Status: SCOPE (not built). Builds on the decisions in docs/plans/install-autoupdate.md
(D1-D10) and the install/uninstall work shipped in v0.3.6/v0.3.7.

> SUPERSEDED IN PART (2026-06-05): the Gateway is no longer a Windows service.
> Wherever this document says `sc stop/start cc-gateway-service`, the shipped
> mechanism is now process-based (POST /shutdown -> swap -> relaunch) on the
> Gateway TRAY app. See docs/plans/gateway-tray-app.md (v2). The staged-copy,
> health-check, auto-rollback, and pin design (DA-1) is unchanged.

Goal: every installed component silently moves to the newest published release on its own,
per-component, without killing live work - so a user never has to re-run an installer to
get a new version.

---

## 1. What already exists (do NOT rebuild)

- Director self-update is BUILT and working. `CcDirector.Core/Update/UpdateService` checks
  GitHub `/releases/latest`, downloads + SHA-256 verifies, and stages; `UpdateInstaller` +
  `Program.cs --apply-update <target> <pid>` is a relauncher that swaps the locked Director exe
  on next start and keeps a `.old` backup. Gated by `-p:UpdaterEnabled=true`. Checks once at
  launch (2s delay); applies at next restart.
- Per-asset versions in the manifest are DONE (each asset in release-manifest.json carries its
  own `version`; UpdatePlanner already compares per-component). Decision D4 is satisfied.
- Engine apply-mechanics exist: ReleaseSource (fetch/parse/download), UpdatePlanner
  (Install/Update/UpToDate/MissingAsset/Pinned per component), UpdateRunner (download -> verify ->
  swap), InstallSwapper (atomic swap + `.old`), PinStore (skip a bad version),
  GatewayServiceCommands (sc.exe stop/start), CockpitSupervisor (restart loop).
- Gateway runs as LocalSystem -> it can self-update machine components and restart its own
  service with NO UAC after first install (D7).

## 2. What's missing (the actual feature)

- A periodic TRIGGER. Today only the Director checks, and only once at launch. Nothing polls.
- Gateway + Cockpit + tools have ZERO update logic.
- Swapping a LOCKED running binary: InstallSwapper.Place uses File.Replace and assumes the
  target is not running. Works for tools (not running); does NOT work for the running Gateway
  exe. The Director already solves this with its relauncher; the Gateway needs an equivalent.
- Reliable installed-version for TOOLS. PyInstaller tool exes may carry no readable file version,
  so UpdatePlanner can't tell if a tool is stale. Auto-updating tools needs trustworthy installed
  state (see Section 6).
- A config toggle (enable/disable, cadence, channel).

---

## 3. Ownership model (who updates what)

Two resident orchestrators, split by trust tier - matches D6 ("resident apps orchestrate"):

| Component | Locked while running? | Orchestrator | Mechanism |
|---|---|---|---|
| Director (per-user exe) | yes | the Director itself | EXISTING: stage on launch, relaunch-swap on restart |
| Tools (per-user bin exes) | no | the Director (per-user, no admin) | direct swap via UpdateRunner (no relaunch needed) |
| Cockpit (machine, supervised child) | yes (child) | the Gateway service | stop child -> extract new zip -> relaunch child (CockpitSupervisor hook) |
| Gateway (machine service exe) | yes | the Gateway service (LocalSystem) | detached self-update helper: stop service -> swap exe -> start service |

Rationale: the Director owns the per-user tier (no admin, it already self-updates); the Gateway
owns the machine tier (it's LocalSystem and already supervises the Cockpit). No component updates
across a trust boundary.

---

## 4. The hard parts

### 4a. Gateway self-update (locked service exe)
The service can't replace its own running exe in-process. Pattern (mirrors the Director's
`--apply-update`): when the Gateway's update check finds a newer gateway asset, it stages the new
exe, then spawns a DETACHED helper running as LocalSystem (the gateway exe itself in a new
`--apply-service-update` mode, or a tiny copied helper) that:
1. `sc stop cc-gateway-service` (and waits for the exe to unlock),
2. swaps `cc-director-gateway.exe` (InstallSwapper.Place, keeps `.old`),
3. `sc start cc-gateway-service`,
4. waits for `/healthz` on 7878.
No UAC (LocalSystem). The helper must outlive the stopped service, so it runs detached, not as a
child of the service process.

### 4b. Cockpit in-place update (easier - no self-update)
The Gateway supervises the Cockpit child, so it can update it WITHOUT stopping itself. In the
CockpitSupervisor loop, before (re)launching: if a newer Cockpit zip is staged, stop the child,
extract over CockpitDir (reuse CockpitPackage.ExtractAsync), then launch the new build. This is
the clean hook and needs no relauncher.

### 4c. Tools (easiest)
Tools aren't long-lived processes, so UpdateRunner can swap them directly. The Director's update
loop plans tools against the manifest and applies any that are behind. Edge case: a tool exe in
use at the moment of swap - retry / defer to next cycle.

---

## 5. Trigger + cadence + config

- Director: keep the on-launch check; ADD a low-frequency timer (e.g. every few hours) so a
  long-running Director still notices releases. Director loop also covers tools.
- Gateway: ADD a background update loop in GatewayWorker (it already hosts long-lived loops) that
  checks the manifest on the same cadence and drives the Cockpit + self-update paths.
- Config (new keys in the existing config.json): `autoUpdate.enabled` (default true),
  `autoUpdate.intervalHours`, `autoUpdate.channel` (latest | prerelease). Single-user tailnet, so
  NO auth/gating around any of this.
- Silent (D5): no banners for tools/Cockpit/Gateway; they apply at the next safe moment. The
  Director keeps its existing "restart ready" affordance for itself.

---

## 6. Installed-version tracking (gating item for tools)

UpdatePlanner needs a trustworthy installed version per component. The Director (.NET) has an
assembly version; PyInstaller tools may not. Add an installed-state manifest written at
install/update time - `%LOCALAPPDATA%\cc-director\config\setup\installed.json` mapping
component-id -> version actually placed. The planner reads this instead of (or in addition to)
file version info, so per-component update decisions are reliable for every component including
tools. This also makes `status` honest (today tools show "version unknown").

---

## 7. Safety - and one decision to revisit

- Keep SHA-256 verify (have) and `.old` backups (have).
- D8 says "keep backups + manual rollback, no auto-rollback." That's fine for the Director and
  tools. BUT the always-on Gateway service is different: if a bad gateway build won't start, the
  machine is left with a DEAD service and no Cockpit, with no human at the console. RECOMMEND
  revisiting D8 for the service ONLY: after a Gateway/Cockpit self-update, health-check the new
  build and AUTO-ROLLBACK to `.old` (+ pin the bad version) if it doesn't come up. This is the
  one place auto-rollback earns its keep. (Open decision DA-1.)
- Pin-on-failure: a version that fails health twice gets pinned (PinStore) so the loop doesn't
  reinstall it every cycle.

---

## 8. Reconcile the two update codepaths

There are currently TWO update implementations: the Director's `CcDirector.Core/Update`
(UpdateService/UpdateInstaller) and the setup engine's UpdateRunner/ReleaseSource. They overlap.
Decision DA-2: either (a) keep the Director's for the Director-self case and use the setup engine
for everything else, or (b) consolidate the Director onto the setup engine so there is ONE update
brain. (b) is cleaner long-term but is a refactor of working code; (a) is lower-risk now. Lean (a)
for the first cut, with a note to converge later.

---

## 9. Phasing

1. Installed-state manifest (Section 6) - unblocks reliable per-component decisions, esp. tools.
2. Tools auto-update via the Director's existing loop (lowest risk; no locked-file problem).
3. Cockpit in-place update via the CockpitSupervisor hook (Gateway owns it; no self-restart).
4. Gateway background update loop + self-update helper (Section 4a) + health-check/auto-rollback
   (DA-1). Highest risk; do last, behind the health gate.
5. Config toggle + cadence; Director periodic timer.
6. End-to-end test: publish vN+1, confirm a running Workstation and a running Gateway both move
   to it silently without losing live work, and that a deliberately-bad gateway build rolls back.

## 10. Decisions

- DA-1: RESOLVED - auto-rollback + health-check for the Gateway/Cockpit self-update (overrides D8
  for the service only). After a Gateway/Cockpit self-update, health-check the new build and
  auto-rollback to `.old` + pin the bad version if it does not come up. (Director/tools keep D8's
  manual-rollback.)
- DA-2: RESOLVED - two update engines for now: the Director keeps CcDirector.Core/Update for its
  own self-update; the setup engine drives everything else (tools, Gateway, Cockpit). Converge to
  one engine later (tracked, not now).
- DA-3 (open): cadence default (every N hours) and whether to also check on tailnet reconnect / wake.
- DA-4: do tools get updated by the Director (per-user) or could the Gateway also refresh a
  machine-wide tool copy? RECOMMEND Director-only (per-user ownership) to avoid cross-tier writes.

## 11. Out of scope

- Code signing / SmartScreen (D10 - stays unsigned + documented).
- Delta/partial downloads (full asset per component is fine).
- macOS auto-update beyond the Director's existing path.
- Any auth/security gating (single-user tailnet).
