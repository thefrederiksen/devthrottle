# Review of pull request 4117 - the Director's start at sign-in and crash restart (Track B, phase 6)

Reviewer: a separate review session, a different agent family from the Developer, opened by the Track B Tech Lead (session 111). Read only. Nothing in the worktree was edited, built or committed.

## Scope

**What I read.** The whole diff of 4117 against 6c783e3d4 (origin/main at the time of review): the proof file `director-restart.md`, `watch-director.ps1`, `com.devthrottle.director-keepalive.plist`, `devthrottle-director.service` and `prove-restart-setup.ps1`. Against the claims, on the same commit: `src/CcDirector.Avalonia/Program.cs` (every exit path, the single-instance guard, `TryRaiseExistingWindow`, the unhandled-exception hooks), `App.axaml.cs` (`OnShutdown`, the shutdown signal, the fatal start-up path, the exception handlers), `SingleInstanceGuard.cs`, `DirectorSupervisor.cs` (how the launcher starts, stops and force-kills the Director, per platform), `LauncherCore.RegisterAutostartSafe`, `InstallLayout.cs` (the Director's path per platform), `LinuxDesktopEntry.cs`, `ClaudeAgentPlugin.cs` (how the Director finds an agent binary), `scripts/package-mac-app.sh` (what the bundle's entry point is) and `scripts/install-mac.sh`.

**What I ran.** `prove-restart-setup.ps1`, from a COPY of the four files in my own scratchpad folder, outside the worktree: 14 PASS, exit 0, no FAIL. Nothing was registered, loaded or started on this machine, and the script writes only under TEMP and deletes it. I swept the diff for real names, machine names, addresses, email domains and user paths.

**What I could not reach.** launchd and systemd behaviour (I am on Windows; the proof says the same). The claim that a Director started by a hidden `powershell.exe` under the Task Scheduler has no pseudo console in its ancestry. Whether the scheduled task registers as written (registering it is forbidden here).

## Verdict

**Two findings that must change before the instructions are handed to the owner; the rest holds.** The exit-code table is true in the code, no deliberate end is restarted, and nothing real is in the files. But the macOS instructions point the agent at the wrong file inside the bundle, and the claim that a second copy "finds the first, shows its window and exits with 0" is true only on Windows - on macOS the agent as written can loop every 30 seconds showing a dialog, forever.

## The questions the brief asked, answered from the code

### Are the exit-code claims true?

**Yes, every row of the table.** Checked at these places (all on 6c783e3d4):

| Claim | Where | Code |
|---|---|---|
| A quit, the window close, the tray exit end with 0 | `App.axaml.cs:1131` | `OnShutdown` ends in `Environment.Exit(0)` |
| The shutdown signal ends with 0 | `App.axaml.cs:769` -> the programmatic shutdown -> `OnShutdown` | same `Environment.Exit(0)` |
| The staged update at start-up hands over with 0 | `Program.cs:234-237` | `return 0` after `TryApplyStagedUpdateAtStartup` |
| A rollback relaunch ends with 0 | `Program.cs:218` | `return 0` |
| A second copy that raises the first one's window ends with 0 | `Program.cs:162` | `return 0` |
| A second copy that cannot raise it ends with 1 | `Program.cs:176` | `return 1` |
| A fatal start-up error ends with 1 | `Program.cs:272`, `App.axaml.cs:992` | `return 1`, `desktop.Shutdown(1)` |
| A crash does not end with 0 | `Program.cs:66-72`, `App.axaml.cs:340-346` | the hooks log and flush; neither swallows, so the runtime's own code is kept |

Also true but not in the table: the instance picker's `desktop.Shutdown()` (`App.axaml.cs:107`) exits 0, a deliberate end; a failed `--apply-update` relauncher exits 1 (`Program.cs:115`), but that is the relauncher process, not the watched Director.

One thing the table cannot show: UI-thread exceptions are marked handled (`App.axaml.cs:360-364`), so a Director broken on its UI thread stays ALIVE and is not a crash in any setup's eyes. That is existing behaviour and outside the brief; I note it because an operator reading "a crash is restarted" may expect it to cover a Director that is up but dead.

### Can the instructions as written start a SECOND Director beside a running one?

**Yes, and on macOS and Linux the outcome is not what the proof says.** The proof's claim, under "How this does not fight Smart Restart or an update", is that a second copy "finds the first through the single-instance guard, shows its window and exits with 0, and the setup stands down. The result is one Director, never two." The guard is cross-platform (a named mutex, `SingleInstanceGuard.cs`), but the window raise is not: `TryRaiseExistingWindow` returns false on anything but Windows (`Program.cs:382-388`, "Win32-only"), so off Windows a second copy shows the "Director is already running" notice and exits 1. See finding 2.

The ways a second copy arises with these setups, from the code:

- **First sign-in on a never-signed-in machine.** The launcher opens the Director there (the proof's own table), and the agent or task starts one too. One of them loses the guard.
- **The launcher's force-kill while restarting.** The proof names it: a non-zero exit, then the setup's copy 10 or 30 seconds later, by which time the launcher's copy holds the guard.
- **The owner opening the Director by hand** while the setup's copy runs: that copy is the hand-started one and exits with a notice. Existing behaviour, not the setups'.

### Can the instructions restart a Director the person quit?

**No, on all three platforms**, as long as the exit code is 0, which the table above confirms: `watch-director.ps1` exits on code 0; `KeepAlive` with `SuccessfulExit false` restarts only on a non-zero exit; `Restart=on-failure` restarts only on an unclean code or signal, with 143 added as clean. The proof script runs the Windows watcher against a fake Director and I watched it pass.

### Is anything real in the instructions?

**No.** The sweep found no real name, machine name, address, domain or user path in the diff; the only account name is the placeholder `alice`, labelled as such each time; the owner is "the owner".

## Findings

### Finding 1 - the macOS agent bypasses the bundle's entry point, so the Director it starts has launchd's PATH

The plist runs `/Users/alice/Applications/Director.app/Contents/MacOS/cc-director`, and the proof tells the owner it "must point at the binary inside the application (`Director.app/Contents/MacOS/cc-director`)". But `cc-director` is not the bundle's entry point. `scripts/package-mac-app.sh` lines 116-134 write `Contents/MacOS/launch` and set `CFBundleExecutable` to it (line 148). `launch` exists for exactly this case - its own comment reads "Apps launched by launchd inherit only a minimal PATH, so prepend the common dirs where `claude`/homebrew tools live" - and it prepends `~/.local/bin`, `~/.claude/local`, `/opt/homebrew/bin`, `/opt/homebrew/sbin` and `/usr/local/bin`, changes to the bundle folder, then `exec`s `cc-director`.

**The harm.** A Director started by this agent gets launchd's PATH (`/usr/bin:/bin:/usr/sbin:/sbin`). The Director finds an agent binary on PATH, at the npm global path, or at `~/.local/bin/claude` (`ClaudeAgentPlugin.cs:26-31`) - nothing else. So Claude Code installed at `~/.claude/local`, and Codex or Gemini from Homebrew, are not found, and sessions on that machine cannot start after a crash restart. That is the Director the setup exists to bring back, and it comes back unable to work. The proof's "Check it" step (`state = running`, `last exit code = 0`) would pass.

**What must change** (the Developer decides how): point `ProgramArguments` at `.../Contents/MacOS/launch`. Because `launch` ends in `exec`, the process id is unchanged and launchd still sees the Director's exit code, so every other property of the agent holds. The proof's sentence about which file to name must change with it.

**Linux has a smaller version of the same gap.** A systemd user unit gets the user manager's PATH, which does not include `~/.local/bin`, and the instructions import only `DISPLAY`, `WAYLAND_DISPLAY` and `XAUTHORITY`. The Director's own probe of `~/.local/bin/claude` covers the official Claude Code installer, so Claude Code works; an agent installed anywhere else off that PATH does not. One `Environment=PATH=%h/.local/bin:%h/.claude/local:/usr/local/bin:/usr/bin:/bin` line, or importing PATH beside the display variables, closes it. I leave the weight of this to the Tech Lead; the macOS case is the one that breaks the product's own default install.

### Finding 2 - "a second copy exits with 0" is Windows-only; on macOS the agent can loop forever

`TryRaiseExistingWindow` returns false off Windows (`Program.cs:382-388`), so a second copy there exits 1 after showing a notice (`Program.cs:165-176`). With `KeepAlive SuccessfulExit false`, launchd treats exit 1 as a failure and starts the Director again after `ThrottleInterval` (30 seconds); launchd has no give-up limit (the proof says so itself), so the loop is endless: every 30 seconds a new copy, a "Director is already running" dialog, exit 1, until the owner runs `launchctl bootout`. The two triggers are in the section above: first sign-in on a never-signed-in machine, and the launcher's force-kill restart that the proof describes as handled. On Linux the same sequence stops after `StartLimitBurst` (5 in 10 minutes), with five dialogs. On Windows the raise can also fail - when the running copy has no main window handle yet (it is on its splash) or none at all - and the watcher then restarts up to five times, each with a modal box the owner must dismiss.

**The harm.** On macOS, an endless dialog every 30 seconds on the owner's screen, from the very setup meant to keep the machine quiet. The proof asserts the opposite for all platforms.

**What must change.** Either the proof's claim becomes "Windows only; on macOS and Linux a second copy exits 1 and the setup retries, which is why the first-sign-in race and the force-kill restart matter there", with the instructions saying what to do (for example `launchctl bootout` and a note that the launcher must not also open the Director), or the product makes a second copy off Windows exit 0 when it finds a live first copy - which is a product change the Developer's brief does not grant, so it is the Tech Lead's call. At minimum the claim must not stand as written.

## Checked and found correct

- **Platform table.** The launcher is the only thing started at sign-in on Windows (Run key) and macOS (launch agent); `RegisterAutostartSafe` records "no mechanism" on Linux (`LauncherCore.cs:269-281`); the launcher opens the Director only on a never-signed-in machine; the launcher starts the Director with `UseShellExecute` on Windows and `/usr/bin/open` on macOS (`DirectorSupervisor.cs:114-116`), so the Director is never a child of the launcher's supervision in a way a crash would reach. The launcher force-kills only after a timeout and only a certified image (`DirectorSupervisor.cs:401-410, 696`).
- **Linux path.** `%h/.local/share/cc-director/app/cc-director` matches `InstallLayout.AppDir` + `cc-director` (`InstallLayout.cs:79, 208-214`) when `XDG_DATA_HOME` is unset; `%h` names no user.
- **The unit's settings.** `StartLimitIntervalSec` and `StartLimitBurst` are in `[Unit]`, where current systemd wants them; `Restart=on-failure` leaves SIGTERM and exit 0 alone; `SuccessExitStatus=143` covers a .NET process ending on SIGTERM; `KillMode=process` leaves the sessions and an updated build alive; `PartOf`/`WantedBy=graphical-session.target` ties it to the desktop.
- **The plist's settings.** `KeepAlive { SuccessfulExit false }` is the documented "restart only on a bad exit"; `RunAtLoad`, `ThrottleInterval 30`, `AbandonProcessGroup`, `LimitLoadToSessionType Aqua` all read as the proof says. The label differs from the launcher's and the Gateway's.
- **The Windows task.** `-ExecutionTimeLimit ([TimeSpan]::Zero)` is the documented way to remove the 72-hour stop; `-MultipleInstances IgnoreNew` prevents two watchers; the task is per-user, interactive, not elevated. `watch-director.ps1` waits on the process it started and reads its exit code, keeps a rolling ten-minute restart count and gives up at five; `-DirectorArguments` is only passed when set.
- **The watcher's "stands down after a handover" cost** is stated plainly in the proof and in the script's own header, with the fix direction (finding 1 in the proof: the launcher should supervise crashes, which survives handovers) left to the Tech Lead. I agree that is the only design that does.
- **The proof script's checks can fail.** I did not repeat the mutation run; the checks compare exact values (`-eq 'false'`, `-eq 'on-failure'`), so a dropped setting fails them.
- **Gate.** No code outside `missions/` changed, so the default gate on the base is the right evidence.

## Observations (no action required)

1. The Windows instructions' Check step says the Director "opens" at sign-in. On a signed-in machine that is now the watcher's doing alone, and on a never-signed-in machine two things open it (finding 2's first trigger). A one-line note would spare the owner a surprise dialog on a fresh install.
2. `director-restart.md` says "it is believed to have been the memory leak"; the Developer's brief says out of memory was the cause on that machine. Either is fine; the two documents should say the same.
3. On macOS, after the owner quits (exit 0) the agent stands down until the next sign-in or a `launchctl kickstart`; the Finder-started Director that follows is unwatched. The proof's design accepts this; the instructions could say it in one line.
