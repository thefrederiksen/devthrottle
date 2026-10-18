# Review of pull request 4228 - License alarm: an alarm that comes and goes tells the owner once

Reviewer: License Switching - Reviewer - 4228 alarm flap (session 2bf3aab1). Read only; nothing was built,
fixed, committed or pushed on the branch.

## Verdict

No finding. Every item on the checklist holds, and the tests fail without the change. Merge.

## Scope

What I read:
- The whole diff of pull request 4228 (branch `license-alarm-flap-held`, one commit 48680acff, base is
  origin/main at 3e47021cb, nine files).
- On the branch, in full: `src/CcDirector.Gateway/Licenses/LicenseAlarmService.cs` and
  `src/CcDirector.Gateway/Licenses/LicenseAlarmFold.cs`; the fixture and the changed tests in
  `LicenseAlarmServiceTests.cs`, `LicenseAlarmFoldTests.cs`, `LicenseAlarmsEndpointTests.cs`,
  `AddLicenseAlarmsPostgresTests.cs`; contract section 9 as changed.
- The readers of what changed: `LicenseEndpoints.Alarms` (the only caller of the banner fold),
  `LicenseMachineHealthAlarm` and `LicenseReadinessService` (the raisers that consume `LicenseAlarmRaised`),
  `LicenseDisplayFold` (the only reader of the `alarm-opened` log kind), the entity mapping in
  `GatewayDbContext`, and the shared client banner `packages/client-core/src/licenses/LicenseAlarmBanner.tsx`
  (renders `alarm.text` verbatim, so the folded count reaches the Cockpit and the phone with no client branch).

What I ran, in a scratch worktree cut from the branch (removed afterwards; the Developer's tree was not touched):
- Alarm tests on the branch as delivered: 50 passed, 0 failed.
- Mutation 1, the hold-down made to never hold (the behaviour before this change): 7 failed, 43 passed. The seven:
  ReopenedWithinTheHoldDown_ShowsButSendsNoEmailAndNoPush, FiveOpeningsInThreeHours_TellTheOwnerOnce,
  TwoMachines_EachToldOnce, GatewayRestartsBetweenCloseAndReopen_StillHeld,
  AnotherKindComingAndGoing_IsHeldToo, TimesInARow_CountsEachReturnInTheRun_AndStartsAgainAfterTheHoldDown,
  and the endpoint test Alarms_AlarmThatCameBack_TheLineSaysItHasComeAndGone. So the tests watch the behaviour,
  not the shape.
- Mutation 2, the close-time comparison loosened from "later than" to "at or later than": 1 failed, the older
  test RaiseAsync_AlarmNoLongerRaised_ClosesAndLogs_ThenRaisedLaterOpensANewAlarm, which reopens at exactly
  six hours. So the "6 hours or more tells again" edge is guarded.
- `.\scripts\test-database.ps1 -Filter "FullyQualifiedName~License"` against a throwaway PostgreSQL in Docker:
  verdict outcome=Completed, 7 executed, 7 passed, 0 skipped. The held query is proved on the production engine.
- Every License unit test on the restored branch (`FullyQualifiedName~License` in Gateway.UnitTests):
  1,153 passed, 0 failed.

What I could not reach:
- The default gate and the parked `Gateway.Tests` hosted license tests (26) were not rerun here; the pull request
  reports them green and I have no evidence against that.
- The banner was not viewed live in the Cockpit or on the phone; the client renders the Gateway's text verbatim
  (checked in source), so the endpoint test is the proof of the words.

## The checklist, item by item

- A reopen inside the hold-down sends no email and no push but still shows the banner and log rows. Yes.
  `Reconcile` still inserts the `license_alarms` row and an `alarm-opened` log row (with the "opened again" words),
  but a held row is not added to `opened`, so it is never pushed; its email state is `held-came-back`, which
  `OpenUnsent` excludes, so neither the raise's own send nor the minute check ever emails it. `ListOpen` filters
  only on "not closed", so the banner shows it. Proved by the within-window test (also at the five-minute check).
- The first opening and an opening after the window still notify. Yes. A hold needs a CLOSED row of the same
  identity closed later than now minus six hours whose email state says the owner was told; a first alarm has no
  such row. At six hours or more the comparison is strictly "later than", so it tells; proved by mutation 2.
- The hold-down survives a Gateway restart. Yes. Both `CameBackWithinHoldDown` and `TimesInARow` read the stored
  rows through a fresh context and hold nothing in memory; the restart test uses a new service instance on the
  same database, and the count test reads 3 from a new instance too.
- When an alarm opens or closes is unchanged. Yes. The open and close branches of `Reconcile` are untouched apart
  from the email state and the log words of a held opening; the still-open path is unchanged.
- It holds per alarm identity. Yes. The hold query filters on kind, machine id and account inside the tenant's own
  context, and the two-machines test shows machine-b told in the same raise that holds machine-a. The walk in
  `TimesInARow` uses the same three fields.
- Nothing can now suppress a FIRST alarm. Yes. The hold requires a prior closed row with email state emailed,
  held, or no-owner-account; a row that closed with its email owed (failed, or never attempted) does not hold its
  return, and that is tested. A null email state at close is also "not told", the conservative side.
- No real names in the public repository. None in the diff (machine-a, machine-b, alice@example.com) and none in
  the commit message. No attribution lines anywhere in the diff or the commit.
- The tests fail without the change. Yes; see mutations 1 and 2 above.

## Observations (not findings; no harm proved, nothing to change before merging)

- The owner email now ends "comes back more than 6 hours later", while the code and the contract say an opening
  at exactly six hours after the close is told. Off by one instant; cosmetic.
- `TimesInARow` runs one query per held open alarm on every banner poll and loads every closed row of that
  identity (two small columns) to walk back to the telling opening. It costs nothing while there is no held alarm,
  and the rows are tiny; it only grows with a machine's whole alarm history.
- A run whose gaps are each under six hours is held for as long as it keeps coming back, however long that is.
  This is the design the brief asked for (measured from the last close), it is stated in the pull request and in
  contract 9.4a, and the banner's count makes it visible.
