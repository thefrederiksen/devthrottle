# Answers to the review of the whole-phase answers

Developer seat "Session Rules - Developer - whole-phase review answers", 2026-10-20, on devlinux, branch
`mission/session-rules-hooks-p1-wholefix`, after a fast-forward to `origin/mission/session-rules-hooks-p1`
at `5ab354493` (which carries the Windows publish fix and the reviews). The review is
`phase-1-whole-fixes-review.md`; both findings are answered below, following the Tech Lead's calls at its end.

## Finding 1 - the Windows check with Git Bash absent is named nowhere. ACCEPTED: it is now named; no change here.

As the Tech Lead's call 1 says, the check is step 4 of `brief-windows-rows.md` on
`mission/session-rules-hooks-p1-windows` (read there at the time of writing): on SOREN_NORTH, a Director
session with `CLAUDE_CODE_GIT_BASH_PATH` pointed at a path that does not exist, the session confirmed to use
the `PowerShell` shell tool, and a `no_match` or `would_deny` line tied to the call id, with the command's
output on screen. The Windows Developer holds it. Nothing on this branch changes for it.

## Finding 2 - the timing scripts measured shell form. ACCEPTED: fixed, re-measured.

**What changed.**

- `scripts/measure-rule-hook.sh`: the hook rows start the evaluator directly - `"$HOOK"` with the event on
  standard input and no `/bin/sh -c` - as the agent does with the exec-form registration. The quoted
  `HOOK_COMMAND` string is gone. The baseline is now `/bin/true` started directly. The shell row is kept,
  relabelled `shell only` and described in the header as the shell start the earlier shell-form registration
  paid, for comparison only; "added" is stated as the row minus the direct baseline, not the shell row.
  The header no longer claims "the exact command ... through /bin/sh -c"; it says the Director registers
  exec form and the script starts the program with no shell.
- `scripts/measure-rule-hook.ps1`, kept as small as the call asked: `Invoke-Once` and `Measure-Row` now take
  a program and an argument list and start it directly, instead of handing a command string to a shell. The
  hook rows run `$Hook` with no arguments. The baseline is a program started directly (Git for Windows'
  `usr\bin\true.exe`, found beside the Git Bash the script already located; `/bin/true` elsewhere), and a
  `shell only` row runs `bash.exe -c true` (`/bin/sh -c true` elsewhere), labelled as the shell start that
  is no longer paid. The header is corrected the same way. On Windows the script still needs Git for
  Windows, but only for those two reference rows; the hook rows never go through it.

**Re-measured on Linux**, `scripts/measure-rule-hook.sh --runs 300`, devlinux, 32 processors, load average
2.16 at the start and 1.98 at the end, exit 0, 604 decision files as expected:

| Row | Median | 95th percentile | Added (median) |
|---|---|---|---|
| baseline, `/bin/true` directly | 1.61 ms | 1.86 ms | - |
| shell only, `/bin/sh -c /bin/true` (comparison only) | 2.69 ms | 3.02 ms | - |
| outside a Director session | 5.96 ms | 6.58 ms | 4.4 ms |
| rules file not there | 6.23 ms | 6.78 ms | 4.6 ms |
| 0 rules | 6.52 ms | 7.11 ms | 4.9 ms |
| 1 rule, matching | 7.07 ms | 7.72 ms | 5.5 ms |
| 50 rules, last matching | 7.27 ms | 8.00 ms | 5.7 ms |

Section 4 of `phase-1-proof.md` now holds this table in place of the 2026-10-18 numbers, and says the old ones
included a shell start: every hook row ran through `/bin/sh -c` against a shell baseline, so their absolute
times carried a shell the shipped hook no longer pays, and their "added" column (4.2 to 6.4 ms) came out
close to the new one because the shell was in the baseline too. The Windows paragraph in that section,
which said the 60 ms Git Bash start was the hook's cost on Windows, now says that cost belonged to shell
form and the Windows number is still to be measured with the changed script.

**The PowerShell script cannot be proven here on Windows - but it did run here.** The call assumed there is
no PowerShell on this machine; there is (`~/.local/bin/pwsh`, PowerShell 7.4.11). So the changed `.ps1` was
run on Linux: `pwsh -NoProfile -File scripts/measure-rule-hook.ps1 -Hook <the published evaluator> -Runs 300`
- baseline 0.96 ms, shell only 1.32 ms, no session 4.79 ms, rules file not there 5.72 ms, 0 rules 6.34 ms,
1 rule 6.84 ms, 50 rules 7.16 ms (medians), 604 decision files, exit 0. That proves the reshaped functions,
the argument passing and the presence check; it does NOT run the Windows branch (finding `true.exe` beside
Git Bash, and `bash.exe -c true`). The Windows Developer runs that as step 1 of `brief-windows-rows.md`, and
needs this branch's version of the script to do it: the copy on `mission/session-rules-hooks-p1-windows` is
still the shell-form one until this branch is merged into the pull request branch.

**No test was written first for this.** These are measurement scripts with no test suite; their own
presence check (the decision-file count and the evaluation-log lines, which fail the script when the hook did
not do what it was timed doing) is the check, and it passed in both runs above.
