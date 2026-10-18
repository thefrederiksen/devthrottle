# Review of pull request 4123 - License Switching Track C, the Director's daily approver check

Reviewer: session "License Switching - Reviewer - Track C 4123 daily approver check" (5c89cc24), 17 October 2026.

## Scope

**What I read.** Only git objects in `D:\ReposFred\devthrottle.worktrees\wt14`; the working tree was not touched.

- The pull request head `refs/review/4123` (698c4e994). It is NOT based on `refs/review/4116b` (25f43a449): neither
  is an ancestor of the other, and 4116b is not on origin/main. So I diffed 4123 against its merge base with
  origin/main, 36e514237 ("Track C: approver registration contract and rule (#4109)"). That diff is 18 files,
  1,309 lines added, 8 removed: the two commits 77a6bb521 and 698c4e994. Every file of it was read in full.
- From the base: `LicenseApproverMessages.cs` (the contract types), `LicenseRenewer.cs`, `LicenseCommandLine.cs`
  (`RunForJsonAsync`, `FirstObject`), `LicenseMessages.cs` (the timing constants), `BackgroundJobs.cs`,
  `LicenseApproverStore.cs` (the profile shape and the not-checked rule), `LicenseApprovalRelay.cs` (the approval
  wait), `AutomationBrowserRegistry`.
- From `refs/review/4116b`: `license_signin.py` (`check_approver`, `_check_once`, `_CheckResources.clean_up`,
  `director_browser`, `_run_harness`, the wait constants) and `license_signin_ops.check_approver` (exit codes).
- The design, the Developer's brief, hub contract section 10 on origin/main, and the proof file in the pull request.

**What I ran.** Nothing. No build, no tests, no tool. The findings below are from reading; where a finding rests on
timing I give the constants and the arithmetic so it can be checked.

**What I could not reach.** The tests were not executed by me; the proof file records a green default gate and a
parked gate with one failure outside this change. I take the proof's word for the gate and did not verify it.

## The brief's checklist, item by item

- **Runs only while switching is on and this Director is the key holder.** Yes. `LicenseApproverCheck.TickAsync`
  line 158 returns before anything else on `!_enabled || !_holder.IsHeld`; `LicenseSwitching.Start` schedules it only
  when `Enabled`. Discovery (own profiles, no email) only when the offer setting is on: `CheckAllAsync` lines 221-222.
  The setting is off unless config.json says exactly `true` (`LicenseApproverOfferSetting.ReadFrom`), tested.
- **Every check reported; a failed one as `check-failed`; the report sent even when every row failed; a non-JSON or
  crashed tool answer reads `check-failed`.** Yes. `CheckApproverAsync` catches `LicenseCommandLineException` and
  answers `NoAnswer` (state `check-failed`); `ReadApproverCheck` answers `NoAnswer` for no JSON, a state outside the
  contract, a `can-approve` naming nobody or someone else, or an answer for another account than asked. The tool's
  own `check-failed` exits 1 but `RunForJsonAsync` returns the output regardless of exit code, so that JSON is read
  too. Nothing in this path can read as `can-approve` without the tool saying so for the account asked. The report
  goes with every row failed and with zero rows. Tests cover each.
- **`detail` built only from the Director's constant sentences.** Yes. `LicenseApproverCheckSentences.For` is six
  fixed strings; the tool's `detail` field is never read (`ReadApproverCheck` reads only `state`, `accountEmail`,
  `shownAccount`). The test plants a marker in the fake tool's text and proves it is absent from the serialised report.
- **`browserProfile` the identifier, `browserProfileName` the display name.** For an assigned record the Director
  sends back the assignment's string as-is (so an old display-name record matches its own check row; the Python
  resolves either spelling). For discovery the identifier comes from the registry (`AutomationBrowser.Id`). The
  display name comes from the registry, not the tool, and is dropped when it fails the Gateway's shape. Acceptable.
- **Concurrency.** The chosen lane is the right lane in kind, but it is not finished: it answers "can a check delay
  or block a sign-in or an approval?" with YES in a way the contract forbids. Findings 1 and 2.
- **Profile stopped after; scheduler entry removed; logging.** The stop is the Python tool's (`_CheckResources.clean_up`
  runs `browser.stop` on every path, including an exception); the Director does nothing extra and needs to do
  nothing extra. `LicenseSwitching.Dispose` calls `ApproverCheck.Stop`. Logging is per method entry and failure, with
  the once-per-reason throttle the renewer uses. Finding 5 is a test gap here.
- **Tests exercise the real job and its real caller.** The tests build the real `LicenseSwitching` and call
  `TickAsync`, the scheduler's one production call, as `LicenseRenewerTests` do. Fakes stand in for the tool, the
  tunnel and the registry only. Good.
- **Public repository.** Only `example.com` addresses, `machine-a`, `owner`; no machine names, no addresses. No
  non-ASCII characters in the added lines. Two literal tab characters in the proof file (finding 4).

## Findings

### 1. An approval or a sign-in waits behind a running check with no deadline, and then runs anyway - HIGH

Files: `src/CcDirector.Core/Licenses/LicenseSignInCoordinator.cs` lines 325 and 331 (`LicenseApprover.ApproveAsync`),
lines 101-107 and 152 (`SignInAsync` and `RunAsync`); `src/CcDirector.Core/Licenses/LicenseBrowserLane.cs` lines
42-59; `src/CcDirector.Core/Licenses/LicenseCommandLine.SignIn.cs` line 232 (`ApproverCheckToolTimeout` = 8 minutes).

**What the code does.** `ApproveAsync` checks `request.NotAfterUtc` at line 325, THEN enters the lane at line 331 and
waits for however long the running check takes, THEN runs `license approve` (up to `ApproveToolTimeout`, 150 s).
Nothing re-checks the deadline after the wait, and the wait itself has no bound but the hub call's cancellation token.

**The numbers.** The Gateway's relay waits `ApproveCommandTimeout` = 3 minutes for the approving Director's answer
(`LicenseApprovalRelay.cs` 276-291: past it, "The Director on <machine> did not answer within 180 seconds" and the
sign-in fails). One check holds the lane for the whole of one `license check-approver` run: start the profile, a
browser step to read the signed-in account (up to `BROWSER_WAIT_SECONDS` = 150 s), `claude auth login` (up to
`URL_WAIT_SECONDS` = 60 s), the page step (150 + 30 s), `browser-harness --reload` (60 s), stop the profile; the
Director's own limit is 8 minutes. A page that classifies `check-failed` keeps the settle loop polling until its
150 s deadline (`_SETTLE_SCRIPT`: it returns early only on `can-approve` or a repeated non-failed state). So a check
that holds the lane for two to seven minutes is not an edge case; it is what a signed-out or broken profile costs.

**The harm.**
- An approval that arrives while a check runs waits past the relay's 3 minutes. The sign-in on the other machine
  fails as `approval-failed` with a sentence that blames the approving Director for not answering - a misleading
  failure the owner will read on the Licenses page. The renewal then tries again a day later; with `RenewAhead` =
  5 days, a machine whose daily check and the other machine's daily renewal happen to coincide burns one of its five
  attempts each time.
- Worse: when the lane finally opens, `ApproveAsync` still runs `license approve` and presses Authorize on a real
  claude.ai page for a sign-in that has already been abandoned (the target's `waiting` login was disposed at the
  deadline). `LicenseMessages.cs` line 483 states the rule this breaks: the approve tool's timeout is sized so that
  "an approval is never made for a Gateway that has stopped listening". The lane wait sits outside that budget.
- The sign-in side has the same shape. `SignInAsync` checks the deadline at line 101, then `RunAsync` waits in the
  lane at line 152, then starts a real `claude auth login` at line 175 before the `wait <= TimeSpan.Zero` check at
  line 188 can fail it as expired. And the Gateway's `SignInCommandTimeout` is 5 minutes: a Gateway-asked sign-in that
  waits more than that behind a check is recorded as a timeout by the Gateway and then succeeds on the Director -
  the case `LicenseMessages.cs` line 489 says must never happen.
- At Director start this is likely, not rare: the renewer and the check both tick at `TimeSpan.Zero`. The renewer
  runs `license renew --check` first (outside the lane), and if the tunnel is up by then the check takes the lane
  first and the renewal's sign-in waits behind it.

The lane tests prove the lane's ordering rules with instant fakes; none of them has a clock or a deadline, so none of
them can see this.

**Why it must change.** The brief asked exactly this question ("Can a check delay or block a sign-in or an
approval?") and the answer the code gives is "yes, for minutes, and then it acts after the deadline". A check is
disposable; an approval is not. Minimum fix: (a) in both `ApproveAsync` and `RunAsync`, bound the lane wait by the
request's deadline (a linked token that cancels at `NotAfterUtc`) and re-check the deadline AFTER entering the lane,
refusing `expired` instead of pressing anything; (b) finding 2, so a check cannot hold work across rows. The better
fix is for arriving work to cancel the running check and have the check re-run that row afterwards - but note that
the Director cancels the tool by ending its process, and a process ended that way does not run the Python `finally`
that cancels the throwaway login and stops the profile, so that route needs a graceful stop (close standard input,
or a stop message) before it is safe. Either way, the proof needs a test with a clock: an approval that arrives
during a check whose deadline passes while it waits is refused `expired` and runs no tool.

### 2. Between rows the check re-takes the lane before waiting work can, so one run can hold work across many checks - MEDIUM

Files: `src/CcDirector.Core/Licenses/LicenseApproverCheck.cs` lines 225-247 (the row loop; the lane is entered per
row at 233-234); `src/CcDirector.Core/Licenses/LicenseBrowserLane.cs` lines 63-98.

`Leave` (line 87) clears `_checking` and completes a `TaskCompletionSource` created with
`RunContinuationsAsynchronously`, so a waiting sign-in or approval is merely QUEUED to the thread pool. The check
thread continues synchronously - adds the row, writes a log line, loops - and calls `EnterCheckAsync` for the next
row, which takes the lane the moment it sees `!_checking && _working == 0`. The queued waiter then runs its loop,
finds `_checking` true again, and waits for the next row. There is no priority for work: nothing in `EnterCheckAsync`
asks whether a sign-in or an approval is waiting. The race is real (thread pool dispatch versus one log write) and
it is unfair in the check's favour; with up to 32 rows at minutes each, an approval can be held for far longer than
one check. The test `TickAsync_AnApprovalArrivesDuringACheck_WaitsForTheCheckToEnd` has one row and so cannot see
it. The design's sentence "a sign-in or approval that starts while a check is running waits for that one check to
end" is not what the code guarantees.

**Fix.** Count waiting work in the lane (`_waitingWork`, incremented around the wait in `EnterWorkAsync`) and have
`EnterCheckAsync` refuse while it is above zero; plus a test that holds two rows and proves the approval enters
between them, not after both.

### 3. The next run is keyed to when the report was TAKEN, not when the checks were MADE, so records can read not-checked for up to 21 hours - LOW to MEDIUM

File: `src/CcDirector.Core/Licenses/LicenseApproverCheck.cs` lines 172-179 and 206 (`_lastRunUtc = _utcNow()` after
the report is taken; rows carry `CheckedAtUtc` from line 243).

A report the Gateway could not take is resent for up to 24 hours (line 172). Suppose the checks ran at T and the
tunnel was down until T + 23 h: the report is taken then (rows 23 h old, inside the Gateway's 26 h window),
`_lastRunUtc` becomes T + 23 h, and the next checks run at T + 47 h. The Gateway's `NotCheckedAfter` is 26 hours
from `CheckedAtUtc` = T, so from T + 26 h to T + 47 h every record this machine checks reads `not-checked`, and Track
A's readiness says the approver is not ready although nothing is wrong with it - a false alarm for 21 hours, one
outage later. A smaller version happens on every run: `_lastRunUtc` is the END of a run, so a run of N checks pushes
the first row's next check to 24 h plus the run's length; a run over two hours (sixteen checks at eight minutes)
makes the first rows read `not-checked` daily for the overrun.

**Fix.** Set `_lastRunUtc` from the time the checks STARTED (or the oldest `CheckedAtUtc` in the report), not from
when the Gateway took it. One assertion in `TickAsync_ReportNotTaken_IsSentAgainAtTheNextLook_WithoutCheckingAgain`
would hold it: advance 23 h, the report goes; advance 1 h more, the checks run again.

### 4. The proof file's gate commands contain literal tab characters - LOW

File: `missions/license-switching/phase-6/track-c/c2-daily-approver-check.md` lines 100 and 103. The text reads
`.\scripts<TAB>est-local.ps1` - a `\t` turned into a tab somewhere between the Developer's shell and the file. The
command as written in the record does not exist. The record is what a later reader uses to re-run the gate; it should
say `.\scripts\test-local.ps1`.

### 5. No test that `LicenseSwitching.Start` schedules the check and that off never does - LOW

File: `src/CcDirector.Core.UnitTests/Licenses/LicenseRenewerTests.cs` lines 269-293 assert `Renewer.IsScheduled` and
`Reporter.IsScheduled` for on and off; `ApproverCheck.IsScheduled` is asserted nowhere. The brief's item "the
scheduler entry is removed when switching is turned off" is true by reading (`Start` returns before scheduling when
`!Enabled`; `Dispose` calls `ApproverCheck.Stop`) but has no test, so a later edit that moves the `ApproverCheck.Start`
line above the `Enabled` guard would pass the gate. Two assertions in the two existing tests close it.

### 6. The display-name shape is a second copy of the Gateway's regular expression - LOW

File: `src/CcDirector.Core/Licenses/LicenseApproverCheck.cs` line 83 (`NameShape`) duplicates
`LicenseApproverStore.BrowserProfileShape` (Gateway, line 113). The Director cannot reference the Gateway project, so
the copy is understandable, but the two can drift and the symptom when they do is a whole row refused `invalid-row`
for an optional field - silently, in the Gateway's answer. The right home is `LicenseApproverContract` in the
Contracts project (C1's types; one line, through the Tech Lead), with both sides reading it.

## Observations - no change required

- With the offer setting on, a profile that is also an assigned approver is checked twice per run (once with the
  account, once with none): two full browser runs and two of the 32 rows. The brief asked for exactly this, so it is
  built to the brief; the Tech Lead may want discovery to skip profiles already covered by an assignment.
- The run holds one of the Director's four background slots for its whole length (up to 32 checks at minutes each).
  The renewer already does the same for shorter runs; worth knowing, not a defect today.
- The Developer's "why this lane" reasoning is sound: the coordinator's lock would deadlock a self-approving
  Director, and the key store lock guards the wrong thing. Findings 1 and 2 are about the lane being unfinished, not
  about it being the wrong lane.

## Verdict

Not ready to merge as it stands. Finding 1 is the brief's own question answered the wrong way, with a real approval
pressed after its deadline; finding 2 is the mechanism that makes finding 1 worse. Findings 3 to 6 are small and
should ride along in the same fix.
