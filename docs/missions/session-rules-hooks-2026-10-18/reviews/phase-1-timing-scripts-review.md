# Review - the timing scripts in exec form (commit `c3c5e96b6`)

Reviewer seat "Session Rules - Reviewer - timing scripts in exec form", 2026-10-20, on devlinux, in a
worktree at `c3c5e96b6`. The work is one commit: `scripts/measure-rule-hook.sh`,
`scripts/measure-rule-hook.ps1`, section 4 of `phase-1-proof.md`, and `reviews/phase-1-whole-fixes-answers.md`.
The Developer's answers and the commit message were read as claims, not as proof. Everything below is from
the files at this commit and from runs on this machine.

## The questions asked, answered first

| Question | Answer |
|---|---|
| Does the shell script measure what the agent does with an exec-form hook: direct start, event on standard input, nothing in front of it? | **Six rows of seven, yes.** The "outside a Director session" row starts the evaluator through `env`, a program in front of it, which costs about 0.9 ms of that row's 4.4 ms "added". Finding 1. |
| Does the PowerShell script, on its Linux path? | **Yes, on every row.** Run here on PowerShell 7.4.11; numbers agree with the committed ones. |
| Does the PowerShell script, on its Windows path (read, not run)? | **Yes for the hook rows** - the program is started by `ProcessStartInfo` with `UseShellExecute` false, which is a direct process start. The two reference rows have problems a Windows run will meet: a false error on one valid Git Bash setting (finding 3), and a baseline program whose start cost on Windows is unknown here (risk, below). |
| Is the baseline fair? | **On Linux, yes.** A program started directly, as every hook row is; the two cheapest rows are 1.5 to 1.6 ms here and in the commit. The "shell only" comparison row in the PowerShell script does not start a program, so it is not "the same program through the shell" as its header says. Finding 2. |
| Is each row's number traceable to a run? | **Not to a committed run.** No raw script output is in the commit or under `evidence/`; the numbers exist only as the Developer's transcription, in two places that agree with each other. I re-ran both scripts here and every row agrees within noise (tables below), so the numbers are reproducible, but not traceable. Finding 4. |
| Is anything claimed that the run did not show? | **One sentence.** The Windows paragraph states as fact that the 60 ms Git Bash start "is no longer part of its cost"; no Windows run exists, and that rests on the vendor's documentation. Finding 5. |

## Scope

**Read:** the whole of both scripts at this commit; the diff of all four files; `ClaudeHookInstaller.cs`
(`BuildSettings`, the exec-form registration with `args` empty); the exec-form evidence trail in
`phase-1-whole-review.md`, `phase-1-whole-answers.md` and `phase-1-whole-fixes-review.md`, which proves on
devlinux that the installed agent honours an empty `args` and spawns the bare path. That proof is the
premise of these scripts and I did not re-run it; it was done by a different reviewer with a control row.

**Ran:** published the evaluator from this worktree (2,704,056 bytes, the size the proof names);
`scripts/measure-rule-hook.sh --runs 300`; `pwsh -NoProfile -File scripts/measure-rule-hook.ps1 -Hook <that
evaluator> -Runs 300`; and a nine-row micro-measurement with the shell script's own `measure` function to
quantify the two effects in findings 1 and 2. Load average on this machine was 8.8 at the start of the shell
run and 4.2 at the end of the micro-measurement, higher than the 2.2 the Developer reported, so my absolute
numbers are a little noisier; the medians still agree.

**Could not reach:** the Windows path of the PowerShell script (no Windows machine here). Its Windows branch
is lines 83 to 100 and the fixture choice at line 53; everything I say about it is from reading.

## My runs

Shell script, 300 runs per row, exit 0, 604 decision files (expected 604), four evaluation logs of 302 lines:

| Row | Committed median | My median | Committed 95th | My 95th |
|---|---|---|---|---|
| baseline, `/bin/true` directly | 1.61 ms | 1.53 ms | 1.86 ms | 1.77 ms |
| shell only, `/bin/sh -c /bin/true` | 2.69 ms | 2.72 ms | 3.02 ms | 3.11 ms |
| outside a Director session | 5.96 ms | 5.91 ms | 6.58 ms | 6.68 ms |
| rules file not there | 6.23 ms | 6.08 ms | 6.78 ms | 6.64 ms |
| 0 rules | 6.52 ms | 5.90 ms | 7.11 ms | 7.35 ms |
| 1 rule, matching | 7.07 ms | 6.95 ms | 7.72 ms | 7.98 ms |
| 50 rules, last matching | 7.27 ms | 7.42 ms | 8.00 ms | 8.26 ms |

PowerShell script on its Linux path, 300 runs per row, exit 0, 604 decision files: baseline 1.07 ms, shell
only 1.23 ms, no session 4.71 ms, rules file not there 5.56 ms, 0 rules 6.06 ms, 1 rule 6.65 ms, 50 rules
6.93 ms (medians). The committed numbers are 0.96, 1.32, 4.79, 5.72, 6.34, 6.84, 7.16. They agree.

Micro-measurement, same method as the shell script, 300 runs each, the evaluator from this worktree:

| What was timed | Median | 95th percentile |
|---|---|---|
| `/bin/true` directly | 1.49 ms | 1.70 ms |
| `env /bin/true` | 2.40 ms | 2.81 ms |
| `/bin/sh -c /bin/true` (shell, then a program) | 2.69 ms | 3.05 ms |
| `/bin/sh -c true` (shell builtin, no program) | 1.70 ms | 1.99 ms |
| evaluator, no session, through `env -u ... -u ...` (the script's row) | 5.97 ms | 6.78 ms |
| evaluator, no session, `( unset ...; exec "$HOOK" )` | 5.23 ms | 5.96 ms |
| evaluator, no session, started directly (variables already absent) | 5.00 ms | 5.63 ms |
| `/bin/true` directly, again | 1.57 ms | 1.89 ms |
| evaluator through `env`, again | 5.93 ms | 6.78 ms |

## Findings

### Finding 1 - the "outside a Director session" row has `env` in front of the evaluator. Medium.

`scripts/measure-rule-hook.sh` lines 122 to 124 and 150. `run_hook` is
`env -u CC_SESSION_RULES_FILE -u CC_SESSION_DECISION_DIR "$HOOK"`. That is not a shell, but it is a program
started in front of the evaluator: the shell forks, executes `env`, and `env` then executes the evaluator - one
more program image loaded per run than the agent loads. The other four hook rows set the two variables
inline and start `"$HOOK"` directly, so this row is the only one measured differently from the rest.

**What breaks.** The row is 0.9 ms too high. Measured above: 5.97 ms through `env` against 5.00 ms direct,
and `env /bin/true` against `/bin/true` shows the same 0.9 ms. The committed table says this row adds 4.4 ms;
started as the agent starts it, it adds about 3.4 ms. The header (lines 8 and 9) says the script "starts the
evaluator directly, with no shell", which this row does not do, and the proof's row at line 157 carries the
inflated number. The headline rows (1 rule, 50 rules) are not affected.

**Why it must change.** The row exists to say what a session that has no rules pays per command, and it
answers that question 25 percent high. The script's own claim of a direct start is false for it.

**What would close it.** Remove the two variables once, at script level, after lines 132 to 139 have recorded
the operator's own session (`unset CC_SESSION_RULES_FILE CC_SESSION_DECISION_DIR`), so `run_hook` becomes
`"$HOOK"` like the others. The subshell form `( unset ...; exec "$HOOK" )` is also direct in the agent's sense,
but still costs 0.2 ms over a plain start here. Then re-measure that one row and correct line 157 of the proof.

### Finding 2 - the PowerShell "shell only" row starts no program, and its header says it does. Low.

`scripts/measure-rule-hook.ps1` line 167 runs `$shellProgram -c true`: `/bin/sh -c true` on Linux and
`bash.exe -c true` on Windows. In both shells `true` is a builtin, so the row measures a shell start and
nothing else. Line 15 describes it as "the same program through Git Bash -c (/bin/sh -c elsewhere)" - it is not
the same program; no program is started. The shell script's row (line 149) is `/bin/sh -c /bin/true`, which
does start the program through the shell, and is what the earlier shell-form registration actually paid: a
shell, then the evaluator.

**What breaks.** The two scripts' "shell only" rows measure different things under the same label, and the
proof (line 171) says the PowerShell script was "changed the same way". Here the difference is 1.0 ms
(2.69 ms against 1.70 ms in the micro-measurement; the script runs show 2.72 ms against 1.23 ms). On Windows,
where a program start under the Git Bash runtime is far dearer than a builtin, the PowerShell row will
understate what shell form paid by more than that. This row is marked comparison only and feeds no "added"
column, so the harm is a mislabelled comparison, not a wrong result.

**What would close it.** `-c` with the full path of the baseline program (`$trueProgram`) on both
platforms, so the row is shell plus the same program, as in the shell script; or keep the builtin and change
line 15 to say the row is the shell's start alone.

### Finding 3 - on Windows, `true.exe` is derived from the wrong parent when the Git Bash setting names `usr\bin\bash.exe`. Low.

`scripts/measure-rule-hook.ps1` lines 97 to 99. The baseline program is found as two directories above
`$gitBash` plus `usr\bin\true.exe`. That is right when `$gitBash` is `<Git>\bin\bash.exe`, the path the
script derives itself (line 94). But line 85 takes `CLAUDE_CODE_GIT_BASH_PATH` as given, and Git for Windows
also ships `<Git>\usr\bin\bash.exe`, a valid value for that setting. From there the derivation yields
`<Git>\usr\usr\bin\true.exe`, which does not exist, and line 99 throws "No true.exe ... beside the Git Bash"
although it is beside it.

**What breaks.** A false error on a valid machine setting; the script refuses to run. It fails closed, with a
message that sends the operator looking for a file that is there.

**What would close it.** Look for `true.exe` in the same directory as `$gitBash` first (that is where it is
when the setting names `usr\bin\bash.exe`), then two levels up under `usr\bin`.

### Finding 4 - no row's number is traceable to a run. Low.

Neither the commit nor `evidence/` holds the output of the 2026-10-20 runs. Section 4 of the proof and
`phase-1-whole-fixes-answers.md` carry the same table, transcribed by the Developer; they agree with each
other (checked by diff) and with my runs (above), so I accept the numbers as reproduced. But "traceable to a
run" means a reader can find the run, and no reader can. The exec-form proof in this same mission committed
its raw evidence (`evidence/exec-form-*`); the timing proof should do the same, both for this run and for the
Windows run still to come.

**What would close it.** Commit the full standard output of each measurement run (header, rows, load averages,
decision count, log lines) under `evidence/`, and have section 4 name the file.

### Finding 5 - the Windows paragraph states as fact what no run has shown. Low, wording.

`phase-1-proof.md` lines 178 to 180: "Since `bdcf94e39` the evaluator is registered in exec form and is
started with no shell, so that 60 ms is no longer part of its cost". The registration is a fact; that the
agent on Windows starts it with no shell rests on the vendor's documentation, as the whole-fixes review said
in so many words, and the 60 ms is "no longer part of its cost" only if that documentation holds on Windows.
The next clause does admit the number is unmeasured, which is honest; the sentence before it is not hedged.

**What would close it.** "by the vendor's documentation, which the Windows run is to confirm" in that
sentence, so the claim and its basis sit together.

## A risk for the Windows run, not a finding

The Windows baseline is Git for Windows' `true.exe`, a program built on the MSYS runtime. Its start includes
that runtime's initialisation, which is known to be far heavier than a native program's start, while the
evaluator is a native executable with none of that. If so, the Windows baseline overstates "what any exec-form
hook costs before it does anything", and every "added" number comes out low. I cannot measure this here. The
Windows Developer should report the baseline row's absolute number and read it against finding 2's row: if
`true.exe` alone takes many milliseconds, the baseline is the MSYS runtime, not a process start, and a native
program with no runtime (or the evaluator's own "no session" row, which is a native start that does nothing)
is the fairer reference.

## What is right

- The hook rows in both scripts are the agent's start: the program, no shell, the event on standard input,
  the two session variables and nothing else distinguishing the rows. The PowerShell script removes the two
  variables on the process environment, with no wrapper, on every row.
- The presence check holds in both scripts: 604 decision files from the two matching rows, four logs of 302
  lines, and the script fails if the count is wrong. Both exited 0 here.
- The shell script's guard for an operator's own session is unchanged and was not exercised in my run (this
  shell carries neither variable), which the script says plainly by printing nothing for it.
- The baseline on Linux is fair and tight (95th percentile within 0.3 ms of the median in every run).
- The proof says what replaced what, why the old "added" column came out similar, and that the Windows path
  of the PowerShell script was not run. The evaluator size it names is the size this worktree publishes.
- The PowerShell script requires version 7 (line 1), so `$IsWindows` is defined on every platform it runs on.
