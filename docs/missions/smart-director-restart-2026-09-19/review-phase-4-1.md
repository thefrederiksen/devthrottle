# Review - Smart Director Restart phase 4 (1 of 1): restored sessions inherit dev reports

Reviewer seat: opened by the Delivery Lead (session number 150). Review of `git diff 0346b3992 HEAD` on branch
`smart-restart/p4-dev-reports`, head `1a7019a10`, in the worktree `D:/ReposFred/devthrottle-smart-restart-p4-review1`.
This file is written to that worktree and not committed.

## SCOPE - what I read, what I ran, what I could not reach

Read in full: the phase 4 Developer mandate, `mission.md` (section 5.3 item 13), the Developer's proof, and every
file in the diff: `DevReportInheritance.cs` (new), `DevReportStore.cs`, `WorkspaceEndpoints.cs`, `GatewayHost.cs`,
`WorkspaceRestoreDtos.cs`, `GatewayClient.cs`, `DirectorRestore.cs`, and the four test files
(`DevReportInheritanceTests.cs`, the additions to `DirectorRestoreTests.cs`, the one row added to
`SessionKeyGuardTests.cs`, and the additions to the hosted `WorkspaceRestoreRouteTests.cs`).

Read for context, not changed by this work: `SessionKeyGuard.cs`, `DevReportEndpoints.cs`, `DevReportDelivery.cs`
(the settle and refusal rules), `WorkspaceStore.cs` (the restore lease, the restore marks, and the provenance
restore that keeps an ordinary write from setting a restored session id), `GatewayDbContext.cs` and
`GatewayDatabase.cs` (the tenant query filter and the explicit-tenant context), the dev report table mappings and
their unique index, and the restore command handler in `ControlApiHost.cs` (the caller of `PrepareAsync` and
`RunAsync` in the real product path).

Ran, all in the foreground, every count read from the summary line:

- The mandate's check, `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~DevReport|FullyQualifiedName~Drain|FullyQualifiedName~Restart"`:
  **0 failed, 665 passed, 0 skipped, 665 total** - exactly the proof's "after" count.
- The session key guard suite, filter `SessionKeyGuardTests`: **0 failed, 287 passed** - matches the proof.
- The hosted route class, `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~WorkspaceRestoreRouteTests"`:
  **0 failed, 21 passed, 0 skipped** - in the foreground, 49 seconds. The proof's claim that the hosted route tests
  really ran is reproduced; no hosted run needed to leave the foreground today.
- The hosted dev report routes class, filter `DevReportRoutesHostedTests`: **0 failed, 17 passed** - so the proof's
  combined 38 is real (21 plus 17).

What I could not reach:

- I did not mutate the source to re-run the Developer's revert proofs: my mandate forbids changing any tracked
  file, even temporarily. Instead I read each new test and asked whether it can fail: every refusal test asserts a
  specific exception message or a specific status code over the real rule and the real stores, so each goes red if
  its guard is removed. The Developer's own mutation table names which test went red for each guard, and the
  mutations cover the store write, the note move, the lease check, the caller's selection of seats, and both
  authorization halves.
- I did not re-measure the 650 baseline on untouched `origin/main`. I verified the arithmetic instead: 665 total
  minus the fifteen new test methods visible in the diff (eleven rule tests, four restore tests) is 650, and the
  after count is reproduced exactly.
- No real Director restart was performed (phase 5). The two-Gateway-process race during a deploy swap is named by
  the Developer and is not tested here either; I confirmed the single-process case is safe because publishing,
  item inserts and the pass all take the same in-process lock in `DevReportStore`.

## What I verified, so the findings below are the whole list

- **The join is the record's, not the caller's.** The request body names a Director and a seat and nothing else.
  The old session id is the seat's captured session id, which no write can change, and the new one is the seat's
  restored session id, which only a restore mark carrying the start token of the Director that started the seat can
  write, or the spawn door's own record of that token (`WorkspaceStore.RecordRestoreMark`, `RecordRestoredByClaim`,
  and `RestoreStoredMarks`, which puts the stored marks back on every ordinary write). There is no field in which a
  caller can name either session. Test 8 proves an ordinary write of a restored id does not stick.
- **Authorization, three independent halves.** The route refuses anything that is not a Director's credential
  (`IsDirectorCredential`), refuses a credential that is not the one the named Director is connected on
  (`callerIsDirector`, which is scoped to the request's own account), and the rule itself refuses a Director that
  does not hold a live restore lease. The session key guard is an allow list that does not name the new route, and
  the route refuses a session again on its own, so opening the guard alone would not be enough - the Developer's
  mutation 6 demonstrates the two refusals are independent. A session can drive a restore (an existing capability)
  but can never itself ask for the pass.
- **Tenancy on the hosted Gateway.** The workspace is read through a context scoped to the caller's account, and
  the workspace table carries the global tenant query filter, so another account's workspace is a not-found. The
  dev report store is always called with an explicit tenant, and every report, item, version and reply table is
  tenant scoped. Test 5 covers another account's report under the same session id. The pass route cannot even
  reveal whether another account's workspace exists.
- **No schema change was needed, as claimed.** The pass updates existing columns only: the report's session id,
  and the session id on the items. The unique index on (account, session id, key) is respected by the
  already-published check, which leaves such a report frozen with the old session and names its key in the answer.
  No migration file is in the diff and none was needed for what the diff does.
- **Everything keyed on the session that should follow, follows.** Report ids, versions, the owner's notes and
  answers and their delivery state, and the agent's replies all follow the report; the link is unchanged. The
  delivery drain reads items by the session id they carry, and the pass updates that column. Asked twice is safe:
  the second ask passes nothing and changes nothing.
- **A failed pass does not fail the restore.** A refused or failed ask is caught, logged and written into the
  seat's outcome as a plain sentence; the seat has come back and is recorded as restored. A seat that did not come
  back is never asked for.
- **The restore asks only after the record says what the seat came back as.** The ask happens after the restored
  mark, because the Gateway reads the join from that record; asking before it would be asking about a seat that
  has not come back, and the rule refuses that with its reason.

## Findings

### 1. (Low) The start-of-run retry ask for a seat that is already back is never reported to anyone but the log

`src/CcDirector.ControlApi/Drain/DirectorRestore.cs`, lines 326-330 (the loop `foreach (var back in doc.Seats
.Where(... RestoredSessionId is set ...))`). The sentence returned by `PassDevReportsAsync` there is discarded;
only the file log keeps it.

What breaks, for whom: the retry loop exists for exactly one case - a Director that died between the create and
its own ask, so that seat's pass was never asked for and never reported on any outcome. When the next run asks
again and the ask SUCCEEDS, nothing needed saying and there is no harm. When the next run asks again and the ask
FAILS - the realistic case is a Director on new code against a hosted Gateway that does not yet have the route,
which is the state of the world until the owner deploys - the reports of that seat stay frozen and the only record
is a log line. The owner, who was promised that a failed pass is said on the seat's outcome, is told nothing: the
seat is not in that run's targets, so it has no outcome row at all. The links freeze silently in the one case the
loop was built for.

Why it must change, or be accepted out loud: the fix is not obvious, because a seat already back has no outcome
row in this run by design and adding one changes the restore's result contract. But the mandate asks what happens
when the pass fails during a restore, and here the answer is "the log says so and nobody else". If the Delivery
Lead accepts it, it should be accepted as a named limit in the proof, the way the last-seat-of-a-workspace case is;
at present the proof says a refused ask "is reported on each seat's outcome", which is true only for seats this
run brought back or found recorded mid-run, not for seats already back when the run read the workspace.

### 2. (Low) A note the settle pass already refused for the ended session moves with the report but stays refused, so the restored session never receives it

`src/CcDirector.Gateway/DevReports/DevReportStore.cs`, `PassToSession` (the items keep their status and only the
session id changes), and `src/CcDirector.Gateway/DevReports/DevReportDelivery.cs`, `RefuseWaitingForEndedSession`
at line 268, which marks a held item "This session has ended" when a session ends with items still held.

What breaks, for whom: the owner writes a note on a report; the session is then ended by the drain (the smart
restart's own act, for example a session ended at the time limit without finishing its turn) with the note still
held; the settle pass marks the note refused because the session has ended; the restore brings the session back
and the pass moves the note to the new session - still refused. The note the owner wrote for the session's
attention never reaches the restored session, and "the notes follow the report" is true only for notes that were
still waiting when the pass ran.

Why it must change, or be accepted out loud: the Developer names exactly this in the proof and declines to make
the delivery ruling here, which is the right instinct - reviving a refused delivery state is a ruling for the
delivery rules, not for this change. I state it as a finding so the Delivery Lead carries it forward: it is the one
case where a report is genuinely half-inherited, and the sentence "a note held for the old session is afterwards
held for the new one" (proof, test 3) is true only while the note is still held rather than already refused. The
owner sees the refusal state plainly on the item, so nothing is hidden; but the mission's own wording - the
restore "must pass ownership of the old session's reports to the new one" - is worth one deliberate decision
rather than silence.

## Verdict

No finding above blocks the merge. The authorization design is the strongest part of this change: the join cannot
be forged, every refusal is a presence check with its reason, and the two refusal halves are independent. The
counts in the proof reproduce exactly, including the hosted route tests, which I ran in the foreground. Both
findings are honesty gaps at the edges of a flow whose centre is sound, and both are already half-acknowledged in
the proof; finding 1's claim in the proof ("is reported on each seat's outcome") is the only sentence in the proof
I would correct before this branch is called done.

END OF REVIEW
