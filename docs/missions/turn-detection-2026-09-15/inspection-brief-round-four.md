# Inspection brief, round four - the shadow log contention fix

Round three returned AGREE on all seven earlier findings. **Product code has changed since**, so
that AGREE is stale on two files and this round covers only the delta.

## What happened after round three

The full `CcDirector.Core.Tests` suite failed twice on the rebased tree - two different tests in
`ContentTurnRuleTests`, on two consecutive nine-minute runs. A Manager diagnosed it and fixed it.
Its account: the test's own reader and the shadow log's writer were fighting over one Windows file
handle - `File.ReadAllLines` denies writers, `File.AppendAllText` denies readers - so whichever lost
the race failed. The reader losing threw a sharing violation; the writer losing had its append
swallowed and **the row was lost for ever**, because the check that produced it had already been
taken off the books. That second mode reads exactly like a check that armed nothing.

**Do not trust that account.** It is written by the seat that did the work, about its own work.

## The delta

Commits `c3643917e`, `43a11c676` and `b44781cf6`. Get them with `git diff 0f3fc22e5..HEAD`. The
product files are `src/CcDirector.Core/Wingman/TurnDetectionShadowLog.cs` and
`src/CcDirector.Core/Wingman/TerminalStateDetector.cs`. Read shipped code from `origin/main` with
`git show origin/main:path`, never from `D:\ReposFred\devthrottle`, which is a different and stale
checkout.

Round three's review is `docs/reviews/turn-detection-phase-one-inspection-round-three.md`. Its
residual "a locked live file still costs the row" is the same defect this fix addresses - check
whether it is now closed, narrowed, or merely moved.

## What to attack

**The diagnosis itself, first.** The Manager claims the session was SETTLED, a check WAS armed, and
nothing faulted - so this is a test defect and not the product defect the mandate feared. That
conclusion is what unblocked the merge. Verify it independently. If a burst on a settled session can
fail to arm a check, everything else here is beside the point.

**The retry, and its bound.** An append now retries for a budget under the process-wide lock. Does a
stuck handle stall every other session's append behind it? What happens at the bound - is the row
lost, is that logged, and does anything claim otherwise? Is the budget reachable in a real
contention, or is it decoration?

**The claim that the share mode is deliberately unchanged.** The Manager says widening what the
writer permits buys nothing, because a reader holding `FileShare.Read` denies the write regardless -
and says it wrote a guard, found it passed identically before and after, and DELETED it. Check that
reasoning holds, and that the append's open is genuinely byte-for-byte what `File.AppendAllText`
did, including the byte-order mark only on an empty file.

**The reader-side fix.** The test now reads with `FileShare.ReadWrite` and counts only
newline-terminated lines. Can a half-written row still be counted? Can a row be counted twice? Is
the newline rule true of the writer's actual output?

**Whether the fix closes the failure or hides it.** Two green runs on an intermittent failure narrow
it; they do not settle it. The Manager says so itself. What settles it is the mechanism and the
reverts - so check the reverts actually reproduce the two observed symptoms, and that the mechanism
explains BOTH failures rather than one.

**Anything the delta broke that round three had closed.** The seven earlier findings were verified
closed against code that has since changed in two files. Re-check the ones that touch the shadow log
and the detector's check path.

**Every sentence claiming something is proven**, in the new comments and the report, against the
code beneath it.

## How to report

Write to `docs/reviews/turn-detection-phase-one-inspection-round-four.md`: AGREE or DISAGREE on the
first line, then each finding with the file and line it rests on and what would have to be true for
it to be wrong.

Plain ASCII only. Never name any assistant, model or vendor. Reply with ONE SINGLE LINE - fleet
messages truncate at the first newline.
