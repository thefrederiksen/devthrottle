# Inspection brief, round three - the fixes for round two

Two inspections have come before you, both returning DISAGREE. Round one found six defects. Round
two found that four of those six were not actually closed, and added a seventh. A third Manager has
now fixed all seven and has been reaped.

**Your job is those fixes.** The documented failure here is a fix that is itself half-done: round
two proved exactly that about round one's work. Do not assume anything is closed because a report
says so.

## Read, in this order

1. `docs/reviews/turn-detection-phase-one-inspection.md` - round one.
2. `docs/reviews/turn-detection-phase-one-inspection-round-two.md` - round two. It ran probes rather
   than reasoning from the code, and its findings are demonstrations. Match that standard.
3. `docs/missions/turn-detection-2026-09-15/build-report.md`, the round-two section - what the
   Manager says it did. **Do not trust it.** It is written by the seat that did the work, about its
   own work.
4. `docs/missions/turn-detection-2026-09-15/handoff.md` - the settled rulings, including one the
   Architect got wrong and the Manager correctly deviated from.

The fixes are `5226f0f99`, `3b14d4255` and `e48d4c7f8`. Get them with `git diff 505a5a7cf..HEAD`.
Read shipped code from `origin/main` with `git show origin/main:path`, never from
`D:\ReposFred\devthrottle`, which is a different and stale checkout.

## What to attack

**Each of the seven, closed or not.** For each, find the code that closes it and say whether it does
on EVERY path, not just the one the new test drives.

**The conditional latch release - hardest first.** After a fault the latch is released only when the
session is not already in `Working`, and the quiet timer is armed in a `finally`. Attack the
condition itself: what if the state write landed but the session was ALREADY Working for another
reason? What if the state changes between the write and the check? Is `ActivityState` readable
without a race here? Construct a fault where the session ends up neither counting down nor able to
re-enter the settled path - which is the failure this shape exists to prevent.

**The open-the-turn fallback.** Past the retry bound the rule now opens the turn instead of dropping
the burst. Does it? Trace the fourth consecutive fault to an actual `Working` write. Round two
demonstrated the previous version losing the turn there with a probe; reproduce that probe against
this version and say what happens.

**The retention sweep, again.** Round two showed it deleting an aged file it did not write. Does it
now delete ONLY names it can prove it wrote, and is that proof a positive match rather than an
extension check? Put an unexpected file in the directory and see what happens to it.

**The rollover.** A failed rollover must no longer cost the row. Make the rollover fail and check
whether the observation still lands.

**The pins, against the SHIPPED defaults.** Round two's sharpest finding was that the tests pin the
named constants while production consumes separate live fields, so it changed the production
defaults only and 102 tests stayed green. Do that again. Change only the shipped constructor
defaults and the live retention properties, leave every named constant alone, and report what stays
green.

**Every sentence claiming something is proven**, in the code comments and in the report, checked
against the code beneath it. Round two found two false claims; the report says both are corrected in
place. Verify that, and look for new ones introduced by the fixes.

## How to report

Write to `docs/reviews/turn-detection-phase-one-inspection-round-three.md`: verdict AGREE or
DISAGREE on the first line, then each finding with the file and line it rests on and what would have
to be true for it to be wrong. State explicitly which of the seven you consider closed and which you
do not.

Plain ASCII only. Never name any assistant, model or vendor. Reply with ONE SINGLE LINE - fleet
messages truncate at the first newline.
