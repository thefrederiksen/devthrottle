# Review of pull request 4202: warn at 80% when no other account has room

Reviewer: License Switching - Reviewer - 4202 no room at 80% (session 6003fa99). Date: 2026-10-18.

## Verdict

No finding. The change does what the task brief asks, every check in the review brief holds, and the tests fail
without the change. Two observations below are notes for judgment, not defects; neither proves harm.

## Scope

What I read:
- The task brief (phase6-fix-no-room-80.md) and the pull request (`gh pr view 4202`, its one commit ce4ad4f80, merge
  base 432e54a28 on origin/main).
- The full diff: `missions/license-switching/contract.md`, `LicenseDecisionEngine.cs`, `LicenseDecisionModel.cs`,
  `LicenseReadinessFold.cs`, `LicenseReadinessService.cs`, and the two test files.
- The code around it on the branch: `LicenseAlarmService.cs` in full (identity, Reconcile, push, email),
  `LicenseAlarmFold.cs` in full (banner, push body, email for a machine-less alarm), the fold's `Judge`,
  `AlarmConditions`, `HighestWindowAtAlarm` and `AtAlarm`, the engine's `TargetsWithRoom`, `WhyNotTarget`, `HasRoom`,
  `IsReadingStale`, `IsTired`, and the test fixture (`BothReport`, `Tick`, the reset times in `LicenseTestData`).
- Every caller of the `LicenseReadinessTick` record (only `NotRun` and the service build it).

What I ran, on the pull request commit checked out in my own worktree (wt18), in the foreground:
- `dotnet test` of `CcDirector.Gateway.UnitTests` filtered to `LicenseNoRoomAlarmTests`, `LicenseReadinessServiceTests`
  and every `LicenseAlarm*` class: 71 passed, 0 failed.
- A revert proof: one inserted line made `NoRoomAlarmConditions` return an empty list; the same filter minus the alarm
  classes went 6 failed of 40. Those six are the one changed existing test (`Judge_NoOtherAccountHasRoom...`), the
  four no-room service tests that assert an open alarm, and the long-email fold test. The two tests that assert
  nothing opens still passed, as they should. The source was restored in the same command; `git status` was clean
  after, the worktree was parked back on origin/main, and the test project rebuilt so the binary matches the source.
- A sweep of every added line of the diff for an `@` address or an owner name: only `account-a@example.com`,
  `account-b@example.com`, `@example.com`, `machine-a`, `machine-b`. The commit message and pull request body carry
  no attribution.

What I could not reach: I did not run the default gate or `-Parked -Filter License` myself (the author reports both
green); I did not run a Gateway or look at the banner on a client. The client renders the Gateway's folded `Text`
verbatim and `MachineName` null is already a case the banner fold and its tests handle, so I did not need to.

## The checks in the review brief

- **Exactly one alarm per account in use, never per machine, never every minute.** `NoRoomAlarmConditions` takes the
  distinct normalized `ActiveAccountEmail` of every machine judged `NoRoom`, so one condition per account; the condition
  carries no machine, and `RaiseAsync`'s identity is (machine id empty, account), so a second machine cannot make a
  second row, and a duplicate would make `RaiseAsync` throw rather than open two. The still-open path in `Reconcile`
  only updates the two sentences and sends nothing. The service test has two machines on the account and asserts one
  row, one push, one email, and (0 opened, 1 still open, 0 closed) on the next minute.
- **Opens only at 80% or more on a fresh reading with truly no account with room.** `NoRoom` comes from
  `NextTarget` being null (the same room judgment the Licenses page and the 97% switch use). The alarm needs the
  account in use's reading fresh (`IsReadingStale`) and a window at or above 80 whose reset has not passed
  (`AtAlarm`), the same rule as `not-ready-at-80`. Tested at 79.9% (nothing), 85% (opens), and at 90% stale (nothing).
- **Closes when room returns or use falls under 80%.** When another account has room, no machine is judged `NoRoom`,
  so no condition is built and `Reconcile` closes the open row; when the account is fresh and under 80, the fresh
  branch adds nothing and `continue`s past the stay-open branch, so it closes. No machine left on the account also
  closes it. All three are in the code; the first two are tested.
- **First-reset account and time.** `FirstBackWithRoom` takes, per other account, the latest reset among windows at
  or over 97%, extended by any bar that ends later, and keeps the earliest. I checked the three pure tests by hand
  (B at +30h not +20h because its 5-hour window is also full; a bar to +50h wins over a reset at +20h; a null reset or
  a passed reset leaves the account out) and the service test's `2026-10-15 03:12 UTC` (T0 + 4 days, B's weekly
  reset in the fixture). The 400-character fallback drops the names and keeps the time, tested with two 312-character
  addresses.
- **The 97% no-room email is unchanged.** `NoRoomEmail`, `Decide` and `LicenseOwnerMailer` are not in the diff.
- **Track A's not-ready-at-80 alarm is unchanged.** `AlarmConditions` changed only in a doc comment. `NoRoom` and
  `NotReady` are exclusive per account (the target is per account), so the two kinds can never both open for one
  account. The one behaviour change in the service is that a failed raise of one kind no longer stops the other kind's
  raise; the failure is still logged and still thrown (as an `AggregateException`), and the tenant boundary catch is the
  same. The existing not-ready tests pass unchanged; one existing test at 90% with no room now asserts the no-room
  alarm is the only one open, which is the new truth.
- **No real names.** See the sweep above.
- **The tests fail without the change.** The new test files name new symbols and cannot compile against main; the
  revert proof above shows what the assertions actually hold.

## Observations (no harm proven; for the Delivery Lead's judgment, nothing to do before merging)

1. "No other account has room" inherits the engine's existing meaning, which includes an account whose latest reading
   is too old to trust. If the only other account's spare reading is 31 minutes old and showed 10%, the alarm will say
   no other account has room and the action will be to free up or add an account, when the truer remedy is a fresh
   reading. This is how the `NoRoom` state on the Licenses page and the 97% path already read; the pull request did not
   introduce it and the Licenses page shows reading ages.
2. `FirstBackWithRoom` leaves out an account whose full window's reset has already passed, although that account is
   the one most likely to have room on its next reading (it stays "no room" until a fresh reading shows the new number).
   The alarm then says no other account's reading says when it will have room again. Conservative, not wrong.

Note for the merge: origin/main moved past the pull request's merge base while I reviewed (78da6d1b2, an unrelated
reviewer-seat change); the usual rebase-or-merge applies.
