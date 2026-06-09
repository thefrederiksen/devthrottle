# cc-launcher: Windows tray launcher with a REST API (v1, local)

> Concrete v1 implementation plan for issue #243 (CC Launcher). Scope confirmed: **.NET tray
> app**, **general app launcher + Director supervisor**, **local-only** (Gateway remote-relaunch
> is a fast-follow).

## Context

When the Claude Code agent spawns apps from its own process tree, child `claude` processes inherit
the agent's non-TTY pseudo-console (ConPty) and die (~3s) - the "rule 0b" problem. Today the only
workaround is a hand-registered `cc-director-launch` Windows scheduled task. There is a
`tools/cc-launcher/` but it is **macOS-only** (a launchd Python daemon); the Windows equivalent is
filed as **#243** and is unbuilt.

The user wants a **Windows .NET tray app** that (a) launches/supervises apps on the machine with
**clean process parentage** (so launched apps are NOT children of any ConPty), and (b) exposes a
**REST API** an agent can POST to - replacing the scheduled-task hack and giving a general
"launch any app" primitive.

Intended outcome: an always-on tray icon ("CC Launcher") that survives Director crashes/updates,
launches/restarts/stops cc-director and arbitrary apps cleanly, and answers a token-gated loopback
REST API.

## Template to copy

`src/CcDirector.GatewayApp` is the blueprint - mirror it closely:
- `CcDirector.GatewayApp.csproj` - Avalonia `WinExe`, `net10.0-windows`, `app.ico`/`app.manifest`,
  `Assets/tray.ico` as `AvaloniaResource`; published single-file win-x64.
- `Program.cs` - `[STAThread] Main`, named `Mutex` single-instance, options parse,
  `StartWithClassicDesktopLifetime`.
- `App.axaml.cs` - `ShutdownMode.OnExplicitShutdown`, create the tray controller.
- `GatewayTrayController.cs` - Avalonia `TrayIcon` + `NativeMenu`; `Start()` builds the tray,
  registers autostart, kicks off the Kestrel host on a background task.
- Autostart via `tools/cc-director-setup-engine/GatewayAutostart.cs` (HKCU `...\Run`).

## Implementation

### 1. New project `src/CcDirector.Launcher` (the tray exe `cc-launcher.exe`)
Copy the GatewayApp skeleton: `Program.cs` (mutex `CcDirector.Launcher.SingleInstance`), `App.axaml`
+ `App.axaml.cs`, `LauncherTrayController.cs`, `LauncherAppOptions.cs` (`--port`, `--no-autostart`),
`app.manifest`, `app.ico`, `Assets/tray.ico`. References `CcDirector.Core` (for `CcStorage`,
`FileLog`, `CommandLineLauncher`) and `cc-director-setup-engine` (for `InstallLayout`).
Tray menu: status line, "Restart Director", "Open Logs", "Start with Windows" toggle, "Quit".

### 2. Loopback REST host `LauncherHost` (minimal API + token, mirrors ControlApiHost)
- Kestrel bound to `IPAddress.Loopback` only, fixed default port **7900** (override via `--port`),
  the way `src/CcDirector.ControlApi/ControlApiHost.cs` binds loopback.
- Token gate copied from `src/CcDirector.ControlApi/DirectorAuth.cs` (Bearer header; `/healthz`
  public). Persist `{port, token, pid}` to a discovery file
  `%LOCALAPPDATA%/cc-director/config/launcher/launcher.json` (mirrors `InstanceRegistration`) so an
  agent/CLI can find it. Reuse `CcStorage.ToolConfig("launcher")`.
- Endpoints (all loopback + token except `/healthz`):
  - `GET  /healthz` -> `{ok, version, pid, uptimeS}`
  - `GET  /status` -> launcher info + `{director: {running, pid, path}}` + recently launched pids
  - `POST /launch` `{path|app, args?, cwd?}` -> launch arbitrary app with clean parentage; `{ok, pid}`
  - `POST /director/start` | `/stop` | `/restart` -> supervise cc-director
  - `POST /shutdown` -> quit the launcher (graceful)

### 3. `LaunchService` - the CLEAN-PARENTAGE core (Core-style, unit-tested)
The launcher itself runs outside any ConPty (started by the Run key / Start Menu), so a child it
starts has clean parentage. The recipe (validated against existing code):
- `ProcessStartInfo { UseShellExecute = true }` for GUI apps (shell association, no ConPty) - same
  as the Gateway's `POST /directors` launch and `UpdateInstaller.Relaunch`.
- For a hidden/headless child: `UseShellExecute = false, CreateNoWindow = true` (no console handle),
  as `CockpitSupervisor`.
- NEVER bare `UseShellExecute=false` without `CreateNoWindow` (inherits the parent console).
- Route `.cmd`/`.bat` through `CommandLineLauncher.Build` (`src/CcDirector.Core/Utilities`). Validate
  the path exists before `Process.Start`; throw on missing (no fallback). Track launched PIDs.

### 4. `DirectorSupervisor`
- Resolve the installed Director exe via `InstallLayout.Default().PathFor(Director)`
  (`%LOCALAPPDATA%/cc-director/app/cc-director.exe`) + dev-build fallback, mirroring the Gateway's
  `ResolveDirectorExe()`.
- `start`: launch via `LaunchService` if not already running. `stop`: POST `/shutdown` to the
  Director's Control API (discover its port via `instances/{id}.json`), fall back to process stop.
  `restart`: stop -> wait for exit -> start (the Director auto-applies any staged update on startup,
  so we never fight `UpdateInstaller`). Log every transition (`FileLog`).

### 5. `LauncherAutostart` (HKCU Run key) + self-registration on startup
New `LauncherAutostart.cs` modeled on `GatewayAutostart.cs`: value `CcDirectorLauncher`, `"<exe>"`.
The tray controller calls `EnsureRegistered` on startup (unless `--no-autostart`), and the "Start
with Windows" menu toggles it - the same pattern the Gateway uses. This is what makes it always-on.

### 6. Installer + release wiring
- `tools/cc-director-setup-engine/ComponentKind.cs`: add `Launcher`.
- `ComponentRegistry.cs`: add `Launcher` component (`Id "cc-launcher"`, asset
  `cc-launcher-win-x64.exe`, both roles) + include in `Apps`.
- `InstallLayout.cs`: `ComponentKind.Launcher -> {LocalRoot}/launcher/cc-launcher.exe`.
- Recorded in `installed.json` by the existing `UpdateRunner` placement path (no special installer
  step needed in v1 - the app self-registers autostart on first run, like the Gateway). Add a build
  entry alongside the Gateway in `scripts/local-build-avalonia.ps1` and the release asset list.

### 7. Tests (`src/CcDirector.Launcher.Tests` or fold into Core.Tests)
- `LaunchService`: builds the correct `ProcessStartInfo` (UseShellExecute/CreateNoWindow per app
  type), `.cmd` routing, missing-path throws. (Assert on a seam returning the built PSI, not a real
  spawn.)
- `DirectorSupervisor`: exe-path resolution order; restart sequencing.
- Token auth: missing/wrong Bearer -> 401, `/healthz` public.

## Security posture
A loopback REST that launches arbitrary executables is powerful, so: bind 127.0.0.1 ONLY, require
the Bearer token (32-byte random, persisted in user config) on every endpoint except `/healthz`, and
log every launch with the resolved path + caller. No remote exposure in v1.

## Out of scope (v1)
- Gateway registration + remote relaunch (#243's cross-machine "bring a machine's Director back up")
  - fast-follow once the local primitive is proven.
- Window control (the Mac daemon's minimize/focus) - not requested for Windows v1.
- Self-update of the launcher itself (it's tiny; ships with releases, replaced on install).

## Verification
- Build: `dotnet build src/CcDirector.Launcher` - 0 warnings; `dotnet test` for the new tests.
- REST smoke (non-session-creating): start the launcher, read `launcher.json` for port+token, then
  `GET /healthz`, `GET /status`, and `POST /launch {"path":"notepad.exe"}` -> confirm Notepad opens
  as a child of cc-launcher (NOT of claude) via Process Explorer / `Win32_Process.ParentProcessId`.
- Director supervision (session-creating - bring the launcher up cleanly first, then)
  `POST /director/restart` and confirm the Director (and its child claudes) come back and survive,
  proving clean parentage end-to-end.
