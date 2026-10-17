# Answers to the review of pull request 4140 - the approver alarm

17 October 2026, by the Track C Developer for approver registration. The review is `review-4140.md`. All four findings
are fixed, each with a test. Each new test was shown to fail with its fix taken back out of `LicenseApproverAlarm.cs`
(the fixes reverted in the source together: the four new tests and the first test's identity assertion went red, five
failed of fourteen; then the source was restored).

## 1. Two log lines per tenant per minute with nothing to say - FIXED

`LicenseApproverStore.ListQuietly` reads the approvers with no log line, for the minute raise only; `List` keeps its
line for the routes. `ConditionsNow` uses it, and returns before reading the machines when the tenant has no approver.
`RaiseAsync` logs only when there is at least one approver that cannot approve; the alarm service already logs every
open and close, so the counts are no longer repeated. The alarm's log writer can be handed in (the Gateway's log
otherwise), so a test can watch it.

Test: `AMinuteRaiseWithNothingToSay_WritesNoLogLine` - a raise with no approver and a raise with a healthy approver
write no line.

## 2. One long sentence silences the tenant's whole raise - FIXED

`ConditionsNow` checks each approver's two sentences against `LicenseAlarmService.MaxSentenceLength` before passing
them. One past it is left out with a line naming its account (`REFUSED the alarm of account=...`), so it costs that one
alarm and never the others. The other sentence rules (a full stop at the end, no control characters) already hold by
construction: the readiness sentences end in a full stop and every stored field is shape-checked. I did not cap the
detail or ask for Track A's limit to change: with the fields the stores allow, a sentence over 400 characters takes an
email near 100 characters or more, and losing that one alarm, logged, is the honest answer short of a contract change.

Tests: `AnApproverWhoseSentenceIsTooLongForAnAlarm_CostsOnlyItsOwnAlarm_TheOthersStillOpen` (a 300-character account
with a 200-character detail is left out; account A's alarm still opens) and
`AnApproverWithALongDetailAndALongAccount_WithinTheLimit_StillOpens` (the review's case: a 200-character detail and a
100-character account still open).

## 3. The alarm identity flips when the machine first reports - FIXED

Every approver alarm is now about the account alone: the condition's machine is always null, stored as "no one
machine". The sentences still name the machine, and there is one approver per account. Contract 10.6 and `c1-alarm.md`
say so.

Tests: `AnApproversMachineReportingForTheFirstTime_KeepsTheSameAlarm_NoSecondPushOrEmail` (the alarm opened before the
machine reported is the same alarm after it reports: still open, nothing closed, one push, one email), and the first
test now expects the empty machine identifier and name.

## 4. Conditions read outside the per-tenant gate - FIXED

`LicenseApproverAlarm` keeps one `SemaphoreSlim` per tenant, and `RaiseAsync` reads the records and raises the alarm
under it, so every raise reads after the previous raise wrote.

Test: `TwoRaisesOfOneTenant_RunOneAtATime_SoTheSecondReadsTheRecordsAsTheyAreWhenItsTurnComes`. The first raise is held
inside its push; the record goes healthy, a second raise starts and must not finish, then the record goes signed out
again; on release the second raise reads signed out and leaves the alarm open. Without the wait it reads the healthy
record at once and closes an alarm that is true again. A plain "the second has not finished" assertion would not have
proved it, because the alarm service's own gate also makes the second raise wait; the final state is what tells them
apart.

## The notes

The hosted proof gap is not closed in this pull request; the path is covered by the unit tests over the real alarm
service, and a hosted assertion after a signed-out report over the tunnel is a fair follow-up. The other notes need no
change.
