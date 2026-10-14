# License Switching - answer to the whole-phase review of the Licenses page

Answers `review-phase-3-whole.md`, finding by finding, as the Delivery Lead ruled: finding 1 fixed; findings 3 to 6
fixed where the fix is a small change in the Gateway fold, otherwise answered; findings 2 and 7 answered; finding 8
left for the owner.

| Finding | What was done |
|---|---|
| 1. Overview and forecast fold a machine mid-switch from two pictures | **Fixed** |
| 2. The Switch now confirmation has no sentence per target | Answered, stands |
| 3. One outage, four banners | Answered, stands |
| 4. Two Gateway sentences on the never-read card disagree | **Fixed** |
| 5. Two words the page writes from a boolean | **Fixed** for the role word; the other waits on finding 2 |
| 6. Fields served with no sentence and never drawn | Answered, stands |
| 7. Approvers cannot be set before a machine reports | Answered, stands |
| 8. No Licenses page on the phone | For the owner |

## 1. Fixed - one picture for the card and the schedule

`LicenseDisplayFold.Forecast` now folds from `LicenseSwitchService.SnapshotSwitchNowDecidesOn(picture)`, which is the
same picture the overview's Switch now offer already used. It also runs `LicenseForecaster.Forecast` on that picture
itself. Its signature changed from `(picture, forecast)` to `(picture, histories)`, so no caller can hand it a forecast
made from another picture. The route, and the two tests that called it, pass the histories. Now a switch the Gateway
knows it never sent (`pending`, `director-offline`):

- does not take the machine out of the forecast's `moves`, `EveryMachineHeldText` or `nextSwitch.machines`;
- is never called "a switch sent to it has not finished".

A switch that WAS sent, and has had no answer, still holds its machine back on both routes. The stale sentence on
`SnapshotToDecideOn` ("which is what the Licenses page and the forecast should show") was corrected to match.

**Proof.** `LicenseDisplayFoldTests.ForecastAndOverview_ASwitchKnownNeverSent_AreFoldedFromOnePicture_SoTheThreeSentencesAgree`
reproduces the reviewer's state through the real store: two machines on account A, each with a pending `director-offline`
Switch now row to B. It then asserts the three sentences agree:

- the card offers Switch now: "Moves m1 and m2 to B.";
- the page-level schedule sentence says the machines then move to B, and never "Nothing would be switched";
- each schedule row reads "Next: B, in about 1 hour 13 minutes.", beside the overview's true "Waiting to switch ...
  the machine's Director is not connected."

The fix was committed first (`e5912ce7c`), then reverted by hand to `picture.Snapshot`. With the fix reverted, that
test failed and the other 167 license fold and hold tests passed. With the fix restored, all 168 passed. Its partner
`ForecastAndOverview_ASwitchSentWithNoAnswer_HoldsTheMachineBackOnBoth` pins the other side: a sent row with no answer
offers nothing on the card, and the schedule row says why.

No served shape changed for this finding. The sentences in `cockpit-api.md` section 3 were already the ones now served
for this state, so the document needed no edit.

## 2. Answered - a sentence per Switch now target

This stands as owed. It is a new field on `LicenseSwitchTargetDto`, plus a sentence for a target that can be chosen
but is barred or is not the Gateway's pick. The page would then draw the picked target's sentence and stop writing
"The Gateway's choice" itself. That touches the contract, the regenerated examples, the fixtures and the
confirmation dialog, so it is more than a few lines. It belongs with the next change to that dialog. Until then the
barred target's card, behind the dialog, carries the barred sentence.

## 3. Answered - one outage, four banners

This is not a Gateway verdict. When the Gateway does not answer, there is nothing for it to fold. Each poll reports its
own failure, which is right when only one fails. Merging four identical failures into one banner is a layout choice
in the page (rule 7 allows layout there), not a sentence the fold can write. So it was left out of this
Gateway-fold pull request. It stays a fair small Cockpit change for whoever next works on `LicensesView.tsx`.

## 4. Fixed - the never-read card

- A holding's read error that leaves the old numbers standing (`network`, `rate-limited`, `unexpected-answer`) now
  ends "The numbers shown are the last ones read." only when some machine has read that account. When none ever has,
  it ends "No usage has been read for this account yet.". `ReadErrorText` takes whether the account was ever read,
  and `Machine` works that out from the overview's accounts.
- An account never read: `readAgeText` is "Never read on any machine" (it was "Never read"). The extra usage text
  is "Paid extra usage not known" (it was "Not known"), in line with "Paid extra usage is on" and "... is off".

Tests: `Holding_ReadError_PointsAtTheNumbersShown_OnlyWhenTheAccountWasEverRead` (new), and
`Account_NeverRead_IsStale_AndSaysNoReadingHasArrived` (now asserts both new sentences). `cockpit-api.md` section 2
gives the new sentences.

## 5. Fixed for the role word - `roleText` on each holding

`LicenseMachineHoldingDto.RoleText` is "Active" or "Spare", folded on the Gateway. `LicenseAccountCard.tsx` draws
`holding.roleText` and no longer branches on `isActive`. The client type in `licensesClient.ts` has it. The
section 2 example of `cockpit-api.md` was produced again by `produce_overview_example.py`: zero differences, and five
`roleText` fields added. `fixtures.ts` was then rewritten by `copy_fixtures.py`, so `fixtures.test.ts` still holds the
fixtures to the document. The shape test `Overview_AnswersTheExactShapeTheLicensesPageIsBuiltFrom` lists the new field.
`Holding_RoleText_IsActiveOrSpare` pins the two words.

"The Gateway's choice" is still written by the page from `isAutomaticChoice`. It goes away with finding 2's
per-target sentence, as the review proposes, and not before. A label alone would just be a second field to replace.

## 6. Answered - fields served and never drawn

This stands. The fix proposed is mostly drawing, which is page work: the rate beside each window, and a send count
on the log row. Dropping `isReadingStale` and `rateLimitTier` would break the Tech Lead's ruling that "these field
names are final". Neither part is a small change to the fold. The rate ("28.5% an hour") is already folded and served
as `rateText` on every forecast window, so drawing it is a page change with no Gateway work. The send count has no
sentence yet. If the owner wants it on the row, the sentence belongs in `timingText` for open rows, folded on the
Gateway, and it would be done with the page change that draws it.

## 7. Answered - approvers before any machine reports

This stands. The approvers part is drawn inside `LicenseSignIns`, together with the sign-ins table, which needs
machines. Drawing it in the empty branch means splitting that component and its tests. That is more than a few
lines, and it is page work, not fold work. The order the review describes still works one step later: the first
Director to report brings the page out of its empty state, and the approver can be set then.

## 8. For the owner

Left as the review wrote it, not built. Question: should the phone get a read-only Licenses screen? It would show the
card status pills, the page-level next-switch sentence and the newest log rows, with no writes, and be built in
`client-core` so both surfaces share it, as Settings does. The review recommends yes. If the answer is no, the
mission record should say so, so that phase 5 does not look for it.

## Added by the Delivery Lead - the hold migration generated again

Main was red. Pull request 3963's hold migration (`20261014003138_AddLicenseSwitchHold`, and `20261014003240` in the
PostgreSQL project) sorted BEFORE the Factory Seats migration `AddBriefVersionToCronRuns` (`003217` and `003248`). That
Designer was made without the hold column, so the newest Designer of each provider did not carry the current model.
Production has applied `AddBriefVersionToCronRuns` and has not applied the hold migration. So the hold migration was
removed from both projects, its column was taken out of both model snapshots, and it was generated again with
`dotnet ef migrations add` as the newest of each provider: `20261014061558_AddLicenseSwitchHold` (SQLite) and
`20261014061624_AddLicenseSwitchHold` (PostgreSQL). Its `Up` and `Down` are word for word what they were, and the two
regenerated snapshots are byte for byte what main had. No migration production has applied was touched.
`AddLicenseSwitchHoldPostgresTests` now names the new id, and `AddBriefVersionToCronRuns` as the migration before it.

**What production applies on the next deploy:** one migration, `20261014061624_AddLicenseSwitchHold`. It adds the
nullable column `gateway.license_switch_log."HeldUntilUtc"` (`timestamp with time zone`). Nothing else.

**One thing to know.** A SQLite Gateway built from main between pull request 3963 and this one will have applied the
old `20261014003138` id and already has the column. The new id will then try to add the column again, and that
Gateway's migration will fail. No release carries the old id (v2.20.0 was cut before pull request 3963), so this
reaches only a Gateway someone built from main in that window. That Gateway's fix is to drop the column, or delete
the old history row, before it starts.

## What was run

Each run was done one at a time, with its output written to a file. These are the counts on the final commit.

- `dotnet test src/CcDirector.Gateway.UnitTests`: 11,861 of 11,861. (Before the migration fix the same run was
  11,859 and 2 failed: the two `TheNewestMigrationsDesigner_IsDiscovered_AndCarriesTheCurrentModel` cases. Both pass
  now.)
- `.\scripts\test-database.ps1` (a throwaway PostgreSQL in Docker): GREEN, 74 of 74, none skipped. This includes
  `AddLicenseSwitchHoldPostgresTests` on the new id.
- The hosted license tests (`CcDirector.Gateway.Tests`, the `Licenses` namespace and `LicenseHostTests`): 20 of 20,
  run before the migration fix. That fix changes no code those tests run.
- Cockpit (`apps/cockpit`, all of it): 1,187 of 1,187. `packages/client-core`, all of it: 2,068 of 2,068.
- `.\scripts\test-local.ps1` (default, not `-Parked`): GREEN, 4,481 tests in 10 suites, at `971100458`.

**Restore audit.** Every .NET restore on this machine now fails with NU1902 and NU1903. Advisories were published
against `Microsoft.AspNetCore.SignalR.Common` 10.0.9 (high severity) and
`Microsoft.AspNetCore.SignalR.Protocols.MessagePack` 10.0.9 (moderate). Main fails the same way, so this branch did
not cause it. Every .NET run above was therefore made with `NuGetAudit=false`, for that local run only. Nothing in
the repository was changed to turn the audit off.

## Not proved

- The parked suites (`-Parked`) were not run, by the brief: another mission holds that lock. The gate's coverage gap
  line names `Core.Tests`, `Gateway.Tests` and `Gateway.UnitTests`. Of those, `Gateway.UnitTests` was run whole, and
  `Gateway.Tests` only for its license tests.
- The page was not opened in a browser against a Gateway built from this tree. The Cockpit tests draw the
  regenerated fixtures, but nobody looked at the reviewer's offline-Director scenario on a screen after the fix.
- Only finding 1 was shown red with its fix reverted. The tests for findings 4 and 5 were not run against reverted
  code; they assert the new words exactly.
- The migration was not applied to the hosted database. Its proof is the two Designer tests and the database tests
  on a throwaway PostgreSQL, which migrate from `AddBriefVersionToCronRuns` to the new id and back.
- No lint and no separate type check were run. The Gateway build runs the web type check, which passed.
