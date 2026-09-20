# Proof - phase 1, task 2: the restart cycle drains through the real drain (defect #3169)

Branch `smart-restart/p1-cycle-real-drain`, cut from `origin/main` at `2092941b6`. Commits `e24a2e541`
(the change) and `7aa760fa0` (a fix to one of my own tests, found by the revert proof - see below).

## What changed

- NEW `src/CcDirector.ControlApi/Restart/DirectorDrainRestartStep.cs` - the real `IRestartCycleDrain`.
  It takes a factory for the drain through its constructor. `Availability` builds a drain and throws it
  away (building one reads nothing, sends nothing and does not take the one-at-a-time gate): available
  when the factory gives one, not available with a plain reason when it gives none. `RunAsync` names the
  record, writes the order's reason (and which request and which session asked) into it, passes each
  progress CHANGE on once as one sentence, and maps the result: ready to restart becomes `Drained` with
  the workspace id; not ready becomes `Blocked` with the workspace id and the drain's own reason; a drain
  already running becomes `Blocked` with that drain's own sentence and no workspace id, because this one
  touched nothing; no Gateway client becomes `Unavailable`.
- `ControlApiHost.cs` - `RestartDrainStep()` returns the real step over `CreateDrain`; it and
  `JudgeRestartEligibility()` became instance members. They are `internal` rather than `private` so a
  test can watch the host's wiring. Nine lines added and five removed in that file, all in one place.
- `NoDrainOnThisBuild` is deleted. The claim was swept, not only the symbol: the seam comment in
  `ControlApiHost.cs`, the `Unavailable` verdict, the `RestartDrainAvailability` record, the
  `IRestartCycleDrain` summary and its `Availability` comment in `DirectorRestartCycle.cs`, and the
  `DrainAvailable` comment in the contracts now say what is true. The `Unavailable` verdict is kept.
- The workspace id rule moved from the desktop dialog into `DrainPaths.WorkspaceIdFor`, and the dialog's
  `MintId` now calls it, so the desktop drain and the cycle name their records by one rule. One line added
  and six removed in `DrainDirectorDialog.axaml.cs`.
- The drain itself is unchanged. The cycle runs it with the drain's existing default options.

## The check

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

| When | Passed | Failed | Total |
|---|---|---|---|
| Before, untouched `origin/main` at `2092941b6`, run by me | 418 | 0 | 418 |
| After | 428 | 0 | 428 |

The arithmetic: 418, minus 1 test deleted (`A_build_with_no_drain_stops_before_touching_anything_and_says_so`,
which constructed the deleted stand-in), plus 11 new tests, is 428.

Also built with no warnings and no errors: `src/CcDirector.Avalonia` (the dialog line) and
`src/CcDirector.Gateway.Tests` (a parked suite that mentions the old sentence in strings).

## Each new test, in one plain sentence

All in `src/CcDirector.Gateway.UnitTests/Restart/DirectorDrainRestartStepTests.cs`. Every test that
reaches a verdict runs the real cycle over the real step over the real drain on the `DrainTestRig`; only
the live sessions and the Gateway are faked. None hands the cycle a finished outcome.

1. `RunAsync_SessionsRunning_DrainsEveryOneBeforeTheLauncherIsAsked` - with a lead, its worker and a
   standalone session running, every one is asked to close and seen gone BEFORE the launcher is asked,
   the launcher is asked once and last, and at that moment no session is running. The order is one list
   written to by both the fake Director and the fake Gateway, read top to bottom. This is the test the
   issue asks for.
2. `RunAsync_DrainEndsNotReady_StopsTheCycleAndTheLauncherIsNeverAsked` - when one session never writes
   a handover, the cycle stops on the drain's own reason and names the record, the machine is not
   re-checked, the launcher is never asked, and that session is still running and was never asked to close.
3. `RunAsync_NoGatewayClient_IsUnavailableAndNothingIsTouched` - through the real cycle, a Director with
   no Gateway client stops at the drain step saying it is not connected to a Gateway, and neither the
   machine check nor the launcher is reached.
4. `RunAsync_NoGatewayClient_ReturnsTheUnavailableVerdictWithNoRecord` - asked directly, the step answers
   `Unavailable` with no workspace id.
5. `Availability_GatewayClientPresent_IsAvailableAndChangesNothing` - asking whether the drain is
   available says yes and messages nobody, closes nobody, captures nothing on the Gateway, writes no
   file and leaves no drain holding the gate.
6. `Availability_NoGatewayClient_IsNotAvailableAndSaysWhyInPlainWords` - the reason says the record must
   be readable while the machine is down and what to do about it.
7. `RunAsync_OrderReason_IsWrittenIntoTheRecordAndToldToTheSession` - the order's reason is in the
   capture, in the stored record and in the message the session receives, and the record names the
   request and the session that asked.
8. `RunAsync_ADrainIsAlreadyRunning_IsBlockedWithThatReasonAndTouchesNothing` - while another drain holds
   the Director, the cycle stops on that drain's own sentence (which names its record), its own sessions
   are never messaged, nothing is captured, and the launcher is never asked.
9. `RunAsync_Progress_PassesEachChangeOnOnceAsOneSentence` - the drain reported more times than the step
   passed on, no two sentences in a row are the same, and they run from capturing to finished. The test
   fails itself if the drain never repeated, so it cannot pass without exercising the rule.
10. `RestartDrainStep_OnTheHost_IsTheRealStepOverTheRealDrain` - a real `ControlApiHost` hands the cycle
    the real step.
11. `JudgeRestartEligibility_HostWithNoGatewayClient_SaysItCannotDrainAndWhy` - a real host with no
    Gateway client answers the eligibility question with "cannot drain" and the Gateway reason, not the
    old sentence about the build.

Two supporting changes to existing tests. `DirectorRestartCycleTests.Gateway` went from private to
internal and gained `WhenLauncherAsked`, so the new tests use the existing cycle fake. And
`DirectorDrainTests`, `DirectorRestartCycleTests` and the new class share a new xUnit collection
(`DirectorGatesCollection`): the drain and the cycle each hold a process-wide gate, the new tests take
both, and xUnit runs separate classes side by side, so without it a drain test and a cycle test would
refuse each other. The collection only makes those three classes run one after another.

## The revert proof

Committed first (`e24a2e541`), then mutated. Every run below is the whole check, not a narrower filter,
and every run rebuilt (no `--no-build`).

**Mutation A - the stand-in put back, as the mandate asks.** `NoDrainOnThisBuild` restored and
`ControlApiHost.RestartDrainStep()` returning it again. Result: 426 passed, 2 failed. Red: tests 10 and
11. The other nine stayed green, and that is the honest reading: they build the step themselves, so they
cannot see what the host wires. Tests 10 and 11 exist for exactly that reason.

**Mutation B - the step made to behave as the stand-in did** (`RunAsync` answers `Unavailable` at once
and drains nothing). Result: 421 passed, 7 failed. Red: tests 1, 2, 3, 4, 7, 8 and 9. Tests 5 and 6 are
about `Availability`, which this mutation does not touch, and 10 and 11 are about the host. So every new
test goes red under one of the two mutations, and the order test goes red under B.

**Restored** with `git checkout`, REBUILT, run again: 428 passed, 0 failed. `git status` clean and
`git diff HEAD` empty before that run.

Two things the revert proof turned up, both worth knowing:

- My first attempt at mutation B (`if (order is not null) return ...`) did not compile - warnings are
  errors here and the compiler saw the rest of the method dereference a null. NO test ran, so that
  attempt proved nothing and is not counted.
- The first real run of mutation B showed 65 failures, not 7. Test 8 parked a drain on the process-wide
  gate and, when its assertion failed, never let it go, so every drain test after it was refused. That
  was a defect in my test, not in the product. Fixed in `7aa760fa0` (the parked drain is released in a
  `finally`), and mutation B was then run again from the start; 7 is the count after the fix.

## What I could not reach

- **A host that HAS a Gateway client.** `_gatewayClient` is only set by starting the host, which reads
  the machine's Gateway settings and dials out. So "available is true" is proved on the step (test 5),
  and "the host wires the step" is proved on the host (tests 10 and 11), but no single test shows a
  started host answering `DrainAvailable = true`.
- **`StartRestartCycle` end to end.** It needs a Gateway client too. It was already passing
  `RestartDrainStep()` and `JudgeRestartEligibility` to the cycle and I changed neither call.
- **Nothing was run against a real Director, a real session or a real Gateway.** Tests only, as the
  mandate says.
- **The parked suites and the full default gate were not run.** Only the mission's check, plus the two
  builds named above.

## Things the next reader should know

- A drain that THROWS (the Gateway cannot be reached for the capture, or the drain refuses at preflight)
  is not mapped to a verdict. It reaches the cycle's one catch, which abandons the request with the
  error's message and no workspace id - even though a preflight refusal has already stored a record
  under one. I left it: catching in the step would be a second entry-point catch, and the mandate names
  three mappings, not four. If the Reviewer thinks the record should be named there, it is a small change.
- The workspace id is stamped to the minute (the existing rule, moved not changed), so two cycles started
  inside one minute would ask for the same id.
- `DirectorRestartRequestServiceTests` and `DirectorRestartRequestRouteTests` still carry the sentence
  "this Director build carries no drain". I left them: they are the Gateway relaying whatever an older
  Director in the fleet answers, which such a Director still does. They claim nothing about this build.
- The mission document, section 5.1, still says the cycle is wired to `NoDrainOnThisBuild`. It is the
  record of what was found, and it is the Delivery Lead's file, so I did not edit it.
