# Review - round 2 track A: every status explains itself (pull request 3607)

Reviewed 7 October 2026 by a separate review session. Read-only: nothing in the worktree was edited.

## Verdict: CHANGES REQUESTED - one defect in the FAILING clearing rule

The clearing rule clears a failure that is not over when the failed row has no subject. On the live Website
Business record it would have cleared "no ceo run had started by 02:00 Eastern" three seconds after it was
written, on the strength of the row that emailed the owner about that very failure. Everything else asked of
this track holds: the reasons are folded on the Gateway and rendered verbatim, the Handled route is scoped and
append-only, PAUSED tells "Nothing scheduled" from "switched off", and the head may carry any title.

## Scope

- Diff: `git diff origin/main...HEAD` in `D:/ReposFred/_wt/factories-screen-review-r2a` (HEAD 12a71208c, one
  commit on top of origin/main b7802982f). Eighteen files: the fold, the two endpoint files, the DTOs, the
  client, five Cockpit files, PLAN.md, and four test files.
- Read against: MANDATE-round-2-owner-feedback.md items 1, 2, 5 and 6; BRIEF-r2-status.md; PLAN.md "Round 2".
- Real rows: `cc-devthrottle factory activity --factory website-business --json` pulled by outcome (30 failed,
  853 done, 63 escalated, 0 asked, 195 started, and the nothing-to-do rows of 28 September, 5 October and
  6 October). The rule in `IsOver` was replayed over every one of the 30 failed rows.
- Run by me: `CcDirector.Gateway.UnitTests` filtered to FactoriesScreenRound2Tests, FactoriesScreenFoldTests
  and FactoryAgentsSwitchTests - 96 passed, 0 failed; the Cockpit file `FactoriesRound2.test.tsx` - 7 passed.
- Not run by me: the real-host route test in `CcDirector.Gateway.Tests` (the pull request reports 13 of 13),
  the full parked suites, and the local gate. I take the pull request's word for those and say so.

## Finding 1 - a failure with no subject is cleared by any later subject-less "done" row of the same seat

**Where.** `FactoriesScreenFold.IsOver` and `SameSubject` (src/CcDirector.Gateway/Factory/FactoriesScreenFold.cs).
`SameSubject(null, null)` is true, so a failed row with no subject is "over" the moment the same seat writes any
later row with outcome `done` or `nothing-to-do` and no subject, whatever that row is about. PLAN.md "Round 2"
writes this in as the rule ("a row with no subject matches only another row with no subject").

**Why the premise does not hold on real rows.** The rule rests on `done` meaning "the work succeeded" and on
"a step a seat only intends is `started`". On the live record the seats of Website Business write the START of
a run as `done` ("run.start (done): scout run 12 started" - 46 such rows), and the chain seat writes its
failure notification as `done`. Six of the thirty live failures have no subject (four `send.plan (failed)` from
Sender, `run.finish (failed)` from Scout, `run.sweep (failed)` from chain), and under the rule every one of
them is cleared by a row that is not a success at the failed thing:

| Failed row (no subject) | Cleared by | Gap |
|---|---|---|
| chain, 2026-10-05 01:28:52, "run.sweep (failed): result=failed: not started: no ceo run had started by 02:00 Eastern" (id 35f6b298) | chain, 01:28:55, "run.notify (done): emailed the owner: Website Business: ceo run 35 failed (2026-10-04)" (id 6f744b70) | 3 seconds |
| scout, 2026-09-27 11:20:59, "run.finish (failed): result=failed: scout run 7; ... the proof check failed" | scout, 2026-09-28 11:00:52, "run.start (done): scout run 12 started" | 23 h 40 min |
| sender, 2026-09-25 12:19:35, "send.plan (failed): add_batch.py: STOP: 2026-09-28 has 0 free slots, batch has 2" (and three like it on 26, 27 and 29 September) | sender, 13:04:24, "run.finish (done): result=succeeded: sender run 2 ..." | 45 min to 2 h |

**The harm.** The first row is the one the mandate is about: a factory whose CEO run never started is the
failure the owner most needs to see, and the list would have shown FAILING for three seconds and then RUNNING
(or NEEDS YOU), because the seat's own "I emailed the owner that it failed" row counts as the seat succeeding.
The second row shows the general mechanism: the next run merely STARTING clears the previous run's failure
before anything has been proven to work. The Sender rows are arguable - the seat's own run.finish claimed
success - but they clear on the run's summary, not on a later success at planning the send, and the row's
text says the planning stopped. None of these is "a later successful row for the same seat and subject" in
the sense the owner wrote: there is no subject, and the clearing row is not about the same thing.

**What the tests cover and do not.** `Failing_DoesNotClearOn` has a case "a later run-wide success with no
subject", but it pairs a subject-less done row with a failure THAT HAS a subject. There is no test with a
subject-less failure, so the behaviour above is untested rather than tested-and-chosen. The pull request's own
sentence "checked against the live Website Business rows" checked the four 12:02 keep.page rows (which do have
subjects) and not the six without one.

**What would fix it.** A failed row with no subject should clear only by Handled (the owner's act), or - if a
self-clear is wanted - on a later subject-less row of the same seat whose text is a run-finish success, which
is a text match the fold would have to own. Either way PLAN.md "Round 2" needs the sentence changed and a test
added for a subject-less failure followed by a subject-less done row (and by a run.start row) that must stay
FAILING.

## What holds (checked, no defect)

**The status reason is the Gateway's and the client composes nothing (CLAUDE.md rule 7).** `StatusLine`,
`StatusHref`, `WaitingHref`, the Failing card's `Heading`, `Note`, each item's `What`, `By`, `Note`,
`HandledLabel`, `HandledBusyLabel`, `LinkLabel` and `SessionLabel` are all strings the fold stamps.
`StatusWord` (FactoryParts.tsx), `FailuresCard` and `FailureItem` (FactoryFailures.tsx) render them verbatim and
branch only on null for layout (link or no link, button or no button). The only client-made string is a React
`key` for a schedule item, which is not shown. The Cockpit tests assert verbatim rendering of fixture strings
marked "(fixture)", so a client that composed its own words would fail them.

**The subject-ful half of the clearing rule behaves on real rows.** All 24 failed rows with a subject clear on
a later same-seat same-subject done or nothing-to-do row: the eight Front Desk check failures of 28 September
and the three of 5 October each clear on the trigger's next "nothing to do" check of the same trigger; the four
keep.page 404s of 6 October clear on Sender's same-business rows. Two draft.redraft failures of 29 September
("R&R Fence & Stain LLC", "Sharp Visions Fence and Construction LLC") have no later success and would have
counted for their full 24 hours - correct, the record never says they were resolved.

**It does not clear on the wrong things.** Checked in `IsOver` and by the six-case theory: an earlier success,
another seat's success, another subject's success, a `started` row, an `escalated` row, and a correction of
another row (`CorrectsId is null` is required of the clearing row, so "Marked handled" on an old escalation does
not read as the seat succeeding). The factory is pinned by `SameId(r.Factory, f.Factory)` before `IsOver` sees
the rows, so a success from another factory cannot clear. Reverting the `CorrectsId is null` guard or the
same-subject check turns the positive tests red; the theory's cases are negatives and stay green under the old
rule by design, which is fine because the positive cases carry the proof.

**One small inaccuracy in PLAN.md.** It says the four 12:02 failures are cleared by "keep.recorded (done)" at
12:11. On the record the first clearing row is "keep.export (done): domains: none offered" at 12:10:26-28,
same seat and business. Rule-consistent, and it illustrates that the rule is at the level of the business, not
the step: a different step about the same business clears the failed step. That is what the owner asked for
("same seat and subject"), so it is not a defect, but the plan's example should name the row that clears.

**The Handled route (POST /gateway/factories/{factory}/failures/{id:guid}/handled).**
- Behind `FactoryAgentsGate` (mapped on `factoryGate` in GatewayHost.cs line 4938); the switch test lists it
  among the gated routes.
- Through `FactoryAgentsViewEndpoints.Owner`: a request with no bound account is 403, a SESSION KEY is 403. It
  accepts the account's device credential and a Director's machine token, and records which in the actor
  string ("owner (device:...)" or "owner (machine-token)"). That is exactly the posture of the existing
  escalation Handled and pause routes it reuses - not a regression, but "owner-only" means "not a session key"
  here, as it does everywhere on this surface.
- Tenant-scoped at every step: `Registry.Find(tenant, factory)`, `ReadAll(..., tenant, ...)` for the failed rows
  of that registered factory only, `Corrections(a, tenant)`, `a.Append(tenant, ...)`. A guid from another
  tenant's record is a 404 because the read is filtered to this tenant and factory.
- Writes a NEW row: `CorrectingRow` builds an `AppendFactoryActivityRequest` with `CorrectsId` = the failed row,
  outcome `done`, actor = the owner string. Nothing updates the failed row; the route test asserts it is still
  returned by the record afterwards. A second press is refused with "This failure is already handled." and a
  cut corrections read refuses rather than guessing.
- The reread after Handled sees the correction: `AllCorrections` unions the tenant-wide corrections with the
  window rows that carry `CorrectsId`, and the handled row is always later than the failure it corrects, so it
  is inside both the 24-hour list window and the 7-day page window whenever the failure still counts.

**PAUSED says why.** `PausedText` yields "Nothing scheduled" only when no seat names a schedule the Gateway has
and no seat trigger exists, and otherwise counts ("2 schedules switched off", "1 trigger paused", "1 named
schedule no longer exists"). Tested for each shape. One pre-existing limit it inherits: the triggers counted are
those whose `FactoryAgent` equals a seat ID, and the live website-new-mail trigger names its seat "Front Desk"
while the manifest's seat ID is "front-desk" (and the "chain" trigger names no seat at all). For a factory whose
only live automation is such a trigger, PAUSED and its new reason would both ignore it. Website Business has
enabled schedules so it is not affected today; this filter is not part of this change.

**The head may have any title.** `ceoSeat` is kept and read as the head; `HeadText` gives "CFO Ruth Calder",
the button "Talk to Ruth Calder" (or "Talk to the CFO" on a duplicate name), the card "Latest from the CFO", and
no head gives "No head named" / "This factory has no head named." The registry requires a seat role, so the
"head" fallback is unreachable but harmless.

**Status line wording.** One failure: "Sender failed today 12:02: ...". Several from one seat: "Sender: 4
failures, newest today 12:02: ...". Several seats: "N failures from M seats, newest ...". Text cut at 90
characters with "...". NEEDS YOU: "63 decisions since 21 Sep 09:00; newest Sender, today 12:12: ..." which puts
the Gmail sign-in item the mandate cares about at the top of the line. The failure line uses the seat's clock
and the waiting line the account's zone, the same split the rest of the screen already makes.

## Summary for the Lead (round 1)

Fix finding 1 (subject-less failures must not self-clear on a subject-less done row; amend PLAN.md and add the
test), correct the PLAN.md example row, and this is ready to merge. Nothing else found.

---

# Round 2 - the fix (head 4ddd94fe7, 7 October 2026)

## Verdict: APPROVED - finding 1 is fixed, nothing new found

## Scope

- The worktree is at 4ddd94fe7, which `gh pr view 3607` confirms is the pull request's head; origin/main is still
  b7802982f, so the base has not moved. One new commit on top of round 1's 12a71208c: "fix(factories): a failure
  with no subject clears only by Handled".
- Delta read in full (`git diff 12a71208c..HEAD`): three files - `FactoriesScreenFold.cs` (the rule), 
  `FactoriesScreenRound2Tests.cs` (two new tests), and PLAN.md "Round 2". The other fifteen files of the pull
  request are byte-for-byte what round 1 reviewed; the whole-diff stat against origin/main changed only in those
  three files, so nothing new entered elsewhere.
- Run by me at the new head: `CcDirector.Gateway.UnitTests` filtered to FactoriesScreenRound2Tests and
  FactoriesScreenFoldTests - 81 passed, 0 failed. The amended rule was replayed over the same 30 real failed
  rows of Website Business pulled in round 1. Not run by me: the real-host route test, the parked suites, the
  local gate, the Cockpit tests (no Cockpit file changed in the delta).

## Finding 1 - fixed

**The code.** `IsOver` now returns false straight after the handled check when the failed row's subject is
null or whitespace, so a subject-less failure can only be over by a correcting row. `SameSubject` now requires
both subjects to be present before comparing, so two empty subjects no longer match. The comment on the rule
and PLAN.md say the same thing and name the real rows that forced it (the 46 `run.start (done)` rows, chain's
`run.notify (done)` three seconds after its `run.sweep (failed)`).

**On the real rows.** Replaying the amended rule over all 30 live failures: the six with no subject (four
Sender `send.plan (failed)`, Scout's `run.finish (failed)`, chain's `run.sweep (failed)`) are now cleared by
nothing on the record and would count for their 24 hours or until Handled - the chain failure of 5 October no
longer vanishes after three seconds. The 22 with a subject still clear on the same rows as before (the trigger
checks on their next "nothing to do", the four keep.page 404s on Sender's same-business rows at 12:10). The two
draft.redraft failures of 29 September with no later success still count, as they should.

**The tests watch the fix.** `Failing_ASubjectlessFailure_IsNotClearedByALaterSubjectlessDoneRow` pairs a
subject-less failure with a later subject-less `done` row in each of the three live shapes (run.start,
run.notify, run.finish) plus a subject-less nothing-to-do row, and asserts FAILING. Under round 1's rule each of
those `done` rows was same seat, later, no correction and matching empty subjects, so the row would have read
RUNNING and the test would be red - it is not a test that stays green on revert.
`Failing_ASubjectlessFailure_ClearsOnHandled` builds the correcting row from `FailureHandledRow` and asserts the
one remaining clearing path still works for a subject-less failure. The existing six-case theory and the
subject-ful positive tests are unchanged and still pass, so the fix did not narrow the rule for rows with a
subject.

**PLAN.md.** The example row is corrected to `keep.export (done)` at 12:10 with the note that the rule is at the
level of the subject, and the subject-less rule is written in as its own paragraph with the evidence. The
sentence "Steps a seat only intends are `started`" remains; it is true of the site-checker's "(intended)" rows
and no longer load-bearing, since a subject-less `done` row can no longer clear anything.

## Nothing new

The route, the DTOs, the client, the Cockpit files and the other tests are identical to round 1 and the round 1
assessment of them stands. No new finding.
