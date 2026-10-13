# Answers to the review of pull request 3936

The review is `3936-review.md`, at head `7579cfb43`. Each finding is answered below with the Tech Lead's ruling on
it, what was changed, and the test that holds it. The branch was first rebased onto main `8a62ffa29`.

## 1. A Director's own device key can make both writes - MUST FIX. Fixed.

Ruling: both writes are for a signed-in person only; refuse a Director's device key and the shared machine token as
well as session keys, the way `FleetManagerOwnerDevice.Require` does.

Both routes now call exactly that rule before anything else (`LicenseEndpoints.Map`, `OwnerOnly`). The owner's
signed-in browser or phone is allowed; everything else is answered `403` `owner_only` with the sentence that rule
writes, and nothing is stored, written or sent. The comments in `SessionKeyGuard.cs` and `GatewayHost.cs` and
section 6 of `cockpit-api.md` now say who is refused, with a table.

Over real HTTP, against a started Gateway:

- `LicenseHostTests.BothWrites_AskedWithTheMachinesOwnCredential_AreRefused_AndNothingIsStoredOrWritten` - the key
  `HostedTestEnrollment.Enroll` returns for the machine, the one the reviewer's reproduction used: both `403`
  `owner_only`, no date stored, the open switches unchanged. Before the fix these two calls answered 200.
- `LicenseHostTests.BothWrites_AskedWithARaisedSessionKey_AreRefused_AndNothingIsStoredOrWritten` - a session the
  owner raised through the real raise route: both `403`, and the same key still reads the overview.
- `LicenseWritesSelfHostedTests.TheSharedMachineToken_ReadsTheLicenses_AndIsRefusedBothWrites` - a self-hosted
  Gateway, where the shared token is a credential: it reads, and both writes are `403` `owner_only`.
- `LicenseHostTests.BothWrites_AskedByTheOwnersPhone_AreAllowed_LikeTheOwnersBrowser` - the other allowed surface.

The handlers take the owner check as a function, so the unit tests can run them without a started host; two unit
tests hold that each handler asks it FIRST and stops (mutations V01 and V02).

## 2. A body with no `renewsOn` clears the stored date - fixed.

Ruling: a body without `renewsOn` is 400 `invalid_body`; only an explicit null clears.

The route now reads whether the body NAMED `renewsOn` (`LicenseEndpoints.ParseRenewalBody`), whatever its capitals,
as every other field name is read. A body that leaves it out is `400` `invalid_body` and the stored date is kept.
Tests: `RenewalDate_ABodyThatDoesNotNameRenewsOn_IsRefused_AndTheStoredDateIsKept` (the reviewer's `renewOn`, the
field absent, and `renews_on`), `RenewalDate_OnlyAnExplicitNull_Clears`,
`RenewalDate_ABodyThatIsNotTheRequest_IsRefused400_NeverAnError`, and over real HTTP
`LicenseHostTests.RenewalDate_AMisspelledFieldOverRealHttp_IsRefused_AndTheStoredDateIsKept`. Mutations V03, V04.

## 3. The owner email of contract 8.1 is not built, and nothing says so - fixed in words, and the word is kept.

Ruling: say that the failed renewal is stored and shown and the owner email is still to come; keep an unknown reason
word from being lost to the email.

- `contract.md`: the header of section 8 now reads "the Gateway side is built in part", and 8.1 has a paragraph
  saying what the Gateway has built (stored, cleared, folded onto the page), what it has not (the owner email), that
  until then a failed renewal is seen once and not twice, and that the email's rule must key on
  `RenewalFailedAtUtc`.
- `cockpit-api.md` section 6: "This is the only place a failed renewal is shown today", and why.
- The pull request's description says the same.
- The store no longer drops a reason word it does not know: it stores `not-known`
  (`LicenseStore.RenewalErrorNotKnown`), never the word as given, so "a reason was given" survives. A failure with no
  word at all stores none - which is why the rule must key on the time. The page's sentence for such a holding is
  unchanged. Tests: `ApplyReport_AReasonWordTheContractDoesNotName_IsStoredAsNotKnown_...`,
  `ApplyReport_AFailedRenewalWithNoReasonGiven_StoresNoReason`. Mutations V13, V14.

## 4. `accountEmail` has no length limit and is written to the log as typed - fixed.

Ruling: `accountEmail` and `targetAccountEmail` longer than 320 characters or holding a control character are 400.

Both routes refuse such text with `400` `invalid_account` before it is looked up, logged or said back. Exactly 320
characters is still an email like any other. Tests:
`RenewalDate_AnAccountEmailLongerThanItsColumn_OrHoldingAControlCharacter_IsRefused_BeforeItIsLookedUp`,
`SwitchNow_AnAccountOrATargetLongerThanItsColumn_OrHoldingAControlCharacter_IsRefused_AndNeverSaidBack` (the answer
is checked not to carry the text back). Mutations V05 to V09.

## 5. While a Director is offline, every Switch now click writes another row - fixed.

Ruling: a Switch now identical to a switch already open and waiting for that machine (same source, same target)
answers with the waiting row and writes nothing new; update `switch-now-method.md`.

`LicenseSwitchService.SwitchNowAsync`: when the machine's waiting switch (`pending`, `director-offline`) has the
same source and the same target and has not expired, nothing is written and nothing is sent for it; the answer
carries that switch, with its own identifier, and the minute check goes on sending it. Another target still ends the
waiting switch and writes a new one, and so does asking again after it has expired. `switch-now-method.md` and
`cockpit-api.md` say so. Tests: `SwitchNow_Again_TheSameSwitch_AnswersWithTheOneStillWaiting_WritesNothing_AndSendsNothing`
(which also shows the minute check then completing that same switch), `SwitchNow_Again_ToAnotherTarget_Replaces...`,
`SwitchNow_Again_AfterTheWaitingSwitchHasExpired_WritesANewOne`, and through the route
`TheSameSwitchNow_AskedAgainWhileItsSwitchWaitsForTheDirector_AnswersWithTheWaitingSwitch_AndWritesNothing` (target
named, and target chosen by the Gateway). Mutations V10 to V12.

Not changed: two requests arriving at the same moment are still decided one after the other under the tenant's gate,
so the second now finds the first's waiting switch and answers with it.

## 6. A barred account can be named as the target, and the page is told nothing about the bar - field added.

Ruling: add a finished sentence on the account for a bar (null when not barred); document that the page never sends
the default call when `switchNowTargetAccountEmail` is null.

Every account now carries `barredUntilUtc` and `barredText` ("Left out of the automatic choice for 2 hours, until its
5-hour window resets: it triggered a switch. It can still be chosen by hand."), both null when it is not barred. They
are the last two fields of an account, after `switchTargets`; nothing was renamed. `cockpit-api.md` section 6 has the
rule for the default call in bold. Tests: `Account_BarredFromTheAutomaticChoice_SaysSoInOneSentence_AndCanStillBeChosenByHand`,
`Account_WhoseBarHasEnded_IsNotBarred_AtTheVeryMomentItEnds`, and the overview's exact-shape test. Mutations V17, V18.

## 7. "Renews today" is the one sentence without the date - fixed.

It is now "Renews today, 11 Oct", and the comment that says the date is always given is true. Mutation V15.

## 8. The PostgreSQL migration test alters no existing switch log row - added.

`AddLicenseRenewalPostgresTests` now stores a machine, a holding and a switch log row BEFORE the migration. After
it: the switch log row says what it said and is found by its account both in SQL and through the real store; the
holding has no failed renewal and kept its sign-in expiry; after the Down both are still there.
`scripts\test-database.ps1`: green, 71 of 71, none skipped.

## 9. Entry and exit lines removed from two public reads - ruled.

Ruling: a read that runs every minute logs failures only - accepted. Both comments now say it is by that ruling
(`LicenseSnapshotSource.ReadPicture`, `LicenseReadModel.Read`).

## 10. What the proof claims that the reviewer did not rerun - covered by 1.

The real-HTTP test with a raised session key is `BothWrites_AskedWithARaisedSessionKey_...` above.

## Mutations for this round

Each breaks one behaviour, runs all 479 license unit tests and is restored; the restored tree passed 479 of 479.

| | The behaviour broken | Went red |
|---|---|---|
| V01 | The renewal date is stored for a caller that is not the owner's own device | 1 |
| V02 | Switch now runs for a caller that is not the owner's own device | 1 |
| V03 | A body that does not name `renewsOn` clears the date (the code before the review) | 3 |
| V04 | `renewsOn` is only seen when spelled with a small r | 1 |
| V05 | An account email of exactly 320 characters is refused | 1 |
| V06 | A control character is let through | 2 |
| V07 | No length limit | 2 |
| V08 | The target of Switch now is not checked | 1 |
| V09 | The account of the renewal route is not checked | 1 |
| V10 | The same Switch now asked again replaces the waiting switch (the code before the review) | 2 |
| V11 | A Switch now to another target is answered with the waiting switch | 2 |
| V12 | A waiting switch that has expired is answered as if it still stood | 1 |
| V13 | A reason word the contract does not name is stored as given | 1 |
| V14 | A reason word the contract does not name is dropped (the code before the review) | 1 |
| V15 | "Renews today" does not give the date | 2 |
| V16 | An account is still barred at the very moment its bar ends | SURVIVED - see below |
| V17 | A bar is shown on every account | 1 |
| V18 | The page is told nothing about a bar | 2 |

V16 survived because it changes nothing that can be seen: the store already leaves out a bar whose time has come
(`LicenseStore.ListActiveBars`, `UntilUtc > now`), so the fold never receives one to judge, and its own comparison is
a second guard. The test of that moment (`Account_WhoseBarHasEnded_...`) passes through the store's.

The owner-only rule inside `Map` is not reachable by a unit test, so no mutation covers it; the four real-HTTP tests
under finding 1 do, and the reviewer's own reproduction is the "before".

## What was run after the changes

On commit `8d9da5d39` (this branch rebased onto main `8a62ffa29`):

| Run | Result |
|---|---|
| License unit tests (`--filter "FullyQualifiedName~Licenses"`) | 479 passed, 0 failed |
| The whole `CcDirector.Gateway.UnitTests` suite | 11,202 passed, 0 failed |
| License host tests over real HTTP (`LicenseHostTests`, `LicenseWritesSelfHostedTests`, `LicenseSwitchCheckHostedTests`) | 20 passed, 0 failed |
| `scripts	est-database.ps1` | GREEN - 71 executed against the throwaway PostgreSQL, none failed, none skipped |
| `scripts	est-local.ps1` (the default gate, as asked) | GREEN at `8d9da5d39`, tree clean: all ten suites passed |

The default gate leaves out the parked Gateway suites and says so; the whole Gateway unit suite and the license host
tests in the table are the part of them this change touches. The other shards of `CcDirector.Gateway.Tests` and
`CcDirector.Core.Tests` were not run in this round. The two tests that were red on main in the earlier `-Parked` run
are fixed on main now, and the window placement test that failed that day passed in the whole-suite run above.
