# Fix task, round two - the four findings the second inspection left open

You are a fresh Manager. Two have come before you. The first built items one to four; the second
closed six inspection findings. An independent Inspector has now reviewed those fixes and returned
**DISAGREE**: round-one findings 1 and 4 are closed, and findings 2, 3, 5 and 6 are not, plus one
new finding about a false claim in the report.

**Work only in this worktree** (`D:\ReposFred\devthrottle-turn-detection`) on branch
`mission/turn-detection-phase-one`.

## Read these first

1. `cc-devthrottle workflow instructions mission --version 17` - your conduct.
2. `docs/reviews/turn-detection-phase-one-inspection-round-two.md` - your mandate. It ran probes
   rather than reasoning from the code, so its findings are demonstrations, not opinions.
3. `docs/reviews/turn-detection-phase-one-inspection.md` - round one, for what was originally asked.
4. `docs/missions/turn-detection-2026-09-15/handoff.md` - the settled rulings. Do not reopen them.

**Every finding is accepted.** The rulings below are the Architect's and they are settled - they
resolve the parts where the inspection identified a problem without choosing between the ways out.

## The rulings

**Finding 1 - the fourth consecutive fault loses the turn.** The retry bound was invented to stop a
spin and it created the exact failure the whole design forbids. Ruling: **when the bound is
exceeded, do not drop the burst - OPEN THE TURN.** Falling back to today's behaviour is the
conservative direction and it is always available. A rule that cannot decide must never decide
against the user; the cost of a wrong open is a blue session that settles ten seconds later, and the
cost of a wrong drop is work that sits red for ever. Fix the off-by-one in the comment, the report
and the log line while you are there: the drop happens on the fourth failed execution, not the third.

**Finding 2 - the two shipped activation paths keep the faulted-latch hazard.** This one is NOT a
regression from this mission: the same shape is on `origin/main` and I verified that myself. It is
in scope anyway, and this is a deliberate widening. The Inspector demonstrated a real state-change
subscriber throw leaving a session latched `Working` for ever, on the paths every Director runs
while the switch ships off, and we are holding the exact three-line remedy already applied next
door. Filing an issue and walking past it would be indefensible. Apply the same latch release to
`MarkActiveFromByte` and `MarkContinuousActive`, and say in the commit that it fixes a pre-existing
fault rather than one this mission introduced.

**Finding 3 - the seam's guarantees.** Two separate defects, two different answers.

- The size verdict and its numbers are gathered from three separate calls, so a rule could report a
  magnitude that is not the one its verdict used. Ruling: **the size rule answers once, with one
  result carrying the verdict, the magnitude and the threshold together.** Then they cannot
  disagree, rather than being trusted not to.
- An ambiguous frame sets both verdicts true without asking either rule. That is CORRECT behaviour
  and must not change - an unreadable screen is not a question for a content rule. The defect is
  the comment claiming every check asks both candidates. Ruling: **fix the comment, not the code.**
  Say that an unreadable frame short-circuits to the conservative open without consulting either
  rule, and why.

**Finding 4 - the sweep deletes files it cannot prove it owns.** The Inspector dropped an aged
`owner-notes.jsonl` into the directory and the next append deleted it. An extension is a deny
boundary, not proof of ownership. Ruling: **the sweep deletes only names this log can prove it
wrote** - the exact shapes the writer creates and nothing else. Enumerate what to DELETE, never what
to skip. Anything it does not recognise is left alone and said so, once, rather than silently.

**Finding 5 - the rollover.** Three parts:

- A failed rollover currently skips the append, so the observation is lost and only a healthy log
  sink shows it. Ruling: **a failed rollover must never cost the row.** Append anyway; an oversized
  file is a smaller harm than a missing observation.
- The cross-process race: the file is per SESSION, and a session belongs to exactly one Director
  process, so two processes writing one session's file is not reachable. Ruling: **write that
  reasoning down** in place of the current implied claim that the lock covers it. If you find the
  premise is false, stop and tell me rather than designing around it.
- The size bound is tested before the record is appended, so a file can exceed it by one record.
  Ruling: **state the bound honestly** - the maximum plus at most one record - rather than
  tightening the algorithm.

**Finding 6 - the pins cover the constants, not the shipped defaults.** The Inspector changed the
production defaults only, left every named constant alone, and all 102 focused tests stayed green.
Every behaviour test injects its own timings and the retention tests overwrite the live properties,
so nothing asserts that the shipped constructor and static initialisation actually consume the
values we think they do. Fix it for all of them: the settling window and its cap, the retention size
and age and sweep interval, and the shadow log's enabled default.

**Finding 7 - a false claim in the report.** The report says the lengthened wait means late and
never can no longer be confused. The Inspector showed the same diagnostic appears for a row that is
never written. Correct the claim. If a positive signal distinguishing the two is cheap, add it; if
it is not, say plainly in the test that the diagnostic cannot tell them apart.

## How you are judged

- Every fix gets a test watched failing with the reported symptom first.
- For findings 1, 2 and 4 the Inspector's own probe sequences are in the review. Reproduce them as
  permanent tests - a defect demonstrated by a throwaway probe and then fixed without one is a
  defect with nothing stopping it coming back.
- `.\scripts\test-local.ps1` green, plus the focused parked tests explicitly. Do NOT run `-Parked`
  in full; the Gateway suite's lock is unwinnable on this machine today (issue #2862) and that
  suite runs in continuous integration after the merge.
- Do not open a pull request and do not merge.
- Plain ASCII everywhere. No mention of any assistant, model or vendor anywhere.

## Reporting

Append a round-two section to `docs/missions/turn-detection-2026-09-15/build-report.md`, per
finding, with the test, the mutation watched red, and anything NOT closed. Report to the Architect
ONCE, in a single line.
