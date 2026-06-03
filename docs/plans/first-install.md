# First-Time Install of All Pieces (from a GitHub Release)

Status: PLAN. Not built. This is the single, end-to-end plan for getting a clean
Windows machine from "nothing installed" to "every CC Director component installed
and running" by downloading one published GitHub release.

Scope is FIRST-TIME INSTALL only. Auto-update (re-pulling newer per-component
versions later) is explicitly out of scope here and is tracked separately
(docs/plans/install-autoupdate.md). Where a first-install choice would block updates
later, it is called out, but no update behavior is built in this plan.

---

## 1. Goal and the two target outcomes

A user downloads a single bootstrap exe (`cc-director-setup-win-x64.exe`) from the
GitHub Releases page and runs it. They pick one of two roles and the installer does
the rest, pulling all binaries from that same release:

- WORKSTATION (no admin): Director app + all CLI tools, on PATH, with a Start Menu
  shortcut. This already works today.
- GATEWAY (admin once): everything in Workstation, PLUS the always-on
  `cc-gateway-service` Windows service and the Cockpit web UI it supervises on 7470.
  This is the path that does NOT work yet and is the bulk of this plan.

"All of the pieces" means the Gateway outcome. The primary machine is a Gateway.

---

## 2. What already works (do NOT rebuild)

Confirmed by reading the engine and CLI:

- GitHub release discovery + download. `ReleaseSource.FetchLatestAsync()` hits
  `api.github.com/repos/thefrederiksen/cc-director/releases/latest`, finds the
  `release-manifest.json` asset, parses it, and maps every asset name to its
  download URL. (tools/cc-director-setup-engine/ReleaseSource.cs:19-122)
- Download -> SHA-256 verify -> atomic swap with `.old` backup.
  (UpdateRunner.cs:54-103, InstallSwapper)
- First-install semantics: a component not present is planned as Install.
  (UpdatePlanner)
- Per-user layout: Director -> `%LOCALAPPDATA%\cc-director\app`, tools ->
  `%LOCALAPPDATA%\cc-director\bin`. (InstallLayout.cs)
- PATH registration (HKCU\Environment + WM_SETTINGCHANGE broadcast) and Start Menu
  shortcut creation. (PathManager.cs, ShortcutCreator.cs)
- CLI `install` end to end for Workstation. (cc-director-setup-cli Commands.cs)
- WPF wizard runs the engine for a Workstation install (download, PATH, shortcut).
  (tools/cc-director-setup/Services/EngineInstallRunner.cs)

So the Workstation outcome is essentially done. Everything below is about (a) making
the release actually contain all the pieces, and (b) making the Gateway outcome real.

---

## 3. The gaps that block "install all the pieces"

From the code audit, in priority order:

G1. The release does not contain all the pieces. release.yml ships only the Director,
    3 tools, the mac app, and the setup wizard. Gateway and Cockpit are not built;
    only 3 of ~26 shippable tools are built. (Phase 1 work, diff already drafted.)

G2. No service installation in the installer. NSSM registration of
    `cc-gateway-service` is 100% in `scripts/install-gateway-service.ps1`. Nothing in
    the engine, CLI, or WPF wizard registers the service. (GatewayServiceCommands.cs
    exists but is unused.)

G3. The service install script builds from SOURCE (`dotnet publish` of the Gateway and
    Cockpit projects), not from downloaded release assets. A clean end-user machine has
    no repo and no .NET SDK, so the script cannot run as-is.

G4. NSSM is an unmet external dependency. The script expects nssm.exe at
    `%LOCALAPPDATA%\Microsoft\WinGet\Links\nssm.exe`. A clean machine will not have it.

G5. Cockpit first-install extraction is unowned. The Cockpit ships as a `.zip`;
    `UpdateRunner` deliberately SKIPS `.zip` assets and punts extraction to the
    "Gateway-side updater." That is fine for updates (service already running) but on
    FIRST install the service does not exist yet, so nobody extracts the Cockpit.

G6. The wizard cannot select the Gateway role. `EngineInstallRunner.Role` is hardcoded
    to Workstation; there is no UI for it and no admin-elevation flow.

G7. .NET runtime prerequisite ambiguity. RESOLVED 2026-06-03 (opposite of the original
    W2 plan): the payload apps are framework-dependent again and the .NET 10 ASP.NET
    Core runtime IS a required prerequisite. The Setup wizard detects it and offers a
    one-click winget install (falls back to the manual download link). This trades a
    bundled runtime for a ~440 MB smaller download.

G8. Per-component version stamping for tools. PyInstaller exes may carry no file
    version resource, so installed-version detection is unreliable. This does NOT block
    first install (absent == install) but WILL block correct update decisions later.
    Noted for Phase 2; we just must not regress it.

Explicitly NOT gaps (per single-user tailnet policy): no auth, no PIN, no token, no
firewall rules. This is a single-user tailnet; the Gateway/Cockpit bind locally
and Tailscale handles reachability. Do not add security gating.

---

## 4. Workstreams

### W1 - Release ships every piece (Phase 1)

Already drafted in `.github/workflows/release.yml` (uncommitted working tree):

- Add `build-gateway-win` -> `cc-director-gateway-win-x64.exe` (self-contained,
  single-file).
- Add `build-cockpit-win` -> `cc-director-cockpit-win-x64.zip` (self-contained publish
  folder, zipped).
- Convert the tools job to a 26-tool matrix; 6 heavyweight tools marked
  continue-on-error until proven green.
- Wire all into `create-release`; the manifest auto-discovers every asset.

Remaining W1 tasks:
1. Decide version policy: keep one global tag version (`new-release.ps1` bumps the 5
   files) for first release; `scripts/release-asset-versions.json` stays unused until
   Phase 2. (Lowest-friction, matches the engine.)
2. First validation tag `v0.0.0-test1`: confirm the release contains every expected
   asset name plus `release-manifest.json` with version + sha256 for each.
3. Bring the 6 heavyweight tools green one at a time; remove their continue-on-error.

### W2 - Self-contained everywhere (kills the runtime prerequisite)

> SUPERSEDED 2026-06-03: this was reversed. The Windows payload apps (Director,
> Gateway, Cockpit) now publish **framework-dependent** (`--self-contained false`) to
> cut ~440 MB off the download; the **.NET 10 ASP.NET Core runtime is a required
> prerequisite** that the Setup wizard detects and can auto-install via winget. Only
> the Setup wizard and Setup CLI stay self-contained (they bootstrap before .NET
> exists). macOS Director stays self-contained. See the prereq item ".NET 10 Runtime"
> in `tools/cc-director-setup/Services/PrerequisiteChecker.cs`.

- Confirm Director and Gateway publish self-contained single-file (already in the
  release jobs).
- Confirm Cockpit publishes `--self-contained true` (already in the drafted job).
- Result: no .NET runtime needed on the target. Update `INSTALLATION.md` to state the
  runtime is bundled, and keep the prereq check focused on the agent CLI only (W6).

### W3 - Service install from release assets, no external NSSM

This is the core new capability. DECIDED (D1): native Windows service hosting; NSSM is
removed from the install path entirely.

Native Windows service hosting:
1. Add `Host.UseWindowsService()` (Microsoft.Extensions.Hosting.WindowsServices) to the
   Gateway so it runs as a first-class Windows service.
2. Register/unregister with built-in `sc.exe` / `New-Service` - no NSSM, no external
   download. This aligns with "fix the root cause, no external crutch."
3. Move the env/config NSSM was injecting (CC_DIRECTOR_ROOT, OPENAI_API_KEY,
   CC_COCKPIT_MANAGED, CC_COCKPIT_EXE) into the service's environment or a config file
   under `%ProgramData%\cc-director\config`.
4. Retire NSSM from the existing scripts/install path and update GatewayServiceCommands
   (which models nssm calls) to sc.exe-based control.

Then:
5. Add a service-install step to the ENGINE (a `GatewayServiceInstaller` class) so it is
   testable and reusable, invoked by both CLI and WPF. It must:
   - place the Gateway exe at `%ProgramFiles%\CC Director\gateway\cc-director-gateway.exe`
     (from the downloaded asset, not a build),
   - register the service to auto-start, running as LocalSystem with
     `CC_DIRECTOR_ROOT` = the installing user's `%LOCALAPPDATA%\cc-director`,
   - start it and wait for `http://127.0.0.1:7878/healthz`.
4. Add a CLI surface: `cc-director-setup-cli install --role gateway` performs the full
   Gateway install (requires elevation; see W5).

### W4 - Cockpit first-install extraction

1. At install time (elevated, role=Gateway), the installer EXTRACTS
   `cc-director-cockpit-win-x64.zip` into
   `%ProgramFiles%\CC Director\cockpit\` before starting the service.
2. The service (CC_COCKPIT_MANAGED=1, CC_COCKPIT_EXE set) launches the Cockpit on 7470,
   exactly as the existing service does.
3. Installer waits for `http://127.0.0.1:7470/` to return 200 and reports success.
4. Keep the existing `UpdateRunner` skip-zip behavior for the UPDATE path (service-owned)
   - only the FIRST-install extraction is new, and it lives in the installer because the
   service is not yet running.

### W5 - WPF wizard: role selection + elevation

1. Add a Role step (Workstation vs Gateway) to the wizard before Install. Default
   Workstation.
2. Workstation: unchanged (current engine flow).
3. Gateway: the Install step shells the elevated CLI (DECIDED, D2). The wizard launches
   `cc-director-setup-cli install --role gateway` with a UAC prompt and streams its
   output into the wizard. The CLI is the single source of truth for the Gateway
   install; the GUI stays a thin front-end and is NOT itself elevated.
4. Show the Cockpit URL (http://localhost:7470) and service status on the Complete step.
5. WPF is the one installer (DECIDED, D3). All role-selection and Gateway work goes into
   `tools/cc-director-setup` (WPF). The half-built `cc-director-setup-avalonia` wizard is
   shelved (not maintained) for this work; Windows-only is acceptable because the Gateway
   is Windows-only. Revisit Avalonia only if/when a macOS GUI install is in scope.

### W6 - Prerequisites (minimal, honest)

1. Keep `FrameworkDetector` checking for a Claude/Codex CLI on PATH; block with a clear
   message + link if absent (the product is useless without it).
2. Drop the .NET runtime check entirely (W2 makes it unnecessary).
3. Gateway role only: require `OPENAI_API_KEY` in the user environment (the Gateway
   needs it to start) and fail loudly with the exact fix if missing. This mirrors the
   existing script check (install-gateway-service.ps1:39-43).
4. No security/auth prompts. (policy)

### W7 - Clean-machine end-to-end test (the actual deliverable)

1. On a fresh Windows VM (no repo, no SDK, no NSSM): download
   `cc-director-setup-win-x64.exe` from the test release and run it.
2. Workstation pass: Director launches from the Start Menu shortcut; `cc-pdf --help`
   (and a sample of tools) runs from a NEW shell (PATH took effect); spot-check 3-4
   tools including one heavyweight.
3. Gateway pass: run the wizard as Gateway, accept the UAC prompt; confirm
   `cc-gateway-service` is Running and auto-start; `http://localhost:7878/healthz` and
   `http://localhost:7470/` both answer.
4. Idempotency: re-run the installer; it reports everything up to date and does not
   break the running service.
5. Produce an HTML report (cc-html boardroom) with screenshots of the running Director,
   the service in services.msc, and the Cockpit page.

---

## 5. Order of operations

1. W1 (release ships everything) + W2 (self-contained). Nothing else can be tested
   until a real release exists. Tag `v0.0.0-test1`, verify assets.
2. W3 (service install in engine/CLI) + W4 (Cockpit extraction). Test Gateway install
   via CLI against the test release on a VM, headless, before touching the GUI.
3. W5 (WPF role + elevation) once the CLI Gateway path is proven.
4. W6 folds into W3/W5 as the checks are added.
5. W7 throughout, but the formal clean-machine pass is the final gate.

Rationale: prove the Gateway install from the command line first (smallest surface),
then put the GUI on top of a path that already works.

---

## 6. Definition of done

- A single GitHub release contains: Director (win + mac), Gateway, Cockpit, the setup
  wizard, and all 26 verified tools, plus `release-manifest.json` listing each with
  version + sha256.
- On a clean Windows machine with only the setup exe downloaded:
  - Workstation install yields a working Director + tools on PATH, no admin.
  - Gateway install (one UAC prompt) yields a Running `cc-gateway-service` that survives
    reboot and serves the Cockpit on 7470, with no repo, no .NET SDK, and no
    pre-existing NSSM on the machine.
- Re-running the installer is idempotent.
- No build-from-source anywhere in the install path.

---

## 7. Decisions (all resolved - plan is build-ready)

D1. RESOLVED: native `UseWindowsService` + sc.exe; NSSM removed from the install path.
D2. RESOLVED: the wizard shells the elevated CLI (`install --role gateway`) and streams
    its output; the GUI itself is not elevated.
D3. RESOLVED: WPF (`tools/cc-director-setup`) is the one installer; the Avalonia setup
    variant is shelved for this work.
D4. RESOLVED: leave the Gateway tray out of first install. The headless service runs the
    Gateway + Cockpit on its own (the current install script already retires the tray).
    The tray's Restart/Quit were built for the old tray-owns-Gateway model and would need
    rewiring to sc.exe service control; defer as a later convenience.

---

## 8. Out of scope (deliberately)

- Auto-update / per-component re-pull of newer versions (separate plan).
- macOS Gateway/Cockpit/tools (Gateway is Windows-only by design).
- Android apps (separate distribution channel).
- Any authentication, PIN, token, or firewall configuration (single-user tailnet).
- Tool version stamping for update decisions (G8) beyond not regressing it.
