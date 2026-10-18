# Phase 6, Track B - Forget this machine

**Why.** The review of pull request 4172 (finding 3) found that no machine can be removed. A machine that is
renamed, whose operating system user changes, or that is taken out of service keeps its old row for ever. With the
alarms of 4172, that row becomes a red banner on every page, telling the owner to do something they cannot do. The
Tech Lead decided: a small separate pull request that adds a Forget this machine action for the owner only, made of a
Gateway route and a Cockpit button, with the fold deciding when it is offered. Forgetting ends the machine's open
health rows, so its alarms close, and removes its row. A machine that reports again comes back. There is no automatic
age-out.

## What was built

- **The verdict is folded once, on the Gateway** (`LicenseDisplayFold.ApplyForget` and `ForgetVerdict`). Each machine
  of the overview has `canForget` and `forgetText`. It is offered only for a machine whose status is `report-refused`
  or `not-reporting`, and never while a sign-in (`signInsInProgress`) on it is under way. A switch recorded on such a
  machine does not stop it (review finding 1): a machine that is gone never answers its switch. It runs last in the overview, after the sign-in fold, so a sign-in in progress counts. The Cockpit draws the
  button where `canForget` is true and decides nothing.
- **The route** is `POST /gateway/licenses/machines/forget`, with the body `{"machineId": "..."}`. It is owner only,
  by the same `FleetManagerOwnerDevice.Require` rule as the other license writes, and its body must be
  `application/json`. A session key is refused by `SessionKeyGuard`, which does not list the route. The route folds
  the same overview the page gets (the new `FoldOverview`, now shared with the overview route) and refuses exactly
  what the page does not offer, with the same sentence: `409` `forget_not_offered` or `sign_in_in_progress`. An identifier that is not this account's machine is `404` `machine_not_found`.
- **The store does it in one save** (`LicenseStore.ForgetMachine`):
  - it ends the machine's open `report-refused` rows (by `IsRefusalOf`, the same rule as the page) and its open
    `machine-silent` rows;
  - it ends the machine's open switch rows as `failed`, with reason `machine-forgotten`. A machine that is gone never
    answers, and the minute check settles an unanswered switch only from a later report, so such a switch would never
    end. Ending it also means no open row names the machine row that is gone; the minute check throws on one that does;
  - it removes its holdings and its machine row;
  - it writes one `forgotten` row ("Machine forgotten"), which keeps the machine's identifier and name.

  The store reads the machine's open rows again and refuses an open sign-in, with nothing changed: a sign-in is only a
  delay, because the sign-in check ends one on a machine it has not seen for long enough. It also refuses, with
  nothing changed, a machine with no open refusal that has reported again since the page was read: its last accepted
  report is not stale by `LicenseStaleness.IsMachineStale`, the page's own rule (round two, finding 1). A refused
  report never moves the last accepted report, so a refused machine is never refused for this. The ended switch's
  `reasonText` is "The machine was forgotten, so this switch can never be answered." (round two, finding 2). That read and the save are
  NOT one transaction (review finding 4): a sign-in started between them stays open in the log on a machine that is
  gone. A machine none of whose reports was ever accepted is forgotten under the stand-in identifier the page lists it
  under, and its refusal ends.
- **The approvers on that machine go with it** (review finding 3). The route reads the accounts whose approvers are
  on this machine and user, the store's row names them ("It approved the sign-ins of ...; that approver is removed
  too."), and after the store's save `LicenseApproverStore.RemoveForMachine` removes them. Their approver alarms close
  on the next check. Unlike the owner's own removal, it leaves no removal record, so a machine that comes back
  registers its approver again. The approvers live in the account's settings, apart from the store, so the two writes
  are not one save; when the store refuses, the approvers stay.
- **What closes by itself afterwards.** The refused and silent alarms of 4172 (merged as `8994f5e1a`) close on the
  next minute, because their rows are ended. The readiness alarm of a machine closes because the machine is no longer read. An
  open renewal warning about it is ended by the renewal email check, which finds that the machine no longer holds
  that sign-in. A machine that reports again is a new row, like a machine reporting for the first time.
- **The Cockpit** shows a "Forget this machine" button under the machine's status in the schedule, and a
  confirmation that shows the Gateway's `forgetText`. After it, the answer's sentence is shown and the page reads
  everything again. A refusal is shown in the Gateway's sentence, and the confirmation stays open. The client function
  is `forgetLicenseMachine` in client-core. The phone has no Licenses screen.
- **Documents.** `cockpit-api.md` has the two machine fields, the `forgotten` log kind and the route section with
  its answers, and the "Who may call the writes" table now covers three writes. The overview example was produced
  again from the real route (`produce_overview_example.py`: 0 differences, the two new fields added), and
  `fixtures.ts` was copied from it again (`copy_fixtures.py`).

## The tests

`src/CcDirector.Gateway.UnitTests/Licenses/LicenseForgetMachineTests.cs` runs the real handler over the real store on
a throwaway SQLite file, and reads the page back through the real overview route.

- `ASilentMachine_IsOfferedForgetting_AndForgettingItTakesItOffThePage_EndsItsSilence_AndKeepsTheLog`: the offer
  and its sentence, the answer's sentence and row, the machine off the page, its silence ended, its holdings gone,
  and the log still holding the earlier row.
- `AForgottenMachineThatReportsAgain_ComesBack`: it comes back, reporting, with nothing to forget.
- `AReportingMachine_IsNotOffered_AndTheRouteRefusesItWithTheSameSentence`: `409` with the page's own sentence.
- `AMachineThatDiedWithASwitchUnanswered_IsOffered_AndForgettingEndsTheSwitchAsFailed` (review finding 1): a switch
  marked unanswered stays open through three days of the real minute check. The machine is offered and forgotten, the
  switch is `failed` with `machine-forgotten`, and the minute check then runs with nothing to say.
- `AMachineThatDiedReportingASwitchInProgress_IsOffered_AndForgotten` (review finding 1): the switch its last report
  named does not stop it.
- `TheStoreRefusesAMachineThatReportedAgain_AfterThePageWasRead_AndChangesNothing` (round two, finding 1): the store
  alone, called on a machine that reported again with an unanswered switch and an approver; it refuses, and the
  switch, the machine and the approver stay.
- `ASilentMachineWithASignInUnderWay_IsNotOffered_AndTheStoreRefusesItToo`: not offered, and the store refuses it
  with nothing changed.
- `AMachineThatApprovesSignIns_TakesItsApproversWithIt_AndSaysSo` (review finding 3): the answer names the account,
  only the approver on that machine goes, and no removal record is left.
- `AMachineWhoseReportsWereNeverAccepted_IsForgottenUnderItsStandInRow_AndItsRefusalEnds` and
  `ARefusedMachineWithARow_IsForgotten_AndItsRefusalEnds`: the real ingest refuses the reports, and the machine is
  forgotten.
- `ForgettingASilentMachine_ClosesItsAlarm_OnTheNextRaise` and
  `ForgettingARefusedMachineThatNeverReportedWell_ClosesItsAlarm_OnTheNextRaise`: the reason this exists. The real
  `LicenseMachineHealthAlarm` and alarm service have the alarm open, the machine is forgotten, and the next raise
  leaves no alarm open.
- `OneAccount_CannotForgetAnotherAccountsMachine`: `404`, at the route and at the store, and nothing changes.
- `AMachineIdThatIsNotOne_IsRefusedBeforeAnythingIsRead` (three cases) and
  `ACallerThatIsNotTheOwner_IsRefused_AndNothingChanges`.

Also changed:
- `LicenseEndpointsTests`: the machine's exact field list now has `canForget` and `forgetText`.
- `LicenseGuardTests`: the route is not on the session key guard's list.
- `LicenseHostTests` (`LicenseWritesSelfHostedTests`): the shared machine token is refused this write too, with `403`
  `owner_only`, over real HTTP.
- `apps/cockpit/src/licenses/LicensesViewForget.test.tsx`: no button where it is not offered; the confirmation in
  the Gateway's sentence; one `application/json` POST naming the machine; the answer shown as given; a refusal shown
  with the confirmation still open.

**Revert proofs.**
- Before the review: I made the store skip its open switch and sign-in check. The two store tests failed, and the
  other ten passed. With the check restored, all twelve passed.
- Round two, with 17 tests: without the store's staleness check, its new test fails; without the reason's sentence,
  the unanswered-switch test fails (it now checks the log's `reasonText`). In each case the other sixteen passed, and
  with the code restored all seventeen passed.
- After the review, with 16 tests: putting back the fold's refusal of a machine with a switch in progress fails the
  two finding 1 tests; skipping the approver removal fails the finding 3 test. In each case the other tests passed,
  and with the code restored all sixteen passed.
- After the rebase onto 4172, I made the store leave the open health rows open. Five tests failed: the two alarm
  tests and the three that check the rows end. The other nine passed. With the store restored, all fourteen passed.

## The gate

Run on commit `79ae2a7d1`, after the round-two review, on origin/main `58e563ba0`, with TEMP and TMP on D:. The next
commit changes only this file.

- **Default run: GREEN**, tree clean. Every suite completed, including Core.UnitTests (2381) and the parked
  Gateway.UnitTests tests this change reaches (1059).
- **`-Parked -Filter License`: GREEN**, tree clean. Gateway.UnitTests: 1115. Gateway.Tests: 26 (shard 3 of 4, with
  the self-hosted write test). Core.UnitTests: 259. Avalonia.Tests: 32.
- **Cockpit:** `vitest run src/licenses` passed 113 tests in 6 files. `npm run typecheck --workspaces` is clean.
  `copy_fixtures.py` gives the committed `fixtures.ts`.

## What the live proof should look for (after a deploy; this piece deploys nothing)

- A machine that has been stopped for a while shows "Forget this machine" in the schedule. A reporting machine does
  not.
- Forgetting it takes it off the page, writes a "Machine forgotten" row in the Licenses log, and closes its "Machine
  stopped reporting" banner within a minute.
- Starting its Director again brings the machine back.
