# Phase 6, track B - the Director starts at sign-in and comes back after a crash

## What was asked

On 16 October 2026 a Director ran out of memory, died, and stayed down until the owner noticed. While it was
down, its machine could not be switched to another license. The brief asks what exists today, on each platform,
to start the Director at sign-in and to start it again after a crash:

- If the product installs that and it does not restart after a crash, close the gap in the product.
- If it is the machine's own setup, write down exactly what the owner sets.
- Either way, a deliberate quit must stay quit, and nothing may fight Smart Restart.

## The answer

**The product installs nothing that starts the Director at sign-in on a signed-in machine, and nothing that
restarts it after a crash, on any platform.** It does install a LAUNCHER (the tray program) on Windows and
macOS, and it starts that at sign-in.

- **Windows and macOS:** the Tech Lead ruled that the product closes the gap through the launcher it already
  installs. That is pull request #4118: the installed launcher starts a crashed Director again, never after a
  quit, and follows the Director through updates and Smart Restart. There, nothing is left for the owner to
  set, and nothing should be set: an owner's own restart setup would only add a second thing starting the
  Director.
- **Linux:** no launcher is installed, so nothing in the product can do it. This is the machine's own setup.
  The owner's instructions are below, with the one setup file committed beside this document and checked by
  `director-restart/prove-restart-setup.ps1`. No product code is changed by this pull request.

An earlier version of this pull request also gave the owner a Windows scheduled task and a macOS launch agent.
They were removed after review, because #4118 does the same job better, and the review found two defects in
them (see `review-4117.md` and `review-4117-answers.md`).

## What exists today (read on origin/main at 0702c88ce)

| | Starts at sign-in | Restarts after a crash |
|---|---|---|
| **Windows** | The LAUNCHER, from the per-user Run key (`LauncherCore.RegisterAutostartSafe`, written with `--at-login`). The launcher opens the Director only when the machine has never signed in (`NeverSignedInOpener`). | Nothing today; the launcher, with #4118. |
| **macOS** | The LAUNCHER, from the launch agent `com.devthrottle.cc-launcher` (`LauncherLaunchdAutostart`, RunAtLoad). Same never-signed-in rule for the Director. | Nothing today; the launcher, with #4118. |
| **Linux** | Nothing. No launcher is installed (`RegisterAutostartSafe` records "no mechanism on this platform"); the Director gets an app-menu entry only (`LinuxDesktopEntry`). | Nothing. |

The Gateway, not the Director, is the one program the product already restarts after a crash: a systemd user
unit with `Restart=on-failure` on Linux, and a launch agent on macOS.

## How the Director ends, which is what the Linux restart rule is built on

On Linux the owner's unit decides by the exit code, which systemd can read because it starts the Director
itself. Every way the Director ends ON PURPOSE exits with code 0, and every crash does not. This was read out of
the code, not assumed, and the review checked every row against the code:

| How it ends | Exit code | Where |
|---|---|---|
| The person quits it (File, Exit, the window's close, the tray) | 0 | `App.OnShutdown` ends with `Environment.Exit(0)` |
| Its own shutdown signal | 0 | the same `OnShutdown` path |
| It hands over to an update it is installing at start-up | 0 | `Program.Main`, after `TryApplyStagedUpdateAtStartup` |
| It rolls back a bad update and starts the restored build | 0 | `Program.Main`, the rollback block |
| The instance picker hands over to another instance | 0 | `App`, `desktop.Shutdown()` |
| It cannot start at all (a fatal start-up error, shown in a message) | 1 | `Program.Main` catch, and `desktop.Shutdown(1)` |
| A second copy for the same instance | 0 on Windows (it brings the first one's window forward); **1 everywhere else** | the single-instance guard; `TryRaiseExistingWindow` is Windows-only |
| An unhandled error kills it | not 0 (the .NET crash code) | the runtime |
| Something kills it: an out-of-memory kill, `kill -9` | killed by a signal | the operating system |

Logging out sends SIGTERM. systemd counts SIGTERM as a clean stop; a .NET program that handles SIGTERM can also
report exit code 143, so the unit lists 143 as clean too.

**A second copy exits with 1 on Linux.** The guard keeps a second copy from running, but it cannot bring the
first copy's window forward off Windows. So it shows "Director is already running" and exits with 1. A unit
that restarts on failure would read that as a crash and start it again, up to its limit, with a dialog each
time. The unit therefore checks, before every start including every restart, whether a Director is already
running for this account (`ExecCondition`). If one is, systemd skips the start: that is not a failure, and it is
not retried. This was the review's second finding, and it is how the unit avoids that loop.

**"A Director" means ANY process named `cc-director` that this account runs**, not only the installed one. That
includes three cases (review of #4117, round two):

- a named instance;
- a development build run from a repository;
- the update relauncher (`--apply-update`), which runs for a few seconds.

While any of them runs, the unit skips starting the installed Director. For the relauncher that is a moment. For a
development build it lasts as long as that build runs, and then nothing watches the installed Director. On a
machine where development builds run, start the installed Director by hand after closing them, or accept that it
is not watched meanwhile.

A fatal start-up error also exits with 1, and restarting is the wrong answer to it. The restart limit stops
that: after five starts in ten minutes, systemd gives up.

**One case is not a crash to the unit or to anything else.** An error on the Director's user-interface thread
is caught and logged, and the Director stays running. A Director that is up but broken in that way is not
restarted by anything. That is existing behaviour, outside this brief.

## How a deliberate quit stays quit

The unit restarts only on a non-zero exit code or an unclean signal (`Restart=on-failure`). Every deliberate end
of the Director exits with 0, or with SIGTERM's 143, which the unit lists as clean (`SuccessExitStatus=143`).
So none of these is restarted:

- a quit;
- an update handover;
- a rollback handover;
- logging out;
- `systemctl --user stop`.

## How this does not fight Smart Restart or an update

**Smart Restart.** Smart Restart's restart goes through the launcher, and Linux has none. So there is no Smart
Restart to fight on Linux.

**An update.** The Director installs a staged update itself, at start-up: it starts the new build and exits with
0. The unit reads that as a deliberate end and does not restart it.

- **The new build survives the handover.** By default, systemd kills every process the unit started when the
  main process exits, which would take the new build with it, and every agent session too. `KillMode=process`
  turns that off.
- **The cost:** the new build is no longer watched by the unit until the next sign-in. If it crashes before
  then, it stays down, as it does today.

**Two Directors.** The start condition means the unit never starts a Director beside one that is already
running. That covers one opened from the app menu, or one that took over after an update.

## The owner's instructions (Linux)

These change the owner's machine, so the owner makes them. Nothing here was set on any real machine.

1. Copy `director-restart/devthrottle-director.service` to
   `~/.config/systemd/user/devthrottle-director.service`. It names no user: `%h` is systemd's word for the home
   folder, and `%U` for the account's user number.
2. **Check the search path in the file** (`Environment=PATH=...`). A user unit does not get the desktop's search
   path, and the Director finds the agents it runs on the search path. The file lists the usual install folders.
   If an agent on this machine is installed somewhere else, add that folder to the line.
3. **Make sure the desktop hands its display to systemd.** GNOME and KDE do this themselves. On other desktops,
   run `systemctl --user import-environment DISPLAY WAYLAND_DISPLAY XAUTHORITY` once at sign-in, for example
   from the desktop's own autostart.
4. Run `systemctl --user daemon-reload`, then `systemctl --user enable --now devthrottle-director.service`.
5. **Check it:**
   - `systemctl --user status devthrottle-director` shows `active (running)`.
   - Start a session in the Director, to confirm it can find the agents.
   - Quit the Director from its menu. The status now shows `inactive (dead)` with `status=0/SUCCESS`, and the
     Director is not started again.
6. **To undo:** run `systemctl --user disable --now devthrottle-director.service`, then delete the file.

Notes:

- It needs systemd 243 or later (for `ExecCondition`) and `pgrep`. Every current Ubuntu, Debian and Fedora
  release has both.
- The start is skipped while ANY `cc-director` of this account runs: a named instance or a development build
  counts, not only the installed Director.
- Do not also keep an XDG autostart entry for the Director. The unit's start condition would skip its own start
  if the autostart one came first, and then nothing would watch the Director.
- After a quit, the unit stays stopped until the next sign-in or `systemctl --user start devthrottle-director`.
  A Director opened from the app menu after that is not watched.

## Proof

`missions/license-switching/phase-6/track-b/director-restart/prove-restart-setup.ps1 -Wsl`, run on Windows with
WSL (systemd 255) on 17 October 2026:

```
PASS Restarted only on failure, so exit 0 and SIGTERM stay stopped (Restart=on-failure)
PASS A SIGTERM reported as 143 counts as a stop on purpose
PASS Sessions survive the Director exiting (KillMode=process)
PASS A crash loop gives up (StartLimitBurst set)
PASS Started with the desktop (WantedBy=graphical-session.target)
PASS No second Director: the start is skipped while one runs (ExecCondition with pgrep)
PASS The agents can be found: the search path names the user install folders
PASS WSL: the check ran in strict mode (set -u took effect, no stray first line)
PASS WSL: with no Director running the condition lets the start go ahead (exit 0)
PASS WSL: with a Director running the condition skips the start (exit 1)
All checks passed.
```

What the checks are:

- **The first seven read the unit** and prove that it:
  - restarts only on failure;
  - treats a SIGTERM's 143 as a stop on purpose;
  - leaves the sessions alive;
  - gives up on a crash loop;
  - starts with the desktop;
  - skips its start while any Director of this account runs;
  - puts the agents' install folders on the search path.
- **The last three run in WSL**, from a script file written without a byte-order mark.
  - The first proves the script ran in strict mode (`set -u`).
  - The other two run the unit's own start condition, copied from the file: once with nothing running, which lets
    the start go ahead, and once with a throwaway process named `cc-director` running, which skips it.

  Nothing is registered or started as a service.
  - **The fix:** the first version piped the script into `sh`. PowerShell put a byte-order mark in front of it,
    which turned the first line into an unknown command, so `set -u` never ran (review of #4117, round two).
  - **The check can fail:** with the file written WITH a mark again, the strict-mode check reports FAILED.

**The checks can fail.** With the `!` taken out of the start condition, the same script reported 3 checks
FAILED and exited 1. The file was then restored. (The first version of this pull request showed the same for
`Restart=always` and `KillMode=control-group`, which the same checks still compare exactly.)

**systemd itself accepts the unit.** `systemd-analyze --user verify` (systemd 255, in WSL, on a copy in a
temporary folder) raised one error: the Director is not installed in that WSL. That is expected, and it had no
complaint about any setting.

**What was NOT proven.** The unit was not enabled on a real Linux desktop session, and the brief forbids changing
a real machine. It is proven there only by the instructions' own "Check it" step, when the owner sets it up. Two
things are relied on, not observed:

- systemd's documented behaviour of `Restart=on-failure`, `KillMode=process` and a skipping `ExecCondition`;
- the guard's exit code 1 off Windows, which was read from the code, not run.

## Findings

1. **The memory leak was not looked at.** It is out of scope, and nothing about it was obvious from this work.
