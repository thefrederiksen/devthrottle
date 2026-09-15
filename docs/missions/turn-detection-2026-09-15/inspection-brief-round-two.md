# Inspection brief, round two - the fixes for the six findings

Round one returned DISAGREE with six findings. All six were accepted and a fresh Manager fixed
them. **Your job is the fixes, and the documented failure mode here is a fix that is itself
half-done.** On pull request 1598 in this repository an inspector found a headline feature did not
work, then found the fix for that was half-done, then found the same fault in five more places. Do
not assume round one's findings are closed because a report says so.

Read round one first: `docs/reviews/turn-detection-phase-one-inspection.md`. Then the fix report,
the section titled "The six inspection findings", in
`docs/missions/turn-detection-2026-09-15/build-report.md`. **Do not trust it** - it is written by
the seat that did the work, about its own work.

The fixes are the two commits `6cd3135de` and `9e838f0a5`. Get them with
`git diff df1c5aea7..HEAD`. Read shipped code from `origin/main` with `git show origin/main:path`,
never from `D:\ReposFred\devthrottle`, which is a different and stale checkout.

## What to attack

**Each of the six, closed or not.** For each, find the code that is supposed to close it and say
whether it does, in every path - not just the one the new test drives. Round one's findings 1 and 2
were both "a turn can be lost"; check that no NEW way to lose one was introduced by the fixes
themselves.

**The retry the fix invented.** Finding 2's fix restores a faulted burst and retries, bounded at
three consecutive faults. Nobody asked for the retry and nobody has measured a fault. What happens
on the fourth? Is the counter reset on success, and if not, does a session that faults three times
over its whole life stop checking for ever? Can the restore itself throw?

**The two functions the fix deliberately did NOT change.** The report says `MarkActiveFromByte` and
`MarkContinuousActive` have the same latch shape as the one that was fixed and were left alone as
"today's shipped path". Is that true, or is the fixed hazard live in them? If it is live, the
switch-off path - which is what every Director runs - carries it.

**The retention sweep.** It deletes files. Does it enumerate what to DELETE, or what to skip? What
does it do to a file another process holds open, to a path that is not what it expects, to a
directory that does not exist? A destructive sweep that leans to delete is the dangerous kind.

**The rollover.** One predecessor kept, four megabytes. What happens if the roll fails? Can a
concurrent append and roll lose or duplicate rows, and would anything notice?

**The interface seam.** Finding 3 said production bypassed the rules. Does it now hold and invoke
them on EVERY path, or only the one the new test drives? Can the shipped threshold and the rule's
own threshold still diverge anywhere?

**The constants.** Round one found several that could move while the suite stayed green. Take the
new pins and try to move each value again. The report claims the literal assertion is placed last
deliberately so a behavioural assertion fires first - check that is actually so, and that the
behavioural assertion really depends on the value.

**The flake.** The report says a test was flaky, that the wait was lengthened from five seconds to
fifteen, and that the cause is stated rather than proven. Is a longer wait a fix or a mask? Could
the same symptom be a real defect - a row that is never written - rather than a late one?

**Every sentence that claims something is proven**, in the code comments and in the report,
checked against the code beneath it.

## How to report

Write to `docs/reviews/turn-detection-phase-one-inspection-round-two.md`: verdict AGREE or DISAGREE
on the first line, then each finding with the file and line it rests on and what would have to be
true for it to be wrong. Say explicitly which of round one's six you consider closed and which you
do not.

Plain ASCII only. Never name any assistant, model or vendor. Reply with ONE SINGLE LINE - fleet
messages truncate at the first newline.
