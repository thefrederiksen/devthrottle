# Gateway Tray App - v2: the tray app becomes THE Gateway (service retired)

**Status:** v2 PLAN agreed 2026-06-05 (Soren + agent), not built.
**History:** v1 (2026-05-24, below this was originally written) built the tray app
as `CcDirector.GatewayApp` (#140). The first-install work (2026-06-01) then moved
the shipped Gateway to a native LocalSystem Windows service (first-install.md
decision D1) and the tray app was shelved, unshipped. v2 reverses that: the tray
app is promoted to the one shipped Gateway host and the service retires.
**Supersedes:** decision D1 in docs/plans/first-install.md.
**Blocks:** the Gateway-hosted Agent Brain and the Gateway Turn Brief agent
(docs/plans/agent-brain.md follow-up; docs/architecture/wingman/TURN_BRIEFING.md;
handover #173). Those need claude.exe running AS THE USER - this switch is what
makes that true.

---

## 1. Why the service must go (decided, with reasoning)

The Gateway is becoming the smart layer of the fleet: it will host a warm headless
claude session (the Agent Brain) that stamps a Turn Brief for every turn. That
claude must authenticate with Soren's Max OAuth credentials, which live in
`%USERPROFILE%\.claude` - the USER's profile.

The shipped Gateway runs as `cc-gateway-service`, a LocalSystem service
(`GatewayServiceCommands.cs: obj= LocalSystem`). LocalSystem has no claude
credentials and lives in session 0 - the exact unverified context issue #172
deferred. Alternatives weighed 2026-06-05:

- **Service under Soren's account:** requires the Windows password at install;
  a later password change makes the service silently fail at next start (logon
  failure) until re-entered. Rejected: silent time bomb.
- **LocalSystem + redirecting claude's config dir to the user profile:**
  fallback-flavored hack (ownership, ACLs, divergent transcript paths). Rejected.
- **Tray app in the user session, started at logon:** claude runs as Soren with
  zero ceremony. The fleet is already logon-bound (Directors are desktop apps),
  so the service's only advantage - running before logon - protects a state
  where everything the Gateway serves is dead anyway. **Chosen.**

Accepted cost: after a reboot the Gateway is down until logon. Mitigation:
Windows' "use my sign-in info to finish setting up after an update" relaunches
startup apps after patch reboots.

## 2. What already exists (the head start)

`src/CcDirector.GatewayApp/` (#140, builds against current tree, NOT shipped):

- Avalonia tray app hosting `GatewayHost` in-process - so Cockpit supervision,
  Director registry, comm queue, recordings, every REST endpoint come for free.
- Tray menu: status line, Open Dashboard (tailnet), Open Cockpit (tailnet),
  Open Logs Folder, Restart Gateway, Quit. Tailnet-only URLs by design.
- `Autostart.cs`: idempotent HKCU Run-key registration (per-user, correct scope).
- Session-scoped single-instance mutex; port-conflict diagnosis (distinguishes
  "another gateway", "foreign app on port", "bind failed").
- `Assets/tray.ico` from #140.

What ships today instead: `CcDirector.Gateway` console exe registered via
`sc create ... obj= LocalSystem` by `GatewayServiceInstaller`, self-updating via
`GatewaySelfUpdate` with `sc stop/start` callbacks.

## 3. Phase 1 - Promote the tray app to THE Gateway host

1. **Revive and verify.** Build `CcDirector.GatewayApp` against current
   `GatewayHost`; run it; confirm Cockpit supervision (7470), /healthz, Director
   registration, comm queue, recordings behave identically to the service.
   (Note: full-solution builds are currently blocked by a running dev Gateway
   holding bin\ file locks - Soren closes that process first.)
2. **New identity.** New Gateway application icon + matching tray icon
   (VisualStyle.md). The Gateway gets its own look, distinct from the Director.
3. **Settings window.** Avalonia window from the tray menu ("Settings...").
   v1 deliberately small:
   - Status panel: lockstep version (+sha), port, uptime, registered Director
     count, Cockpit state.
   - Autostart toggle (wraps `Autostart.EnsureRegistered`/`Unregister`).
   - Open logs / open config shortcuts.
   Laid out to grow tabs later - Agent Brain controls land here next.
4. **CLAUDE.md rules throughout:** responsive UI (<100ms feedback, async I/O),
   enterprise logging on every public method, no fallbacks, tests.

## 4. Phase 2 - Installer / updater switch

**No backward compatibility (Soren, 2026-06-05): the service Gateway has no real
install base. The installer simply switches to the tray app; service code is
DELETED, not migrated.** The only service instance anywhere is the dev one on
soren-north - removed by hand once (`sc stop` + `sc delete cc-gateway-service`,
delete the old install dir), not by product code.

1. **Install location (D-TRAY-1, locked):** `%LOCALAPPDATA%\cc-director\gateway\`.
   Install AND self-update run unelevated - same model as the Director. The
   elevated `setup-cli install --role gateway` requirement disappears entirely.
2. **Engine:** `GatewayServiceInstaller` -> `GatewayTrayInstaller`:
   copy exe -> write HKCU Run key -> start tray app -> /healthz probe.
   `GatewayServiceCommands` (sc.exe builder) is deleted.
3. **Self-update rework.** Keep staged-copy + .old rollback + health-probe +
   bad-version pinning (`GatewaySelfUpdate`); swap service callbacks for process
   ones: detached updater (`--apply-update`) asks the running tray app to exit
   gracefully (shutdown endpoint; the single-instance mutex release confirms
   exit), swaps the exe, relaunches, probes /healthz, rolls back + pins on
   failure.
4. **release.yml:** the gateway asset becomes the published `CcDirector.GatewayApp`
   WinExe. Keep the existing asset name (`cc-director-gateway-win-x64.exe`) -
   with no install base there is no in-the-wild service updater to protect, and
   reusing the name means ComponentRegistry and create-release barely change.
5. **Uninstaller:** quit tray app, remove Run key, delete install dir.
6. **Reference sweep.** 23 files reference `cc-gateway-service`; each is updated
   or deleted. Notables:
   - `scripts/deploy-cockpit.ps1` (Stop-Service) -> tray-model deploy (graceful
     stop via REST/process, swap Cockpit zip, restart).
   - `scripts/install-gateway-service.ps1`, `grant-service-control.ps1`,
     `redeploy-gateway.ps1`, `verify-gateway.ps1`, `test-gateway-selfupdate.ps1`
     -> rewritten for the tray model or deleted.
   - `CcDirector.Avalonia/MainWindow.axaml.cs` error text ("Is the
     cc-gateway-service running...") -> tray wording.
   - `CcDirector.Gateway/Program.cs`: console mode stays for dev (`dotnet run`);
     the `AddWindowsService` branch and the `--apply-service-update` path are
     deleted now (no installed service will ever invoke them).
   - Docs: INSTALLATION.md, first-install.md (mark D1 superseded),
     auto-update-scope.md, install-autoupdate-test-procedure.md.
7. **Tests:** `GatewayAndPinsTests` sc-command expectations replaced by
   tray-installer tests; self-update tests get process-based fakes; engine +
   Gateway.Tests green.

## 5. Phase 3 - Verify live

1. On soren-north: hand-remove the dev service (`sc stop` + `sc delete
   cc-gateway-service`, delete old install dir), run the unelevated tray
   install; verify from the PHONE over the tailnet (Cockpit 7470 + gateway
   front door). No localhost verification.
2. Logoff/logon: tray auto-starts, gateway up, Cockpit supervised.
3. Self-update cycle: stage old, update to new, health-verify; force a bad build
   to prove rollback + pinning still work in the process model.
4. Tray menu Quit/Restart behave; second launch hits the mutex, logs, exits.
5. Enable "use my sign-in info to finish setting up after an update" on
   soren-north so patch reboots come back without manual logon.

## 6. Out of scope (next plans, in order)

1. AgentBrain rework: REST-to-a-Director client -> in-process claude hosting
   (ConPty + direct JSONL) so the Gateway owns its warm session outright.
   Depends on this plan.
2. Gateway Turn Brief agent on the reworked brain; Director push doorbell +
   heartbeat; rawState/assessedState two-owner model (TURN_BRIEFING.md, #173).
3. Director-side brief pipeline deletion.

## 7. Decisions log

- **D-TRAY-1 (locked 2026-06-05):** gateway install dir is
  `%LOCALAPPDATA%\cc-director\gateway` - fully unelevated lifecycle.
- **No backward compatibility (locked 2026-06-05):** no install base exists; the
  installer switches outright, service code is deleted, asset name is reused.
