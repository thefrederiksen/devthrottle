# Gateway Tray App - Implementation QA Report

**Date:** 2026-06-05
**Plan:** docs/plans/gateway-tray-app.md (v2)
**Scope:** The Gateway converted from a LocalSystem Windows service to a per-user
tray application. Service code deleted (no backward compatibility - no install
base). Everything per-user under `%LOCALAPPDATA%\cc-director`; no elevation
anywhere in the lifecycle.

---

## What was built

| Area | Change |
|---|---|
| `CcDirector.GatewayApp` | Promoted to THE Gateway (`AssemblyName cc-director-gateway`). Absorbs Cockpit supervision + the periodic self-update loop (managed mode). New `--managed`, `--settings` flags; port-scoped single-instance mutex. |
| New icon | New Gateway identity: accent-blue hub-and-spokes app icon + tray icon (replaces the Director-green chevron family). |
| Settings window | Avalonia, VisualStyle dark theme: live status (state/port/uptime/director count/Cockpit reachability/mode), autostart toggle (HKCU Run key), Open Logs / Open Config. Opens from tray menu, left-click, or `--settings`. |
| `GatewayHost` | `OnShutdownRequested` hook + `POST /shutdown` endpoint (answers 200, then hands off; 501 when no handler). |
| Self-update | `--apply-update` helper mode in the tray exe: POST /shutdown -> exe-writability wait (the real exit barrier) -> swap with `.old` backup -> relaunch (`UseShellExecute=true`, no stdio inheritance) -> /healthz -> auto-rollback + version pin on failure. `GatewaySelfUpdate` delegates renamed (process, not service). |
| Engine | `GatewayTrayInstaller` replaces `GatewayServiceInstaller` (Cockpit extract -> OPENAI_API_KEY user-env check/write -> start tray `--managed` -> health + autostart verification). `GatewayAutostart` (shared HKCU Run-key helper). `GatewayServiceCommands` DELETED. `InstallLayout` reduced to ONE per-user root (ProgramFiles/ProgramData roots deleted). Uninstaller: stops installed tray/Cockpit (path-scoped), removes Run key + dirs. |
| CLI / WPF wizard | Gateway role: no elevation, no UAC; preflight = OPENAI_API_KEY only. `GatewayTrayLauncher` replaces the elevated handoff. Welcome step copy updated. |
| `CcDirector.Gateway` | Now the gateway library + DEV console host only (`AssemblyName CcDirector.Gateway`); `AddWindowsService` + `--apply-service-update` deleted; update loop removed (tray owns it). |
| release.yml | `build-gateway-win` publishes `CcDirector.GatewayApp`; asset name unchanged (`cc-director-gateway-win-x64.exe`). Local single-file publish verified (33 MB framework-dependent WinExe). |
| Scripts | `deploy-cockpit.ps1`, `redeploy-gateway.ps1`, `verify-gateway.ps1`, `test-gateway-selfupdate.ps1` rewritten for the tray model. `install-gateway-service.ps1`, `grant-service-control.ps1` DELETED. |
| Docs | INSTALLATION.md (master spec) rewritten to the one-root model; superseded banners on auto-update-scope.md, install-autoupdate.md, install-autoupdate-test-procedure.md. |

## Test results

| # | Check | Result |
|---|---|---|
| 1 | Engine tests (incl. new GatewayAutostart/TrayInstaller, single-root layout, uninstaller) | PASS 107/107 |
| 2 | Gateway.Tests on HEAD + only-these-changes (isolated worktree graft) | PASS 152/153 - the 1 failure (`ChatEndpointTests.RootPath_serves_cards_director`) also fails on PURE HEAD; pre-existing, unrelated |
| 3 | GatewayApp / CLI / WPF wizard / Gateway.Tests builds (Release) | PASS, 0 warnings |
| 4 | Single-file publish (release.yml command, run locally) | PASS - `cc-director-gateway.exe` produced |
| 5 | Live run (port 7899, `CC_GATEWAY_NO_TAILSCALE=1`) | PASS - /healthz OK, saw the live fleet (3 Directors, 10 sessions) |
| 6 | Settings window live | PASS - screenshot `settings-window.png`: version+sha, state, port, uptime, Director count, Cockpit reachability, mode, autostart toggle |
| 7 | `POST /shutdown` graceful exit | PASS - `{"shuttingDown":true}`, process exited |
| 8 | Self-update happy path (live, throwaway instance) | PASS - outcome=Updated: stop -> swap (`.old` kept) -> relaunch -> healthy on 9.9.9 |
| 9 | Self-update rollback path (live, dead health port) | PASS - exit=1 (RolledBack), instance healthy on the real port afterward |

**Working-tree note:** the shared checkout also carries ANOTHER session's in-flight
work (Director registration/heartbeat refactor: `GatewayClient.cs`,
`InstanceRegistration.cs`, rewritten Gateway test files). That session's suite
currently fails 46 tests on `OPENAI_API_KEY` in the full parallel run - confirmed
NOT caused by this work via the isolated-graft run in check #2.

## Known limitations / follow-ups

- The rollback live test simulates a bad build via a dead health port; the
  rolled-back file swap under a crashed-at-startup build is covered by the
  GatewaySelfUpdateTests unit fakes.
- The live machine (machine-a) still runs the OLD Program Files + service
  install. Migration is manual (one elevated step), see below.
- `docs/testing/install-autoupdate-test-procedure.md` needs a full rewrite on the
  next clean-machine QA pass (banner added).

## Migration of machine-a: DONE (2026-06-05)

Executed live, user-approved UAC for the one elevated step:

1. Elevated: `sc stop` + `sc delete cc-gateway-service`; working Cockpit build
   copied from Program Files into `%LOCALAPPDATA%\cc-director\cockpit`;
   `C:\Program Files\CC Director` removed.
2. Unelevated: published tray exe (built from HEAD f1daf1c + this work in an
   isolated worktree, since the shared tree's Core was mid-edit by another
   session) placed at `%LOCALAPPDATA%\cc-director\gateway`, started `--managed`.
3. `scripts/verify-gateway.ps1`: **6/6 PASS** - exe paths, autostart Run key
   (`--managed`), tray process, /healthz (live fleet: 3 Directors), Cockpit
   answering on 7470. Tailnet URLs resolved (machine-a.tail0123.ts.net).

Leftover: `C:\ProgramData\cc-director` (old service logs) is ACL-locked and
needs one elevated delete if/when desired. Remaining user step: log off/on once
and re-run verify-gateway.ps1 to confirm logon autostart; check the Cockpit from
the phone.

---

## One URL: Cockpit behind the front door (same day, also DEPLOYED)

Plan: docs/plans/one-url-cockpit.md. Implemented and live:

| # | Check | Result |
|---|---|---|
| 1 | Fallback proxy (YARP IHttpForwarder, `{*path}` pattern) Gateway -> loopback Cockpit; explicit REST endpoints keep precedence | PASS - /healthz + /sessions answer from the Gateway; everything else proxies |
| 2 | Blazor circuit (WebSocket) through the proxy | PASS - full Cockpit home live through :7899 and the tailnet front door (screenshot `cockpit-via-proxy.png`) |
| 3 | Cards dashboard -> Blazor `/fleet` page (machine groups, wingman status dots, rename, Open session, unreachable banner) | PASS - live fleet data, styled (screenshot `fleet-page.png`) |
| 4 | exes/transcripts/dictionary moved to Cockpit-served pages (same paths; same-origin REST through the front door) | PASS (screenshot `transcripts-via-proxy.png`) |
| 5 | Gateway serves NO UI: directory/manager/api pages deleted; /voice redirect moved | PASS - root test asserts the proxy fallback |
| 6 | Cockpit-down interstitial ("Cockpit starting...", auto-refresh, 503) | PASS - verified by killing the test Cockpit |
| 7 | Tailscale: front door only; legacy :7470 mapping removed (provisioner cleanup) | PASS - `tailscale serve status` shows only 443 -> 7878 for cc-director |
| 8 | Tests | Engine 107/107; GatewayDirectoryRegistration + CockpitParity 23/23 (incl. the new root-proxy test with an injectable dead cockpit port) |
| 9 | LIVE deploy | PASS - new gateway exe + cockpit publish deployed to `%LOCALAPPDATA%`; `https://machine-a.tail0123.ts.net/{,fleet,exes,transcripts,healthz}` all 200 over the tailnet |

Gotchas recorded for posterity:
- `MapFallback()` without a pattern uses `{*path:nonfile}` - it silently skips
  file-like paths, 404ing every CSS/JS asset. Use `MapFallback("{*path}", ...)`.
- Scoped CSS (`.razor.css`) needs the `<AssemblyName>.styles.css` link in
  App.razor - this project had never used CSS isolation before.
- `WebRootFileProvider`, not physical wwwroot paths, for files served out of
  wwwroot (works across dev / bin-run / publish).

Phone bookmark change: the Cockpit is now just `https://machine-a.tail0123.ts.net/`
(the :7470 bookmark is dead); the Cards view lives at `/fleet`.
