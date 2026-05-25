# Gateway Tray App - Implementation Plan

**Status:** Draft for review
**Date:** 2026-05-24
**Related:** [Gateway-Manager-Implementation.md](Gateway-Manager-Implementation.md), `src/CcDirector.Gateway/`

---

## 1. Problem

The gateway today is a foreground **console app** (`cc-director-gateway`, `OutputType=Exe`).
Someone has to start it in a terminal and keep that window open. That is not sustainable
for a process that is meant to always be running whenever the user is logged in.

A Windows Service is the wrong model: the gateway (and CC Director itself) depends on the
logged-in user session - the user must be logged in for any of this to work. A service
running under `svchost`/SYSTEM gains nothing and complicates the lifecycle.

## 2. Solution

Wrap the existing gateway host in a small **Avalonia tray app** (system tray / notification
area icon, lower-right of the taskbar). It runs quietly in the background, starts on login,
and is never really "closed" - closing minimizes to the tray; only an explicit Quit stops it.

This is a lifecycle swap, not a rewrite. The gateway is already cleanly factored:

- `GatewayHost` (`src/CcDirector.Gateway/GatewayHost.cs`) is a self-contained
  `IAsyncDisposable` with `StartAsync()` / `StopAsync()`.
- `Program.cs` does nothing but new up `GatewayHost`, start it, and block on Ctrl+C.

The tray app replaces "block on Ctrl+C" with "block on the Avalonia app lifetime, owning
the same `GatewayHost`."

## 3. Design decisions (agreed)

| Decision | Choice |
|---|---|
| Hosting model | Wrap gateway in ONE process. Avalonia shell hosts `GatewayHost` in-process. |
| UI framework | Avalonia (matches CC Director; built-in `TrayIcon` + `NativeMenu`). |
| Autostart | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registry entry, registered idempotently on first run. |
| Single instance | Enforced via a named mutex. Second launch surfaces the existing instance and exits. |
| Console exe | Kept. `cc-director-gateway` (headless console) stays for debug/CI/scripts. Both reference the same `GatewayHost`. |
| Status window | Deferred. v1 is tray icon + right-click menu only. |

## 4. Scope

### In scope (v1)
- New project `CcDirector.GatewayApp` - Avalonia, `OutputType=WinExe` (no console window flash).
- Hosts `GatewayHost` in-process on a background task at startup.
- Tray icon with status dot (running / stopped) and a right-click menu.
- `ShutdownMode = OnExplicitShutdown` so closing a window never kills the process.
- Named-mutex single-instance guard.
- Idempotent `HKCU\...\Run` autostart registration.
- Clean `StopAsync()` on Quit.

### Out of scope (later)
- Status window (double-click the tray icon -> port, token, Tailscale state, log tail, director count).
- "Start on login" toggle UI (the Run key is written unconditionally in v1).
- Retiring the console exe.

## 5. Tray menu (v1)

```
[*] Gateway running on :7878      (status line, green/red dot)
    Open Dashboard                (launches Tailscale HTTPS URL, or http://127.0.0.1:7878)
    Open Logs Folder              (opens %LOCALAPPDATA%\cc-director\logs\gateway)
    Restart Gateway               (StopAsync -> StartAsync)
    ----
    Quit                          (StopAsync, then app shutdown)
```

## 6. Implementation steps

1. **Create `CcDirector.GatewayApp`** Avalonia project (`WinExe`, `net10.0`),
   referencing `CcDirector.Gateway`. Add to the solution.
2. **App lifecycle** (`App.axaml` / `App.axaml.cs`):
   - `ShutdownMode = OnExplicitShutdown`.
   - On framework init: named-mutex guard; if already held, exit (optionally signal the
     running instance to open the dashboard).
   - Start `GatewayHost` on a background task; log start/failure via `FileLog`.
3. **Tray icon + `NativeMenu`** with the menu above. Wire each item:
   - Open Dashboard - resolve the best URL (Tailscale serve URL if present, else loopback)
     and `Process.Start` it with `UseShellExecute = true`.
   - Open Logs Folder - open the gateway log directory.
   - Restart - `await StopAsync(); await StartAsync();` with status-dot update.
   - Quit - `await StopAsync();` then `Shutdown()`.
4. **Status dot** reflects host state (green when listening, red when stopped/failed).
5. **Autostart**: write `HKCU\...\Run\CcDirectorGateway` = exe path on startup if missing
   (idempotent; overwrite if the path changed).
6. **Build/distribution**: add a build target/script so the tray exe lands alongside the
   other artifacts. Decide install location (likely `%LOCALAPPDATA%\cc-director\bin` or the
   app install dir).
7. **Console exe untouched**: `cc-director-gateway` keeps working as-is.

## 7. Tests

- Unit: autostart registry writer (idempotent; updates on path change). Mutex guard logic.
- Unit/integration: tray app starts `GatewayHost` and `/healthz` responds; Quit calls
  `StopAsync` and releases the port.
- Manual: login autostart works; double-launch surfaces the single instance; closing any
  window leaves the tray icon running.

## 8. Open questions

- Install location for the tray exe and who writes the Run key (the app itself on first
  run, or the setup tool `cc-director-setup-avalonia`)?
- Icon asset: reuse a CC Director icon or a distinct gateway icon for the tray?
- Should Restart also re-assert Tailscale serve mappings (it already does via
  `GatewayHost.StartAsync` -> provisioner)?
