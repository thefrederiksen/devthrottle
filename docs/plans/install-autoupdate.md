# CC Director - Install & Auto-Update (Windows)

Status: PLAN (agreed high-level design, 2026-06-01).
Scope: Windows only. macOS stays manual-install and cannot host the Gateway.

> NOTE: For install LOCATIONS, the authoritative master spec is
> `docs/install/INSTALLATION.md`. Where this plan's older path examples (e.g.
> `C:\cc-tools`, `app\`) differ, the master spec wins.

> SUPERSEDED IN PART (2026-06-05): the Gateway is no longer a Windows service
> (`cc-gateway-service` is retired). It ships as a per-user TRAY app under
> `%LOCALAPPDATA%\cc-director\gateway`, started at logon - no elevation anywhere
> in the lifecycle. See docs/plans/gateway-tray-app.md (v2).

This document records the decisions Soren and I agreed and turns them into a
concrete, phased implementation plan grounded in the code that exists today.

---

## 1. Goals

1. ONE installer program with two front-ends over one shared engine:
   - UI front-end - a human clicks through it.
   - CLI front-end - an agent or scheduled task drives it headless, with
     machine-readable output and exit codes.
   Because both call the same engine, a human and an agent install/update
   identically - no second code path to drift.
2. Two install types chosen up front: Workstation and Gateway.
3. Silent automatic updates for every component, client and server, that never
   interrupt live work and never require a UAC prompt after the first install.
4. Keep the existing setup tool and the existing Director self-update - they are
   the starting point, not a rewrite.

---

## 2. Current state (what already exists)

This is a survey of the relevant code so the plan builds on it rather than
reinventing it.

### Install
- `tools/cc-director-setup/` - WPF setup wizard. Its `Services/` already hold a
  usable install engine: `GitHubReleaseService`, `ToolInstaller`, `PathManager`,
  `ShortcutCreator`, `InstallDetector`, `PrerequisiteChecker`, `ProfileStore`.
  PROBLEM: the orchestration lives in `MainWindow.xaml.cs` (the `RunInstallAsync`
  / `RunRepairAsync` flow), fused to WPF. There is no headless entry point.
- `tools/cc-director-setup-avalonia/` - the macOS sibling.
- `docs/install/install-prompt.md` - the agent-driven install paragraph.
- `scripts/install-gateway-service.ps1` - elevated NSSM install of
  `cc-gateway-service` (LocalSystem), files in `C:\cc-tools\cc-director-gateway`
  and `...-cockpit`, with the Gateway supervising the Cockpit (`CC_COCKPIT_MANAGED=1`).
- `scripts/deploy-cockpit.ps1` - admin-free Cockpit swap (Stop-Service / copy /
  Start-Service), relies on a one-time SCM + ACL grant to the user.
- `scripts/build-tools.bat` - builds all ~35 tools to `%LOCALAPPDATA%\cc-director\bin`.

### Update
- `src/CcDirector.Core/Update/UpdateService.cs` - checks GitHub
  `/releases/latest`, maps OS+arch to a SINGLE asset name (`AssetNameFor` today
  only knows `cc-director-win-x64.exe`), fetches `release-manifest.json`, verifies
  SHA-256, stages the build, raises `UpdateStaged`.
- `src/CcDirector.Core/Update/UpdateInstaller.cs` - applies a staged build via a
  hidden `--apply-update <target> <pid>` relauncher: waits for the old process to
  exit, swaps the exe (`.new` -> target, previous -> `.old`), relaunches. Keeps a
  `.old` backup. This is the mechanism we extend to the Gateway and tools.
- `UpdaterState` - persists `StagedVersion` / `StagedExecutable` / `InstallTarget`
  / `DismissedVersion` / `LastCheckedAt`.

### Release
- `.github/workflows/release.yml` - triggers on a `v*` tag. Builds the Director
  (win + mac), exactly THREE Python tools (cc-pdf, cc-html, cc-word), and the
  setup wizard. Generates `release-manifest.json` with a single top-level
  `version` and an `assets` map of `{ size, sha256, platform }` per file.
- Version is bumped in 5 files by `scripts/new-release.ps1`.

### Config / data
- Per-user under `%LOCALAPPDATA%\cc-director` (app, bin, config, vault, logs).
  `CC_DIRECTOR_ROOT` overrides the root; the Gateway service uses it so a
  LocalSystem service still reads the interactive user's data.

---

## 3. Decisions (agreed)

| # | Decision | Choice |
|---|----------|--------|
| D1 | Installer shape | One tool, UI + CLI front-ends, one shared engine |
| D2 | Install types | Up-front toggle: **Workstation** vs **Gateway** |
| D3 | Gateway relationship | Gateway = Workstation + always-on Gateway/Cockpit service (a superset, not a disjoint path). Usually someone's main workstation, not a headless box - hence "Gateway", not "Server". Exactly one on the tailnet. |
| D4 | Version unit | **Independent per component** - Director, Gateway, Cockpit, each tool carry their own version |
| D5 | Cadence | **Silent-auto** everywhere; apply at next restart so live work is never killed |
| D6 | Trigger | **Resident apps orchestrate** - Director (while open) and Gateway (always) run the CLI updater for all present components |
| D7 | Gateway update privilege | **Self-updating service** - the LocalSystem service swaps + restarts its own build; no UAC after first install |
| D8 | Update failure handling | **Keep `.old` backups + manual rollback** (`...setup rollback <component>`); no auto-rollback/health-check |
| D9 | Framework bootstrap | **Detect + guide** (link to Claude Code / Codex official installer + Re-check); we never own their install mechanics |
| D10 | Code signing | **Stay unsigned, document** the SmartScreen "More info -> Run anyway" step; revisit for external users |

---

## 4. Target architecture

### 4.1 The shared engine

Extract a headless `CcDirector.Setup.Core` (or a `Core/` folder inside the setup
tool) from the WPF window. It owns:

- Component model: a registry of installable components, each with a kind
  (`director` | `gateway` | `cockpit` | `tool`), an install location resolver,
  and a current-version reader.
- Release access: reuse/relocate `GitHubReleaseService` + the manifest reader.
- Install operations: download, SHA-256 verify, place, PATH, shortcut (reuse
  `ToolInstaller`, `PathManager`, `ShortcutCreator`).
- Update operations: per-component "is behind?" check, stage, apply, rollback.
- Prerequisite detection (reuse `PrerequisiteChecker`) including the framework
  detect+guide (D9).

Both the WPF UI and a new CLI are thin shells over this engine. The existing
`RunInstallAsync` logic in `MainWindow.xaml.cs` moves into the engine; the window
just calls it and renders progress events.

### 4.2 Install types (D2/D3)

The first thing the installer resolves is the install type.

- UI: a toggle on the welcome step - "Workstation" vs "Gateway".
  - Workstation subtitle: "This machine (Director + tools)."
  - Gateway subtitle: "This machine, and it also hosts the Gateway for the tailnet."
- CLI: `--role workstation|gateway` (default `workstation`).

Flow:

```
Workstation install (no admin):
  detect prerequisites (incl. framework: detect + guide)
  install/refresh Director  -> %LOCALAPPDATA%\cc-director\app
  install/refresh tools     -> %LOCALAPPDATA%\cc-director\bin  (+ PATH)
  Start Menu shortcut

Gateway install (= Workstation, then one elevated step):
  <everything the Workstation flow does>
  THEN, elevated once:
    publish/register cc-gateway-service (Gateway + Cockpit) in C:\cc-tools
    (this is the existing install-gateway-service.ps1 logic, folded into the engine)
```

The elevation is scoped to only the Gateway step. The CLI self-elevates (UAC) for
that step when `--role gateway` and not already elevated; the UI shows a shield on
the Gateway option.

### 4.3 Versioning model for independent components (D4)

"Independent per component" needs each asset to carry its OWN version so a single
release can move one tool without bumping the Director. Concrete change to the
manifest:

```jsonc
{
  "version": "0.4.0",          // release tag, informational only
  "assets": {
    "cc-director-win-x64.exe": { "version": "0.4.0", "sha256": "...", "platform": "windows" },
    "cc-director-gateway-win-x64.exe": { "version": "0.4.0", "sha256": "...", "platform": "windows" },
    "cc-director-cockpit-win-x64.zip": { "version": "0.4.0", "sha256": "...", "platform": "windows" },
    "cc-pdf-win-x64.exe": { "version": "1.2.0", "sha256": "...", "platform": "windows" },
    "cc-html-win-x64.exe": { "version": "1.1.3", "sha256": "...", "platform": "windows" }
    // ...one entry per shipped component
  }
}
```

Update rule (per component): the resident app reads each installed component's
version, compares to that asset's `version` in the latest manifest, and updates
ONLY components that are behind. So cutting a release that only changed cc-pdf
re-stamps cc-pdf to 1.2.0; nothing else is "behind", so nothing else moves.

This keeps a single release pipeline (one manifest, one set of GitHub assets)
while letting any subset of components roll forward independently. Each
component's version is read from: assembly version (Director/Gateway/Cockpit),
and an embedded version for tools (PyInstaller build stamp / a `--version` the
engine can call, or a per-tool version recorded at install time in
`config/director/installed-components.json`).

### 4.4 Update orchestration (D5/D6)

- Director (while open) and Gateway service (always) each run an in-process loop
  that periodically invokes the engine's "update all present components" routine.
  No separate scheduled task.
- For each behind component: download, SHA-256 verify, stage. Apply rules:
  - Director: stage, apply at next Director startup (existing `UpdateInstaller`
    path - never kills a session).
  - Tools: a tool binary not currently running is replaced in place; the next
    invocation picks it up. (No "is it running" gate - acceptable per D6 context.)
  - Gateway / Cockpit: see 4.5.
- Silent: no banner, no prompt. (The existing `UpdateStaged` -> banner wiring in
  the Director can stay as a no-op or be repurposed to a quiet status line; not
  required.)

### 4.5 Gateway self-update (D7)

The Gateway runs as LocalSystem, so it already has the rights to write
`C:\cc-tools` and to restart its own service. Mechanism:

1. The Gateway's update loop finds the Gateway and/or Cockpit asset is behind.
2. It downloads + SHA-256 verifies into a staging dir.
3. Apply:
   - Cockpit: it is a supervised CHILD process, not the service itself. The
     Gateway can stop the child, swap `C:\cc-tools\cc-director-cockpit` (keeping
     `.old`), and relaunch the child - no service bounce. This generalizes the
     existing `deploy-cockpit.ps1` into the service.
   - Gateway exe: the running service exe is locked, same problem the Director
     solves. Reuse the relauncher idea: write the staged exe, then have the
     service request its own restart (NSSM restart / `sc stop`+`start` from a
     short-lived helper the service spawns) so the swap happens while the exe is
     not running, then NSSM brings the new exe back up. Keep `.old`.
4. Because all of this runs inside the already-privileged service, NO UAC and any
   actor can trigger a check.

### 4.6 Rollback (D8)

- Every swap (Director, Gateway, Cockpit, tool) keeps the previous build as
  `.old` (Director already does; extend to the rest).
- `...setup rollback <component>` restores `.old` over the live build, restarts
  the component, and pins away from the bad version (`DismissedVersion`-style)
  so the update loop will not immediately re-stage it.
- No automatic health-check or auto-rollback.

### 4.7 CLI surface (D1)

```
cc-director-setup install   [--role workstation|gateway] [--tools <group|all>] [--quiet]
cc-director-setup update    [--component <name>|all] [--quiet]     # what resident apps call
cc-director-setup rollback  <component>
cc-director-setup status                                           # installed components + versions, JSON with --json
cc-director-setup uninstall [--role ...]
```

Exit codes are meaningful (0 ok / non-zero per failure class). `--json` on
read commands for agent consumption. No window in CLI mode.

### 4.8 Framework bootstrap (D9)

`PrerequisiteChecker` detects Claude Code / Codex. If missing: the UI shows the
official install link + a "Re-check" button; the CLI prints the link and exits
with a distinct "prerequisite missing" code. The installer never runs the
framework's installer itself.

---

## 5. Release pipeline changes

`.github/workflows/release.yml` today ships only the Director + 3 tools + wizard.
For the model above it must also:

1. Build and publish the Gateway and Cockpit as release assets
   (`cc-director-gateway-win-x64.exe`, `cc-director-cockpit-win-x64.zip`).
2. Build and publish ALL tools that ship to clients (currently only 3 are in CI).
3. Emit a per-asset `version` in `release-manifest.json` (section 4.3), sourced
   from each component's own version stamp rather than the single tag.

Open: whether per-tool versions are maintained by hand or derived. Simplest start
- give each component a version file it owns, and have CI read it per asset.

---

## 6. Phased implementation

- **Phase 1 - Headless engine.** Extract the install/update engine from
  `MainWindow.xaml.cs` into a testable core. WPF UI becomes a thin shell. No
  behaviour change yet. Unit tests for the engine.
- **Phase 2 - CLI front-end.** Add the `install/update/rollback/status` commands
  over the engine. Verify a full Workstation install + update purely from the CLI.
- **Phase 3 - Install-type toggle.** Wire Workstation vs Gateway into UI + CLI;
  fold `install-gateway-service.ps1` into the engine's elevated Gateway step.
- **Phase 4 - Independent versioning + release.** Per-asset versions in the
  manifest; extend `release.yml` to publish Gateway/Cockpit + all tools; teach the
  updater to compare per component.
- **Phase 5 - Resident orchestration.** Director + Gateway run the engine's
  "update all present" loop on a cadence (silent). Extend `.old` backups to tools
  + Gateway/Cockpit.
- **Phase 6 - Gateway self-update.** Service-side staging + Cockpit child swap +
  Gateway exe relaunch-restart. `rollback` for server components.
- **Phase 7 - Polish.** Update `docs/install/*` with the unsigned/SmartScreen
  walkthrough; framework detect+guide copy; uninstall.

---

## 7. Open questions / risks

- Per-tool version source of truth (hand-maintained vs derived) - decide in P4.
- Locked Gateway exe restart: confirm NSSM can cleanly relaunch the swapped exe
  via a spawned helper without leaving the service in a stopped state.
- Independent-component support cost (D4): "which version of each of N things"
  diagnosis - mitigate with `...setup status --json` and logging the full
  component/version set on every update run.
- Tool replaced while in use: D6 accepts "next invocation picks it up"; a tool
  mid-run keeps the old file handle, which is fine on Windows (rename-over works
  if we swap, not truncate). Confirm swap uses replace semantics like the Director.

---

## 8. References

- Engine source: `tools/cc-director-setup/Services/`, `MainWindow.xaml.cs`
- Update core: `src/CcDirector.Core/Update/{UpdateService,UpdateInstaller,UpdaterState}.cs`
- Gateway service: `scripts/install-gateway-service.ps1`, `scripts/deploy-cockpit.ps1`
- Release: `.github/workflows/release.yml`, `scripts/new-release.ps1`
- Install prompt: `docs/install/install-prompt.md`
