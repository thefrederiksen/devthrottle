# Every scheduled run is told to report, by the product - proof

Developer brief 10 (Factory Control, step 1). Two parts in one pull request.

Everything below was run on 13 October 2026 on SOREN_NORTH at commit `4cbca302e`, which sits directly on
origin/main at `f2d877df3`, except where a line says otherwise.

**Added after the review, on the Architect's ruling:** an ok run now honours its schedule's close-itself setting
(the section "An ok run honours the close-itself setting" below), and the skip rule matches the command
`cc-devthrottle run result` rather than the bare words `run result` (the reviewer's observation).

## What changed

### Part 1 - the closing instruction (Gateway only)

When the Gateway starts a scheduled run, the prompt that run is given ends with one closing instruction to report
how it went. The wording lives in one place, `src/CcDirector.Gateway/Running/ScheduledRunClosingInstruction.cs`,
and is added in one place, `DirectorCronSessionStarter.StartAsync`, to the create the Director is handed. No
Director change and no tools change is needed: the Director types the first prompt it is handed, as it always has.

A schedule whose stored prompt is

    Triage the overnight errors and file what you find.

now starts a session whose first prompt is exactly

    Triage the overnight errors and file what you find.

    When you have finished, the last thing you do is report how this scheduled run went: run `cc-devthrottle run result ok`, or `cc-devthrottle run result problem "<one line why>"` if something went wrong.

(the owner's prompt with trailing blank space removed, one blank line, then the instruction on one line).

A prompt that already names the command `cc-devthrottle run result`, in any letter case, is handed over exactly as
the owner wrote it. The bare words are not enough: a prompt that says "check the test run results" has not told
its run to report, and still gets the instruction.

**Every path that starts a scheduled run.** A seed schedule starts a session in exactly one place: `CronEngine.FireAsync`
calls `ICronSessionStarter.StartAsync`, and `DirectorCronSessionStarter` is its only production implementation
(`GatewayHost.cs`, where the engine is built). The paths into `FireAsync` are:

| Path | How it reaches the starter | Gets the instruction |
| --- | --- | --- |
| The timer, at the schedule's own time | `CronTenantSweep` -> `EvaluateDueAsync` -> `FireAsync` | yes, tested |
| A catch-up (the sweep reached the schedule late) | the same sweep; the run is recorded `catch-up` | yes, tested |
| Run now (`schedule run`, the Run now button) | `CronRunEndpoints` -> `RunNowAsync` -> `FireAsync` | yes, tested |
| A schedule linked to a factory | any of the three above; the starter also stamps the factory | yes, tested |
| A retry | there is none: a fire that starts no session is recorded "did not run" and is not tried again | not applicable |

Two things start sessions and are deliberately NOT given the instruction, because neither is a run that can report:

- **A work-list schedule.** It drains a list through the work-list runner, which starts many sessions and records
  none of them on the run; the run is recorded `untracked`, and `run result` from one of those sessions is refused
  ("was not started by a schedule"). Telling them to report would produce a refusal every time.
- **A factory trigger.** It is not a schedule and writes no run record.

### Where the addition can be seen

- **Not** on the schedule: the stored prompt is untouched, so the Schedule screen, `schedule show`, `schedule list`
  and the "what this seat's schedule runs" text in a Talk to the boss briefing all show the owner's words.
- **Not** on the run record: a run record holds no prompt.
- **Not** in the Gateway log: the start line logs the stored prompt.
- **Yes** in the session itself: it is part of the first prompt typed into the run's session, so it is in the
  terminal, in the session's transcript, and anywhere else that shows what the session was actually sent.

### What was left alone

- The close-itself setting (`AutoDismiss`) is read and handed over exactly as before, and is not written.
- How a run is found for `run result` is unchanged: by the calling session's own key. No run id is passed.

### An ok run honours the close-itself setting (Gateway only)

`cc-devthrottle run result ok` used to close the calling session always. With every run now told to report, that
would have closed the sessions of schedules whose owner switched close-itself OFF. The Architect ruled that the
setting wins, and it is decided on the Gateway, in `CronRunResultService`:

| The run reports | Close-itself ON (the default) | Close-itself OFF |
| --- | --- | --- |
| ok | `closeSession: true` - "This session now closes itself." | `closeSession: false` - "This session stays open, because its schedule is set not to close itself." |
| a problem | `closeSession: false` - stays open for someone to look at | the same |

The result is recorded either way; a run that stays open by its setting is ok, not a problem. The seat's existing
"stayed open" marker is folded from how the run's session ended and is not touched here.

The setting is read from the schedule as it stands when the run reports. A schedule deleted while its run was
going has no setting left to read, so its run closes, as the default does.

**No tools change is needed, and this was checked, not assumed.** The command line closes the session only when
the Gateway's answer carries `closeSession: true`, and otherwise prints the Gateway's sentence and
`session: stays open`. That is so in the source at the `v2.19.0` tag, and the copy installed on this machine
(`%LOCALAPPDATA%\cc-director\pyenv\Lib\site-packages\cc_devthrottle\run_ops.py`) is the same file as that tag.

### Part 2 - `cc-devthrottle factory manager set` is removed

Since pull request 3941 only the owner's own device may set the Factory Manager mark, so the command could only
ever be refused. Removed: the command, its agent-discoverable action `factory-manager-set`, its help, its section
of `docs/cli-reference.md`, the `set_mark` function, and its four tests. `show` and `clear` stay. The Gateway route
stays. `cc-devthrottle factory manager set` now fails as an unknown command (exit 2, "No such command 'set'") and
sends nothing. This part ships with the next tools release; Part 1 does not depend on it.

Left as history, not edited: the v2.19.0 release notes and the earlier proof files under
`docs/qa/factory-control/`, which describe the command as it was then.

## Each test fails without the change

Each mutation was made in the production code, the project was rebuilt (never `--no-build`), the whole test class
was run, and the file was restored from the commit. Run at `3a7c3aacf`, before the rebase; the production and test
files are the same after it, except the run-now test's clock value.

| Mutation | Tests that went red (of 14) |
| --- | --- |
| The starter hands over the stored prompt (the change removed) | 4: `EvaluateDue_ARunStartedAtItsOwnTime_...`, `EvaluateDue_ARunCaughtUpLate_...`, `RunNow_ARunStartedByHand_...`, `EvaluateDue_ARunOfAScheduleLinkedToAFactory_...` |
| The skip rule can never match | 3: `EvaluateDue_APromptThatAlreadyNamesRunResult_IsHandedOverUnchanged`, and both cases of `PromptFor_APromptThatAlreadyNamesRunResult_IsLeftExactlyAsItIs` |
| The starter forces the close-itself setting on | 1: `EvaluateDue_TheCloseItselfSetting_...(closesItself: False)` |
| The starter writes the composed text onto the schedule it was handed | 0 - see "Not proven" |
| Restored, rebuilt | 0: 14 passed |

The ruling and the tightened skip rule, run at `4cbca302e` over the three affected test classes (112 tests), each
mutation rebuilt and restored:

| Mutation | Tests that went red (of 112) |
| --- | --- |
| An ok run always closes (the ruling removed) | 3: `Report_Ok_WhenTheScheduleIsSetNotToCloseItself_IsRecorded_AndTheSessionStaysOpen`, `Report_OkSentAgain_WhenTheScheduleIsSetNotToCloseItself_StillLeavesTheSessionOpen`, and over the route `Result_Ok_WhenTheScheduleIsSetNotToCloseItself_AnswersThatTheSessionStaysOpen` |
| The setting alone decides, whatever the result | 4: `Report_Problem_LeavesTheSessionOpen_WhateverTheCloseItselfSetting(closesItself: True)`, `Report_Problem_IsRecordedWithItsReason_AndTheSessionStaysOpen`, and two earlier ran-past-its-shift tests |
| A deleted schedule leaves the session open | 1: `Report_Ok_WhenTheScheduleWasDeletedWhileItsRunWasGoing_ClosesAsTheDefaultDoes` |
| The skip rule matches the bare words again | 2: both cases of `PromptFor_APromptThatOnlyHappensToSayRunResult_StillGetsTheInstruction` |
| Restored, rebuilt | 0: 112 passed |

Two of the new tests pass with or without the ruling, because they describe what was already true:
`Report_Ok_WhenTheScheduleIsSetToCloseItself_TellsTheSessionToClose`, and the close-itself OFF case of the problem
test. They are there so both settings are written down for both results.

Command line, run at `734e309b9`: with `cli.py` and `factory_manager_ops.py` put back as they were on main, 5 failed
and 12 passed - `test_set_IsNotACommand_FailsAsUnknownAndSendsNothing`, `test_help_ListsShowAndClear_AndNoSet`,
`test_theModule_HasNoWayToSetTheMark`, `test_actions_ListTheFactoryManagerCommandsAndTheNameCheck`,
`test_actions_json_is_unchanged`. Restored: all pass.

## The runs

Tree clean for each.

| Command | At | Result |
| --- | --- | --- |
| `.\scripts\test-local.ps1` | `4cbca302e` | GREEN, 10 suites, every one `outcome=Completed`: 4,562 passed, 0 failed, 0 skipped (Core.UnitTests 1720, Avalonia 1079, Engine 86, HostedAgent 89, Launcher 218, Terminal 82, Reclaim 305, setup 39, setup-engine 870, setup-cli 74) |
| `dotnet test src\CcDirector.Gateway.UnitTests` | `4cbca302e` | 11,258 passed, 0 failed, 0 skipped (11,249 before the ruling's nine new test cases) |
| `python -m pytest -q` in `tools\cc-devthrottle` | `734e309b9` | 4,319 passed, 3 skipped, 0 failed. Not run again: nothing under `tools\` changed after that commit. |

The command line suite was run in a fresh virtual environment holding the dependency floor the continuous
integration job pins (`typer==0.16.1 click==8.2.1 rich==13.0.0 requests==2.31.0 cryptography==42.0.0`). The
machine's own Python has click 8.1.8, below that floor, and fails hundreds of tests in this suite for a reason
that has nothing to do with this change (`stderr not separately captured`, in tests this change does not touch).

The first default gate run was red, on my own test: `GateTestsDoNotDependOnTheMachineTests` caught a
`DateTime.UtcNow` in the run-now test. The test now takes its time from the schedule. That is the second commit.

## Not proven

- **Nothing was run live.** No schedule was created or run on the live Gateway, no Director was handed the text and
  no real session received it. That an agent given the instruction actually reports is not proven here.
- **A prompt that is only a slash command.** For a stored prompt such as `/mail-desk`, the instruction arrives
  after the command, so the skill receives it as its arguments. Whether each such skill then reports is not
  proven, and a built-in command such as `/help` ignores it, so a run of one would still be recorded "did not
  report".
- **The "stored prompt is unchanged" test cannot be made red from the starter.** It stayed green even when the
  starter wrote the composed text onto the schedule it was handed, because the store hands out copies and a fire
  writes only the last-run fields. It guards against a future change that writes the prompt back; it is not
  evidence that this change was needed.
- **The parked suites `CcDirector.Gateway.Tests` and `CcDirector.Core.Tests` were not run**, and neither was
  `.\scripts\test-local.ps1 -Parked`. The Gateway unit tests were run by hand instead, as the common rules allow.
- **The command line suite ran on Windows only**, in one colour mode, on the dependency floor only. macOS, Linux,
  the other colour mode and the newest dependencies were not run.
- **No length limit** on a schedule's stored prompt was found on the Gateway, so the addition (about 200
  characters) cannot push a prompt past one there. A limit on the Director's side was not looked for.

- **The close-itself ruling was not run through a real command line against a real Gateway.** The Gateway's answer
  is tested, and the command line's handling of that answer was read in the released source and the installed
  copy; the two were not exercised together.
- **The setting is the schedule's setting when the run reports, not when it started.** Switching it between the
  start and the report changes what the report does. Fixing it on the run would need a new stored field.

## The decision that was raised, and its answer

Raised in the first version of this file: `run result ok` closed the calling session whatever the schedule's
close-itself setting. The Architect ruled that the setting wins. That is now built, above.
