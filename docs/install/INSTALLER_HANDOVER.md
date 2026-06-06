# CC Director Installer — Handover

Status as of 2026-06-04. Latest release: **v0.6.3**. This document hands off the cross-platform
installer for testing on another machine (especially the Windows box where a fresh install stalls, and
a Mac where the macOS installer has never been run on-device).

---

## 1. What the installer is

One installer experience on **two thin GUIs over one shared engine**:

- **Windows wizard (WPF)** — `tools/cc-director-setup/`, ships as `cc-director-setup-win-x64.exe`. The
  production Windows installer. Self-contained (no .NET needed to run the wizard itself).
- **macOS wizard (Avalonia)** — `tools/cc-director-setup-avalonia/`, ships as
  `cc-director-setup-mac-arm64.zip` ("CC Director Setup.app", ad-hoc signed). New in v0.6.x; never run
  on a real Mac yet.
- **Shared engine** — `tools/cc-director-setup-engine/` (`CcDirector.Setup.Engine`, net10.0,
  cross-platform). All install/update/uninstall logic. Both GUIs + the headless CLI
  (`tools/cc-director-setup-cli/`) drive it.

**Scope:** Workstation-only on macOS (the Gateway role/service is Windows-only). Apple Silicon only on Mac.

### What it installs
- **The Director app**: Windows -> `%LOCALAPPDATA%\cc-director\app\cc-director.exe` (framework-dependent,
  needs the .NET 10 runtime). macOS -> `~/Applications/CC Director.app` (self-contained).
- **The cc-* CLI tools** as ONE shared Python venv (not 26 separate exes):
  - bundled Python -> `%LOCALAPPDATA%\cc-director\python` (win) / `~/Library/Application Support/cc-director/python` (mac)
  - venv -> `…\cc-director\pyenv`
  - shims -> `…\cc-director\bin\cc-<tool>.cmd` (win) / `~/.local/bin/cc-<tool>` symlinks (mac)
- **Skills** into `~/.claude/skills/`.

### Release assets (per tag, built by `.github/workflows/release.yml`)
Windows: `cc-director-win-x64.exe`, `cc-director-gateway-win-x64.exe`, `cc-director-cockpit-win-x64.zip`,
`cc-director-setup-win-x64.exe`, `cc-director-setup-cli-win-x64.exe`, `cc-python-win-x64.zip`,
`cc-tools-pyenv-win-x64.zip`, `cc-tools-pyenv-extras-win-x64.zip`.
macOS: `cc-director-mac-arm64.zip`, `cc-director-setup-mac-arm64.zip`, `cc-python-macos-arm64.tar.gz`,
`cc-tools-pyenv-macos-arm64.tar.gz`, `cc-tools-pyenv-extras-macos-arm64.tar.gz`.
Plus `release-manifest.json` (SHA-256 of every asset).
The `-extras-` assets are the ON-DEMAND tier (cc-crawl4ai + cc-docgen, issue #174): not installed by
default, added to the same shared venv via `cc-director-setup-cli install-extras`, and automatically
restored after every core bundle rebuild if previously installed.

---

## 2. Key files

| Area | File(s) |
|------|---------|
| Install layout (paths, per-OS) | `tools/cc-director-setup-engine/InstallLayout.cs` |
| Python tools install (extract→venv→offline pip→shims) | `tools/cc-director-setup-engine/PythonToolsInstaller.cs` |
| macOS Director placement (ditto + de-quarantine) | `tools/cc-director-setup-engine/MacAppPlacer.cs` |
| PATH / shortcut / mac PATH block | `tools/cc-director-setup-engine/InstallFinalizer.cs` |
| Uninstall | `tools/cc-director-setup-engine/Uninstaller.cs` |
| Auto-update (incl. bundle refresh + migration) | `tools/cc-director-setup-engine/ToolUpdater.cs` |
| Release fetch + download | `tools/cc-director-setup-engine/ReleaseSource.cs` |
| Pre-filled GitHub issue helper | `tools/cc-director-setup-engine/IssueReporter.cs` |
| Engine log seam (Sink) | `tools/cc-director-setup-engine/EngineLog.cs` |
| WPF install orchestration | `tools/cc-director-setup/Services/EngineInstallRunner.cs` |
| Avalonia install orchestration | `tools/cc-director-setup-avalonia/Services/EngineInstallRunner.cs` |
| Bundle builders | `scripts/build-python-bundle.ps1` (win), `scripts/build-python-bundle.sh` (mac) |
| Mac .app packager | `scripts/package-mac-app.sh` |
| Release pipeline | `.github/workflows/release.yml` |

---

## 3. Build & run locally (for testing on another machine)

Prereqs: .NET 10 SDK; for the bundle scripts also `uv` + Python 3.

**Run the Windows wizard from source:**
```
dotnet run --project tools/cc-director-setup/CcDirectorSetup.csproj
```
**Run the macOS wizard from source (on a Mac):**
```
dotnet run --project tools/cc-director-setup-avalonia/CcDirectorSetup.csproj
```
Both wizards fetch the **latest GitHub release** and install from it — so running from source still pulls
the real published bundle. To test against a specific build, install the published
`cc-director-setup-*` for that version instead.

**Build the Python tools bundle locally** (proves the dep gate + produces the assets):
```
# Windows
powershell -ExecutionPolicy Bypass -File scripts/build-python-bundle.ps1
# macOS
bash scripts/build-python-bundle.sh
```

**Engine tests:** `dotnet test tools/cc-director-setup-engine.Tests/CcDirector.Setup.Engine.Tests.csproj`
(104 pass; an opt-in live install test runs only when `CC_PYBUNDLE_DIR` points at a built bundle).

---

## 4. Logs (where to look)

`%LOCALAPPDATA%\cc-director\logs\setup\setup-<timestamp>.log` (Windows) /
`~/Library/Application Support/cc-director/logs/setup/setup-<timestamp>.log` (macOS).

**As of v0.6.3** the wizards route the engine's detailed step logs into this file (before v0.6.3 the whole
apply phase was discarded — `EngineLog` defaults to a no-op and the GUIs didn't wire its `Sink`). To watch
a stuck install live: `Get-Content <log> -Wait -Tail 50` (PowerShell) / `tail -f <log>` (mac).

The wizard also shows the log path on the **Install** and **Complete** screens, with **Open log folder**
and **Report a problem on GitHub** buttons (the install-screen buttons mean a hung run is still reportable).

---

## 5. THE OPEN BUG to test/diagnose

**Symptom:** a fresh Windows install (and an update) **stalls on the `python-tools` step**. The install
screen sits on "python-tools Installing…" and may never reach Complete.

**Why we couldn't see it (fixed in v0.6.3):** engine step logs were discarded, so the log went blank right
after `PrepareAsync`. v0.6.3 now logs every engine step.

**How to diagnose on the test machine (use v0.6.3+):**
1. Run `cc-director-setup-win-x64.exe` (v0.6.3) and reproduce the stall.
2. `tail -f` the setup log. You'll now see, with timestamps:
   `downloading cc-python-win-x64.zip` → `extracting bundled Python` → `creating the shared Python venv`
   → `installing 23 tools offline from the wheelhouse`.
3. **The gap between the last two timestamps is the culprit step.** Capture the full log.

**Leading hypothesis:** the offline `pip install` of all 23 tools (`PythonToolsInstaller.InstallAsync`,
step 4) is slow (~3 min normally; rebuilds the whole venv even on an update) or genuinely hung. It uses
`ProcessRunner.Run` with **no timeout**, so a hang sits forever. Other candidates the log will rule in/out:
the `python -m venv` step, or extraction of the 334 MB wheelhouse (antivirus can stall this on Windows).

**Likely fixes once confirmed** (do NOT pre-apply — let the log decide):
- Add a timeout + clear failure to the venv/pip `ProcessRunner` calls.
- Stream pip progress to the log/UI so it's visibly alive.
- Skip the tools reinstall on update when the bundle version is unchanged (avoid the 3-min rebuild every update).

---

## 6. Other known follow-ups
- **Anonymous problem reporting** — GitHub issue **#167**. The current report button opens a pre-filled
  GitHub issue, which requires the user to be signed in to GitHub. End users have no account. Plan: a
  hosted relay (Cloudflare Worker holding a bot token) + email/copy fallback. Not built.
- **macOS install never run on-device** — CI builds all mac assets green (incl. the arm64-wheel gate), but
  nobody has run the `.app` installer on a real Mac (place Director → build venv → run a tool). This is the
  main thing to test on a Mac.
- **Avalonia mac Launch button** — `CompleteStep` launches `installPath/cc-director`, but on mac the
  Director is `~/Applications/CC Director.app`; the launch should `open` the .app. Minor polish bug.
- **Dead Avalonia code** — `ToolsStep`, `ToolGroupRegistry`, `InstallProfile`, `ProfileStore`,
  `GitHubReleaseService` are unused after the engine rewire (harmless; remove later).

---

## 7. References
- Plan (full design): `C:\Users\soren\.claude\plans\shiny-swimming-coral.md` (local to the dev box).
- Recent release tags: v0.5.0 (shared-venv tools), v0.6.0 (mac installer), v0.6.1/.2 (issue reporter +
  install-screen report button), **v0.6.3** (engine logging routed to the setup log).
- To cut a release: bump the 5 version files (`src/CcDirector.Avalonia/CcDirector.Avalonia.csproj`,
  `tools/cc-director-setup/CcDirectorSetup.csproj` + its `MainWindow.xaml` `Text="vX"`,
  `tools/cc-director-setup-avalonia/CcDirectorSetup.csproj` + its `MainWindow.axaml`), commit, tag `vX`,
  push the tag. The release pipeline builds everything on the tag.
