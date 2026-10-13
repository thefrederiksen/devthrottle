# Proof: the owner is emailed once when a sign-in about to end cannot be renewed

License Switching, phase 2 (Gateway), part 4 of contract section 8: section 8.1, "ONE owner email per holding per
failure streak". 13 October 2026.

## What was built

- `LicenseRenewalFailureLog` - one row of the switch log, of kind `renewal-failed`, for one failure streak of one
  sign-in a machine holds. No table and no column is added. The row is what makes the email go out once: what
  became of the email is its reason word (`owner-emailed`, `owner-email-failed`, `no-owner-account`), a row recorded
  as emailed is never changed back, and it is stored, so a Gateway that restarts in the middle of a streak does not
  email again - with one exception: a Gateway that stops between the mail service's confirmation and the row being
  marked emails once more after it starts.
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

- **The guard of break 13 is held by reading only.** It is the guard that leaves alone an account no Director has
  reported for. The break as written did not compile. The review took the guard out, out of tree, and every test
  stayed green: without it the check reads the account four times instead of once and still does nothing. So no
  test of behaviour can see it; `AnAccountNoDirectorHasReportedFor_IsLeftAlone` is a test of "an account with
  nothing to do does nothing", not of the guard. No test counts the reads.
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

## The review round (13 October 2026)

The review (`../reviews/3948-review.md`) asked for changes; `../reviews/3948-answers.md` answers each finding and
holds the runs of the round. Everything above this section describes the code BEFORE that round. In particular the
streak no longer ends on a report that lacks the failure time, the row's sentence and the email changed, and the
test of break 11 that "renewed" with an earlier end is rewritten.

When this section was written no break of the round had been run. They have been since: the next section holds
them.

## The breaks of the review round and of the re-review (13 October 2026)

This replaces the sentence above that says no break of the review round has been run. Each break was made in the
product code on a clean committed tree, the tests were built and run, the file was put back with `git checkout`, and
the tree was checked clean after each. "unit" is `LicenseRenewalEmailServiceTests` and `LicenseSwitchServiceTests`
together; "hosted" is `LicenseRenewalEmailHostedTests` (3 tests). A theory with several cases red is named once.

### Round 1 of the review, run on `04281f832`

Before the re-review's changes. The unit run had 105 tests then.

| # | The break | File | Result | Red |
|---|---|---|---|---|
| 1 | A report that only lacks the failure time ends the streak again | `LicenseRenewalEmailService.cs` | unit: 3 red, 102 green; hosted: 1 red, 2 green | `ADirectorThatRestarts_AndReportsTheSameSignInWithNoFailureTime_EndsNothing_AndTheOwnerIsEmailedOnce`, `AReportWithNoFailureTime_AndASignInThatEndsEarlierOrWasMadeEarlier_EndsNothing`, `ASendThatHadFailed_IsNotTriedWhileTheMachineReportsNoFailureTime_AndIsTriedWhenItFailsAgain`, `ARenewalThatWorks_ClosesTheWarning_OnTheNextMinuteCheck_AndAReportThatOnlyLacksTheFailureTimeDoesNot` |
| 2 | A sign-in that ends later is not taken as a renewal | `LicenseRenewalEmailService.cs` | unit: 3 red, 102 green; hosted: 1 red, 2 green | `ARenewalThatWorks_ShownByASignInThatEndsLater_EndsTheStreak_AndALaterFailureIsANewOneWithItsOwnEmail`, `ASecondSignInOfOneMachine_ThatStartsFailingLater_IsItsOwnStreak_AndEachEndsOnItsOwn`, `TheRow_IsAnsweredByTheLogRoute_InWords_OpenWhileTheStreakLasts`, `ARenewalThatWorks_ClosesTheWarning_OnTheNextMinuteCheck_AndAReportThatOnlyLacksTheFailureTimeDoesNot` |
| 3 | A sign-in made later is not taken as a renewal | `LicenseRenewalEmailService.cs` | unit: 1 red, 104 green | `ARenewalThatWorks_ShownByASignInMadeLater_WithTheSameEnd_EndsTheStreak` |
| 4 | The sign-in time the machine reported is not kept on the row | `LicenseRenewalFailureLog.cs` | unit: 1 red, 104 green | `ARenewalThatWorks_ShownByASignInMadeLater_WithTheSameEnd_EndsTheStreak` |
| 5 | The email does not say when the machine last reported | `LicenseRenewalEmailService.cs` | unit: 2 red, 103 green | `TheEmail_AboutAMachineThatHasBeenSilentForAWeek_SaysHowOldItsReportAndTheFailureAre`, `TheEmail_SaysWhichSignInWhyAndWhatToDo_InPlainSentences` |
| 6 | A failure time ahead of the Gateway clock is said as a distance | `LicenseRenewalEmailService.cs` | unit: 1 red, 104 green | `TheEmail_WhenTheFailureTimeIsAheadOfTheGatewaysClock_SaysTheMomentAndNoDistance` |
| 7 | The machine sentence is not cleaned: only a plain space is a space | `LicenseRenewalEmailService.cs` | unit: 2 red, 103 green | `AMachineNameWithALineBreak_IsOnOneLineInTheSubject`, `TheEmail_WhenTheGatewayKnowsNoReason_SaysTheMachinesSentence_OnOneLine_WithNothingThatIsNotText_AndCutShort` |
| 8 | The machine sentence is not cut short | `LicenseRenewalEmailService.cs` | unit: 1 red, 104 green | `TheEmail_WhenTheGatewayKnowsNoReason_SaysTheMachinesSentence_OnOneLine_WithNothingThatIsNotText_AndCutShort` |
| 9 | The machine sentence is in the email also when the Gateway knows the reason | `LicenseRenewalEmailService.cs` | unit: 1 red, 104 green | `TheEmail_SaysWhichSignInWhyAndWhatToDo_InPlainSentences` |
| 10 | The guard against two checks of one account never refuses | `LicenseRenewalEmailService.cs` | unit: 1 red, 104 green | `ASecondCheckOfOneAccount_WhileTheFirstIsWaitingForTheMailService_SendsNothing` |
| 11 | A confirmed email whose row is not marked is not remembered by the next check | `LicenseRenewalEmailService.cs` | unit: 1 red, 104 green | `AnEmailTheMailServiceConfirmed_IsNeverSentAgainByThisGateway_WhileTheDatabaseRefusesToMarkItsRow` |
| 12 | A mail sender that throws is not caught as a failed send (renewal email) | `LicenseRenewalEmailService.cs` | unit: 1 red, 104 green | `AMailSenderThatThrows_IsAFailedSend_TriedAgainLaterEachTime_AndItsMessageIsNotWrittenDown` |
| 13 | The expired active sign-in is said to end in the future | `LicenseRenewalEmailService.cs` | unit: 1 red, 104 green | `TheEmail_ForTheActiveSignInThatHasAlreadyExpired_SaysItHasEnded` |
| 14 | A confirmed no-room email whose row is not marked is sent again | `LicenseSwitchService.cs` | unit: 1 red, 104 green | `NoTarget_AnEmailTheMailServiceConfirmed_IsNeverSentAgainByThisGateway_WhileTheDatabaseRefusesToMarkItsRow` |
| 15 | A mail sender that throws is not caught as a failed send (no-room email) | `LicenseSwitchService.cs` | unit: 1 red, 104 green | `NoTarget_AMailSenderThatThrows_IsAFailedSend_TriedAgainLaterEachTime_NotEveryMinute` |
| 17 | The page is told the sign-in time of a failed-renewal row as a start | `LicenseDisplayFold.cs` | unit: 1 red, 104 green | `ARenewalThatWorks_ShownByASignInMadeLater_WithTheSameEnd_EndsTheStreak` |
| 18 | The owner email word has no sentence | `LicenseDisplayFold.cs` | unit: 1 red, 104 green | `TheRow_IsAnsweredByTheLogRoute_InWords_OpenWhileTheStreakLasts` |

**Break 16 was NOT CAUGHT on `04281f832`**: with the email check put back behind the switch pass, the hosted test
stayed green (3 of 3). The test waited up to 60 seconds for the email, and the Gateway gives up on a switch after 60
seconds, so the email arrived just in time and the test passed about a minute late. The test was changed: it now
waits half that time, and it checks that the other account's switch is still `pending` when the email has gone. The
break was then run again on `77cc61980`:

| # | The break | File | Result | Red |
|---|---|---|---|---|
| 16 | The email check waits behind the switch pass | `GatewayHost.cs` | hosted: 1 red, 2 green | `ARenewalEmail_DoesNotWaitBehindAnotherAccountsDirectorThatHasNotAnsweredItsSwitch` |

### The re-review, run on `77cc61980`

The unit run has 111 tests.

| # | The break | File | Result | Red |
|---|---|---|---|---|
| 19 | An end later by any amount is a newer sign-in: no tolerance | `LicenseRenewalEmailService.cs` | unit: 4 red, 107 green | `ASignInWhoseEndMovesLater_IsANewerSignInOnlyWhenItMovesByMoreThanOneSignInsEndCanDiffer`, `AnEndThatMovesBySecondsOnEveryRefresh_ForThreeDays_IsOneStreakAndOneEmail` |
| 20 | The tolerance is two days, so a sign-in that ends an hour and a second later is not a newer one | `LicenseRenewalEmailService.cs` | unit: 2 red, 109 green | `ASignInWhoseEndMovesLater_IsANewerSignInOnlyWhenItMovesByMoreThanOneSignInsEndCanDiffer` |
| 21 | An open row whose email failed is not tried while the machine reports no failure time | `LicenseRenewalEmailService.cs` | unit: 1 red, 110 green | `ASendThatHadFailed_IsTriedAgainOnItsWait_AlsoWhenTheMachineNoLongerReportsTheFailureTime` |
| 22 | An email about a report with no failure time still gives a reason line | `LicenseRenewalEmailService.cs` | unit: 1 red, 110 green | `ASendThatHadFailed_IsTriedAgainOnItsWait_AlsoWhenTheMachineNoLongerReportsTheFailureTime` |

### What these breaks do not show

- Breaks 1 to 15, 17 and 18 were run before the re-review's changes and not again after. The lines they break are
  still in the code, some moved; the line of break 2 now carries the one-hour tolerance, and breaks 19 and 20
  are the breaks of that line as it is now.
- In break 7 the cleaning of the machine's sentence and of the machine's name is one piece of code, so one break
  shows both.
- No break was made of the order in which the wait is set and the row is written after a failed send.
- No break was made in `LicenseContract.SignInEndTolerance` itself; break 20 changes the line that uses it.
- The whole `Gateway.UnitTests` suite, `-Parked`, `test-database.ps1` and the default gate were not run on
  `77cc61980`. The runs of the re-review are in `../reviews/3948-answers.md`.
