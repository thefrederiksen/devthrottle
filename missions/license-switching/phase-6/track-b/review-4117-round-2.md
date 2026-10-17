# Review of pull request 4117, round two - Linux only, a systemd user unit

Reviewer: a separate review session, a different agent family from the Developer, opened by the Track B Tech Lead. Read only. Nothing in the worktree was edited, built or committed.

## Scope

**What I read.** The whole of the new head bf0d8bac7 against its base 0702c88ce: `director-restart.md`, `director-restart/devthrottle-director.service`, `director-restart/prove-restart-setup.ps1`, `review-4117-answers.md`, and my own round-one review as the Developer committed it. The macOS launch agent and the Windows watch script are gone. Outside the diff, to check the claims the document makes: `LauncherRestartClient.cs` (what Smart Restart does when a launcher is or is not running), the Director's exit paths and the second-copy guard as read in round one, and the `--apply-update` relauncher.

**What I ran.** The proof script, copied to my own scratchpad folder (never from the worktree): `prove-restart-setup.ps1 -Wsl`, twice. The first run died with `System.OutOfMemoryException` at a trivial string split on line 34; the machine had about 3.2 GB free of 64 GB at the time, and the second run, a minute later, passed all 9 checks with exit 0. I count the first run as the machine, not the script. I swept the new head's diff for real names, machine names, addresses, email domains and user paths.

**What I could not reach.** A real Linux desktop with systemd 243 or newer running a graphical session. WSL Ubuntu on this machine has systemd but no `graphical-session.target`, so the unit's install target, `PartOf` and `After` lines were read, not exercised. Neither was `Restart=on-failure` itself: no Director was crashed under the unit.

## Verdict

**Both round-one findings are answered and the Linux-only shape is sound. Nothing must change before merge.** One observation about the proof transcript (a byte-order mark reached `sh`, so one line of the WSL check did not run, and the committed transcript does not show the error my run printed), and three about the start condition that the document should carry so an operator is not surprised.

## Round-one findings, re-read

### Finding 1 - the macOS launch agent bypassed the bundle's `launch` entry point: ANSWERED

The launch agent is gone with the macOS scope. The same hazard existed in a different form on Linux - a unit that starts the binary directly gets systemd's minimal PATH, not the login shell's - and the unit answers it with `Environment=PATH=%h/.local/bin:%h/.claude/local:%h/.npm-global/bin:/usr/local/bin:/usr/bin:/bin`, and the document has a step telling the operator to extend that line if their agent binaries live elsewhere. Correct.

### Finding 2 - "a second copy exits 0" was Windows only: ANSWERED

The exit table now says a second copy exits 0 on Windows and 1 everywhere else, which matches the code (`TryRaiseExistingWindow` is Win32 only, so the non-Windows path falls through to exit 1). And the unit no longer depends on the second copy's exit code at all: `ExecCondition=/bin/sh -c '! pgrep -u %U -x cc-director'` skips the start when a Director is already running, so the unit never starts a second copy in the first place. Since systemd 243 an `ExecCondition` exit in 1 to 254 skips the start without counting as a failure, so a person who starts the Director by hand does not burn a restart budget entry. Correct.

## The unit, read line by line

`Restart=on-failure` with `SuccessExitStatus=143` means a clean quit (exit 0), SIGTERM and a stop through systemd itself do not restart, and a crash (any other non-zero, or a signal) does. `RestartSec=10`, `StartLimitIntervalSec=600` and `StartLimitBurst=5` in `[Unit]` give five starts in ten minutes before the unit stands down, which is the same budget pull request 4118 uses. `KillMode=process` leaves the Director's children (the agent sessions) alive when the Director is stopped by systemd, which is what Smart Restart and an update handover need. `%h` and `%U` are the user-unit specifiers for the home folder and the user id; the paths resolve to the installed app under `~/.local/share/cc-director/app`. `WantedBy=graphical-session.target` with `PartOf` and `After` ties it to the desktop session. All as the document describes.

The document also says that Smart Restart goes through the launcher and so does not trip the unit. I read `LauncherRestartClient`: it raises the named signal `LauncherRestartDirector` and, when nothing is listening, returns a sentence saying no launcher is running and that closing and reopening installs the update. On a Linux machine with no launcher the person therefore quits (exit 0, no restart) and the unit's `ExecCondition` lets the next manual start through. Consistent, though not run.

## Observations

1. **The proof transcript omits an error my run printed.** On the second run the WSL check printed `sh: 1: set: not found` with an invisible character before `set`: the byte-order mark. `prove-restart-setup.ps1` sets `$OutputEncoding` to UTF-8 without a mark, which is the right intent, but the script text that reaches `sh` over the pipe still begins with one, so `sh` treats the first line as a command named `<mark>set` and fails to find it. The result is that `set -u` never ran inside the WSL check, so an unset variable in that check would expand to empty instead of failing. The nine checks still passed on their own merits. The committed transcript in `director-restart.md` shows the 9 PASS lines and no `set: not found` line; either the Developer's environment did not emit the mark or the line was left out. Worth one of: strip the mark where the text is piped, or drop the `set -u` line and say so.
2. **`pgrep -x cc-director` matches any process of that name belonging to the user.** A named-instance Director, a development build run from a repository, and the short-lived `--apply-update` relauncher are all `cc-director`. While any of them runs, the unit skips starting the installed one. For the relauncher this is a few seconds; for a development Director it is as long as it runs. The document should say "skips the start while ANY Director of this user runs", not only the installed one.
3. **After an update handover the new build is unwatched until the next login.** The document already says this. It follows from `KillMode=process` and the unit tracking only the process it started.
4. **The gate was not re-run and did not need to be.** Nothing outside `missions/` changed in this head.
5. **No real name, machine name, address, email domain or user path** in the new head's diff. Placeholders only.
