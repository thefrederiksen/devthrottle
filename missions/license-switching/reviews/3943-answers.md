# Answers to the review of pull request 3943

The review is `3943-review.md`, beside this file, written at head `4808b48f0`. These answers are for head
`efd7988f3` plus the commit that adds this file. 13 October 2026.

Every finding is answered with what was changed, the test that holds it, and the break that turned that test red.
The breaks are in `../proof/gateway-approval-relay.md`, section "The review round"; the numbers below are the
break numbers there.

## The findings

### 1. The limit on approvals in flight did not bound anything - FIXED

`LicenseApprovalRelay.Accept` now refuses ON THE HUB CALL, by throwing, and keeps nothing for a refused request: no
entry, no task, no command, and no line that quotes the request.

- Past the limit of 8 for the account it throws `LicenseApprovalsBusyException`; `DirectorHub.RequestLicenseApproval`
  turns that, like a bad identifier, into a failed call with the relay's own sentence.
- The account and the address are checked on the call too. An account that is not an email of at most 320
  characters, and an address that is missing or longer than 4,096 characters, fail the call. The length of each is
  looked at before the text is copied or trimmed.
- The count is a number kept for each account under one lock with the set of relays in flight. It is read, not
  counted, so a call costs the same however many relays are running. The dictionary is no longer copied.
- A place is given back when its relay ends, and the limit is each account's own.

Tests: `Accept_EveryRequestPastTheLimit_IsRefusedOnTheCall_AndTheHundredthHoldsNothingMoreThanTheFirst` (eight held
open, then a hundred more each with its own identifier: each throws, the count stays eight, no command goes to
either Director, another account is still served, and the eight places come back);
`Accept_ARequestWhoseAccountOrAddressIsNotOne_IsRefusedOnTheCall_AndHoldsAndSendsAndQuotesNothing` (twelve cases,
two of them two million characters long); through the real hub,
`RequestLicenseApproval_PastTheAccountsLimit_FailsTheCall_AndTheGatewayHoldsNothingForIt` and
`RequestLicenseApproval_WithAnAccountOrAnAddressThatIsNotOne_FailsTheCall_WithASentenceThatQuotesNothing`.
Breaks 1, 2, 3, 4, 5 and 27.

One thing changed with it that the review did not ask for by name: a request with no account or no address used to
be answered `refused` / `approval-failed` by command. It now fails the call, as the ruling says for a request with
an address or account too long. The Director ends its sign-in on a failed call either way.

### 2. The 15 minute rule could record a sign-in as failed when it worked - FIXED

`LicenseSignInService.SettleUnansweredAsync` replaces `SettleUnanswered`. Nothing is tried until no Director could
still be running the sign-in (15 minutes after it was asked). Then, once a minute:

1. **The Director is asked again, with the same `SignInId` and the same `NotAfterUtc`.** That deadline has passed,
   so a Director that never saw the sign-in answers `failed` / `expired` and starts nothing, and one that ran it
   answers with its stored result. Either answer ends the row. The row's `Replayed` column is now really written.
2. **Only when that gives no answer** - no Director connected, no answer within 60 seconds, a dropped tunnel, or an
   answer that is not this sign-in's result - the row is settled from the machine's report, and only from a report
   the Gateway accepted AFTER those 15 minutes. Until one arrives the row stays open.
3. **A renewal is judged by the sign-in's time having changed**, never by one clock against another. The time the
   machine had reported for the account before the ask is kept on the row when the row is written, and the renewal
   is `renewed` when a later report shows another time.

Both tests the ruling named exist: `Settle_TheTunnelDroppedAndNoReportSince_StaysOpen_HoweverLongItIs` and
`Settle_ARenewalThatWorked_OnAMachineWhoseClockIsAMinuteBehind_EndsRenewed`. With them:
`Settle_AsksTheDirectorAgainWithTheSameIdentifier_InItsOwnScope_AndEndsTheRowWithItsStoredAnswer`,
`Settle_ADirectorThatNeverSawTheSignIn_SaysItStartedNothing_AndTheRowEndsFailedExpired`,
`Settle_AReportAcceptedWhileTheSignInCouldStillBeRunning_SettlesNothing`,
`Settle_ADirectorThatGivesNoAnswerWhenAskedAgain_LeavesItToTheReport_OpenUntilOneComes` (four kinds of no answer),
`Settle_ARenewal_WhoseSignInIsStillTheOneFromBeforeItWasAsked_EndsFailed_WhateverTheClocksSay` (a machine whose
clock is six hours ahead), and on a real hosted Gateway over the real tunnel
`AnUnansweredSignIn_IsAskedAgainByTheHostsMinuteCheck_OverTheTunnel_AndEndsWithTheDirectorsStoredAnswer` and
`AnUnansweredSignIn_WhoseDirectorLostItsTunnelAndHasNotReportedSince_StaysOpen`. Breaks 13 to 18, 20 and 28.

`cockpit-api.md` section 7.5 and the contract's section 8.4 now say this rule.

**Three things to know, because they are consequences and not what the ruling spelled out:**

- **A row can now stay open for a long time.** A machine switched off in the middle of a sign-in is shown as "Sent,
  no answer yet" until it comes back. That is the truth, and section 7.5 says so to the page's Developer.
- **`Start` no longer settles a row past its time before it checks for a running sign-in.** Asking a Director is not
  something a route that must answer at once can wait for. So for up to a minute after the 15 minutes a second
  sign-in for that machine and account is refused `sign_in_running`, until the minute check has settled the row.
  The test that held the old behaviour is replaced by
  `ARowThatCannotBeSettledYet_StillBlocksASecondSignIn_AndOnceSettledItNoLongerDoes`.
- **Where the time from before the ask is kept.** There is no free column for it and this pull request adds no
  migration, so it is in the row's `DirectorStartedAtUtc`, which a sign-in row never used (the page is told `null`
  for a sign-in's start, as section 7.5 already said). `LicenseSignInLog` says so, and reads it only through
  `SignedInBefore`. If a column of its own is wanted, it is a small migration on both providers; say so and it is a
  follow-up.

### 3. A relay that throws left the asking Director with no answer - FIXED

`RelayAsync` enters the scope inside its `try`, and anything `DecideAsync` throws is now answered: the asking
Director is sent `refused` / `approval-failed` with "The Gateway failed while relaying this approval
(InvalidOperationException)." - the kind of failure and nothing of its message.

Tests: `Relay_ASendToTheApprovingDirectorThatThrows_StillAnswersTheDirectorThatAsked_AndTheLogHasNoAddress` (the
sender throws an exception whose message holds the address and the key: the answer is checked, and the log is
checked to hold neither), `Relay_AnApproversSettingThatCannotBeRead_StillAnswersTheDirectorThatAsked`, and
`Relay_AScopeThatCannotBeEntered_IsLoggedByItsTypeOnly_AndLeavesNothingInFlight`. Breaks 7, 8 and 9.

A scope that cannot be entered is logged and not answered: no command can be sent without the scope.

### 4. Text from a request reached log lines before it was checked - FIXED

- The relay writes the account only after `Accept` has checked it, and a refusal line names the asking Director and
  why, nothing of the request - not even its identifier.
- `LicenseSignInService.Start` logs the account and the machine identifier only after both have been checked; the
  identifier is then the parsed one.
- `LicenseApproverStore.Set` refuses a machine name, a user or an account that holds a control character, a line or
  paragraph separator, or a format character, and writes its line only after the checks. `IsAccountEmail` refuses
  those characters for every caller, which also covers the two sign-in routes and the relay. The character rule is
  the one the Switch now route on main already had; there is now one copy of it.

Tests: `Set_ANameOrAccountHoldingACharacterThatIsNotText_IsRefused_NothingIsStored_AndTheLogDoesNotQuoteIt` (ten
characters in each of the three fields), `Set_NamesWithSpacesAndLettersOfAnyAlphabet_AreTaken`,
`Start_NothingTheCallerSentIsLoggedBeforeItIsChecked`, and the relay's twelve cases of finding 1.
Breaks 6, 19, 21, 22, 23 and 24.

### 5. "No log line carries the address" was not proven for the host's own lines - FIXED

`NoLineOfTheHostsLog_CarriesTheAddressOrTheApproval_AcrossAnApprovedRelayAndADroppedTunnel` captures the log of a
real hosted Gateway across an approved relay and across a relay whose approving Director loses its tunnel while it
holds the command, both through the real hub, `GatewayHost.SendCommandAsync` and `DirectorCommandRouter`. It first
shows the router's lines are in the capture - the line for the answered command and "TUNNEL DROPPED mid-command" -
and then that no line holds the address, the sealed approval or the key. Break 30.

### 6. The hosted tests held less of the "who may call" table than the document said - FIXED, both ways

The tests now send both: a session key the owner raised through the real raise route
(`TheFourWrites_AskedWithASessionKeyTheOwnerRaised_AreStillRefused_AndNothingIsStoredOrSent`) and the shared machine
token of a self-hosted Gateway (`LicenseSignInWritesSelfHostedTests`). And the table in `cockpit-api.md` section 7
now has a column for each credential and names the test that holds it.

**One fact the table had wrong and now has right:** a session key, raised or not, is answered `403` with
`session_key_out_of_scope`, because `SessionKeyGuard` stops it before the route is reached. `owner_only` is what a
Director's own key and the machine token get. Break 29 shows it: with the routes' own check removed the Director's
key and the machine token get through and the raised session key still does not.

### 7. No test wrote a sign-in row on PostgreSQL - FIXED

`LicenseStorePostgresTests.A_sign_in_row_is_written_sent_left_unanswered_ended_and_read_back` runs every statement
of `LicenseSignInLog` on PostgreSQL and reads every value back, including the kept sign-in time and a sentence cut
to the column's width. `.\scripts\test-database.ps1`: 72 of 72, none skipped.

### 8. The approvers are per tenant and durable - NOTED, nothing to change

### 9. Tenant isolation - the wrong-scope test is ADDED

`Accept_CalledWhileAnotherAccountsScopeIsInEffect_StillRelaysInTheAskingDirectorsOwnScope`: the calling thread is
inside account two's scope, account two has an approver and a Director of the same names, and both commands still
go out in account one's scope with account one's browser profile.

### 10. Two small mismatches - FIXED

- `not-key-holder` is in the list of reason words of section 7.5.
- The Gateway waits for `license-approve` until the sooner of 3 minutes and the request's `NotAfterUtc`. A wait that
  ended at the deadline is answered `expired`, and a request arriving at or after its deadline is answered `expired`
  with no machine asked. Breaks 10, 11 and 12.

## What I found on the way

1. **Three of my new test classes failed about once in every few hundred log captures, and the cause was mine.** The
   Gateway unit test assembly runs four classes side by side, and the log is one per process. A class that captures
   it must be in `FileLogCaptureCollection`; `LicenseSignInKeepsNothingTests` was, and the three classes I added log
   checks to were not. When another class's capture closed in the middle of one of mine the log was off for a
   moment and a line was lost. All four are in the collection now. I also replaced the way these tests read the log
   (`CapturedLog`): it takes each line in memory as it is written, and fails by name when it captured nothing, so an
   empty capture can never pass a "no line carries X" check. After the fix the 709 license unit tests were run 35
   times, and no run failed on the log again.
2. **One of the first 25 of those runs failed on something else, once**: SQLite "UNIQUE constraint failed:
   device_import_markers.SourcePath". I did not capture which test. A device registry built with no path is one per
   machine, and twenty unit test files build one that way, so two classes can open it at the same moment.
   `RequestLicenseApprovalHubTests` was one of the twenty and now opens its own over the test's own database; the
   10 runs after that change were all green. The other nineteen are main's and are not touched here. The cause is a
   reading of the code after one sighting, not something I watched happen.
3. **Two guards I added to `LicenseDisplayFold` were code nothing reaches**, and breaks 25 and 26 showed it by
   staying green: a sign-in row is folded by `LicenseSignInFold`, which already answers `null` for its start time.
   The guards are removed, and break 25 was run again against the line that really does it.

## What was run on the final head

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` | 709 passed, 0 failed |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~License"` | 39 passed, 0 failed |
| `.\scripts\test-database.ps1` | GREEN, 72 executed, 0 failed, 0 skipped |
| `.\scripts\test-local.ps1` | GREEN at `efd7988f3`, tree clean, every suite Completed |
| `dotnet test src/CcDirector.Gateway.UnitTests` (the whole suite, which the default run does not cover) | 11,473 passed, 0 failed |

**Not run in this round:** `.\scripts\test-local.ps1 -Parked`, so the whole of `Gateway.Tests` and `Core.Tests`
did not run; of `Gateway.Tests` only the 39 license tests did. `FakeTunnelDirector`, which many hosted tests use,
gained one method and lost nothing.
