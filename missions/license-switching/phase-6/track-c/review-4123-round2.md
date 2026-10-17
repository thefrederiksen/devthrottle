# Second review of pull request 4123 - the Director's daily approver check, after the fixes

Reviewer: session "License Switching - Reviewer - Track C 4123 second review" (1eb1e824), 17 October 2026.

## Scope

**What I read.** Only git objects in `D:\ReposFred\devthrottle.worktrees\wt14`; the working tree was not touched, nothing
was built, no test was run.

- The new head `refs/review/4123b` (7c81b484d). It is a rebase onto main (merge base 311ce98a8), so the diff against
  the first head `refs/review/4123` is noisy. I read the diff of the fix commits instead, `git diff 215cd9940
  refs/review/4123b`: 13 files, 770 lines added, 59 removed (bad46adb6, 20d572b1f, b140fd785, e38673081, 7c81b484d).
- In full at the head: `LicenseBrowserLane.cs`, `LicenseSignInCoordinator.cs` (both classes), `LicenseApproverCheck.cs`,
  `LicenseApproverCheckTests.cs`, the lane tests, the new `ControlledDelay`, the fake tool's `CheckApproverAsync`.
- For the arithmetic: the six timing constants in `LicenseMessages.cs` (lines 480-502), `LicenseApprovalRelay.DecideAsync`
  (the relay's wait and the deadline it forwards), `LicenseSignInService` (how a sign-in's `NotAfterUtc` is stamped,
  the five-minute wait, and the settle path that asks a Director again), `LicenseSignInExecutor` (the Director's
  command handlers), `RunForJsonAsync` (how a tool run is ended at its limit), the `BackgroundJobs` run loop, the
  renewer's call into `RenewAsync`, and the sentence table in `LicenseApprovalExchange.cs`.
- The first review and the Developer's answers.

**What I could not reach.** The gate and the revert proof are the Developer's word; I did not run them. Where a finding
rests on timing I give the constants and the arithmetic.

## The six answers, checked against the code

1. **HIGH - waits behind a check with no deadline, then runs anyway. Fixed for the case it was raised for.**
   `EnterWorkAsync(what, longestWait, ct)` answers null once the wait runs out (lane lines 67-122), both callers look at
   the time again once in (coordinator lines 168-172 and 366-369), and no tool runs past that. The allowances are right:
   approval 180 s - 150 s = 30 s; Gateway sign-in 300 s - 180 s - 90 s = 30 s, measured from before `StartSignInAsync`
   so the URL wait eats into the approval wait, not into the Gateway's. The renewal waits until its own ten-minute
   deadline, which nobody else waits on. Each of the seven tests can fail: the two "outlasts" tests assert the asked
   wait before anything awaits, so a missing timer fails rather than hangs; the "ends only after" test is the second
   look; the "ends in time" test is the positive side; the sign-in test asserts no browser question and no login.
   What remains is below: the start-by is measured before the coordinator's one-at-a-time lock (finding 1), the
   refused sign-in's sentence (finding 2), and the margin (finding 3).
2. **MEDIUM - the check re-takes the lane between rows. Fixed.** `_waitingWork` is counted under the same lock the
   check's entry test reads (lane lines 77, 94, 100, 113, 134), so a check can enter only when nothing is waiting; a
   waiter that gives up or is cancelled decrements and signals. Work waiting when a row ends therefore always goes
   before the next row. `TickAsync_AnApprovalArrivesDuringARunOfTwoChecks_GoesInBetweenThem_NotAfterBoth` is
   deterministic on the fixed code and fails (two tool calls, not one) on the old code, because the fake gate's
   continuation runs inline on `SetResult` and the old check re-entered synchronously. The lane test
   `EnterCheck_WhileWorkIsWaiting_LetsThatWorkGoFirst` never fails on the fixed code but is a race on the old one (two
   pool continuations); acceptable, the check test is the deterministic witness.
3. **LOW to MEDIUM - next run keyed to the report, not the checks. Fixed.** `_unsentStartedUtc` is taken before
   `CheckAllAsync` and becomes `_lastRunUtc` when the report is taken (lines 188-190, 206). The test's third tick returns
   null without the fix (next run at T + 47 h), so it can fail.
4. **LOW - tabs in the proof file. Fixed.** Zero tab characters in the file at the head.
5. **LOW - no test that Start schedules the check. Fixed.** Both assertions are present and read the real property.
6. **LOW - a second copy of the regular expression. Fixed.** One `LicenseApproverContract.BrowserProfileShape`; the
   Gateway's store is that object and the Director reads it. No non-ASCII character in any added line of the fix.

## The brief's questions

- **Can an approval or a Gateway-asked sign-in still run a tool after its deadline, or wait past the Gateway's wait?**
  Not in the lane: both start within 30 s of arrival or not at all. An approval that starts at second 30 and whose tool
  runs to its 150 s limit answers at second 180, exactly the relay's wait, with no margin for transit (finding 3).
- **A check that is mid-row when the work arrives:** the work waits at most 30 s (renewal: at most 10 min), then is
  refused `expired` or enters. **A check that overruns its 8-minute limit:** `RunForJsonAsync` ends the tool at 8 min,
  the exception leaves the lane through the `using`; the work gave up at 30 s. **The daily renewal:** waits up to 10
  min behind a check, which is at most one check now; it holds the coordinator's one-at-a-time lock while it waits,
  which is what finding 1 is about.
- **Does waiting work always get the lane before the next row?** Yes (answer 2 above).
- **Does anything new break a sign-in that arrives when no check is running?** Yes: finding 1.
- **The accepted refusal's sentence:** the approval's own sentence (`TooLateMessage`) is accurate but never reaches the
  owner (observation). The SIGN-IN side's sentence is misleading: finding 2.

## Findings

### 1. A Gateway-asked sign-in that waited on the coordinator's one-at-a-time lock, with no check running, is now refused as if a check had held it - MEDIUM, new in this fix

File: `src/CcDirector.Core/Licenses/LicenseSignInCoordinator.cs` lines 85-86 (`startBy` computed from arrival), 103
(`_oneAtATime.WaitAsync` after it), 168-172 (the refusal and its log line).

**What the code does.** `startBy` is arrival plus 30 s. The sign-in then waits for `_oneAtATime`, which the daily
renewal holds for the whole of `RenewAsync` (lines 139-157): up to 4.5 min per due account while it signs in, or up to
10 min while IT waits in the lane behind a check. A second Gateway-asked sign-in for another account on the same
machine holds it the same way (the Gateway refuses a second sign-in only for the same machine AND account,
`LicenseSignInService` lines 29-33). When the lock frees after more than 30 s and no check is running, `EnterWorkAsync`
returns at once, the second look `_utcNow() > startBy` is true, and the sign-in is refused `expired` - having never
touched the lane.

**The harm.**
- Before this fix the same sign-in ran after the lock freed. The Gateway recorded `Timeout` at five minutes if it
  overran, and `SettleOneAsync` asked the Director again and took the stored result, so a late success still ended the
  row `added` or `renewed`. Now it ends `failed`. "Add a spare" for a second account while the first is still
  signing in, or while the daily renewal is running, fails where it used to work.
- The log line (line 171) says "an approver check on this machine ran until it was too late to start". No check ran.
  A reader chasing this in the Director log will look for a check that is not there.
- The owner's sentence is finding 2's: "No approval came back from the other machine in time." Nothing was asked.

**Fix.** Either compute `startBy` after `_oneAtATime` is taken (then the allowance governs the lane wait only, which is
what the answer describes, and the no-check behaviour is exactly what it was before), or keep it as is on purpose and
make the refusal say what held the sign-in. Either way the log line must not name a check when none ran. A test: a
renewal holds the lock; a Gateway-asked sign-in arrives; 31 s pass; the lock frees with no check running; assert the
sign-in RUNS (first option) or is refused with a sentence naming the lock (second), and never the check sentence.

### 2. A sign-in refused because a check held the lane tells the owner "No approval came back from the other machine in time" - MEDIUM

File: `src/CcDirector.Core/Licenses/LicenseSignInCoordinator.cs` line 172 (`LicenseSignInAnswer.Failed(Expired)`), 261-266
(`Sentence` maps the code only); `LicenseCommandLine.SignIn.cs` line 64 (`Failed` takes no message);
`LicenseApprovalExchange.cs` line 111 (the sentence for `expired`).

The approval path got its own sentence (`TooLateMessage`). The sign-in path did not: it answers the reason code
`expired`, and `Sentence()` turns that into "No approval came back from the other machine in time." The Gateway stores
`answer.Message` on the row (`LicenseSignInService.Record`, the `Ok` case) and the Licenses page shows it. For this
refusal no approval was asked for and no login was started; the sentence describes a different failure, and the owner
would look at the approver machine for a fault that is on this one. The brief asked for exactly this. The test
`SignIn_TheGatewayAskedFor_DuringACheck_ThatOutlastsItsTime_FailsExpired_AndStartsNoLogin` asserts the outcome and code
but never the message, so it does not hold the sentence.

**Fix.** A sentence of its own for this answer, like the approver's ("An approver check was running on this machine
until it was too late to start the sign-in in time, so nothing was started."), carried through `Build` into
`Message`; one assertion on `result.Message` in that test.

### 3. The 30-second allowance leaves no margin, so a tool run near its limit can still answer after the relay stopped listening, or press Authorize after the target gave up - LOW

File: `src/CcDirector.Core/Licenses/LicenseSignInCoordinator.cs` lines 313, 349-350, 366-370;
`src/CcDirector.Gateway/Licenses/LicenseApprovalRelay.cs` lines 274-278; `LicenseMessages.cs` lines 485-488.

**The numbers.** The relay waits the sooner of 180 s and the sign-in's deadline, from its send. The approver may start
the tool until arrival + 30 s and the tool may run 150 s: the answer can leave at arrival + 180 s, the whole of the
relay's wait, with the two transits unpaid. Before this fix the 30 s was that margin. And `startBy` is capped at
`NotAfterUtc`, not at `NotAfterUtc` less the tool's run: in the ordinary case the target's deadline is `startedUtc` +
180 s and the approver's arrival is a few seconds after `startedUtc` (URL printing, two hops), so the tool may still be
running for those few seconds after the target has ended its login and the relay has answered `expired`. The constant's
own comment (lines 485-487) says the tool timeout is sized so "an approval is never made for a Gateway that has stopped
listening"; the answer to finding 1 says the approval "may start only while the Gateway is still waiting", which is
true of the start and is not the same promise.

**Why LOW.** It needs a lane wait near 30 s AND a `license approve` run near its 150 s limit; a normal run is tens of
seconds. The second half was true before this change (the old deadline check at line 358 allowed the same). It is
reported because the fix's sentence overstates what it guarantees.

**Fix, if wanted.** `startBy = min(arrival + ToolStartAllowance, NotAfterUtc - ApproveToolTimeout)` with a few seconds
taken off the allowance for transit; or accept it and write the accepted window into the constant's comment.

## Observations - no change required

- The approver's `TooLateMessage` never reaches the owner: the target Director maps the refusal's reason code to its own
  sentence (coordinator lines 241-247) and drops `approval.Message`. "The approving machine was mid-check" and "the
  approving machine was slow" read the same on the Licenses page. Not misleading; lossy. Tech Lead's call whether the
  approver's sentence should travel.
- In `EnterWorkAsync` the `_delay(...)` call (lane line 82) sits outside the `try`; a synchronous throw there would leave
  `_waitingWork` raised for the life of the process and no check would ever run again. `Task.Delay` cannot throw for
  the spans used (all 10 min or less), so it is not reachable today; moving the call inside the `try` costs nothing.
- The lane's log line prints "at most -N seconds" for work that arrives already past its start-by. Cosmetic.
- The renewal still holds a background slot, the renewer's lock and the coordinator's lock for up to 10 min while it
  waits behind a check; with finding 2 fixed that is at most one check (8 min). As the first review said: worth knowing.

## Verdict

The HIGH is closed for the case it was raised for: nothing waits in the lane past the time it could still start, the
time is looked at again once in, and waiting work goes before the next row. Two MEDIUM items remain, both in the
sign-in path and both a handful of lines: the start-by is measured before the one-at-a-time lock, which turns a sign-in
that used to work into an `expired` failure with a log line naming a check that never ran (finding 1), and the
refused sign-in's sentence to the owner describes a different failure (finding 2). I would fix both in this pull
request; finding 3 can be accepted in writing or taken as a follow-up.
