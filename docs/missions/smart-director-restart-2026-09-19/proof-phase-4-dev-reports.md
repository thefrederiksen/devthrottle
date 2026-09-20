# Proof - Smart Director Restart phase 4 - restored sessions inherit dev reports

Branch `smart-restart/p4-dev-reports`, cut from `origin/main` at `0346b3992` (checked 0 commits behind before
any file was read). Mission document section 5.3 item 13. No pull request is open; the Delivery Lead sends this
branch to a Reviewer first.

**Corrected on 2026-09-20, when review 1 was answered.** Two sentences below over-claimed where a failed pass is
reported. They are marked in place, and `review-phase-4-1-answers.md` beside this file answers both findings.

## The decision

It was buildable as the mandate asked: one Gateway change plus one call from the restore. **No schema change.**
A report row already carries its session id in an ordinary column, and so does each note and answer, so passing
a report is an update of existing columns. No migration, no new table, no new index.

## What was built

**The Gateway (the authority).** One new route, `POST /gateway/workspaces/{id}/restore/dev-reports`, in
`src/CcDirector.Gateway/Api/WorkspaceEndpoints.cs`. Its body names a Director and a SEAT, and nothing else.
The rule is in one new class, `src/CcDirector.Gateway/DevReports/DevReportInheritance.cs`:

- The old session id is the seat's captured session id. The new session id is the seat's restored session id.
  Both are read from the STORED workspace. That is the join the record already held; no second one was made.
- There is no field in which a caller can name either session. The restored id can only be written by a restore
  mark carrying the start token of the Director that started the seat, or by the spawn door's own record of
  that token. An ordinary write of the workspace keeps the stored copy.
- It passes only when all of these are PRESENT: the workspace is captured, the asking Director holds a live
  restore lease, the seat exists, the seat names what it came back as. Anything else refuses with the reason.
- It is authorised exactly as the restore's marks are: a Director's credential, and the one the named Director
  is connected on. A session key never reaches the route (the session key guard is an allow list that does not
  name it) and the route refuses a session and a person's device itself as well.

The store half is `DevReportStore.PassToSession`: in one transaction the old session's reports, and the notes
and answers on them, change owner. Report ids, versions and replies are untouched, so the link is the same
link. A report whose file the new session had ALREADY published is left with the old session, frozen as
before, and named in the answer - the pair (session, file) is unique, and the new session will keep publishing
to the one it made. Asking twice is safe: the second time the old session has nothing left.

**The restore (the one call).** `DirectorRestore` asks once per seat, only AFTER the "restored" mark is
written, because the Gateway reads the join from that record. Three more things it does:

- A seat that failed to come back is never asked for. Its reports stay frozen, as today.
- A refused or failed ask does not fail the seat. The seat HAS come back; it is logged, and when the seat is one
  this run brings back its outcome carries a plain sentence saying the reports did not pass and why.
  `SeatRestoreOutcome` gained one optional field, `DevReports`. (Corrected when review 1 was answered: a seat that
  was ALREADY back when the run read the workspace is asked for again but has no outcome row in this run, so for it
  the Director log is the whole record. Finding 1, and the wider limit that no product surface reads the restore
  result at all, are answered in `review-phase-4-1-answers.md`.)
- At the start of every run it asks again for each seat already back, because a Director that died between
  the create and its own ask would otherwise never be retried for that seat.

Files: `WorkspaceRestoreDtos.cs` (request and answer), `DevReportInheritance.cs` (new), `DevReportStore.cs`,
`WorkspaceEndpoints.cs`, `GatewayHost.cs` (wiring), `GatewayClient.cs`, `DirectorRestore.cs`, and tests.

## The check - counts

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~DevReport|FullyQualifiedName~Drain|FullyQualifiedName~Restart"

| When | Failed | Passed | Skipped | Total |
|---|---|---|---|---|
| Before, untouched `origin/main` at `0346b3992` | 0 | 650 | 0 | 650 |
| After, final source, fresh build | 0 | 665 | 0 | 665 |

The difference is exactly the fifteen new tests in that filter (eleven rule tests, four restore tests).

Outside the mandate's filter, because the names do not match it:

- `SessionKeyGuardTests`: 287 passed, 0 failed (one new row).
- The hosted project, which DOES cover the route touched. `src/CcDirector.Gateway.Tests`, filter
  `WorkspaceRestoreRouteTests|DevReportRoutesHostedTests`: 38 passed, 0 failed, 0 skipped.
  `WorkspaceRestoreRouteTests` alone: 21 passed, of which two are new.

## What each new test proves, in plain words

Rule tests (`DevReportInheritanceTests`), over the real workspace store and the real dev report store on one
migrated database. Every seat that "came back" came back the way a restore does it - lease, started mark with
a token, restored mark with that token - not by writing the field.

1. `WithoutThePass_...GetsANewReportAtANewLink` - the defect itself, pinned: without the pass, the restored
   session publishing the same file gets a new report with a different id.
2. `Pass_ARestoredSeat_...UpdatesTheSameReportAtTheSameLink` - after the pass, the same publish is version 2 of
   the SAME report id, the old session has no reports left, and version 1 is still readable.
3. `Pass_TheNotesTheOwnerWroteBeforeTheRestart_...` - a note held for the old session is afterwards held for
   the new one, so it is delivered to the session that has the report.
4. `Pass_AskedTwice_IsSafe` - the second ask passes nothing and changes nothing.
5. `Pass_OnlyThatSeatsReports_AndOnlyInThisAccount` - another seat's report and another account's report
   under the same session id are untouched.
6. `Pass_TheNewSessionAlreadyPublishedThatFile_...` - that one report stays frozen with the old session and
   the answer names its file; the old session's other report still passes.
7. `Pass_ASeatThatHasNotComeBack_IsRefused_...` - a seat not brought back keeps its reports.
8. `Pass_ARestoredIdWrittenByAnOrdinaryWrite_IsNotAJoin_SoNothingPasses` - THE TAKE-OVER REFUSAL. A caller
   writes another session's id onto the seat as "what it came back as" and the pass is then asked with a valid
   lease. The write did not stick, the seat has not come back, nothing passes, the stranger has no reports.
9. `Pass_ByADirectorThatDoesNotHoldTheLease_...` - another Director, a lapsed lease, and no lease: each
   refused with its own reason.
10. `Pass_ASeatThatIsNotInTheWorkspace_OrABlankRequest_IsRefused`.
11. `Pass_AnAuthoredWorkspace_IsRefused` - a typed workspace never passes reports. This one document is built
    by hand, and says so: the store will not let an authored workspace hold these fields.

Restore tests (`DirectorRestoreTests`), the real `DirectorRestore` against a fake Gateway whose pass goes
through the REAL rule and the REAL stores, so the caller is watched, not a hand-built input:

12. `RunAsync_ARestoredSeatRepublishingTheSameFile_...` - a whole restore run, then the new session publishes
    the file: same report id, version 2; exactly one ask, naming the seat and this Director.
13. `RunAsync_ASeatThatIsNotBroughtBack_...NothingIsAskedForIt`.
14. `RunAsync_TheGatewayRefusesThePass_TheSeatStillComesBack_...` - the seat is restored and recorded; its
    outcome says the links stay frozen and carries the Gateway's reason.
15. `RunAsync_ADirectorThatDiedBeforeItCouldAsk_TheNextRunAsks...`.

Guard row: `POST .../restore/dev-reports` is refused to a session key.

Hosted tests (a real booted hosted Gateway, real session keys, a tunnel Director on its own device key):

16. `A_restored_session_republishing_the_same_file_...` - a session publishes through the real session route;
    the real restore runs over the real Gateway client; a NEW session with a NEW key publishes the same file
    and gets the same report id, version 2; the owner's link still answers and names the restored session.
17. `The_dev_report_pass_is_refused_to_a_session_to_the_owners_browser_to_another_workstation_and_to_a_Director_without_the_lease`
    - 403, 403, 403, 409; and with the lease but the seat not back, 409 "has not come back". The report has
    not moved.

## Revert proofs

The work was committed first (`ab246c1e2`), so each `git checkout -- <file>` restored the fix and not an older
state. Every run below was the FULL mandate filter with a real build, never a narrower filter and never
`--no-build`. After the last one, `git status` and `git diff HEAD` were empty and the final counts above were
taken on a fresh build.

| Mutation | Result | Tests that went red |
|---|---|---|
| 1. The store does not change the report's session | 6 failed, 659 passed | 2, 4, 5, 6, 12, 15 |
| 2. The notes are not moved | 1 failed, 664 passed | 3 |
| 3. The lease is not checked | 1 failed, 664 passed | 9 |
| 4. The restore asks for failed seats instead of restored ones, and skips seats already back | 4 failed, 661 passed | 12, 13, 14, 15 |
| 5. Four guards off at once: seat came back, captured only, blank seat, file already published | 5 failed, 660 passed | 7, 8, 10, 11, 6 |
| 6a. The session key guard allows the new route | 1 failed, 286 passed | the guard row |
| 6b. The route does not check whose credential it is (hosted run) | 1 failed, 20 passed | 17 |

Test 1 pins the defect and has no mutation of its own; it goes red only if publishing itself changes. Test 16
was not separately mutated on the hosted host; mutations 1 and 4 cover the same path in the unit project.

One thing mutation 6 showed: with the guard opened, a session key was STILL refused by the route's own check.
The two refusals are independent.

## What I could not reach, and what is not covered

- **Nothing is deployed, and until the owner deploys the Gateway a restore cannot pass reports.** A newer
  Director against the current hosted Gateway gets "not found" on the new route. That is written to the Director log
  as "did NOT pass" for every seat, and onto the outcome of each seat this run brings back, and the seat still comes
  back. No harm, no benefit. (Corrected when review 1 was answered; see `review-phase-4-1-answers.md`.)
- **A pass lost on the LAST seat of a workspace is not retried.** A run with no seat left to bring back is
  refused before it starts, so the ask-again loop never runs for it. Narrow (the Director must die in the
  moment between the mark and the ask), and those links freeze exactly as they do today. Fixing it would mean
  letting a restore run with nothing to restore, which is a change to the restore's own rules and outside
  this mandate. Recommendation: leave it unless a real restart shows it.
- **Two Gateway processes during a deploy swap.** If the restored session publishes the file on the other
  process between this pass reading and writing, the unique index refuses the write and the ask fails with an
  error. The seat is unaffected; the next ask keeps that one report frozen and says so. Not tested.
- **Notes the Gateway had already refused.** If the settle pass marked a held note "this session has ended"
  before the restore ran, the pass moves it but does not un-refuse it. Reviving a refused note is a delivery
  ruling, not mine to make here.
- **Several Directors on one machine share a machine key**, so one can ask in another's name. This is the
  same limit the restore marks already state; the lease is what stops it, and the pass needs the lease too.
- The default gate (`scripts\test-local.ps1`) was not run; the mandate names its own check. The hosted project
  is parked there, which is why it was run by hand and is named above.
- The first hosted run outlasted the ten minute foreground limit and the shell moved it aside by itself; it
  finished (21 passed) and was read from its output file. Every later hosted run stayed in the foreground.
- No real Director restart was performed. That is phase 5.
- `origin/main` gained four commits while this was built. None touches a file this branch touches, and a trial
  merge (`git merge-tree`) is clean. The counts above are on this branch's base, not on that merge.
