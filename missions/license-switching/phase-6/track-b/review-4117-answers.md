# Answers to the review of pull request 4117

The review is `review-4117.md`. The Tech Lead's ruling with it: pull request #4118 (the launcher starts a crashed
Director again on Windows and macOS) is in, so cut this pull request down to what the launcher does not cover, and
make sure every claim left stands on every platform it names. Every point is answered below, in the review's
order.

## Finding 1 - the macOS agent bypasses the bundle's entry point

**Agreed, and the file is gone.** The reviewer is right. The bundle's entry point is `Contents/MacOS/launch`, which
puts the agents' install folders on the search path before it starts `cc-director`. Pointing launchd straight at
`cc-director` would bring a crashed Director back unable to find Claude Code under `~/.claude/local`, or Codex and
Gemini from Homebrew.

The macOS launch agent was removed from this pull request with the rest of the macOS section, because #4118 does
the job on macOS. #4118 does not have this defect: the launcher starts the Director with `/usr/bin/open` on the
application, which runs the bundle's real entry point.

**The Linux half is fixed in the unit.** It now sets
`Environment=PATH=%h/.local/bin:%h/.claude/local:%h/.npm-global/bin:/usr/local/bin:/usr/bin:/bin`. The
instructions tell the owner to add any other folder an agent is installed in, and to start a session in the "Check
it" step, which proves the agents are found. The proof script checks the line names the user install folders.

## Finding 2 - "a second copy exits with 0" is Windows-only

**Agreed. The claim is gone, and the unit no longer depends on it.** `TryRaiseExistingWindow` is Windows-only, so
off Windows a second copy shows "already running" and exits with 1. The rewritten proof says so in the exit-code
table and in a paragraph of its own.

- **macOS:** the endless 30-second loop the reviewer describes cannot happen now, because the launch agent was
  removed. On macOS the launcher's own watch (#4118) never starts a second Director in that way, for two reasons.
  It starts only when the machine reads as nothing running. And `/usr/bin/open` on an application that is already
  open brings it forward rather than starting a second process.
- **Linux:** the unit now runs `ExecCondition=/bin/sh -c '! pgrep -u %U -x cc-director'` before every start,
  including every restart. With a Director already running for the account, the condition exits with 1 and systemd
  skips the start: not a failure, and not retried. So there is no dialog and no loop. Without a Director running,
  it exits with 0 and the start goes ahead.
  - **Observed, not only read:** the proof script runs that exact command line in WSL. It allows the start with
    nothing running and skips it with a throwaway process named `cc-director` running.
  - **The check can fail:** with the `!` removed, the script reported 3 checks FAILED.
- **Windows:** the reviewer notes the raise can also fail there, when the first copy has no window handle yet.
  That matters to an owner's setup, which is gone. It does not matter to #4118, whose start is skipped while the
  instance is held.

**The first-sign-in trigger** was the launcher opening the Director while an owner's agent or task also did. It
does not arise on Linux, which has no launcher, and the Windows and macOS setups are gone.

## Checked and found correct

Nothing to answer. Two of the items now refer to files that were removed: the plist's settings and the Windows
task.

## Observations

1. **"Opens" at sign-in on Windows, and two openers on a fresh install.** These instructions were removed with
   the Windows section. #4118's watch does not open the Director at sign-in, only after a crash, so it does not
   add a second opener beside the launcher's never-signed-in rule.
2. **"Believed to have been the memory leak" against "out of memory".** Aligned. The proof now says the Director
   ran out of memory, as the brief does.
3. **After a quit the setup stands down until the next sign-in, and a Director opened afterwards is unwatched.**
   That case is now Linux only, and it is said in one line in the instructions' notes.

## The note about the user-interface thread

A Director whose user-interface thread throws stays alive, so nothing sees it as a crash. Agreed, and it is now
said in the proof, as existing behaviour outside this brief.

## Gate

This pull request still changes nothing outside `missions/`, and nothing in it is built or tested by the gate,
so the gate was not run again on the rebased head. What it would test is exactly origin/main 0702c88ce, which
pull request #4115's gate runs cover (`review-4115-answers.md`). The proof script above is this pull request's
test, and it was run on the rebased head.

## Round two

The round-two review is `review-4117-round-2.md`. It found both round-one findings answered and nothing that must
change. The Tech Lead asked for two of its observations to be dealt with before merging.

1. **The byte-order mark stopped `set -u` in the WSL check.** **Fixed.** The review saw `sh: 1: set: not found` with an
   invisible character in front of `set`.
   - **The cause:** the script was piped into `sh`, and PowerShell put a byte-order mark in front of it, so `sh` read
     the first line as an unknown command. My earlier attempt, setting `$OutputEncoding`, did not stop it in every
     environment.
   - **The fix:** the check now writes the script to a temporary file with no mark, runs that file in WSL, and
     deletes it.
   - **A new tenth check** requires the line `strict=on`, which the script prints only after `set -u` has run, and
     requires no "not found" anywhere in the output.
   - **The check can fail:** with the file written with a mark again, it reports FAILED.
   - **The transcript:** the committed one shows the run on the new head, with all 10 checks passing.
2. **The start is skipped while ANY Director of the user runs.** **Said.** `pgrep -x cc-director` matches any process
   of that name the account runs: a named instance, a development build, and the short-lived update relauncher.
   The proof now says so in its own paragraph, with what it means for a machine that runs development builds. The
   instructions' notes and the unit's own comment say it too, and the check's description reads "any Director of
   this account".
3. **The new build is unwatched after an update until the next sign-in.** Already said in the proof, and the review
   agrees.
4. **The gate.** Not re-run: nothing outside `missions/` changed. The review agrees.
5. **No real name.** Checked again: nothing.

Two things the review could not reach, which are also stated in the proof: a real Linux desktop with
`graphical-session.target`, and a crashed Director under the unit.
