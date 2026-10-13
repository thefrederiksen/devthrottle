# Pull request 3948 - answers to the review

The review is `3948-review.md` (twelve findings). The rulings are the Tech Lead's. 13 October 2026.

The code of these answers is commit `ba8a178f3`; main was merged in after it as `5fa222360`.

## The runs, on `5fa222360`

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` | 763 passed, 0 failed |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~License"` | 42 passed, 0 failed |
| `.\scripts\test-database.ps1` | GREEN - 73 executed against the throwaway PostgreSQL, none failed, none skipped. Run on `ba8a178f3`, before main was merged in; not run again after. |
| `.\scripts\test-local.ps1` | GREEN at `5fa222360`, tree clean, every suite `outcome=Completed` |

On `ba8a178f3` the default gate was RED on one test of `Core.UnitTests`, the one that names four test files of pull
request 3878 as reading a real clock. Main fixed those files in pull request 3951; with main merged in the gate is
green.

NOT run: `.\scripts\test-local.ps1 -Parked` and the whole `Gateway.UnitTests` suite. **No revert proof of this round
has been run yet.** Every test named below is green; none of them has been seen red against a break. The table of
breaks follows in a later commit, as ruled.

## The findings

1. **A Director restart ended the streak and the owner was emailed again - FIXED as ruled.** A streak now ends as
   renewed only on something present: the holding's `SignInExpiresAtUtc` is later than the end the row holds, or its
   `SignedInAtUtc` is later than the one the machine reported when the row was opened (kept on the row, in
   `DirectorStartedAtUtc`, read with `LicenseRenewalFailureLog.SignedInWhenOpened`). A report that only lacks the
   failure time ends nothing. A machine that no longer holds the sign-in still ends the streak (finding 9).
   - The test that "renewed" with an EARLIER end is rewritten: `ARenewalThatWorks_ShownByASignInThatEndsLater_...`.
   - The reviewer's case: `ADirectorThatRestarts_AndReportsTheSameSignInWithNoFailureTime_EndsNothing_AndTheOwnerIsEmailedOnce`
     (failing, no failure time, failing: one email, one row, open).
   - Also: `ARenewalThatWorks_ShownByASignInMadeLater_WithTheSameEnd_EndsTheStreak`,
     `AReportWithNoFailureTime_AndASignInThatEndsEarlierOrWasMadeEarlier_EndsNothing`,
     `ASendThatHadFailed_IsNotTriedWhileTheMachineReportsNoFailureTime_AndIsTriedWhenItFailsAgain`, and the hosted
     test `ARenewalThatWorks_ClosesTheWarning_OnTheNextMinuteCheck_AndAReportThatOnlyLacksTheFailureTimeDoesNot`.
   - **What is still true and should be known.** When the machine did not say a sign-in time when the row was
     opened, only the later end can end the streak; a sign-in time that appears later is not taken as a renewal.
     While a machine reports no failure time, a send that had failed is not tried again: nothing says the renewal
     is failing at that moment. The email's last line now says when another email is sent in these same words.

2. **The email and the row did not say how old the facts are - FIXED.** The email says "The last try to renew it
   failed 7 days 2 hours ago (at ... by the machine's clock)." and "the-mac last reported to DevThrottle 7 days ago
   (at ...). This email is written from that report; what the-mac has done since is not known here." When the
   failure's time is ahead of the Gateway's clock only the moment is said. The row's sentence says the same two
   moments and when the sign-in ends, all as moments and none as a distance, because the sentence is stored and read
   later. Tests: `TheEmail_AboutAMachineThatHasBeenSilentForAWeek_SaysHowOldItsReportAndTheFailureAre`,
   `TheEmail_WhenTheFailureTimeIsAheadOfTheGatewaysClock_SaysTheMomentAndNoDistance`.
   **Not changed:** a machine that never comes back keeps its row open and "Failing". Nothing here ends it.

3. **The machine's own sentence went into the email as sent - FIXED.** The email says the Gateway's own sentence for
   a reason it knows and leaves the machine's out. Only when the Gateway knows no reason is the machine's sentence
   in it: on one line, every control, format and separator character made a space, cut to 200 characters with
   "(cut short)". The machine's name, which is in the subject, is made one line the same way. Tests:
   `TheEmail_WhenTheGatewayKnowsNoReason_SaysTheMachinesSentence_OnOneLine_WithNothingThatIsNotText_AndCutShort`,
   `TheEmail_SaysWhichSignInWhyAndWhatToDo_InPlainSentences` (a known reason with a detail: the detail is not in the
   body), `AMachineNameWithALineBreak_IsOnOneLineInTheSubject`.
   **Not done:** a link in the machine's sentence is still a link. It is text; only its length and its line are held.

4. **No test held the guard against two checks at once - ADDED.**
   `ASecondCheckOfOneAccount_WhileTheFirstIsWaitingForTheMailService_SendsNothing`, and for the other direction
   `ASecondCheckOfAnotherAccount_IsNotHeldUpByTheFirstAccountsSlowMailService`.

5. **The table's own description did not know the kind - FIXED.** `LicenseLogKinds.RenewalFailed` is the word
   (`LicenseRenewalFailureLog.Kind` is that constant). The entity's summary names the fourth kind and the six columns
   it reuses, and ends "every reader filters by kind". The comments on `Open`, `Ended`, the three owner email words,
   the property comments and `ListLog` name both kinds.

6. **The guard for an account with no machines - the proof now says it is held by reading only.** No test counts
   the reads. `AnAccountNoDirectorHasReportedFor_IsLeftAlone` is a test of "nothing to do does nothing".

7. **Two ways a row stayed unmarked - FIXED for BOTH emails.**
   - An email the mail service confirmed is remembered in memory before its row is marked. A later check of the
     same process marks the row and sends nothing. Tests:
     `AnEmailTheMailServiceConfirmed_IsNeverSentAgainByThisGateway_WhileTheDatabaseRefusesToMarkItsRow` (renewal) and
     `NoTarget_AnEmailTheMailServiceConfirmed_IsNeverSentAgainByThisGateway_WhileTheDatabaseRefusesToMarkItsRow`
     (no account has room). The database is made to refuse by a trigger on the test's own SQLite file.
   - A sender that throws is a failed send: recorded `owner-email-failed` with "the mail sender failed (the type)",
     and the growing wait applies. The wait is set before the row is written. Tests:
     `AMailSenderThatThrows_IsAFailedSend_TriedAgainLaterEachTime_AndItsMessageIsNotWrittenDown` and
     `NoTarget_AMailSenderThatThrows_IsAFailedSend_TriedAgainLaterEachTime_NotEveryMinute`.
   - **This puts a try and catch inside two service methods**, around the one call to the sender. CLAUDE.md rule 4
     keeps try and catch at entry points; this is the ruled exception and it catches nothing else.
   - **A test on main changed meaning and I changed it.** `TickEveryTenant_ChecksEachTenantInItsOwnScope_AndOneTenantsFailureDoesNotStopTheNext`
     made its first tenant fail with a sender that throws. That is no longer a failed check, so the test would have
     passed without showing anything. Its first tenant now fails because the database refuses to mark its row.
   - **Not covered:** the memory is this process's. A Gateway that stops after the confirmation and before the mark
     emails once more after it starts (finding 8). Two Gateway processes on one database are not considered.

8. **"Does not email again" said without its exception - FIXED** in the class comment, the contract paragraph and
   the proof. The pull request description is updated with the same clause.

9. **A sign-in that leaves a report and comes back is emailed about twice - as built**, as ruled.

10. **The renewal check waited for the whole switch pass - FIXED.** `GatewayHost.RunLicenseSwitchCheckAsync` runs
    the email check and the switch pass at the same time, each on its own try. Test:
    `ARenewalEmail_DoesNotWaitBehindAnotherAccountsDirectorThatHasNotAnsweredItsSwitch` (hosted: one account's
    Director holds its switch command; the other account's email goes out while the switch pass is still waiting).

11. **One cause can send many emails in one minute - noted for the Architect**, no change.

12. **Small things.**
    - The expired ACTIVE sign-in: the email now says "is on this account now, and the sign-in has ended." Test:
      `TheEmail_ForTheActiveSignInThatHasAlreadyExpired_SaysItHasEnded`. The expired spare's sentence is finished too.
    - `reasonText` of the three owner email words is now a sentence, for a `no-room` row as well as this one.
      Checked in `TheRow_IsAnsweredByTheLogRoute_InWords_OpenWhileTheStreakLasts`.
    - Pinning `ToAccountEmail` to byte order on PostgreSQL: NOT DONE. It is a change of the column's collation,
      which needs a migration. Left as it is, as ruled.
    - The machine name in the subject: made one line (finding 3).

## What I changed that the review did not ask for

- `startedOnMachineAtUtc` is `null` for a `renewal-failed` row. The row now keeps a sign-in time in the column that
  field is read from, and the page must not be told it as the start of anything.
- The PostgreSQL test opens its rows with a sign-in time and reads it back.
