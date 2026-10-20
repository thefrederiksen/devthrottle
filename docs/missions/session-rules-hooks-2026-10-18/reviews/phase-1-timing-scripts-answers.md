# Answers to the review of the timing scripts

Developer seat "Session Rules - Developer - whole-phase review answers", 2026-10-20, on devlinux, branch
`mission/session-rules-hooks-p1-wholefix`, fast-forwarded to `origin/mission/session-rules-hooks-p1` at
`748139e6c` first. The review is `phase-1-timing-scripts-review.md`; the Tech Lead called all five findings
"fix as the reviewer says". All five are accepted and fixed. The script changes are commit `1c3b51176`; the
re-measurement, the evidence and this file are the commit after it.

## Finding 1 - the "outside a Director session" row had `env` in front of the evaluator. ACCEPTED, fixed.

`scripts/measure-rule-hook.sh`: the two session variables are now removed ONCE, at script level, with
`unset`, right after the script records the operator's own session (its evaluation log and decision drop
counts). `run_hook` is now plain `"$HOOK"`, started like every other hook row. The operator's decision drop
folder is kept in `OWN_DROP_DIR` before the `unset`, so the end-of-run guard that the measurement never wrote
into the operator's own session still reads the right folder. The PowerShell script already removed the
variables on the process environment with nothing in front of the program, so it needed no change for this.

Re-measured (full output `evidence/timing-linux-sh-2026-10-20.txt`): the row is now 5.25 ms median, 3.8 ms
added over the direct baseline, against 5.96 ms and 4.4 ms in the table it replaces - the drop the review
measured for `env`, on a busier machine. Section 4 of `phase-1-proof.md` carries the new row and says the
intermediate table's row had `env` in it.

## Finding 2 - the two "shell only" rows measured different things. ACCEPTED, fixed.

Both scripts now have one comparison row with one name and one meaning: **"through a shell"** - the shell
starting the baseline program, which is what the earlier shell-form registration paid (a shell, then a
program).

- Shell script: `/bin/sh -c /bin/true`, as before; renamed, and the header says what it is.
- PowerShell script: `$shellProgram -c $trueInShell`, which is `/bin/sh -c /bin/true` on Linux and Git Bash
  `-c /usr/bin/true` on Windows - `/usr/bin/true` being Git Bash's own name for the same `<Git>\usr\bin\true.exe`
  the baseline row starts, so no Windows path has to survive bash's quoting. It no longer runs the builtin
  `true`. The header and the start-up line ("through-a-shell row: ...") say the same.

On Linux the PowerShell script's row went from 1.32 ms (a builtin) to 2.36 ms, beside the shell script's
2.64 ms: the two now measure the same thing. The Windows path was read, not run.

## Finding 3 - `true.exe` derived from the wrong parent for a `usr\bin\bash.exe` Git Bash. ACCEPTED, fixed.

`scripts/measure-rule-hook.ps1`: the baseline program is looked for first BESIDE the Git Bash (where it is
when `CLAUDE_CODE_GIT_BASH_PATH` names `<Git>\usr\bin\bash.exe`), then two levels up under `usr\bin` (where
it is for `<Git>\bin\bash.exe`, the path the script derives itself). The first that exists is used. If
neither exists the error names the Git Bash and BOTH places it looked. Not run on Windows; read through for
both settings, and the Linux path, which does not take this branch, ran clean.

## Finding 4 - no row's number was traceable to a run. ACCEPTED, fixed.

The full standard output of both Linux runs at `1c3b51176` is committed, each with the exact command line,
the commit and the time on its first line, and the exit code on its last:

- `evidence/timing-linux-sh-2026-10-20.txt` - `scripts/measure-rule-hook.sh --runs 300`: header, load average
  at start and end (6.77 and 6.41), every row, 604 of 604 decision files, the four evaluation logs' line
  counts and last lines, exit 0.
- `evidence/timing-linux-ps1-2026-10-20.txt` - `pwsh -NoProfile -File scripts/measure-rule-hook.ps1 -Hook
  <the published evaluator> -Runs 300` on PowerShell 7.4.11, its Linux path: the same, exit 0.

Section 4 of `phase-1-proof.md` names both files and says every number in its table is copied from the
first. Section 4 also says the Windows run is to commit its full output under `evidence/` the same way.

The table in `phase-1-whole-fixes-answers.md` (from `c3c5e96b6`, with the `env` row) is superseded by
section 4 and these files; it is left as written, since it is the record of that answer.

One run was thrown away and is said here so it is not hidden: a first run with the fixed scripts, before
they were committed, had a 95th percentile of 27 ms on the "1 rule" row (a burst of load on the machine,
load average 6.3); its medians matched. It was re-run at the commit so the committed evidence names a
commit, and the committed run is the one in the proof.

## Finding 5 - the Windows paragraph stated as fact what no run had shown. ACCEPTED, fixed.

`phase-1-proof.md`, section 4, the Windows paragraph now says: the evaluator is registered in exec form
(a fact, commit `bdcf94e39`); BY THE VENDOR'S DOCUMENTATION the agent starts an exec-form hook with no shell
on Windows too, so the 60 ms Git Bash start is EXPECTED no longer to be part of its cost - "expected, not
measured: no Windows run has shown it yet" - and the Windows run of the changed script is to measure it.

## The risk the review raised for the Windows run

Noted for the Windows Developer, not changed here: Git for Windows' `true.exe` carries the MSYS runtime's
start-up, so on Windows the baseline may be dearer than a native program's start and every "added" number
low. The baseline row's absolute number is printed by the script; read it against the "outside a Director
session" row, which is a native start that does nothing, before trusting the "added" column on Windows.

## The re-run

| What | Result |
|---|---|
| `scripts/measure-rule-hook.sh --runs 300` at `1c3b51176` | exit 0, 604 of 604 decision files; rows in section 4 |
| `pwsh -NoProfile -File scripts/measure-rule-hook.ps1 -Hook <evaluator> -Runs 300` at `1c3b51176` | exit 0, 604 of 604 decision files |
| `bash -n scripts/measure-rule-hook.sh` | clean |

No test suite covers these scripts; their own presence check (the decision-file count, failing the script
when it is wrong) is the check, and it passed in both runs.
