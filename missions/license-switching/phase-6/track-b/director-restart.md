# Phase 6, track B - the Director starts at sign-in and comes back after a crash

## What was asked

On 16 October 2026 a Director died - it is believed to have been the memory leak - and stayed down until the
owner noticed. While it was down its machine could not be switched to another license. The brief asks: what
exists today, on each platform, to start the Director at sign-in and to start it again after a crash? If the
product installs that and it does not restart after a crash, close the gap in the product. If it is the
machine's own setup, write down exactly what the owner sets, per platform. A deliberate quit must stay quit,
and nothing may fight Smart Restart.

## The answer in one paragraph

**The product installs nothing that starts the Director at sign-in on a signed-in machine, and nothing that
restarts it after a crash, on any platform.** What it does install starts the LAUNCHER (the tray program), and
the launcher opens the Director at sign-in only on a machine that has never signed in. The launcher can stop,
start and restart the Director, but it deliberately does not restart one that crashed. So there is no
product mechanism with a gap to close here. This is the machine's own setup, and the owner's step-by-step
instructions are below, one section per platform. The three setup files are committed beside this document
and proven by `director-restart/prove-restart-setup.ps1`, which changes nothing on any machine. No product
code was changed.

## What exists today (read on origin/main at 6c783e3d4)

| | Starts at sign-in | Restarts after a crash |
|---|---|---|
| **Windows** | The LAUNCHER, from the per-user Run key (`LauncherCore.RegisterAutostartSafe`, written with `--at-login`). The launcher opens the Director only when the machine has never signed in (`NeverSignedInOpener`). | Nothing. |
| **macOS** | The LAUNCHER, from the launch agent `com.devthrottle.cc-launcher` (`LauncherLaunchdAutostart`, RunAtLoad). Same never-signed-in rule for the Director. | The launch agent restarts the LAUNCHER (KeepAlive with SuccessfulExit false). Nothing restarts the Director. |
| **Linux** | Nothing. No launcher is installed (`RegisterAutostartSafe` returns "no mechanism on this platform"); the Director gets an app-menu entry only (`LinuxDesktopEntry`). | Nothing. |

The Gateway, not the Director, is the one program the product restarts after a crash: a systemd user unit
with `Restart=on-failure` on Linux (`GatewaySystemdAutostart`) and a launch agent on macOS
(`GatewayLaunchdAutostart`).

`DirectorSupervisor` in the launcher is the closest thing the product has. It stops the Director with its
named shutdown signal (and force-kills it only after a timeout, and only when the process is certified as the
installed image), starts it, and restarts it to install an update or answer Smart Restart. It never watches
for a crash.

## How the Director ends, which is what the restart rule is built on

Every way the Director ends ON PURPOSE exits with code 0. Every crash does not. That is the whole rule, so it
was read out of the code, not assumed:

| How it ends | Exit code | Where |
|---|---|---|
| The person quits it (File, Exit, the window's close, the tray) | 0 | `App.OnShutdown` ends with `Environment.Exit(0)` |
| Its own shutdown signal (the launcher's clean stop, a test rig's teardown) | 0 | the same `OnShutdown` path |
| It hands over to an update it is installing at start-up | 0 | `Program.Main`, after `TryApplyStagedUpdateAtStartup` |
| It rolls back a bad update and starts the restored build | 0 | `Program.Main`, the rollback block |
| A second copy for the same instance finds the first and shows its window | 0 | the single-instance guard |
| It cannot start at all (a fatal start-up error, shown in a message) | 1 | `Program.Main` catch, and `desktop.Shutdown(1)` |
| A second copy that cannot show the first one's window | 1 | the single-instance guard |
| An unhandled error kills it | not 0 (the .NET crash code) | the runtime |
| Something kills it: Task Manager, an out-of-memory kill, the launcher's force-kill after a timeout | not 0, or killed by a signal | the operating system |

On Linux and macOS, logging out sends SIGTERM. systemd counts SIGTERM as a clean stop; a .NET program that
handles SIGTERM can also report exit code 143, so the Linux unit lists 143 as clean too.

Two exits are worth naming because they are code 1 and a restart is the wrong answer to both: a fatal
start-up error, and a second copy that cannot raise the first. The restart limit in every setup below is what
stops these from spinning: after five starts in ten minutes it gives up and says so.

## How a deliberate quit stays quit

**Every setup below restarts only on a non-zero exit, and every deliberate way the Director ends exits with
0.** So a quit is never restarted, and neither is an update handover, a rollback handover, or a clean stop by
the launcher.

The Windows proof runs the watcher against a fake Director, and checks that each of these happens:

- an exit with 0 is started once and never again;
- a crash is restarted, and the quit after it is not;
- a forced kill is restarted;
- a crash loop gives up at the limit;
- a wrong path starts nothing.

For macOS and Linux the proof checks the setting that carries the rule: `KeepAlive` with
`SuccessfulExit false`, and `Restart=on-failure`. Each check was shown to fail when that setting is changed.

## How this does not fight Smart Restart or an update

There are two ways a restart setup could fight the product. Both are handled.

**1. The setup could kill the processes the Director leaves behind.** When the Director's own process exits,
both macOS and systemd kill every process it started, by default. That would end every agent session and the
updated build an update has just handed over to. The setups turn this off: `AbandonProcessGroup` on macOS,
`KillMode=process` on Linux. The Windows Task Scheduler kills nothing when the watched process exits. It
does stop a task after 72 hours by default, though, so the instructions turn that limit off.

**2. The setup could start a second Director beside one the product just started.**

- When the product restarts the Director itself (Smart Restart through the launcher, an update, a rollback),
  the old process exits with 0. The setup therefore stands down; it does not start a second one.
- If the launcher ever has to force-kill a stuck Director while restarting it, the setup sees a non-zero exit
  and starts the Director after its pause. By then the launcher has started one already. The second copy finds
  the first through the single-instance guard, shows its window and exits with 0, and the setup stands down.
  The result is one Director, never two.

**The cost, stated plainly.** After any handover - an update, a rollback, a Smart Restart through the launcher
- the Director that took over is no longer watched. The setup watched the process it started, and that
process has gone. The next sign-in restores the watch. If the Director then crashes before the next sign-in,
it stays down, as it does today. An update is the most common handover, so in practice a crash restart covers
the time from sign-in to the next update. See finding 1 for the fix that would remove this cost.

## The owner's instructions

These change the owner's machines, so the owner makes them. Nothing here was set on any real machine. Replace
`alice` with the account's own folder name wherever it appears.

### Windows

The Windows Task Scheduler cannot do this alone. Its "If the task fails, restart every" setting fires only
when the task fails to START. It does not fire when the program it started later exits with an error. So a
scheduled task runs a small watcher at sign-in, and the watcher restarts the Director.

1. Copy `director-restart/watch-director.ps1` to `C:\Users\alice\AppData\Local\cc-director\watch-director.ps1`.
2. Open PowerShell as the account itself, not as an administrator, and run:

   ```powershell
   $script = "$env:LOCALAPPDATA\cc-director\watch-director.ps1"
   $director = "$env:LOCALAPPDATA\cc-director\app\cc-director.exe"
   $action = New-ScheduledTaskAction -Execute "powershell.exe" `
       -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$script`" -DirectorPath `"$director`""
   $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
   # No time limit (the default stops the task, and the Director with it, after 72 hours), and never two at once.
   $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew `
       -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
   $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
   Register-ScheduledTask -TaskName "DevThrottle Director watch" -Action $action -Trigger $trigger `
       -Settings $settings -Principal $principal
   ```

3. Check it: sign out and back in. The Director opens, and
   `%LOCALAPPDATA%\cc-director\logs\director-watch.log` has a line "Started the Director, process N".
   Quit the Director from its menu. The log says "ended on purpose (exit code 0). Not starting it again", and
   it stays closed.
4. To undo: `Unregister-ScheduledTask -TaskName "DevThrottle Director watch" -Confirm:$false`.

Notes:

- The task is a per-user, interactive task. It runs in the signed-in desktop, so the Director has a window.
  It also runs outside any terminal, which is what the Director's child sessions need.
- If the Director is already open when the task fires, the watcher's copy finds it, shows its window and
  exits with 0, and the watcher stands down. Nothing doubles.
- This is the installed Director. A development slot (`cc-director5.exe` and up) is not meant to be watched.

### macOS

1. Copy `director-restart/com.devthrottle.director-keepalive.plist` to
   `~/Library/LaunchAgents/com.devthrottle.director-keepalive.plist`.
2. Edit the path in `ProgramArguments`, replacing `alice` with the account's folder name. launchd does not
   understand `~`, so the path must be written out in full. It must point at the binary inside the
   application (`Director.app/Contents/MacOS/cc-director`), not at `Director.app`. Starting it with `open`
   would hand it to Launch Services, and launchd could no longer see whether it crashed.
3. Load it: `launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.devthrottle.director-keepalive.plist`.
4. Check it: `launchctl print gui/$(id -u)/com.devthrottle.director-keepalive` shows `state = running`.
   Quit the Director from its menu; it stays closed, and `launchctl print` shows `last exit code = 0`.
5. To undo: `launchctl bootout gui/$(id -u)/com.devthrottle.director-keepalive`, then delete the file.

Notes:

- launchd has no "give up after N" setting. `ThrottleInterval` 30 keeps a Director that crashes at once to
  one start every 30 seconds, until the owner boots the agent out.
- The label is deliberately not the launcher's (`com.devthrottle.cc-launcher`) or the Gateway's, so neither
  installer touches it.

### Linux

1. Copy `director-restart/devthrottle-director.service` to
   `~/.config/systemd/user/devthrottle-director.service`. It names no user: `%h` is systemd's own word for
   the home folder.
2. Make sure the desktop hands its display to systemd. GNOME and KDE do this themselves. On other desktops,
   run `systemctl --user import-environment DISPLAY WAYLAND_DISPLAY XAUTHORITY` once at sign-in (for example
   from the desktop's own autostart).
3. `systemctl --user daemon-reload`, then `systemctl --user enable --now devthrottle-director.service`.
4. Check it: `systemctl --user status devthrottle-director` shows `active (running)`. Quit the Director from
   its menu; status shows `inactive (dead)` with `status=0/SUCCESS`, and it is not started again.
5. To undo: `systemctl --user disable --now devthrottle-director.service`, then delete the file.

Notes:

- Do not ALSO keep an XDG autostart entry for the Director; that would start a second copy at sign-in. The
  second copy would find the first and exit, but it is noise.
- `KillMode=process` matters more here than anywhere else. Without it, every agent session dies with the
  Director, and an update's new build is killed the moment the old one hands over.

## Proof

`missions/license-switching/phase-6/track-b/director-restart/prove-restart-setup.ps1`, run on Windows on
17 October 2026:

```
PASS A deliberate quit (exit 0) is not restarted
PASS A crash (exit 3) is restarted, and the quit after it is not
PASS A forced kill is restarted
PASS A crash loop gives up after the restart limit
PASS A wrong Director path starts nothing and says so
PASS macOS: restarted only after an exit that was not 0 (KeepAlive SuccessfulExit false)
PASS macOS: started at sign-in (RunAtLoad true)
PASS macOS: sessions survive the Director exiting (AbandonProcessGroup true)
PASS macOS: at least 10 seconds between starts (ThrottleInterval)
PASS Linux: restarted only on failure, so exit 0 and SIGTERM stay stopped (Restart=on-failure)
PASS Linux: a SIGTERM reported as 143 counts as a stop on purpose
PASS Linux: sessions survive the Director exiting (KillMode=process)
PASS Linux: a crash loop gives up (StartLimitBurst set)
PASS Linux: started with the desktop (WantedBy=graphical-session.target)
All checks passed.
```

What each check is, in one sentence:

- The first five run the real watcher script against a fake Director that exits with chosen codes, and count
  how many times it was started. They prove that a quit is left alone, that a crash and a forced kill are
  restarted, that a crash loop stops, and that a wrong path says so.
- The macOS checks read the launch agent and prove four things: it restarts only after an exit that was not
  0, it starts at sign-in, it leaves the sessions alive, and it spaces starts out.
- The Linux checks read the unit and prove five things: it restarts only on failure, it treats a SIGTERM's 143
  as a stop on purpose, it leaves the sessions alive, it gives up on a crash loop, and it starts with the
  desktop.

**The checks can fail.** With the watcher changed to restart on exit 0, the macOS agent's `SuccessfulExit`
set to true, and the unit changed to `Restart=always` and `KillMode=control-group`, the same script reported
6 checks FAILED and exited 1. The files were then restored.

**systemd itself accepts the unit.** `systemd-analyze --user verify` (systemd 255, in WSL, on a copy in a
temporary folder) raised one error: the Director is not installed at `%h/.local/share/cc-director/app/
cc-director` in that WSL. That is expected. It had no complaint about any setting. It also warned that the
copy was marked executable; that is the Windows drive the copy came from, and does not apply to the file
itself.

**What was NOT proven.** These setups were not run on a real macOS sign-in or on a real systemd desktop
session, and the scheduled task was not registered on any machine; the brief forbids changing a real
machine. These are proven only by the instructions' own "Check it" steps, when the owner sets them up. The
launchd and systemd behaviour these files rely on is the documented behaviour of `KeepAlive SuccessfulExit`,
`AbandonProcessGroup`, `Restart=on-failure` and `KillMode=process`. It was not observed here.

## Findings

1. **The real fix is in the product, and it is a design decision for the Tech Lead.** The launcher already
   supervises the Director on Windows and macOS. It knows the installed image, and it already stops, starts
   and restarts it. If it also restarted a Director that ended with a non-zero exit, the watch would survive
   every handover, which the owner's setup cannot do. That needs the same give-up limit, and the same
   "exit 0 is a quit" rule. It would still leave Linux with nothing, because no launcher is installed there.
   I did not build this: the brief allows a product change only where the product installs the start-up, and
   it installs none for the Director.
2. **The memory leak was not looked at.** It is out of scope, and nothing about it was obvious from this
   work.
