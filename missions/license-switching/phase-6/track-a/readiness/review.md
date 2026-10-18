# Review of A3 - readiness on every minute, fixed at once, the 80% alarm

Pull request 4151, branch `license-switching-a3-readiness`, one commit 869791fd0 on top of origin/main.
Reviewer: a separate review session. Nothing in the worktree was changed.

## Scope

What I read, all from `origin/license-switching-a3-readiness` (the worktree HEAD is the same commit):

- The whole diff against origin/main: 29 files, 1720 lines added.
- The new code in full: `Licenses/LicenseReadinessFold.cs`, `Licenses/LicenseReadinessService.cs`, the changes to
  `LicenseSignInFold.cs`, `LicenseSignInLog.cs`, `LicenseSignInService.cs`, `LicenseEndpoints.cs`, `GatewayHost.cs`,
  the contracts (`LicenseCockpitDtos.cs`, `LicenseMessages.cs`), client-core, the Cockpit view and its web test, the
  fixtures, the overview example producer.
- What the new code leans on, as it stands on the branch: `LicenseDecisionEngine` (`NextTarget`, `Readiness`,
  `NotReadyWhy`, `IsReadingStale`, `IsTired`), `LicenseAlarmService.RaiseAsync` and its condition checks,
  `LicenseApproverReadiness.WhyNotReady`, `LicenseSignInService.Start` and its refusals, `LicenseSignInLog.TryFinish`
  (a sign-in row ends only as added, renewed or failed), `LicenseSnapshotSource.Build` (every overview machine is a
  snapshot machine, keyed the same way the store keys them), `LicenseStore.HasMachines`, the host's minute pass,
  `IsDirectorConnected`, and the Director side (`LicenseSignInCoordinator` only logs the reason word).
- The tests: `LicenseReadinessServiceTests.cs` (21 tests) and `LicenseReadinessTestKit.cs` in full, the three touched
  test files, the Cockpit web test.
- The record: `phase-6/track-a/readiness/README.md`, `layout_check.py`, the MISSION.md, contract.md and cockpit-api.md
  changes, MISSION.md section 5 on origin/main, the commit message and the pull request body.

What I ran: `dotnet test` on Gateway.UnitTests filtered to `LicenseReadinessServiceTests` in the foreground, TEMP on D:.
Result: 21 passed, 0 failed, 16 seconds. I did not run the gate, the parked gate or the web tests (the pull request body
reports them green; the Tech Lead's own run is the gate of record).

What I could not reach: the screenshots were not opened; the claim that `fixtures.test.ts` is red on origin/main
before this change was not re-checked here. Neither affects the findings.

## The specific checks the brief asked for

- **Readiness and the next target come from the one definition merged in #4105, computed once.** Yes.
  `LicenseReadinessFold.Judge` calls `LicenseDecisionEngine.NextTarget` once per account in use and
  `LicenseDecisionEngine.Readiness` once per machine, and takes the clause from `NotReadyWhy`. Nothing in the new code
  looks at spares, read errors or expiries itself. The 80% test (`AtAlarm`) is new logic but it is the alarm line, not
  readiness, and it reads a pre-reset number the same way `IsTired` does.
- **The approver reason comes from Track C's `LicenseApproverReadiness.WhyNotReady` only.** Yes. The service takes it
  as a function; `GatewayHost` passes `LicenseApproverReadiness.WhyNotReady`; the fold never composes an approver
  sentence of its own, and the alarm's action for that case is the sentence verbatim. It is asked once per account and
  only for an account some machine is not ready for.
- **Alarms are raised through `LicenseAlarmService.RaiseAsync` only.** Yes, one call, in `TickAsync`, with every
  condition true now, so the service's own reconcile opens, keeps and closes them.
- **At most one sign-in per machine and account at a time, and the back-off really waits.** Yes. `Start` refuses
  `sign_in_running` under its lock from every open row, and the check relies on that (tested, including a person's own
  click meeting the same refusal). The fold judges `RetryLater` until `EndedAt(last) + RetryWait(count)` has passed;
  the schedule test checks one second before and at each wait, with a new service each time, so the wait is read from
  the log and survives a restart. The service also skips a tenant whose previous check is still running.
- **Automatic sign-in and the alarm only for tenants with automation on.** Yes, by the same gate the switch check uses
  (`HasMachines`): a tenant none of whose machines has reported gets nothing read, started or raised (tested). Not
  widened.
- **Cross-tenant.** Every read and write is keyed by the tenant: the log, the approver check, the connection check,
  the alarm, the guard dictionary. The per-tenant check is started inside the tenant pass's scope, the same shape as
  the alarm and sign-in services.
- **Rule 7.** The Cockpit draws `readinessText` in `readinessTone` and branches on nothing (one note below).
- **Real names and attribution.** None in the diff, the commit message or the pull request body. The only hits for
  "Claude" are "Claude account" (the product) and the file name CLAUDE.md.
- **Words the owner sees.** Every readiness sentence is pinned by a test and listed in cockpit-api.md. Two of the
  alarm's sentences are where the findings are.

## Findings

### 1. The alarm tells the owner "DevThrottle is signing it in" when the sign-in has failed and nothing is running - should-fix

`src/CcDirector.Gateway/Licenses/LicenseReadinessFold.cs:230` (the problem sentence) and `:246` (the default action).

The alarm's problem sentence carries only the readiness clause ("it holds no sign-in for account-b@example.com"), and
its action is "Nothing to do yet: DevThrottle is signing it in; if this stays, sign machine-a in to ... from the
Licenses page." for every fix except approver-not-ready and Director-offline - so also for `RetryLater` and
`AwaitingReport`.

The harm: the account is at 85% and machine-a's sign-in has failed five times, say with `browser-not-signed-in` or
`approval-failed`, something only the owner can put right. The email, the red banner on every page and the phone push
all say nothing is to be done and that a sign-in is under way, while nothing is running and the next try is up to an
hour away. The Director's reason and the retry time are on the Licenses line (`Text`), but the alarm is the thing the
owner reads first, and it says a false state. The brief's item 4 says the alarm names "the reason", and its item 2
asks for the latest attempt's result in plain words; the "otherwise" wording in the brief was an example for the case
where a sign-in is in fact under way.

Suggested shape, one case each in `AlarmAction` (and the problem sentence can carry the attempt part of `Text`):
- `RetryLater`: "The last sign-in failed at 10:42 UTC (the Director's reason); DevThrottle tries again at 11:42 UTC.
  If this stays, sign machine-a in to account-b@example.com from the Licenses page."
- `AwaitingReport`: "It was signed in at 10:42 UTC and its next report should show it; if this stays, sign machine-a
  in to account-b@example.com from the Licenses page."
- `SigningIn` and `StartNow`: as now.

Since an open alarm's two sentences are brought up to date on every check, this costs no extra email or push. Pin
each in a test as the existing action test does.

### 2. An open alarm is closed, with no word, the moment the account's reading goes stale - should-fix

`src/CcDirector.Gateway/Licenses/LicenseReadinessFold.cs:226`.

A condition is only produced for a fresh reading, so an alarm that is open closes by itself when the reading of the
account in use goes stale (tested: `Tick_At80OnAStaleReading_RaisesNoAlarm`, and the README and contract 9.6 say so).

The harm: the banner vanishing is, by the alarm's own design, the signal that the problem is fixed. A reading goes
stale after six minutes when the machines on the account stop sending usable readings (a usage read failing, a
Director that dropped) while the machine is still not ready and the account was at 85% a moment ago. The owner sees
the alarm clear and reads it as fixed; it reopens, with a second email and push, when a fresh reading comes. The
brief's "fresh reading" is about judging 80% to OPEN an alarm; closing on a stale reading is an extension, and the
only closes the brief asks for are "the machine becomes ready" and "the account drops back under 80%".

Suggested: open only on a fresh reading at or above 80%; keep an open alarm open while the reading is stale; close only
on a fresh reading under 80%, on the machine becoming ready, or on the machine leaving the account. That needs the
fold to know which alarms are open (pass `LicenseAlarmService.ListOpen` rows of this kind into `AlarmConditions`), or
a word from the Tech Lead that the documented behaviour is wanted as it is. If it stays, the record should say in one
line that a banner can clear because the number is no longer known, not because the machine is ready.

### 3. The fold's "a sign-in is running" looks back one day; `Start`'s refusal looks at every open row - note

`src/CcDirector.Gateway/Licenses/LicenseReadinessFold.cs:187` against `LicenseSignInService.Start`'s `ListOpen`.

If an open (`pending` or `unknown`) row were older than `RowsReadFor` (one day), the fold would judge `StartNow`, the
page would say "DevThrottle is starting a sign-in ... now" every poll, and `Start` would refuse `sign_in_running`
every minute, each with a log line. I could not find a way to get there: a row is only written while the Director is
connected, and the settle pass ends a row whose machine has not been seen for a day, which comes before the row is a
day old. So no harm today, but the two reads of "is one running" should be the same read. Simplest: have the fold take
the open rows from `ListOpen` (no time bound) for the running check and the day's rows for the retry count.

### 4. Only failures feed the back-off; a sign-in that ends well but never shows in the report repeats every ten minutes without limit - note

`src/CcDirector.Gateway/Licenses/LicenseReadinessFold.cs:197`.

After an `added` or `renewed` row the fold waits ten minutes for the report, then judges `StartNow` again. If the
report never shows the sign-in (a Director that writes the spare where its report does not read it, or a renewal whose
new sign-in still carries the old refused read error until the next usage read), the check starts a real sign-in,
with a real approval in the approver's browser, every ten minutes for as long as it lasts, and the alarm's words never
change. Every such row is in the Licenses log, so it is visible, and it would be a Director defect, not this code's.
Worth a ceiling (count `added`/`renewed` rows in the last hour that were not followed by readiness and fall back to
the hourly wait), or a line in the record that this is accepted.

### 5. The alarm's problem sentence reads oddly for the refused and expiring clauses - note

`src/CcDirector.Gateway/Licenses/LicenseReadinessFold.cs:230`.

"... it holds a sign-in for account-b@example.com that expires at 2026-10-16 03:05 UTC, before a switch could finish,
the account it would switch to next." The apposition "the account it would switch to next" lands after "before a
switch could finish" and no longer attaches to the account. Only the no-sign-in form is tested. Suggested: "... which
is at 85% of its weekly limit: account-b@example.com is the account it would switch to next, and it {clause}."

### 6. The Cockpit picks a tone of its own for a case the contract says cannot happen - note

`apps/cockpit/src/licenses/LicensesView.tsx:531`: `toneClass(machine.readinessTone ?? "neutral")`.

The line is drawn only when `readinessText` is not null, and the contract says the tone is null exactly when the text
is, so the `"neutral"` never fires. It is still a client choosing a meaning. Narrow the type instead (draw the line
when both are present), or leave it; no harm today.

### 7. Two documented sentences and one claim have no test - note

- The readiness sentence for a refused sign-in ("holds a sign-in for account-b@example.com that was refused") and
  that a refused spare leads to `auto-renewal`: cockpit-api.md lists the clause, no test pins it through the fold.
- The README says the approver check is asked only for an account some machine is not ready for; no test holds that
  (a counting approver function in one test would).

Both small; the sentences come from the one definition's own words, which A2 tests.

## Things I looked for and did not find

- A test that would pass with the code reverted: none. The Gateway tests go through the real store, log, sign-in
  service and alarm; the web test puts different sentences and tones on two rows and reads each on its own row.
- A second readiness or approver computation, a second alarm mechanism, a fallback hiding a failure: none. A refused
  `Start` is logged with its refusal word and looked at again next minute, which is the right shape.
- A wrong rule against MISSION.md section 5: readiness is judged against "the account it would switch to next", which
  section 5 defines as the best account with room; the README explains why a machine ready only for the second-best
  account is still a gap. Consistent with the owner's words.
- The departure from the brief for a Director that is not connected (looked at every minute, no row, instead of the
  retry back-off): documented in the README with a sound reason (an attempt while offline could only write a failed row
  and would delay the sign-in up to an hour after the Director returns). I accept it.
- The two new reason words reaching the Director: it logs the word and nothing more, so no Director change is needed,
  as the README says.
- `GatewayHost`: the readiness check runs after the switch check and the settling, inside the same guarded pass, so it
  reads the rows they just ended; one tenant's failure is caught per tenant.

## Verdict

Mergeable once findings 1 and 2 are answered: fixed, or declined with the reason in the README (finding 2 is a
documented design choice the Tech Lead may keep; finding 1 is words the owner reads that say a false state at the one
moment the alarm matters most, and I would fix it before this goes live). Nothing here must-fix. The rest are notes.
The checks the brief named all hold: one definition of ready, Track C's one approver check, the one alarm mechanism,
one sign-in per machine and account with a back-off that waits and survives a restart, and automation only where it
is switched on.
