# Proof - the trigger

The trigger is a check with no model in it. A Director runs a command on an interval. The command prints how much
work there is. When there is some, the Gateway starts a session for the factory agent that does the work.

Gate numbers, what failed, and what was not run are in [gate-results.md](gate-results.md). This file says what
each test proves.

## How the pieces fit

- **The Gateway holds the definition** (`triggers` table) and the history (`trigger_runs` table), both scoped to
  an account. Routes: `POST/GET /triggers`, `GET/PUT/DELETE /triggers/{id}`, `POST /triggers/{id}/pause|resume`,
  and `GET /triggers/{id}/runs`. On the command line: `cc-devthrottle trigger add|list|show|pause|resume|runs`.
- **The Director fetches with an HTTP pull, not the hub.** Every 30 seconds it calls `GET /directors/{id}/triggers`
  with its own device key, the same shape as the skill store refresh. A trigger's interval is at least a minute,
  so a 30-second pull notices a new one in time. The pull also keeps the switch in one place: with the switch off,
  the route is not mapped, so the Director is handed nothing.
- **The Director runs the check** through the Engine's `ProcessJob`, in the trigger's repository folder, with a
  timeout of half the interval (at least 30 seconds, at most 300). It reports what the process produced
  (`POST /directors/{id}/triggers/{trigger}/checks`) and decides nothing itself.
- **The Gateway reads the result and decides** (`TriggerService`), in this order:
  1. A result that breaks the contract is **failed**, with the reason recorded.
  2. A count of 0 is **nothing-to-do**.
  3. If the trigger is paused, the outcome is **paused**.
  4. If a session this trigger started is still alive, the outcome is **skipped-running**.
  5. Otherwise the Gateway **starts** a session through `MachineSessionSpawner.SpawnOnMachineAsync` on the Director
     that reported, with the origin surface `trigger`. The session is named
     `<factory agent> - <trigger name> - <time>`, and its first prompt has `{count}` filled in.
- **Only one Director checks each trigger.** When several Directors run on one machine, the first to fetch claims
  the trigger, and a report from any other Director is refused. The claim lapses after two intervals plus a
  minute, so a Director that has died hands its triggers on.
- **The Gateway computes the status** (`TriggerStatusFold`; clients render it as given):
  - OK.
  - RED "check failed: reason".
  - RED "start failed: reason".
  - RED "no checks ran" after two intervals with no report.
- **The switch** is `factoryAgents.enabled` in the Gateway's config.json, and it is off by default. The file
  `FactoryAgentsConfig.cs` and the `GatewayHost` switch lines are identical to the ones in the activity record
  pull request (#3272).

## The tests the mandate asked for

All of these are in `src/CcDirector.Gateway.UnitTests/Factory/Triggers/TriggerServiceTests.cs`. Each one uses a
real SQLite Gateway database and a fake session starter that records every call. A test proves that no session
started when the starter recorded zero calls.

| Mandate case | Test | What it proves |
|---|---|---|
| Empty check | `EmptyCheck_WritesNothingToDo_AndStartsNoSession` | A count of 0 writes one nothing-to-do row, and the starter records zero calls. |
| Count 2 | `CountTwo_StartsExactlyOneSession_WhosePromptSaysTwo` | Exactly one session starts, on the reporting Director, with origin surface `trigger`, the mandated name, and a prompt that says 2. The row is `started` and carries the session id. |
| While alive, then after | `ASecondCheckWhileThatSessionLives_IsSkippedRunning_AndAfterItEnds_ANewOneStarts` | A second check while the first session lives writes skipped-running and starts nothing. Once that session has exited, the next check starts a new one. |
| Paused | `Paused_WritesPaused_AndStartsNothing` | A paused trigger with work writes a paused row that keeps the count, and starts nothing. After resume, it starts one. |
| Broken checks | `ABrokenCheck_WritesFailedWithTheReason_TurnsTheTriggerRed_AndStartsNothing` (exit 1, not JSON, no count, timeout) | Each case writes a failed row with its exact reason, turns the status to RED "check failed: reason", and starts nothing. |
| No report | `NoReportForTwoIntervals_IsRedNoChecksRan` | Two intervals with no report turns the status to RED "no checks ran". |
| Switch off | `FactoryTriggerHostTests.SwitchOff_TheRoutesAreNotMapped_AndTheDirectorRunsNothing` (Gateway.Tests) and `DirectorTriggerRunnerTests.SwitchOff_TheGatewayHandsOutNothing_AndTheDirectorRunsNothing` | A real Gateway with the switch off answers 404 to create, report and pause. The Director's real client reads that as "off". The runner then makes zero checks, counted by a check runner that records its calls. |

## Further tests, and why they are there

- **What counts as alive:**
  - `ACrashedSession_HasEnded`: a crashed session releases the lock.
  - `ASessionNotYetInTheList_CountsAsAliveInsideTheGrace_AndEndedAfterIt`: a session that has just been started,
    but has not yet appeared in the session list, still holds the lock for five minutes. Without this, a fast
    second check would start a duplicate.
- `AStartThatFails_IsAFailedRow_AndRedStartFailed`: when the session cannot be started, the row is failed and the
  status is RED "start failed". The failure never goes silent.
- `TwoReportsAtOnce_StartOneSession`: two reports that arrive together start one session, not two (the lock for
  each trigger).
- **Claims:**
  - `AReportFromADirectorThatDoesNotHoldTheTrigger_IsRefused_AndRecordsNothing`: a report from a Director that does
    not hold the claim is refused and records nothing.
  - `Assignments_GoToOneDirectorPerMachine_UntilItsClaimLapses`: one Director per machine checks a trigger, until
    its claim lapses.
- `ATriggerIsTheAccountsOwn`: another account cannot see or report on the trigger.
- `ANameTaken_IgnoringCase_IsRefused`: a name that differs only in case is refused.
- `TheHistory_KeepsAtLeastTheNewest500`: pruning never goes below 500 rows for each trigger.
- `TriggerCheckContractTests`: every way a check can break the contract (it could not start, timed out, has no
  exit code, exited non-zero, printed nothing, printed something that is not JSON, JSON that is not an object,
  no count, a count that is not an integer, a negative count) gives its own reason.
- `TriggerStatusFoldTests`: every status and its exact words.
- `TriggerDefinitionTests`: the interval must be at least one minute, the name rules, and `{count}` substitution.
- **Director runner** (`DirectorTriggerRunnerTests`):
  - A check runs when its interval is up and not before.
  - It never runs twice at once.
  - It stops when the switch turns off.
  - A real command's exit code, output and timeout reach the report.
  - A missing repository folder is reported as a check that could not start.
- `ProcessJobTests` (Engine): the exit code, standard error kept apart from the output, the timeout, and the right
  shell for the operating system.
- `FactoryTriggerHostTests.SwitchOn_CreateReadPause_AndTheDirectorFetchesAndReports_EndToEnd`: over real HTTP
  against a real Gateway, it covers:
  - create, the duplicate-name conflict, and refusing an interval under a minute
  - the Director fetching its assignment, with the timeout
  - a broken check turning the status red
  - pause giving a paused row
  - the real session starter's failure (no Director process is attached) recorded as a start failure
  - the history in order, newest first
- `SessionKeyGuardTests`: a session key may use the trigger routes but not the Director's fetch and report routes.
  Those need a Director's own key.
- `test_trigger_ops.py`: the command line's interval parsing, its requests, and its plain message when the switch
  is off.

## Mutation proof

These were removed one at a time from `TriggerService`, with the source restored after each:

- the alive-lock
- the paused check
- the lock for each trigger

Each removal turned the matching tests above red.

## Not covered

- **The activity record.** Started, failed and paused checks are NOT written to `factory_activity`. Pull request
  #3272 had not merged, and the mandate says not to wait for it.
- **An end-to-end run on a real test Director** (optional in the mandate) was not done.
- **The PostgreSQL proofs in Gateway.Tests.** They did not pass on this machine, so the PostgreSQL migration is
  unverified. See [gate-results.md](gate-results.md).
- **An older Director** does not know the origin surface `trigger`, so it refuses the session start. That shows
  as RED "start failed" until the Director is updated. The failure is visible, never silent.
- **The migrations.** Whichever of this pull request and #3272 merges second must regenerate its migration on top
  of the other's, on both SQLite and PostgreSQL. The model snapshot must never be merged by hand.
