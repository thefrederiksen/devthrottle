# Review of pull request 4140 - License Switching Track C, the approver alarm

Reviewer: a separate review session (License Switching - Reviewer - Track C 4140 approver alarm), 17 October 2026.

## Scope

Read, through git objects only (`refs/review/4140` = 8b3aae7a3, merge base bf6e5038a with origin/main):

- The whole diff (6 files): `LicenseApproverAlarm.cs` (new), `LicenseApproverRegistration.cs`, `GatewayHost.cs`,
  `LicenseApproverAlarmTests.cs` (new), contract section 10.6, `phase-6/track-c/c1-alarm.md`.
- At the head, for context: `LicenseAlarmService.cs` (Track A, the whole file), `LicenseApproverReadiness.cs`,
  `LicenseApproverStore.cs`, `LicenseSignInFold.Label`, `LicenseAlarmFold` (push body, email subject),
  `LicenseAlarmPushNotifier`, `LicenseReadModel.Read`, `LicenseStore.HasMachines`, `LicenseRenewalEmailService.TickEveryTenantAsync`,
  `DirectorHub.ReportLicenseApproverChecks`, `HostedTenantBoundary.EnterScope`, `AsyncLocalTenantContext.Enter`,
  `TenantPass.ForEachTenantAsync`, `GatewayHost.RunLicenseRenewalEmailCheckAsync` and the lifetime cancel in stop,
  `LicenseSignInEndpoints` (the approver routes), `LicenseSignInHostedTests` (set-up, the report calls, the minute-check cases),
  contract section 9, and the Track C design.
- The Track C design document and the review brief.

Ran: nothing in the worktree. No build, no tests. One PowerShell arithmetic script of my own in my session (sentence
lengths, below). Could not reach: the running hosted Gateway; nothing in this review depends on it.

What I checked against the brief and found SOUND (no finding):

- One mechanism. It raises through `GatewayHost.LicenseAlarms.RaiseAsync(tenant, Kind, conditions)` with a constant kind
  declared beside the check, passes every approver of the tenant that cannot approve on every raise, and an empty list
  closes. It never touches another kind. Section 9.2 followed.
- Wording (section 9.3). The sentences are `LicenseApproverReadiness.Verdict`'s: every one ends in a full stop, names
  the account, the machine (the read model's label) and the profile's display name, and gives one action. The title
  "Approver cannot approve sign-ins" has no full stop and no email address. ASCII throughout.
- "Can approve" is a positive reading. `LicenseApproverHealth.StateOf` answers `not-checked` for a null state, a null
  check time, or a check older than 26 hours; `Verdict` gives every state but `can-approve` a `WhatToDo`, so an unchecked
  or stale record alarms. A Gateway that has never heard a check alarms after the first minute tick.
- A Director that stops reporting entirely: the minute tick (`TickEveryTenantAsync`) re-evaluates with the Gateway's own
  clock, so the record goes `not-checked` at 26 hours and alarms with no Director involved. Test
  `AHealthyApproverThatNobodyChecks_AlarmsOnceItsCheckIsOver26HoursOld` proves it over the real alarm service.
- Tenancy. `ConditionsNow` reads the one tenant's approvers and machines; `TickEveryTenantAsync` raises only inside
  `ForEachTenantAsync` with `tenants.Current` (no scope, no raise), the same shape as Track A's tick; `RaiseSoon` enters
  the tenant's scope itself. `AsyncLocalTenantContext.Enter` nests (it restores the previous value on dispose), so
  entering it again on the task started from inside the hub's own scope is harmless. Test
  `OneTenantsRaise_NeverOpensOrClosesAnotherTenantsAlarm` holds the partition.
- The hub never waits. `Report` calls `_afterReport` after the store write; `RaiseSoon` starts a `Task.Run` and returns.
  A failure is caught inside the task and logged (`RaiseSoon FAILED`), never thrown into the hub; cancellation by the
  Gateway's lifetime is logged as a stop. The tick logs one tenant's failure and goes on with the next.
- No alarm storm. An account with no approver raises nothing (design item 4 and contract 10.6 say so; Track A's readiness
  carries "No machine is set to approve sign-ins for X" through `WhyNotReady`). Approver records exist only where the
  owner set one or a Director with license switching on registered one.
- Tests use the real `LicenseAlarmService` on a throwaway SQLite file, the real `LicenseApproverStore` and the real
  readiness; conditions are never hand-built. Records are written through the store (its internal `Change`) in most
  tests, and through a real `LicenseApproverRegistration.Report` in one.
- Public repository: `account-a@example.com`, `machine-a`, `machine-z`, `dir-a`, `acct-one`. Nothing real.

## Findings

### 1. The minute tick writes two log lines per tenant per minute when there is nothing to say - MEDIUM

Files: `src/CcDirector.Gateway/Licenses/LicenseApproverAlarm.cs` line 79 (`RaiseAsync` logs on every call) and
`src/CcDirector.Gateway/Licenses/LicenseApproverStore.cs` line 129 (`List` logs on every call, now called from
`ConditionsNow` every minute).

What happens: `GatewayHost.RunLicenseRenewalEmailCheckAsync` runs `LicenseApproverAlarm.TickEveryTenantAsync` once a
minute for every tenant with a Director connected. For each, `ConditionsNow` calls `_approvers.List(tenant)`, which
writes `[LicenseApproverStore] List: tenant=..., count=0`, and `RaiseAsync` then writes
`[LicenseApproverAlarm] RaiseAsync: tenant=..., cannotApprove=0, opened=0, stillOpen=0, closed=0`. That is 2 lines a
minute, 2,880 a day, for every tenant, forever, whether or not the tenant has a single approver record.

Why it must change: this mission already ruled that a once-a-minute per-tenant read logs nothing when it finds nothing.
`LicenseReadModel.Read` (line 119: "QUIET ON PURPOSE ... by the same ruling (the Tech Lead, on the review of pull request
3936, finding 9)"), `LicenseAlarmService.SendDueEmailAsync` (line 227: "nothing owed: nothing written, nothing logged"),
`LicenseRenewalEmailService.TickAsync` (line 139), `LicenseSwitchService` (line 205) and `LicenseStore.HasMachines`
(line 825: "an account with no machines must leave no trace of the check") all follow it. This pull request puts the
noisiest line of the whole minute check right beside them. Track A's own `RaiseAsync` logs only when
`opened + closed > 0`. On the hosted Gateway the log is one file for every tenant.

Fix: in `RaiseAsync` log only when `conditions.Count > 0 || raised.Opened + raised.Closed > 0` (the alarm service already
logs the open and close), and read the approvers through a quiet path for the tick - either a `List` that does not log,
or move the store's line to the callers that are not once a minute. No test change needed.

### 2. One approver with long names makes every approver alarm of the tenant throw, every minute - LOW to MEDIUM

File: `src/CcDirector.Gateway/Licenses/LicenseApproverAlarm.cs` lines 66-73 (`ConditionsNow` builds a condition from
`verdict.HealthText` unbounded) against `LicenseAlarmService.Checked`/`Sentence` (400-character limit, refused with
`ArgumentException`).

What happens: the alarm service checks EVERY condition (`trueNow.Select(Checked).ToList()`) before it takes the gate and
before `Reconcile`. One sentence over 400 characters throws, and the whole raise for that tenant does nothing: no approver
alarm opens, none closes, and the FAILED line repeats every minute (`TickEveryTenantAsync: the raise FAILED ... ArgumentException`)
and after every check report (`RaiseSoon FAILED`). The approver in the worst state is the one that goes silent, and so do
all the others of that tenant.

The lengths (the readiness sentences' fixed text plus the stored fields, which the stores allow: email 320, profile name
64, machine name 128 and user 128 (a two-machine label is "name (user)"), detail 200, shown account 320):

| sentence | fixed text | email 40, profile 20, label 20, detail 200 | worst legal |
|---|---|---|---|
| check-failed | 66 | 346 | 909 |
| wrong-account | 82 | 242 | 1365 |
| needs-fresh-sign-in | 96 | 176 | 739 |
| not-checked (dated) | 105 | 185 | 748 |

With a 200-character detail (the contract's maximum for a Director's fixed-words sentence), `check-failed` passes 400 at
an email of 95 characters, and with a two-machine label of 45 and a profile name of 30 it is at 381 for a 40-character
email. `wrong-account` passes 400 at an email of 93 characters when the shown account is as long. Unusual, but every
value is one the stores accept today, and the harm when it happens is the silence of a whole tenant's approver alarms.

Fix (the author's call, one of): bound the readiness sentence by construction - cap the detail so `HealthText` can never
exceed `LicenseAlarmService.MaxSentenceLength` (the email is at most 320 by `IsAccountEmail`; the detail is the only
free-text part); or raise Track A's limit with the Tech Lead; and in either case make one condition that is not well
formed cost that one alarm (logged, with the account) and not the tenant's raise. A test: an approver whose detail is
200 characters and whose email is 100 characters still raises and still opens.

### 3. The alarm's identity flips when the approver's machine first reports: a second push, a second email, a false "closed" row - LOW

File: `src/CcDirector.Gateway/Licenses/LicenseApproverAlarm.cs` lines 68-72 (the condition's `MachineId` is the read
model's row when the machine has reported, else null).

What happens: the store lets the owner set an approver for a machine that has never reported ("The machine need not have
reported yet: the owner may set it first", `LicenseApproverStore.Set`). Minute one: the alarm opens "about no one machine"
(identity tenant + kind + empty machine + account), one push, one email: "... has never been checked." Then that machine's
Director starts and pushes its first license report, before its first approver check. Next minute: the condition now
carries the machine's id, which is a DIFFERENT identity in `Reconcile`, so the old alarm closes with the log row
"Alarm closed, because this is no longer true: The approver for ... has never been checked." (it is still true) and a new
alarm opens with the same sentence: a second push and a second owner email for one unchanged problem. The reverse flip
happens if a machine row ever leaves the read model.

Fix: key the approver alarm on the account alone always (`MachineId` null; the sentences already name the machine, and
there is one approver per account), or carry the machine only once it has reported AND accept and document the one
duplicate. The first is simpler and makes test `AnApproverOnAMachineThatHasNeverReported_...` the only shape.

### 4. Conditions are read outside the alarm service's per-tenant gate: a hub raise and a tick raise can reopen an alarm that was just fixed - LOW

File: `src/CcDirector.Gateway/Licenses/LicenseApproverAlarm.cs` lines 77-83 (`RaiseAsync`: `ConditionsNow` then
`_alarms.RaiseAsync`).

What happens: `LicenseAlarmService.RaiseAsync` serialises `Reconcile` per tenant, but the conditions were computed before
the gate. Sequence: the tick reads the records (signed-out); a check report lands and the record becomes can-approve; the
report's `RaiseSoon` reads (nothing wrong), takes the gate first, and closes the alarm; the tick's raise then takes the gate
with its stale list and opens a NEW alarm - one push and one owner email saying the approver is signed out when it is not.
The next minute closes it again with another "closed" log row. The window is one `Reconcile` (a database round trip); the
tick and the Director's daily report are independent, so it is rare but it will happen on a long-running Gateway.

Fix: one `SemaphoreSlim` per tenant in `LicenseApproverAlarm` around `ConditionsNow` + `_alarms.RaiseAsync`, so every raise
reads the records after the previous raise wrote. A few lines; no behaviour change otherwise.

## Notes (not findings)

- Hosted proof gap. No hosted test asserts an approver alarm after a real `ReportLicenseApproverChecks` over the tunnel or
  a real `RunLicenseSwitchCheckAsync` on a hosted Gateway (the `c1-alarm.md` line about `LicenseSignInHostedTests` says only
  that they still pass). `RaiseSoon` swallows failures into the log, so a hosted-only failure would be invisible to the
  suite. I traced the path (scope nesting, explicit-tenant stores, the alarm service's own contexts) and found no defect;
  the unit test's `_ => new NoScope()` is the only scope ever tested. One hosted assertion after `AliceSetsHerApprover`
  with a signed-out report would close the gap.
- The minute pass walks `TenantPass.KnownTenants`, the tenants with a Director bound to the tunnel. A tenant whose every
  Director is offline raises nothing until one reconnects - the same limit as every license tick, and the approving
  machine alone being offline is covered as long as any Director of the tenant is connected.
- No automation-allowed tenant guard exists on the approver routes on this branch (searched). An owner of any tenant can
  set an approver and will then get a true "has never been checked" alarm each minute (idempotent: one push, one email).
  Truthful, and not this pull request's doing.
- The `Opened`/`StillOpen`/`Closed` counts in the log line of finding 1 are the alarm service's; if the line is kept for
  the non-empty case it duplicates `[LicenseAlarmService] RaiseAsync`, which already logs opens and closes.

REVIEW DONE
