# Fix task - the full Core suite fails, and the merge is blocked on it

You are a fresh Manager. Phase one's code is built, three rounds of independent inspection have run,
and the third returned AGREE. **It still cannot merge**, because the full `CcDirector.Core.Tests`
suite fails on the rebased tree.

**Work only in this worktree** (`D:\ReposFred\devthrottle-turn-detection`) on branch
`mission/turn-detection-phase-one`. Commit and push as you go - this machine rebooted without
warning once today already.

## Read first

1. `cc-devthrottle workflow instructions mission --version 17` - your conduct.
2. `docs/missions/turn-detection-2026-09-15/full-suite-failure.md` - **the evidence. This is your
   mandate.** It states what was observed and, deliberately, does NOT state a cause.
3. `docs/missions/turn-detection-2026-09-15/handoff.md` - the settled rulings. Do not reopen them.
4. `docs/reviews/turn-detection-phase-one-inspection-round-three.md` - what is already verified
   closed, so you do not re-do it.

## What is known

Two consecutive full runs, two DIFFERENT tests in `ContentTurnRuleTests`, each about nine minutes.
The second carried this message:

> no shadow row was written past row 1 ...; **NO check is armed for this session, so no row will
> ever be written for this burst**

So it is never, not late. A longer wait cannot fix it.

## What you must NOT do

**Do not diagnose by assumption, and do not fix the test until you know why it fails.** The
temptation here is to call it a flake and widen a timeout - that is exactly what was tried once
already for a related symptom, and round three of the inspection correctly called it a tolerance
change rather than a diagnosis. The diagnostic that exposed this exists because that shortcut was
rejected.

## The first question to settle, because it decides everything else

`OnBytesCore` deliberately does nothing on an already-active session: no screen read, no check, no
row. That is the design - the rule must cost a working session nothing.

So: **was the session settled or already active when the test wrote its burst?**

- If it was already ACTIVE, the product behaved correctly and the TEST is racing - it wrote a burst
  before the session had settled back. Then the fix is in the test, and it must be a fix that makes
  the test wait for the settle it depends on, not one that waits longer and hopes.
- If it was SETTLED, then a burst on a settled session failed to arm a check. That is a PRODUCT
  defect of the first order - it is the exact failure rounds one and two spent themselves chasing -
  and it means a real session can miss a turn.

Answer that with evidence from a run, not from reading the code. Then fix whichever it turns out to
be.

## Two facts that bear on it

- The same suite passed in full on this same work BEFORE the rebase onto `origin/main` at
  `74da12d36` - 4,471 passed, 0 failed. The suite total is unchanged at 4,479, so the rebase added
  no tests to it. Something about main's newer code, or about load, changed the outcome. Find out
  which.
- Both failures happened only in a FULL-suite run on a busy machine. Every green run behind this
  work - the Manager's and all three inspections' - was a focused subset. The full suite under load
  is a condition none of them exercised. Whatever you conclude, the fix has to hold under it.

## Method

Do not pipe a long test run through `tail` or `head`. It buffers, so the output file stays empty and
the assertion message is destroyed, and `$?` becomes the exit code of `tail` - the first run of this
failure reported `exited with code 0` while carrying a failure. Redirect to a file instead.

## How you are judged

The full `CcDirector.Core.Tests` suite passes, run END TO END at least twice in a row, with the
output redirected to a file and the exit code read from `dotnet`. Plus `.\scripts\test-local.ps1`
green. Do NOT run `-Parked` in full - the Gateway suite's lock is unwinnable on this machine today
(issue #2862).

If the answer turns out to be a product defect, it gets a test watched failing with the reported
symptom, like every other fix in this mission.

Do not open a pull request and do not merge. Plain ASCII everywhere. No mention of any assistant,
model or vendor anywhere.

## Reporting

Append a section to `docs/missions/turn-detection-2026-09-15/build-report.md` saying what the cause
actually was, how you established it, and what you changed. Report to the Architect
`b564b0b3-148d-402d-a5ba-ac45414fb437` ONCE, in a single line.
