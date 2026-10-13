# Proof: the owner is emailed once when a sign-in about to end cannot be renewed

License Switching, phase 2 (Gateway), part 4 of contract section 8: section 8.1, "ONE owner email per holding per
failure streak". 13 October 2026.

## What was built

- `LicenseRenewalFailureLog` - one row of the switch log, of kind `renewal-failed`, for one failure streak of one
  sign-in a machine holds. No table and no column is added. The row is what makes the email go out once: what
  became of the email is its reason word (`owner-emailed`, `owner-email-failed`, `no-owner-account`), a row recorded
  as emailed is never changed back, and it is stored, so a Gateway that restarts in the middle of a streak does not
  email again.
- `LicenseRenewalEmailService` - run by the host's minute check for every DevThrottle account. A sign-in whose
  renewal the machine reports as failed (the failure's time is set - never a reason word) and which ends within the
  warning time opens a row and emails the account's owner. A send that failed is tried again, later each time, and
  never again once it has gone out. A report that says the renewal worked, or that the machine no longer holds the
  sign-in, closes the row; a failure after that is a new streak with its own email.
- `LicenseDisplayFold` gives the row its words for the Licenses log, open while the streak lasts. The client reads
  them and decides nothing.
- `contract.md` 8.1 and `cockpit-api.md` say what was built.

## The runs

All on the branch after `origin/main` (`505d51a8f`) was merged into it.

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` | 522 passed, 0 failed |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~License"` | 24 passed, 0 failed |
| `.\scripts\test-database.ps1` | GREEN - 72 executed against the throwaway PostgreSQL, none failed, none skipped |
| `dotnet test src/CcDirector.Gateway.UnitTests`, the whole suite | 11,286 passed, 0 failed, 6 minutes 42 seconds |
| `.\scripts\test-local.ps1` | GREEN at `12060c001`, tree clean, every suite `outcome=Completed` |

`.\scripts\test-local.ps1 -Parked` was NOT run. The default run covers no Gateway test, which is why the whole Gateway
unit suite was run by hand. Of the hosted suite (`Gateway.Tests`) only the 24 license tests ran.

The tests of this part: 26 in `LicenseRenewalEmailServiceTests`, which run as 28 with the cases of one of them (the real store on a throwaway SQLite file, a mail
sender the test controls, a clock the test moves), 2 in `LicenseRenewalEmailHostedTests` (the real host and its
minute check), and 1 in `LicenseStorePostgresTests` (a real PostgreSQL). They use the example accounts and machine
names of the license test data and no real account, machine name or email address.

## The breaks

Each break was made in the product code on a committed tree, the tests were built and run, and the file was put
back with `git checkout`. "unit" is `LicenseRenewalEmailServiceTests`; "hosted" is `LicenseRenewalEmailHostedTests`.

**When they were run.** Before `origin/main` was merged into the branch, and they were NOT run again after the
merge. Breaks 1 to 7 ran when the class had 27 tests, break 12 was run twice (see it), and breaks 14 to 19 ran with
28. The merge brought no change to the four product files broken here: `git diff` of those
four files between the commit the last break ran on (`8a8fc58ac`) and the merged head is empty; the 28 unit and 2 hosted tests are green after it (the runs above).

| # | The break | File | Result | Red |
|---|---|---|---|---|
| 1 | The warning time is not checked: any failing sign-in with a known end is emailed | `LicenseRenewalEmailService.cs` | unit: 2 red, 25 green | `AFailedRenewal_OfASignInEndingLaterThanTheWarningTime_WaitsUntilItIsWithinIt`, `AMachineThatHasGoneQuiet_IsStillEmailedAbout_WhenItsFailingSignInComesWithinTheWarningTime` |
| 2 | A sign-in that has already ended is not emailed | `LicenseRenewalEmailService.cs` | unit: 1 red, 26 green | `TheEmail_ForASpareThatHasAlreadyExpired_SaysSo` |
| 3 | The failure is read from the reason word instead of the failure's time | `LicenseRenewalEmailService.cs` | unit: 2 red, 25 green | `TheEmail_ForASpareThatHasAlreadyExpired_SaysSo`, `TheEmail_IsKeyedOnTheFailureBeingSaid_WhateverReasonWordCameWithItOrNone` (the case with no reason word) |
| 4 | A streak already emailed is emailed again on the next check | `LicenseRenewalEmailService.cs` | unit: at least 4 red | `EachFailingSignIn_GetsItsOwnEmail_OnePerMachineAndAccount`, `TheSameStreak_OnLaterChecks_IsNeverEmailedAgain_AlsoWhenALaterTryFailsToo`, `AGatewayThatRestartsInTheMiddleOfAStreak_DoesNotEmailAgain`, `ASendThatFails_IsTriedAgainLaterEachTime_AndOnceItHasGoneOutNeverAgain` |
| 5 | An account with nobody to email is tried again every check | `LicenseRenewalEmailService.cs` | unit: 1 red, 26 green | `AGatewayWithNoAccountToEmail_RecordsThat_AndNeverTriesAgain` |
| 6 | A streak already emailed is not read back by the check | `LicenseRenewalFailureLog.cs` | unit: 3 red, 24 green | `TheRow_IsAnsweredByTheLogRoute_InWords_OpenWhileTheStreakLasts`, `ASignInTheMachineNoLongerHolds_EndsItsStreak_AndTheRowSaysSo`, `ARenewalThatWorks_EndsTheStreak_AndALaterFailureIsANewOneWithItsOwnEmail` |
| 7 | A row recorded as emailed can be changed back by a later result | `LicenseRenewalFailureLog.cs` | unit: 1 red, 26 green | `ARowRecordedAsEmailed_IsNeverChangedBack_ByALaterResultOfTheSameStreak` |
| 8 | A failed send is tried again at once, with no wait | `LicenseRenewalEmailService.cs` | unit: 2 red, 25 green | `ASendThatFails_IsTriedAgainLaterEachTime_AndOnceItHasGoneOutNeverAgain`, `ASendTheMailServiceAsksToWaitFor_IsNotTriedBeforeThat` |
| 9 | The wait the mail service asked for is ignored | `LicenseRenewalEmailService.cs` | unit: 1 red, 26 green | `ASendTheMailServiceAsksToWaitFor_IsNotTriedBeforeThat` |
| 10 | The wait does not grow with each failure | `LicenseRenewalEmailService.cs` | unit: 1 red, 26 green | `ASendThatFails_IsTriedAgainLaterEachTime_AndOnceItHasGoneOutNeverAgain` |
| 11 | A streak is never ended: a renewal that worked leaves the row open | `LicenseRenewalEmailService.cs` | unit: 3 red, 24 green | `ARenewalThatWorks_EndsTheStreak_AndALaterFailureIsANewOneWithItsOwnEmail`, `ASignInTheMachineNoLongerHolds_EndsItsStreak_AndTheRowSaysSo`, `TheRow_IsAnsweredByTheLogRoute_InWords_OpenWhileTheStreakLasts` |
| 12 | A streak is matched by machine only, so two sign-ins of one machine share a row | `LicenseRenewalEmailService.cs` | unit, first run: 0 red, 27 green - NOT CAUGHT. A test was added and the break run again: 1 red, 27 green | `ASecondSignInOfOneMachine_ThatStartsFailingLater_IsItsOwnStreak_AndEachEndsOnItsOwn` |
| 13 | An account no Director has reported for is not left alone | `LicenseRenewalEmailService.cs` | DID NOT COMPILE (the code after the changed line became unreachable, which this build refuses). Not proven. | - |
| 14 | The row is shown as a plain note: no words of its own kind | `LicenseDisplayFold.cs` | unit: 1 red, 26 green | `TheRow_IsAnsweredByTheLogRoute_InWords_OpenWhileTheStreakLasts` |
| 15 | The row is not open on the page while the streak lasts | `LicenseDisplayFold.cs` | unit: 1 red, 27 green | `TheRow_IsAnsweredByTheLogRoute_InWords_OpenWhileTheStreakLasts` |
| 16 | The host does not run the email check in its minute check | `GatewayHost.cs` | hosted: 2 red, 0 green | `ARenewalThatWorks_ClosesTheWarning_OnTheNextMinuteCheck`, `TheHostsMinuteCheck_EmailsTheOwnerOfTheAccountWhoseRenewalIsFailing_Once_AndNoOtherAccount` |
| 17 | The email text does not say why the renewal failed | `LicenseRenewalEmailService.cs` | unit: 1 red, 27 green | `TheEmail_SaysWhichSignInWhyAndWhatToDo_InPlainSentences` |
| 18 | A sign-in the machine no longer holds is said to have been renewed | `LicenseRenewalEmailService.cs` | unit: 1 red, 27 green | `ASignInTheMachineNoLongerHolds_EndsItsStreak_AndTheRowSaysSo` |
| 19 | A sign-in whose end is not known is emailed about as if it had ended | `LicenseRenewalEmailService.cs` | unit: 1 red, 27 green | `AFailedRenewal_OfASignInWhoseEndTheMachineDidNotSay_EmailsNobody` |

For break 4 the saved output of the run was cut short: four red tests are in it and the totals line is not, so the
count is "at least 4".

## What this does not show

- **Break 13 is not proven.** The guard it would remove is the one that leaves alone an account no Director has
  reported for. The break as written did not compile, and no other break was made for it. The test
  `AnAccountNoDirectorHasReportedFor_IsLeftAlone` is green, and it has never been seen red.
- **The PostgreSQL test was not broken.** It ran green against a real PostgreSQL, and that is all that is claimed.
- **No email was sent.** Every test uses a sender the test controls. Nothing here shows that the mail service
  accepts the message or that it reaches an inbox.
- **`-Parked` was not run**, so the hosted suite as a whole (`Gateway.Tests`) and `Core.Tests` were not run on this
  branch.
- **The breaks were not run again after the merge of main**, as said above.

## After pull request 3943 was merged (13 October 2026)

`origin/main` at `d9ca4957a` (which holds pull request 3943) was merged into the branch as `c45272c94`. One file
conflicted, `GatewayHost.cs`, in two places; both sides were kept: the host builds the sign-in services of 3943 and
the email service of this branch, and its minute check runs the sign-in settling and then the email check, each on
its own try. The email is still keyed on `RenewalFailedAtUtc` being set.

| Run, on `c45272c94` | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` | 748 passed, 0 failed |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~License"` | 41 passed, 0 failed |
| `.\scripts\test-database.ps1` | GREEN - 73 executed against the throwaway PostgreSQL, none failed, none skipped |
| `.\scripts\test-local.ps1` | RED - one test of `Core.UnitTests` failed, of 1,686; every other suite `outcome=Completed` |

**The red test is not about this branch.** It is
`GateTestsDoNotDependOnTheMachineTests.No_gate_test_adds_a_sleep_a_real_clock_a_real_process_docker_or_a_fixed_port`,
and it names four test files as newly reading a real clock: `HostedAiGateMenuCheckRouteTests.cs`,
`HostedAiGateRouteTests.cs`, `DictionarySuggestionDailySweepTests.cs` and `Tenancy/TenantAiGateTests.cs`. `git diff`
of those four files and of the guard's own folder between `origin/main` and this branch is empty, and the last
commit on main to touch them is `26afc4ed1` (pull request 3878). It names no file of this branch. I did not run the
gate on main itself, so that main is red on this test is read from the identical files, not observed.

NOT run after this merge: the whole `Gateway.UnitTests` suite, `-Parked`, and the nineteen breaks. Of the four
product files the breaks were made in, the merge changed `GatewayHost.cs` (the conflict above) and brought main's
changes into `LicenseDisplayFold.cs`; the hosted tests of break 16 and the unit tests are green after it.
