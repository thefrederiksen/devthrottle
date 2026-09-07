# Linux evidence — 2026-09-06

Frames captured of the Director running on Linux, plus a plain statement of what they do and do
not establish. They exist because a cross-compile that produces an ELF binary says nothing about
whether that binary runs.

## The environment these were taken in

| | |
|---|---|
| Host | SORENLAPTOP, Windows 11 **Home** |
| Guest | Ubuntu 24.04.4 LTS, kernel 6.6.87.2, systemd as PID 1, 8 CPUs |
| Display | WSL 2.5.10 with **WSLg 1.0.66** (`DISPLAY=:0`, `wayland-0`) |
| Runtime | .NET 10.0.11 ASP.NET Core, framework-dependent |
| Built from | `origin/main` @ `6fd25597`, RID `linux-x64`, single-file |

Hyper-V was not an option on this host: Windows 11 Home does not ship the role at all — `vmms.exe`
and `virtmgmt.msc` are absent from disk and `Get-VM` does not exist. That is an edition limit, not
a disabled feature.

## The frames

### `director-main-window.png` — 1400x900

The Director's main window: menu bar, session sidebar, repository and browser-profile controls,
gateway status, and the version stamp `v2.0.6 (6fd2559)` matching the commit it was built from.
Skia, HarfBuzz and fontconfig are all working — text shapes and lays out correctly.

It also honestly shows a real degradation rather than hiding it: **"DevThrottle tools — the shared
runtime the tools need cannot start"**. The Python tools bundle has no Linux build yet.

### `setup-wizard-step1.png` — 900x640

The first-run setup wizard, step 1 of 8, rendering in its light theme.

## How they were captured

Window ids were matched by title with `xdotool search --name`, geometry read back from
`xdotool getwindowgeometry`, and each window captured with ImageMagick `import -window <id>`.

This follows the rule that matters from the `demo-vm` capture guidance: **assert a window handle
with a matching title, then look at the frame.** No brightness or ink threshold was used as a pass
condition — a wallpaper passes those. Two details did not transfer from the Windows-guest rules and
are recorded here for the next person: WSLg has no grabbable X root window (`import -window root`
fails with "Resource temporarily unavailable"), and the window is titled **"DevThrottle Director"**,
not "CC Director".

## What these frames do NOT prove

- **Not a desktop-environment session.** WSLg paints windows onto the Windows desktop through its
  own compositor. Window decorations, system tray, `.desktop` launcher and autostart under GNOME
  are all untested.
- **No agent session.** The central claim — a session actually driving an agent inside Linux — is
  *not* shown here. No agent command-line tool was installed in this environment.
- **Captured as root.** A separate non-root run was done for the storage-root fix, but these two
  frames were taken as root.
- **x64 only.** `linux-arm64` builds but has never been run.

A real desktop virtual machine with a normal user account and an agent installed is required before
any of the above can be claimed.
