# Proof - Issue 570: hide Communications/Connections/Scheduler behind the alpha flag

Change: in `src/CcDirector.Avalonia/MainWindow.axaml.cs`, the Tools-menu builder
(`BuildNativeMenu`) now gates the three v1-excluded overlay entries -
**Communications**, **Connections** (Browser Connections), and **Scheduler** -
behind the existing `AlphaMode.IsEnabled` flag explicitly, separated from the
other Tools items by a separator. The menu is rebuilt on `AlphaMode.Changed`, so
toggling alpha mode re-gates the entries live without a restart.

## Finding: sidebar Comms / Scheduler indicators

The issue asked to check the sidebar "Comms pending count" and "Scheduler LEADER
pill" in case either is a clickable entry point into a gated overlay. They are
NOT clickable entry points:

- The Comms / Connections / Scheduler toolbar buttons (and their pending-count
  badge / LEADER pill) were already removed from the user interface in an
  earlier change; the only remaining entry points to the three overlays are the
  three Tools-menu items. The sidebar comment at `MainWindow.axaml` line ~331 is
  stale - those buttons no longer exist.
- `RefreshSchedulerLeaderIndicator` (MainWindow.axaml.cs) only sets the window
  Title text (e.g. "CC Director -- Leader"). It is display-only and opens no
  overlay.
- A code search confirmed that the only code paths that set
  `CommsOverlay.IsVisible = true`, `ConnectionsOverlay.IsVisible = true`, and
  `SchedulerOverlay.IsVisible = true` are inside `BtnComms_Click`,
  `BtnConnections_Click`, and `BtnScheduler_Click`, which are only invoked from
  the three (now alpha-gated) Tools-menu items.

The toolbar "Tools" button opens `ToolsOverlay` (the cc-* tools dashboard), which
is explicitly out of scope and is NOT one of the three gated overlays.

## Screenshots

- `01-alpha-off-mainwindow.png` - default install (alpha mode off). The window
  menu bar shows only File / Session / View / Help. There is no Tools menu and no
  other visible control that opens Communications, Connections, or Scheduler.
- `02-alpha-off-menubar-zoom.png` - the same menu bar, cropped and enlarged for
  legibility (File / Session / View / Help only).
- `03-alpha-on-tools-menu-open.png` - alpha mode on. The Tools menu is open and
  shows Communications, Connections, and Scheduler (above the separator), then
  Claude View / MCP Servers / Agent Templates / Claude Code Settings.

## How the screenshots were captured

- Built the worktree to an isolated test slot (`cc-director13.exe`) via
  `scripts/local-build-avalonia.ps1 -Slot 13`.
- Alpha-off shot: launched the slot through its own scheduled task (per the
  isolation harness) with the default (no config.json) storage root, which yields
  alpha mode off.
- Alpha-on shot: relaunched the same slot exe with an isolated
  `CC_DIRECTOR_ROOT` whose `config/config.json` contained `{"alpha_mode":true}`,
  so the user's shared configuration was never modified. The instance log
  confirmed `[AlphaMode] Load: alpha_mode=True`.
- The shared `%LOCALAPPDATA%\cc-director\config.json` did not exist before this
  work and does not exist after it; the user's running Directors were untouched.

## Build / test

- `dotnet build cc-director.sln` - Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test src/CcDirector.Avalonia.Tests` - Passed, 66 of 66.
