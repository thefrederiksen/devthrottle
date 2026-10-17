# Phase 6, Track A, piece A3 - readiness all the time, fixed at once, the 80% alarm

## Why

The owner, 16 October 2026: "at 80%, of license expiry, we need to make sure that the next License is available on all
the computers. We need to warn about this if it's not the case ... this is an automated step that does not need the
user." His ruling: "Check all the time; start fixing at once, alarm loudly only if it is still not ready at 80%." On 15
and 16 October a machine with no spare was found only at 97%, and nothing fixed it.

## What was built

All of the deciding is on the Gateway (CLAUDE.md rule 7); the Cockpit draws one more sentence per machine, verbatim.

- **`Licenses/LicenseReadinessFold.cs`** - pure. For every machine on an account: the account it would switch to next
  (`LicenseDecisionEngine.NextTarget`, from A2), whether it is ready for it (`LicenseDecisionEngine.Readiness`, the one
  definition of ready, from A2 - nothing here works readiness out a second time), what the Gateway is doing about it,
  and the finished sentence. "No other account has room" is its own state, `no-room`, never a gap. It also builds the
  80% alarm's conditions and their words.
- **`Licenses/LicenseReadinessService.cs`** - the minute check. Each minute, for each tenant whose machines report, it
  judges, starts a sign-in for each machine whose judgement is "start now", and raises the license alarm (A1's one
  mechanism, kind `not-ready-at-80`). Wired in `GatewayHost` after the switch check and the settling of sign-ins with
  no answer, so it reads the rows they just ended. The Licenses page reads the same `Judge`.
- **The Licenses page** - four new fields on each machine (`readiness`, `readinessTargetAccountEmail`, `readinessText`,
  `readinessTone`), drawn by the Cockpit in the schedule under the machine's status, in the Gateway's tone. The phone
  has no Licenses screen (A1 found the same), so the phone only shows the alarm's banner.
- **Two new sign-in reason words**, `auto-spare` and `auto-renewal`, so an automatic sign-in says it was automatic: the
  row's first sentence begins "Started automatically:", and the log's "Why" column says "Automatic: a new spare" or
  "Automatic: a renewal" for as long as the row is kept. A Director only logs the word, so no Director change is needed.
- **`LicenseSignInLog.ListSince`** - the sign-in rows of the last day, from which the retry wait is read.
- Docs: MISSION.md (section 6, "Phase 6: it runs itself"), contract.md section 11 (and 8.4, 9.6), cockpit-api.md
  section 2 (the four fields and every sentence) and 7.5 (the two reason words).

### What is done about a machine that is not ready, in this order

1. A sign-in for that machine and account is running: wait. ("Not ready: ...; a sign-in to B is running, started at
   10:42 UTC.")
2. The account's approver cannot approve - Track C's one check, `GatewayHost.LicenseApproverReadiness.WhyNotReady`, and
   nothing else: start nothing; the reason is the approver's own sentence.
3. The machine's Director is not connected: start nothing; the sentence says so.
4. The last sign-in ended well less than 10 minutes ago: wait for the machine's next report rather than sign it in
   twice. ("...; it was signed in at 10:42 UTC, and its next report should show it.")
5. The last sign-ins failed: wait out the retry wait. ("...; the last sign-in failed at 10:42 UTC (the Director's
   reason); trying again at 10:47 UTC.")
6. Otherwise: start a sign-in now through the existing pipeline (`LicenseSignInService.Start`, then the Director, then
   `LicenseSignInCoordinator` and `LicenseApprovalRelay`, approved by the account's approver).

### Choices made, and why

- **The retry wait is 2, 5, 15 and 30 minutes, then hourly** (the brief's suggestion), counted per tenant + machine +
  account from the failed sign-in rows of the Licenses log, newest first, up to the first row that did not fail. No
  new store, and it survives a Gateway restart (a test proves it by building a new service for every check). Any failed
  row counts, also one a person started with "Add a spare": a failure is a failure, and a person's own click is not
  blocked by the wait (the route does not consult it).
- **A Director that is not connected is looked at every minute, not on the retry wait.** The brief said "retried by
  the same back-off". While a Director is offline nothing can be sent, so an attempt would only write a failed row
  saying so: one an hour for as long as the machine is off, and the sign-in would then start up to an hour after the
  Director came back. Looking every minute costs one read and writes nothing, and the sign-in starts on the first
  minute the Director is back. The sentence and the alarm's action ("Start the Director on machine-a.") say what is
  wrong. If the Tech Lead wants rows for it, it is one change in `WhatIsDone`.
- **A sign-in that is not refused but expires too soon is RENEWED, not added.** "Add a spare" is refused `already_held`
  for an account the machine holds, so the existing pipeline's "Renew now" is the one that can fix a refused or expiring
  sign-in. Both go through the same `Start`.
- **Ten minutes for the report after a sign-in ended well.** Without it the minute after an `added` row, before the
  machine reports, would start a second sign-in.
- **Readiness is against the account it would switch to NEXT.** A machine that is ready only for the second-best account
  would still be switched at 97% (A2's rule), but the owner asked that "the next License is available on all the
  computers", so it is not ready, it is signed in to the next account, and at 80% it raises the alarm.
- **"Automation switched on" is: the tenant's machines report licenses.** There is no Gateway flag; a Director reports
  licenses only with license switching switched on for it, and the 13 October ruling keeps that to the owner's own
  tenant. This is the same rule the switch check and Track C's approver registration (contract 10.3) already use. Not
  widened.
- **The alarm's identity is machine + the account in use.** If the next account changes while the alarm is open, the
  alarm stays open and its sentence is brought up to date, rather than closing and opening a new one with a new email.
- **80% is judged on a fresh reading only** (the staleness rules of contract 5.2), and a window whose reset time has
  passed does not count. A reading that goes stale closes the alarm; the next fresh one reopens it if still true.

## The tests, in plain terms

Gateway, `src/CcDirector.Gateway.UnitTests/Licenses/LicenseReadinessServiceTests.cs` - the real license store, the real
sign-in service and the real license alarm on a throwaway SQLite file; the Directors are fakes that answer as each test
says; the email and the push are recorders; the clock is the test's own. In every test machine-a and machine-b are on
account A, and B has room, so B is the account they would switch to next; machine-b holds a sign-in for B and machine-a
does not (the 16 October picture).

Every readiness sentence, word for word:
- `Judge_AMachineHoldingTheNextAccount_IsReady` - "Ready for account-b@example.com.", tone good.
- `Judge_HoldsNoSignIn_IsNotReady_AndASignInIsStarted` - "Not ready: holds no sign-in for account-b@example.com;
  DevThrottle is starting a sign-in to account-b@example.com now.", reason `auto-spare`.
- `Judge_SpareExpiresTooSoon_IsNotReady_NamesTheExpiry_AndARenewalIsStarted` - a sign-in expiring five minutes from
  now is not ready, the sentence names the time, and the command sent is a renewal (`auto-renewal`).
- `Judge_NoApprover_StartsNothing_AndTheReasonIsTheApproversSentence` - through Track C's real `WhyNotReady`: no
  command, no row, and the sentence ends with "No machine is set to approve sign-ins for account-b@example.com. Set an
  approver for account-b@example.com on the Licenses page."
- `Judge_DirectorOffline_StartsNothing_AndSaysSo` - no command, no row, "... and the Director on machine-a is not
  connected, so it cannot be signed in yet."
- `Judge_ASignInRunning_SaysSigningItInNow` - the check after the start: "... is running, started at 03:12 UTC."
- `Judge_LastSignInFailed_SaysWhenWhyAndWhenItIsTriedAgain` - "... the last sign-in failed at 03:12 UTC (The approval in
  the browser did not complete); trying again at 03:14 UTC."
- `Judge_SignedInAMomentAgo_WaitsForTheReport_AndStartsNoSecondSignIn` - the Director answered "added" and the report
  does not show it yet: no second sign-in, and the sentence says so.
- `Judge_NoOtherAccountHasRoom_IsItsOwnState_NotAGap` - every other account full: "No other account has room for it to
  switch to.", not a gap, nothing started, no alarm.

One sign-in at a time:
- `Tick_AGap_StartsExactlyOneSignIn_AndNoneWhileItRuns` - the first check starts one (to machine-a's Director, for B,
  reason `auto-spare`, row "Started automatically: ...", log "Why" "Automatic: a new spare"); the next check, while it
  still runs, starts none.
- `Start_AnAutomaticSignInWhileOneIsRunning_IsRefused_AndWritesNothing` - what the check relies on: the sign-in service
  refuses a second automatic one, and a person's "Add a spare", with `sign_in_running` while one is open.

The retry waits:
- `Tick_FailedAttempts_AreRetriedOnlyAfterEachWaitOfTheSchedule_EvenAcrossARestart` - every attempt fails. For each
  wait (2, 5, 15, 30, 60, 60 minutes) one second before it ends nothing starts and the next attempt time is the end of
  the wait; at the end exactly one starts. Each check is made by a NEW service over the same database, as after a
  Gateway restart. Every attempt ended before the next started.
- `RetryWait_IsTheDocumentedSchedule` - 2, 5, 15, 30, 60, 60, 60, and no wait before a first failure.

The 80% alarm:
- `Tick_Under80_NotReady_NoAlarm_ButASignInIsStarted` - at 79.9%: not ready, a sign-in started, no alarm, no email, no push.
- `Tick_At80OfTheWeeklyLimit_NotReady_OpensOneAlarm_AndTheNextTickNoSecond` - at exactly 80%: one alarm for machine-a and
  account A, its two sentences pinned, one email, one push naming machine-a; the next check keeps it open and sends
  nothing more.
- `Tick_At80OfTheFiveHourWindow_NotReady_OpensTheAlarm` - the other window: "... at 85.5% of its 5-hour window ...".
- `Tick_At80_TheActionIsTheOneThingToDo_ForEachReason` - Director offline: "Start the Director on machine-a."; no
  approver: the approver's sentence.
- `Tick_TheMachineBecomesReady_TheAlarmCloses` - machine-a reports a sign-in for B: ready, and the alarm closes.
- `Tick_TheAccountFallsUnder80_TheAlarmCloses` - A's window resets to 3%: still not ready, and the alarm closes.
- `Tick_At80OnAStaleReading_RaisesNoAlarm` - 90% on a reading older than its staleness limit raises nothing.

Automation:
- `Tick_ATenantWithoutAutomation_StartsNoSignIn_AndRaisesNoAlarm` - a tenant none of whose machines reported: nothing
  read, no command, no alarm.

Changed: `LicenseEndpointsTests.Overview_AnswersTheExactShapeTheLicensesPageIsBuiltFrom` pins the four new fields and the
example machine's "Ready for account-b@example.com.". The three tests that call the overview route by hand pass the
readiness service (`LicenseReadinessTestKit`).

Web, `apps/cockpit/src/licenses/LicensesViewRows.test.tsx`:
- "shows each machine's readiness sentence verbatim, in its own tone, on its own row" - two machines with DIFFERENT
  sentences and tones, each read on its own row, so a page that drew the first row's sentence everywhere, or wrote its
  own sentence from the state word, fails. Reverted the view and ran it: it failed; restored: it passes.
- "draws no readiness line for a machine the Gateway gives none".

## The screenshots

Taken by `layout_check.py` in this folder: the Cockpit and the phone app on their own development servers, the Licenses
routes answered with the documented example answers of cockpit-api.md except that the machines are machine-a to
machine-i, one for each readiness sentence, in the words the tests above pin. The alarms route answers the 80% alarm in
the pinned words. Every other Gateway call is refused with 503 (so the phone's home screen shows its own "not part of
this layout check" errors - expected, not part of the check). The account cards are the documented example's and still
name its own machines; only the machine lines are what is checked. The script FAILS if a sentence is not drawn exactly
as given, in its tone, on its own machine's row, or if a banner is drawn with no alarm open.

- `licenses-schedule-every-readiness-kind.png` - the schedule: ready (green), starting a sign-in, running, failed with the
  retry time, signed in and waiting for the report, Director offline, no approver (the approver's sentence), a sign-in
  expiring too soon, and no other account has room (neutral).
- `licenses-page-with-readiness.png` - the whole Licenses page with those machines.
- `licenses-80-alarm-banner.png` - the red banner raised by the 80% alarm, above the Licenses page.
- `history-80-alarm-banner.png` - the same banner on another page.
- `phone-home-80-alarm-banner.png` - the same banner on the phone, 390 pixels wide.

## How it was proved

- The license unit tests (`FullyQualifiedName~CcDirector.Gateway.Tests.Licenses`): 1002 passed, 0 failed; the new file
  three times in a row, 21 of 21 each time.
- `.\scripts\test-local.ps1`: GREEN, 11 suites, every one Completed (results in the pull request).
- `.\scripts\test-local.ps1 -Parked -Filter License`, TEMP on D: - results in the pull request.
- The Cockpit Licenses web tests (`npx vitest run src/licenses`): 82 passed and 1 failed - `fixtures.test.ts`, "APPROVERS
  is not example 21 of cockpit-api.md". That test is red on origin/main as it stands, before this change: Track C's
  #4127 changed the approver examples in cockpit-api.md without regenerating `fixtures.ts` or adding the new approver
  fields to the client type (checked by running it with main's `fixtures.ts` and `cockpit-api.md`). This change adds only
  its own readiness fields to `fixtures.ts`, by hand, with the values the producer gives; the Tech Lead was told.
- `npx tsc --noEmit` for the Cockpit, the phone and client-core: clean.
- `python missions/license-switching/phase-6/track-a/readiness/layout_check.py`: passed.

## The failure cases covered

No sign-in; a sign-in expiring before a switch could finish; no approver that can approve; a Director that is not
connected; a sign-in already running; a sign-in that ended well and is not reported yet; a sign-in that failed, retried
after each wait and across a restart; no account with room; under 80%; at 80% on either window; a stale reading; the
machine becomes ready; the account falls under 80%; a tenant without automation.

## Known limits

- The overview-example producer (`phase-3/overview-example`) now passes the readiness service; its example machines
  are both ready, so the documented example shows only "Ready for account-b@example.com.". cockpit-api.md section 2
  lists every other sentence.
- `GatewayHost`'s one line that calls the readiness check each minute is covered only by reading it, as A1's minute
  retry is.
