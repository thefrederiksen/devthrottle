# License Switching - review of pull request 3978

**Verdict: no blocking finding. Merge it, and merge it before anything else is deployed from main.** The regenerated
hold migration cannot harm production: production last ran a commit from before pull request 3963, so it has never
seen the old migration identifier, and the new one adds the one nullable column and nothing else. Finding 1 of the
whole-phase review is fixed from one picture and its test goes red without the fix. The page still decides nothing.
The answers to findings 2, 3, 6 and 7 are reasonable. Four findings follow, none blocking; the first is a condition
on the order of merging and deploying, not a change to the pull request.

## Scope

Read on this worktree, which is pull request 3978 at `3c19f1374` on top of `723f02851` (origin/main at the time
the branch was cut; origin/main has since taken one more commit, the SignalR package move of pull request 3979,
which touches no file this branch touches).

- `MISSION.md` section 5; `review-phase-3-whole.md` and `review-phase-3-whole-answer.md` in full.
- The whole diff against origin/main: the fold, the endpoint, the switch service comment, the contract, the client
  type, the account card, the fixtures, the contract document, the four migration files, the two model snapshots
  (unchanged), the PostgreSQL migration test and the three unit test files.
- The code the diff leans on: `LicenseSwitchService.SnapshotSwitchNowDecidesOn` and `SwitchNowAsync`, the read
  model's choice of an account's freshest reading, the Director's usage reporter for what a failed read carries, and
  `GatewayDatabase` for what happens when a migration fails at start-up.
- The deploy record on GitHub and the git history of the migrations, for what production has and has not applied.

## What was run

Each run went to a file under the session's temporary folder, outside the tree. Every .NET run was made with the
package audit off for that run only, because at the time the restore failed on main for every tree (pull request
3979 has since fixed that on main).

- `FleetOutcomeStopIdentityMigrationTests` in `CcDirector.Gateway.UnitTests`: 3 of 3 passed, in 36 seconds. The
  two Designer cases derive the newest migration from the assembly and compare its model with the current model
  and the snapshot, so they prove the regenerated migrations without naming them.
- `scripts\test-database.ps1`, alone, after the run above: GREEN, 74 of 74 against a throwaway PostgreSQL, none
  skipped, in 1 minute 37 seconds. The run included
  `AddLicenseSwitchHoldPostgresTests.AddLicenseSwitchHold_AppliesOnPostgres_TheStoreWritesAndReadsTheHold_AndItsDownRemovesIt`
  on the new identifier.
- The Cockpit license tests in this worktree, unchanged: 5 files, 81 passed.
- Four revert experiments, each in a detached scratch copy of this commit under the temporary folder, never in this
  worktree; the copy was removed afterwards. Their results are under findings 2 and 3 below.
- My own comparison of each provider's newest Designer with its snapshot, and the sweep of the diff for private
  content and attribution.
- **No tracked file was changed.** This file is the only addition; `git status` shows nothing else.

## ONE - the migration

Every claim in the answer's "hold migration generated again" section was checked and holds.

- **Production has never applied the old identifier.** The newest run of the deploy workflow (run 38508971818,
  started 2026-10-14 at 03:58 universal time, successful) shipped commit `6fa791ff4`. Pull request 3963 merged at
  05:27 universal time the same day as `25e367758`. `6fa791ff4` is an ancestor of that merge, and `git ls-tree` of `6fa791ff4` finds no file
  named `AddLicenseSwitchHold`. No deploy has run since (checked again at the end of this review). The rollback
  workflow's last run was 2026-10-07, so nothing older was put back either. The deploy workflow runs only on
  `workflow_dispatch`, never on a push or a schedule.
- **Production has applied `AddBriefVersionToCronRuns`.** `6fa791ff4` carries all four of its files, so the next
  deploy applies exactly one migration: `20261014061624_AddLicenseSwitchHold`.
- **The new migration adds the nullable column and nothing else.** Git records both renames at 100 percent
  similarity (`R100`): the `Up` adds `HeldUntilUtc` to `license_switch_log` (`TEXT` on SQLite, `timestamp with time
  zone` in the `gateway` schema on PostgreSQL, nullable) and the `Down` drops it, word for word as before.
- **Each provider's newest Designer carries the whole current model.** I extracted the `BuildTargetModel` body of
  each new Designer and the `BuildModel` body of its provider's snapshot and compared them: identical, 5,393 lines
  on SQLite and 5,486 on PostgreSQL. Both snapshots are byte for byte what origin/main has. The only change from the
  old Designers besides the identifier is the `BriefVersion` property the old ones lacked, which is exactly the
  defect that made main red. The Designer of `AddBriefVersionToCronRuns` still lacks `HeldUntilUtc`, which is
  correct for its position.
- **The old identifiers are gone.** `20261014003138` and `20261014003240` appear nowhere on this commit outside the
  two review documents that describe them.
- **The test names the right neighbour.** `AddLicenseSwitchHoldPostgresTests` migrates to
  `20261014003248_AddBriefVersionToCronRuns`, asserts it is the one before the migration under test, applies the
  new identifier, and then migrates to the end with no pending model changes.

### Finding 1. Not blocking, a condition - merge this pull request before any deploy of main

Main today carries pull request 3963 and therefore the OLD identifier. If the deploy workflow is started on main
before this pull request merges, production applies `20261014003240`, and the first start-up after this pull request
then tries to add `HeldUntilUtc` a second time under `20261014061624`. `GatewayDatabase` calls `Migrate()` when a
migration is pending and throws when it fails, and the hosted start-up contract says a throw there stops the site.
So the order is: merge 3978, then deploy. Nothing deploys on its own, so this needs only to be known. The same holds
for the release: the answer says v2.20.0 was cut before pull request 3963, and the frozen candidate `38d30ce60` does
predate it, but no `v2.20.0` tag exists yet (the newest published release is v2.19.0). The tag must go on that
candidate or on a commit that includes this pull request, never on a commit between the two.

The answer's note about a SQLite Gateway built from main in the window is right, and the fix it gives (drop the
column or delete the history row) is the only one.

## TWO - finding 1, one picture for the card, the schedule sentence and each schedule row

The fix is what the review proposed and the answer describes. `LicenseDisplayFold.Forecast` now takes the
histories, folds its snapshot from `LicenseSwitchService.SnapshotSwitchNowDecidesOn(picture)` and runs the
forecaster on that same snapshot, so a caller cannot hand it a forecast made from another picture. `moves`,
`EveryMachineHeldText`, `nextSwitch.machines` and each machine row are all folded from that one snapshot, and the
account card's `ApplySwitchNow` was already asked of it. The three callers (the endpoint and two tests) were
updated and there are no others. The `with` copy that clears the mark keeps every hold, so `HoldInForce` answers the
same on both routes. The overview's "Waiting to switch ... the machine's Director is not connected" sentence still
comes from the open row, which is true and is what the schedule row now sits beside.

**The test goes red without the fix.** In the scratch copy I put the forecast back on `picture.Snapshot` and ran
`LicenseDisplayFoldTests`: 1 failed, 130 passed, and the one that failed was
`ForecastAndOverview_ASwitchKnownNeverSent_AreFoldedFromOnePicture_SoTheThreeSentencesAgree`. With the fix
restored, 131 passed. The test builds the reviewer's state through the real store (two machines, each with a
pending `director-offline` Switch now row) and asserts the card's offer, the page sentence, the next-switch machine
list, the per-account sentence and every machine row against each other. Its partner test pins the other side: a
sent row with no answer still holds the machine back on both routes.

I also read `SwitchNowAsync` for the state the card now offers Switch now in: a second Switch now for the same
source and target while the row is waiting writes nothing and answers with the waiting row, so the offer the card
makes is one the route accepts.

## THREE - findings 4 and 5, the client still decides nothing

**Finding 5, the role word.** `RoleText` is folded on the Gateway, the card draws `holding.roleText`, the client
type has it, the contract document and the fixtures have it, and the shape test lists it. Reverting the fold (no
`RoleText` set) in the scratch copy: `Holding_RoleText_IsActiveOrSpare` failed, 195 passed. The shape test stayed
green, which is right: the property still exists on the contract, and the shape test holds the shape, not the words.

**Finding 4, the never-read card.** Reverting the three sentences in the scratch copy:
`Holding_ReadError_PointsAtTheNumbersShown_OnlyWhenTheAccountWasEverRead` and
`Account_NeverRead_IsStale_AndSaysNoReadingHasArrived` failed, 129 passed. The fold's "was the account ever read"
is the read model's `Freshest`, which exists only when some holding of the account has a read time. The account's
own read error is folded with `accountWasEverRead: true`, and that is sound: the Director's reporter keeps the
previous reading's time and numbers on a failed read, or sends no time at all, so an account-level read error always
sits beside numbers that were read.

The page's only remaining words from a boolean are "The Gateway's choice" from `isAutomaticChoice`, which the answer
rightly ties to finding 2's per-target sentence. Everything else the review listed as the page's own (the subtitle,
labels, headings, confirmation titles) judges no state. Rule 7 holds.

### Finding 2. Not blocking - no page test can tell whether the page shows the Gateway's role word

In the scratch copy I put the card back to `holding.isActive ? "Active" : "Spare"` and ran the Cockpit license
tests: 5 files, 81 passed, exactly as with the fix. Every fixture's `roleText` agrees with its `isActive`, and the
two assertions on the word (`getByText("Spare")`, `queryByText("Active")`) cannot tell the Gateway's word from the
page's. The Gateway side is pinned, so the fix is real; but the reason the review gave for finding 5 was the day a
third role exists, and on that day the page's tests would still pass if the page had quietly gone back to its two
words.

**Proposed.** One card test with a holding whose `roleText` is a word the page does not know (for example
"Replaced", with `isActive` true) asserting that word is drawn. It is a few lines, and it belongs with the next
change to the card.

## FOUR - the answers to findings 2, 3, 6 and 7

All four are reasonable and none hides a defect.

- **2 (a sentence per Switch now target).** Deferred to the next change of the confirmation dialog, with the barred
  sentence on the card behind it meanwhile. A contract field, regenerated examples, fixtures and the dialog together
  are more than a fold change, and the item was already recorded as owed. Fair.
- **3 (four banners for one outage).** Correctly called a layout choice of the page under rule 7: when the Gateway
  does not answer there is nothing to fold. Fair to leave out of a Gateway-fold pull request.
- **6 (served fields never drawn).** The field names are final by the Tech Lead's ruling, the rate is already
  served as `rateText`, and the send count's sentence would be folded with the page change that draws it. Fair.
- **7 (approvers before a machine reports).** Fair, and weaker than the review made it: an approver is a machine and
  a user, so there is nothing to pick until a Director has reported anyway. The order the mission describes works one
  report later.

### Finding 3. Not blocking - a note in the answer is already out of date

The "restore audit" paragraph says every restore fails with NU1902 and NU1903. Pull request 3979, merged after this
branch was cut, moved every SignalR package to 10.0.13 and the restore passes on main again. Nothing to change in
this pull request; the record will read as stale, which is harmless.

### Finding 4. Not blocking - the page was not looked at after the fix, and the answer says so

The answer's "not proved" list is honest: nobody opened the page against a Gateway built from this tree in the
offline-Director state. The fold test reproduces that state through the real store and asserts every sentence the
page draws for it, and the page draws sentences verbatim, so the gap is small. Phase 5's proof run, which produces
this state first, closes it.

## Nothing private, nothing signed

Every address added by the diff is `account-a@example.com`, `account-b@example.com` or the deliberately capitalised
`Account-A@Example.com`; every machine is `the-mac`, `the-windows-machine`, `the-laptop`, `the-linux-machine` or
`machine-of-one`; no host name, address, key, bearer value or path under a user's home folder appears. No commit
message, the pull request body and no added line names an assistant.

## Not reviewed

- The parked suites and the default gate were not run here; the answer's counts were not reproduced.
- The page in a browser; a real Director; a real account; the hosted service.
- `LicenseForecaster`'s numbers and the fold sentences beyond the ones the diff touches.
