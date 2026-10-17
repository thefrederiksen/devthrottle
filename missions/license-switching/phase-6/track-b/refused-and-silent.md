# Phase 6, Track B - the Gateway never goes silent about a machine

**Why.** On 16 October one machine's Director sent a usage report every two minutes for hours, and the Gateway threw
every one away as malformed ("an account held twice"), writing that only into its own log file. The Licenses page
said "Not reporting", so nobody could tell the machine was alive and being refused, or why. Another machine whose
Director had died showed "Not reporting" for ten hours with no time on it.

**What this piece does.** A refused report is now a machine status with the reason in plain English, and a silent
machine says since when. The Licenses log gets one row for each change, never one for each report.

## What was built

### A refused report is a visible machine status

- **Every refusal for what a report says has its own code and its own sentence.** `LicenseUsageIngest.Malformed` and
  `UnreadableReading` now return a `LicenseReportRefusal` (a fixed word and a plain-English clause) in place of a bare
  string. There are 14 reasons (`LicenseReportRefusalCodes.All`). One was added: a report whose operating system user
  is null. Before, that report threw inside the shape check, the hub caught the exception, and the report vanished.
  That was one of the silent drops the brief asked about.
- **The page sentence** is the one the brief gives: "Its report was refused: it holds alice@example.com twice (once in
  use and once as a spare). It is not switched until a report is accepted." After it comes what shows the Director is
  alive: "Its Director is still sending reports: 412 reports refused since 2026-10-16 01:02 UTC, the latest 1 minute
  ago." The code word never appears on the page.
- **Where it is kept: the Licenses log, with no new table.** A refusal is an open row of a new kind,
  `report-refused`, keyed by the Director (the one thing a malformed report cannot get wrong). The first refused
  report writes the row. The same refusal again only increments `SendCount` and `LastSentAtUtc` on that row. A
  different reason ends the row and opens the next one. An accepted report ends it and writes one `report-accepted`
  row: "Its reports are accepted again, after 2 reports refused since ...". All of this is in `LicenseStore`
  (`RecordRefusal`, `RecordAccepted`, `ListOpenMachineHealth`), beside the no-room events, which work the same way.
  No migration is needed.
- **The status is folded on the Gateway** (`LicenseDisplayFold.Machine`): the new `report-refused` status ("Report
  refused", tone bad) comes before "not reporting", because the refusal is why the facts are old. The sentence goes
  in the new `statusDetail` field of the machine. The Cockpit draws it as given (one line in the schedule) and decides
  nothing.
- **A refused machine is never sent a switch.** `LicenseMachineState.ReportsRefused` is set from the open refusal.
  The engine skips that machine before it checks staleness, with the clause "its reports are being refused, so what it
  holds now is not known". The skip appears in the Switch now sentence and in the no-room email, like every other
  skip.
- **A machine that has never had a report accepted, but is refused, still appears on the page.** It has no machine
  row, so the fold builds one from its open refusal (`LicenseDisplayFold.RefusedMachine`): it holds nothing,
  `lastReportAtUtc` is null, `lastReportText` is "No report from it has been accepted yet", and Add a spare is not
  offered ("A spare can be added to machine-a once a report from it is accepted."). It is named the way its report
  named it, or "The machine of Director {id}" when the report named no machine that can be stored. Its row identifier
  is a stable hash of the Director id, so the page keeps one row for it. The overview is not "empty" while such a
  machine exists.
- **Shape is now judged only after the connection is known to own the Director.** Before this change, a malformed
  report was refused before the ownership check. Moving the check after it means a superseded connection can never
  mark a machine refused. Refusals for connection reasons (`NotActiveConnection`, `StaleSequence`) are bookkeeping.
  They are logged and dropped as before, and they write nothing.

### Not reporting since a time, and one row when a machine goes silent

- **The status names the last accepted report.** Past the stale limit (6 minutes, unchanged), the status is "Not
  reporting since 2026-10-11 03:12 UTC". That is the clock style the page already uses for holds. The detail reads
  "No report has come from it for 2 hours 5 minutes. It is not switched until it reports again."
- **The dead-Director limit is 10 minutes** (`LicenseStore.SilentAfter`), which is five report intervals of two
  minutes. It is longer than the 6-minute stale limit, so a Director that is only restarting writes nothing.
- **The detection runs in the existing minute check.** `LicenseSwitchService.PlanTick` calls `WatchSilence` first;
  no new timer was added. The first check past the limit writes one `machine-silent` row: "No report has come from
  it since 2026-10-11 03:12 UTC, for more than 10 minutes: its Director may have stopped. It is not switched until it
  reports again." The first check after a report from that machine is accepted ends the row and writes one
  `reporting-again` row: "It is reporting again: a report was accepted at 03:53 UTC, 41 minutes after the one before
  it." The status clears as soon as the report is accepted, because it is folded from the last report time. The log
  row is ended by the next minute check, at most a minute later.
  Opening and closing happen in the one place that is serialised per account. The close condition is "a report
  accepted after the row was written", so a report that lands while the check is running cannot leave a silence row
  open by mistake.
- **A machine whose reports are refused is never also called silent.** It is alive, and its refusal row is already
  in the log, so there is still one row per change.

### Where the license alarm goes (step 3 - no alarm is built here)

When Track A's alarm is on main, it is one call at each of these two places, and nothing else changes:

1. `LicenseUsageIngest.Ingest`, right after `_store.RecordRefusal(...)`: raise it when the returned
   `LicenseRefusalChange` is `First` or `ReasonChanged`, never `Same`. The line carries the comment "The license alarm,
   once it exists, is raised here when the change is not Same."
2. `LicenseSwitchService.WatchSilence`, where `_store.OpenSilence(...)` returns a row (not null). The line carries the
   comment "The license alarm, once it exists, is raised here when a row was written."

The acceptance rows (`RecordAccepted` returning true, `CloseSilence` returning true) are where the alarm would clear,
if it has a clear.

## Every other place a report can be refused or dropped

| Where | What happens now | Why it is visible, or cannot happen for a healthy machine |
|---|---|---|
| `Malformed` / `UnreadableReading` | Recorded as a refusal (above). | Visible. |
| Shape check threw on a null user (the old code) | Now a refusal, `no-os-user`. | Visible. It was silent before. |
| `NotActiveConnection`, `StaleSequence` | Logged and dropped, as before. | Bookkeeping: a reconnect or a resend. The owning connection's next report is judged normally. |
| No connection registered for the Director (`Ingest`, before the gate) | Logged and dropped. | A Director says Hello before it reports, and Hello registers it. A report before Hello is refused by the hub itself (`RequireBoundDirector`). |
| `DirectorHub.PushLicenseUsage` with no ingest on this Gateway | Logged and dropped. | A Gateway configuration, not a machine: the hosted Gateway always builds the ingest (`GatewayHost`). |
| An exception inside `ApplyReport` (or `RecordRefusal` / `RecordAccepted`), caught and logged by the hub | Logged, contained. | Everything that comes from the report is bounded before the write (names up to 128 characters, emails up to 320, plan 64, tier 128, trigger 32, read errors 32 and 500, the switch id's shape), so a report that passes the shape check fits every column. What is left is the database itself failing, which is not about one machine and cannot be written down in the database that is failing. A Director id longer than its 128-character column cannot happen either: every other table limits a Director id to 64. Two Directors sending the FIRST report for the same machine and user at the same moment can collide on the machine's unique index once; the next report finds the row and succeeds. |
| A report that SignalR cannot bind to `LicenseUsageReport` | Never reaches the method. | The Director's `InvokeAsync` throws, and its reporter loop logs it and sends again at the next report (`GatewayStreamClient.PushLicenseUsageAsync`). It is visible on the Director, and only a Director built against a different contract can send one. |

## The tests, each in one sentence

All are in `src/CcDirector.Gateway.UnitTests/Licenses/LicenseRefusedAndSilentMachineTests.cs`, unless named. They run
real reports through the real ingest into the real store, on a throwaway SQLite file. The page is folded from the
real snapshot source, and the minute check is run by the real switch service on the test's own clock.

- `ARefusedReport_ShowsTheMachineRefused_WithItsOwnSentence_AndWritesOneLogRow` (14 cases, one for each reason): an
  accepted machine sends a report broken in that one way, and the page shows "Report refused" with exactly that
  reason's sentence and no code word, and the log has exactly one refusal row carrying that code.
- `EveryRefusalReason_IsCovered_AndEachHasASentenceOfItsOwn`: the cases above are exactly the codes in
  `LicenseReportRefusalCodes.All`, no two share a sentence, and the shape check returns that code and sentence for each.
- `TheSameRefusalAgainAndAgain_WritesNoFurtherRow_AndCountsTheReports`: thirty identical refused reports, two minutes
  apart, leave one log row whose count is 30 and whose latest time is the 30th report, and the page says "30 reports
  refused".
- `ADifferentRefusal_EndsTheFirstRow_AndWritesExactlyOneMore`: a change of reason ends the first row and opens one
  more, and repeating the new reason adds nothing further.
- `ARefusedMachine_IsNotSentASwitch_AndTheSkipSaysWhy`: a machine at its limit that the engine would switch is no
  longer switched once its report is refused, and the skip and the Switch now sentence both say "its reports are being
  refused".
- `TheMinuteCheck_SendsARefusedMachineNoSwitch`: the real minute check sends no command and writes no switch row for
  that machine.
- `AnAcceptedReportAfterRefusals_ClearsTheStatus_AndWritesOneAcceptedAgainRow`: two accepted reports after refusals
  bring the machine back to "Reporting", end the refusal row, and write exactly one "Its reports are accepted again"
  row.
- `AMachineNeverAccepted_ButRefused_StillAppearsOnThePage`: a Director with no accepted report ever is listed with the
  refusal, no last report time, no holdings and a stable identifier, and the sign-in fold over the same overview does
  not fail on it and offers no Add a spare.
- `AMachineNeverAccepted_WhoseReportNamesNoMachine_IsNamedByItsDirector`: when the report names no machine, the row is
  called "The machine of Director dir-1".
- `AMachineNeverAccepted_ThenAccepted_IsOneMachine_AndSaysItsReportsAreAccepted`: once its first report is accepted,
  the page shows one machine, not the stand-in as well, and one acceptance row.
- `ConnectionBookkeepingDrops_ChangeNothingOnThePage`: broken reports from a superseded connection, and with a sequence
  already sent, leave the machine "Reporting" with no refusal anywhere.
- `AMachineSilentPastTheStaleLimit_SaysSinceWhen` (two cases): at exactly 6 minutes the machine is still "Reporting",
  and one second later it is "Not reporting since 2026-10-11 03:12 UTC" with its sentence.
- `AMachineSilentPastTheDeadLimit_WritesOneRow_AndAReportEndsIt_WithOneMore`: at exactly 10 minutes the minute check
  writes nothing; one second past, it writes one silence row, and three more checks add nothing; a report clears the
  status at once, and the next two checks end the row and write exactly one "reporting again" row.
- `ARefusedMachine_IsNotAlsoCalledSilent`: a machine refused for 20 minutes gets no silence row from the minute check.
- `OneAccountsRefusedMachine_NeverAppearsOnAnotherAccountsPage`: a refused machine of one account is not on another
  account's page or in its log, and that other account's own accepted report neither ends the first account's refusal
  nor writes an acceptance row of its own.
- Updated: `LicenseDisplayFoldTests.Machine_IsNotReporting_OneSecondPastSixMinutesWithoutAReport` now expects the new
  status text, and `LicenseEndpointsTests.Overview_AnswersTheExactShapeTheLicensesPageIsBuiltFrom` the new
  `statusDetail` field. Both changes are the ones this piece intends.
- Cockpit, `LicensesViewRows.test.tsx`: "draws a refused machine's status and its sentence exactly as the Gateway gave
  them, with no last report time" - the schedule shows the Gateway's sentence word for word in that machine's row
  only, with a null `lastReportAtUtc`.
- The API document `missions/license-switching/cockpit-api.md` lists the new status, field and log kinds. Its overview
  example was produced again from the real route by `produce_overview_example.py`, with 0 differences. The Cockpit
  fixtures were copied again from it by `copy_fixtures.py`.

## The gate

Run on this branch, on 17 October 2026, on top of origin/main `08dec1140`:

- License unit tests alone (`FullyQualifiedName~CcDirector.Gateway.Tests.Licenses`): 857 executed, all passed after
  the two updated tests.
- `.\scripts\test-local.ps1` (default): every suite that ran passed (Core.UnitTests 2127, Engine 85, HostedAgent
  85, Launcher 214, Terminal.Avalonia 71, Reclaim 305, DeliveryQualification 17, the three installer suites 39 / 884 /
  74). `CcDirector.Avalonia.Tests` was stopped at the 120-second ceiling while the machine was busy with another
  session's parked run ("OVER BUDGET ... This is NOT a test failure"). Run alone straight after, it passed:
  "Passed! - Failed: 0, Passed: 1063".
- `.\scripts\test-local.ps1 -Parked`: every suite Completed and green except `CcDirector.Gateway.UnitTests`, "Failed: 6,
  Passed: 13854, Total: 13860". The six are prompt-delivery tests, none of them about licenses:
  `PromptOnlyWhenWaitingForInputTests` (five) and
  `FleetManagerEventServiceTests.TheOwnerHasUnsentTextInTheFleetManager_NothingIsTyped_TheGatewaySaysWhy_ThenTheEventsGoAlone`.
  Gateway.Tests (four shards, 1764) and Core.Tests (4538) passed. **The six fail on origin/main as well, without
  this change:** run alone on this branch's commit, all six failed again. Run alone in a clean, detached checkout of
  origin/main `08dec1140`, the same filter gave "Failed: 6, Passed: 50, Total: 56". So they are broken on main
  already (prompt delivery, most likely the "look before typing" change, #4072) and are not caused by this piece.
  `-RerunFailed` could not be used: the parked run was on an uncommitted tree, and the script rightly refuses to
  re-run against a different commit.
- Cockpit: `npx vitest run src/licenses` 5 files, 82 tests passed (81 before, plus the new one); `tsc --noEmit` clean
  in `apps/cockpit` and `packages/client-core`; client-core licenses tests 27 passed.
- `produce_overview_example.py`: "0 differences, 0 fields added" - the document's overview example is the route's
  answer.

## What the live proof should look for

On the hosted Gateway, after deploy, with a Director made to send a malformed report (for example an isolated test
Director whose report holds one account twice):

1. The Licenses page lists that machine with "Report refused", the sentence naming the account and how it is held
   twice, and "Its Director is still sending reports: N reports refused ...", with N going up every two minutes.
2. The Licenses log has ONE "Report refused" row for it, however long it runs; its count rises.
3. Switch now on that machine's account says "is not switched: its reports are being refused".
4. When the Director sends a good report: the status is back to "Reporting" at once, the refusal row reads "Over",
   and exactly one "Reports accepted" row is written.
5. Stop a test Director: after 6 minutes the machine reads "Not reporting since <its last report time>"; within a
   minute after 10 minutes, ONE "Not reporting" log row; start it again, and within a minute one "Reporting again" row.
