# Answers to review 1 - Smart Director Restart phase 4 (restored sessions inherit dev reports)

Branch `smart-restart/p4-dev-reports`. The review being answered is `review-phase-4-1.md` beside this file,
copied in unchanged from the Reviewer's worktree. The Developer that built this work is gone, so this
Developer stands in for it and answers every finding, as the method requires. A Reviewer advises; it does not
command, and a finding must prove harm to be worth a change.

Both findings are answered the same way: **the code behaviour is unchanged, and the record is corrected.**
Neither finding showed harm that a code change would remove, and in finding 1 the remedy the review sketched
would not reach the owner at all - see below. The Reviewer marked both low and said neither blocks the merge.

---

## Finding 1 (Low) - the start-of-run retry ask for a seat already back is never reported to anyone but the log

**Declined as a code change. Accepted as a correction to the proof and to the code comments.**

### What the review has right

It is exactly right about the mechanism. `DirectorRestore.RunStepsAsync` asks again, at the start of every run,
for each seat the workspace already shows as back, and throws the returned sentence away. Such a seat is not one
of this run's targets, so it has no `SeatRestoreOutcome` row, and nothing but the Director's file log records
whether that retry passed or failed. The proof's sentence "its outcome carries a plain sentence saying the
reports did not pass and why" was therefore true only of seats this run brings back, or finds recorded mid-run.

### Why no code changed

Because the remedy would tell the owner nothing. **No product surface reads the restore's result at all.**
Checked on the merged source of this branch:

- `DirectorRestoreResult` has exactly one production caller,
  `src/CcDirector.ControlApi/ControlApiHost.cs` line 1262. It runs the restore in a fire-and-forget task and
  discards what comes back; the command has already answered `Taken = true` with the seat list before the run
  starts.
- Searching `src/`, `apps/`, `packages/` and `tools/` for `DirectorRestoreResult` and `SeatRestoreOutcome`
  finds the declaration, the two places inside `DirectorRestore.cs` that build it, and the tests. Nothing else.

So for EVERY seat, brought back by this run or not, the record of a failed pass that an owner can actually reach
today is the Director log line. Adding an outcome row for a seat this run did not touch would put a sentence in
an object nobody reads, and would make that object say this run restored a seat it never restored - the row
carries a restored session id and no failure. That is a worse claim than the silence it replaces, for no gain.

The realistic failing case the review names - a newer Director against a hosted Gateway that does not yet have
the route - is also not silent in practice: every seat that run brings back logs "did NOT pass" with the
Gateway's reason, and the proof already states out loud that until the owner deploys the Gateway, a restore
cannot pass reports at all.

### What was corrected instead

1. `docs/missions/smart-director-restart-2026-09-19/proof-phase-4-dev-reports.md` - the two sentences that
   over-claimed are corrected in place and marked as corrected, and a note at the top of the proof says so.
2. `src/CcDirector.ControlApi/Drain/DirectorRestore.cs` - the doc comment on `PassDevReportsAsync` carried the
   same over-claim ("is reported on the seat's outcome and logged"). It now says the ask is always logged and is
   reported on the outcome only when the seat is one this run brings back.
3. The same file, the retry loop - a comment now states that throwing the answer away is deliberate, and why,
   and points at this file.

No test is owed, because no behaviour changed. A comment and a document cannot be proved by a test; the code
around them is proved by the fifteen tests the proof lists, and they still pass (counts below).

### Carried forward, named rather than fixed here

Making the restore's answer reach the owner at all - a screen, the restart history, or the command line - is
real work and is not in this mandate, which is one Gateway change plus one call from the restore. It belongs
with the restart history (mission item 11) and the command line door (item 12). The Delivery Lead has it in the
report from this session.

---

## Finding 2 (Low) - a note already refused for the ended session moves with the report but stays refused

**Declined as a code change. Accepted as a deliberate ruling, written down rather than left silent.**

### What the review has right

The sequence is real. The owner writes a note; the note is still held when the drain ends the session;
`DevReportDelivery.RefuseWaitingForEndedSession` marks it refused with the label "This session has ended";
the restore brings the seat back and `DevReportStore.PassToSession` moves the item to the new session with its
status untouched. The note never reaches the restored session. The proof's plain-words line for test 3 ("a note
held for the old session is afterwards held for the new one") is true of a note still held, not of one already
refused. The previous Developer named this case in the proof and declined to rule on it; the Reviewer asked for
the ruling to be made out loud rather than left as a gap.

### The ruling, and why

**A refused item stays refused when its report passes.** Reviving it would be a change to the dev report
delivery rules, not to the restore, and it would risk delivering the owner's words twice.

- The refusal is not hidden. The owner sees the item on the report with the words "This session has ended". The
  report itself is alive at the same link - that is what this change bought - and a note written on it now goes
  to the restored session: a new item takes the report's CURRENT session id (`DevReportStore.AddItems` sets an
  item's session from `report.SessionId`). So the owner's way forward is one note, on the link he was already
  reading.
- Un-refusing would be a genuine hazard, not a neutral improvement. An owner who saw "This session has ended"
  may already have written the note again. Reviving the old one delivers the same instruction to the agent
  twice, and an instruction the owner did not mean to give twice is the kind of harm this mission's rules exist
  to prevent. Nothing in the record says which he wants.
- It is not this mandate's to decide. Item 13 asks that the restore pass OWNERSHIP of the old session's reports
  to the new one. Ownership passes: the report, its versions, its notes and answers with their state, and its
  replies. Changing what a refused delivery means is a ruling for the dev report delivery rules, and the owner
  can reverse it there - if he wants a note refused only because the session ended to be revived when that
  session comes back, that is a small, well-defined change to `PassToSession` plus its own test, on its own
  mandate.

No code changed, so no test is owed. The behaviour the ruling describes is already covered: test 3 proves a HELD
note follows the report to the new session, and the item's status is deliberately untouched by the pass.

---

## The check, on the merged result

`git fetch origin` then `git merge origin/main` into this branch, no rebase and no force push. The merge was
clean: no conflicts, and the five commits main had gained touch none of this branch's source files.

Every count below was read from the run's summary line, on the final source of this branch - that is, after the
merge AND after the three comment corrections above. Every run was in the foreground.

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~DevReport|FullyQualifiedName~Drain|FullyQualifiedName~Restart"

| Run | Failed | Passed | Skipped | Total |
|---|---|---|---|---|
| The mandate's filter, merged result, fresh build | 0 | 696 | 0 | 696 |

696, not the 665 in the proof. The difference is main's, not this branch's: commit `aa8b22912` (Smart Director
Restart phase 1, the smart shutdown run) added `SmartShutdownRunTests` and rows to
`DrainMessagesSmartShutdownTests`, whose names match the `Drain` and `Restart` halves of the filter. This branch
adds no test between the proof's run and this one. The same filter was also run once on the merged result before
the comment corrections and gave the same 696.

The hosted project, which covers the route this work touched. Split so each command stayed well under nine
minutes, and both were run again together on the final source:

    dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~WorkspaceRestoreRouteTests|FullyQualifiedName~DevReportRoutesHostedTests"

| Run | Failed | Passed | Skipped | Total |
|---|---|---|---|---|
| `WorkspaceRestoreRouteTests` alone, merged result | 0 | 21 | 0 | 21 |
| `DevReportRoutesHostedTests` alone, merged result | 0 | 17 | 0 | 17 |
| Both together, final source, fresh build (1 minute 44 seconds) | 0 | 38 | 0 | 38 |

And the guard suite, which sits outside the mandate's filter because its name does not match it:

| Run | Failed | Passed | Skipped | Total |
|---|---|---|---|---|
| `SessionKeyGuardTests`, final source | 0 | 287 | 0 | 287 |

All three match the proof's numbers for this branch's own work: 21, 17 and 287.

## What this session did not do

- No revert proof was re-run. The mutations are the previous Developer's, recorded in the proof, and nothing in
  this session changed a guard, a rule or a test - only two comments and two documents.
- The default gate (`scripts\test-local.ps1`) was not run; this mandate names its own check, and the hosted
  project is parked out of that gate anyway, which is why it was run by hand above.
- Nothing was deployed. This is a Gateway change, merged and not deployed; deploying the hosted Gateway is the
  owner's decision and no seat on this mission takes it.
- No real Director restart was performed. That is phase 5.
