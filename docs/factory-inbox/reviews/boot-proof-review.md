# Review: pull request 4597 - boot proof self-test budgets

Reviewed 2026-11-05 by a separate review session. Worktree D:\ReposFred\devthrottle-fi-boot, branch
fix/boot-proof-self-test at 1aa033b34, base origin/main at 457736d7f. The worktree was not edited.

## Scope

- The one changed file, scripts/prove-image-boots.test.sh (13 lines added, 2 removed).
- The script under test, scripts/prove-image-boots.sh, read in full to check the arithmetic. Confirmed
  unchanged against origin/main (empty diff).
- The cited failing run on GitHub (run 42112688067, the CI workflow on main, 2026-11-05 21:26 UTC): its
  failed-job log was read, not the summary.
- The one-off proof workflow named in the pull request body ("Proof - boot self-test repeat") was checked
  on GitHub: it is disabled.
- Not run locally: the self-test itself (it needs Docker and takes a few minutes; the author's 30 plus 16
  runs on GitHub runners plus the control runs of main's version are the evidence here, and the log of
  the original failure was verified directly).

## Verdict: APPROVE

The claim is correct, the arithmetic is correct, every case that must fail still fails for the stated
reason inside its budget, and no case now passes that should fail. One non-blocking observation below.

## The arithmetic, against the script

The script polls once, then checks `now + 3 > deadline` and fails if so, else sleeps 3 seconds. The
pass path breaks out of the loop BEFORE that check, so a third pass landing exactly at the deadline
second still passes. With a 15-second budget and no late poll, starting-then-healthy goes:

    0s HTTP 000 (fake not yet listening), 3s 503, 6s 503, 9s pass 1, 12s pass 2 (check: 15 > 15 is
    false, so sleep), 15s pass 3, break.

That is six polls and exactly 15 seconds of sleep inside a 15-second budget: zero slack. Any single poll
one second late (docker inspect, curl, jq and `date +%s` integer rounding all add real time) pushes pass
2 to 13s, the check becomes 16 > 15, and the script fails with "last: pass 2". The failed GitHub log
shows exactly that: 0s, 3s, 7s, 10s, 13s, then "no 3 consecutive healthy answers within 15s (last: pass
2)". The script was right to fail; the budget was the defect. The pull request's description of the
cause is accurate.

The comment's "the first poll always finds the fake not yet listening" is not guaranteed (a fast
runner could have the Python server up before the first curl), but the case is unharmed either way: if
the first poll is a 503 instead of 000 the case needs five polls, not six, and passes sooner.

## Cases that must fail, with 15 seconds

- crash: the container exits; the script fails at once on the Running check ("exited after Ns"), and
  the expected stderr line comes from `docker logs`. Budget-independent.
- silent: never listens; six polls of HTTP 000; fails on the deadline check with the message "within 15s
  (last: HTTP 000)". The expected words name 15s, and FAIL_BUDGET_SECONDS is 15, so they still agree.
  The comment in the test calls out this coupling, which is good.
- empty, not-json, array, no-commit, wrong-commit: every poll produces a verdict; passes stays zero; the
  expected words appear on every "not yet" line from the second poll on and in the final message. These
  fail by waiting out the budget, and a shorter budget here is what keeps the whole self-test short.
- flapping: the fake answers 503 on every third request counted server-side. Requests 1 and 2 pass,
  request 3 breaks the run and prints "the run of passes is broken, starting again from zero", requests
  4 and 5 pass, then the deadline check fails. Request 3 lands by the 9s poll at the latest (6s if the
  first poll connected), well inside 15 seconds; the final message is "last: pass 2", exit 1. Correct.

## Nothing new passes that should fail

The 60-second budget reaches only the two cases with expected exit 0 (healthy and
starting-then-healthy). Each still demands "BOOT PROOF PASSED", and starting-then-healthy still demands
"not yet - HTTP 503", so the recovery from a 503 is still proved, not assumed. A longer budget cannot
turn a failing script into a passing one for these shapes: the fake answers 200 with the right commit
for every request after the second, so the only way to not pass is a script defect, and that would
still show as a missing "BOOT PROOF PASSED".

The variable is now passed per invocation instead of exported once; the proof script is the only reader,
so nothing else loses it.

## Non-blocking observation

The old 15-second budget was, by accident, also an upper bound on how long a passing case may take.
A regression in the script that slowed the pass path (for instance PASS_INTERVAL_SECONDS raised to 10,
making starting-then-healthy need about 50 seconds) would have failed the self-test before and will
pass it now, because the pass cases assert words and exit code but never elapsed time. The script's
own PASSED line prints "3s apart", so if that bound is wanted back it is one extra expected word on
the two passing cases ("3s apart"), or an elapsed-time ceiling in run_case for want_exit 0. Not needed
for this fix; worth a thought if the interval ever becomes a tunable.

## Evidence

- `git diff origin/main...HEAD` - one file, the budgets and the per-call variable.
- `git diff origin/main...HEAD -- scripts/prove-image-boots.sh` - empty.
- `gh run view 42112688067 --log-failed` - the 0s, 3s, 7s, 10s, 13s timeline quoted above.
- `gh workflow list --all` - "Proof - boot self-test repeat" is disabled_manually.
