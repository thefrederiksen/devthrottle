# Answers to the second review of pull request 4123

The review is `review-4123-round2.md` (head reviewed 7c81b484d). Answered by the Developer who built it; the fixes are
in 44aeb5dd4.

## 1. MEDIUM - a sign-in that waited on the one-at-a-time lock is refused as if a check had held it

**Accepted and fixed, the first way.** `startBy` for a sign-in the Gateway asked for is now measured once the sign-in
holds the coordinator's one-at-a-time lock, so the 30-second allowance limits only the wait for a running approver
check. A sign-in that waited for the daily renewal, or for another account's sign-in, runs as it did before the lane
existed, and the Gateway's settle path still takes a late success. The log line that names a check is now reached only
when a check really held the sign-in.

Test, as the reviewer described it:
`SignIn_TheGatewayAskedFor_ThatWaitedLongForTheOneAtATimeLock_WithNoCheckRunning_StillRuns` - a renewal holds the
lock (held open inside the fake tool's sign-in), a Gateway-asked sign-in for another account arrives, 31 seconds pass,
the renewal ends with no check running: the sign-in runs and is `added`, the tool was asked for both, and the lane
started no timer. With `startBy` moved back to arrival it fails (`expired`).

## 2. MEDIUM - a sign-in refused because a check held the lane says "No approval came back from the other machine in time"

**Accepted and fixed.** That refusal now carries its own sentence,
`LicenseSignInCoordinator.TooLateMessage`: "An approver check was running on this machine until it was too late to
start the sign-in in time, so nothing was started." `LicenseSignInAnswer` gained an optional `Sentence` - a sentence
this Director wrote for one answer, never the tool's words - which `Build` carries into `Message` ahead of the
reason's own. The code stays `expired`. `SignIn_TheGatewayAskedFor_DuringACheck_ThatOutlastsItsTime_FailsExpired_AndStartsNoLogin`
now asserts the message; with the sentence taken out it fails.

## 3. LOW - the 30-second allowance leaves no margin

**Accepted in writing, as the Tech Lead ruled.** The comment on `LicenseApprover.ToolStartAllowance` now says what it
does and does not promise: it bounds when the tool starts, not when its answer arrives; a tool started at the last
moment and run to its limit answers at the very end of the Gateway's wait; and the start is capped at the request's
deadline, not the deadline less the tool's run, so for a few seconds the tool can still be running after the waiting
machine gave up. It needs a wait near thirty seconds and a tool run near its 150-second limit at once, and the second
half was so before the lane.

## Observations

- **The lane's `_delay` call outside the `try`:** moved inside, as asked, together with the timer's token source, so
  a timer that cannot be started still stops the wait counting; the token source is now disposed as well as cancelled.
- **The approver's `TooLateMessage` does not travel to the asking machine:** left for the Tech Lead, unchanged.
- **The lane log line's negative seconds:** left; cosmetic.

## Proof that the new assertions catch what they are for

With 44aeb5dd4 committed, both fixes were taken back out by hand (`startBy` measured at arrival, the sentence
removed) and the Director's license tests run with a hang guard: exactly the two tests above failed, 219 passed. With
the source restored: 221 passed.

## Gate

The License filter, as the Tech Lead asked: `.\scripts\test-local.ps1 -Parked -Filter "License"` at 44aeb5dd4,
TEMP and TMP on D:: RESULT GREEN, every suite Completed - `CcDirector.Core.UnitTests` 242, `CcDirector.Avalonia.Tests`
32, `CcDirector.Gateway.Tests` 22, `CcDirector.Gateway.UnitTests` 983, the other suites holding no test the filter
names. The run reported the tree dirty: the two files it saw were this answer file and the copied review, both
uncommitted documents and no code. The change touches only the Director's code (Core), so the Gateway suites prove
nothing new; they ran because the filter asks for them.
