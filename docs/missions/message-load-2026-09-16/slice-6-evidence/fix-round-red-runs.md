# Slice 6 fix round - every guard watched failing

Inspection 7 rulings 1 to 6. Each run applied the named mutation(s) to the committed tree (`f1cc4a05`), rebuilt, ran
the named filter, printed each failing test with the first line of its message, and restored the file byte for byte;
`git status` was clean after every batch. Unit filter: `DirectorRestore|WorkspaceRestoreMarks|SessionKeyGuard` (287
tests). Route filter: `WorkspaceRestoreRouteTests` (14 tests, a booted hosted Gateway). Mac mini, 17 September 2026.

## The mutations

| Key | File | What it breaks |
|---|---|---|
| M1 | `src/CcDirector.Gateway/Workspaces/WorkspaceStore.cs` | ApplyStoredProvenance no longer puts the stored restore marks back (RestoreStoredMarks call removed) - the inspection's critical defect. |
| M1b | `src/CcDirector.Gateway/Workspaces/WorkspaceStore.cs` | ApplyStoredProvenance no longer keeps the stored restore lease. |
| M2 | `src/CcDirector.Gateway/Api/WorkspaceEndpoints.cs` | the marks route admits any credential (the Director-credential check is made always false). |
| M2b | `src/CcDirector.Gateway/Util/SessionKeyGuard.cs` | SessionKeyGuard admits any POST under /gateway/workspaces/{id}/restore/..., which would let a session key reach the marks route. |
| M3 | `src/CcDirector.ControlApi/Drain/DirectorRestore.cs` | StillRunning always answers null: a seat still running is started again. |
| M3b | `src/CcDirector.ControlApi/Drain/DirectorRestore.cs` | the arm for a seat still listed under an unreachable Director and never recorded closed is disabled. |
| M4 | `src/CcDirector.ControlApi/Drain/DirectorRestore.cs` | EarlierStartUnresolved always answers null: a seat whose earlier start was never recorded is started again. |
| M4b | `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` | the spawn door no longer records the created session by its token. |
| M4c | `src/CcDirector.Gateway/Workspaces/WorkspaceStore.cs` | a late failure mark overwrites a seat the Gateway already recorded as restored. |
| M4d | `src/CcDirector.ControlApi/Drain/DirectorRestore.cs` | a relay error (a 5xx answer) clears the start token as if nothing had been started. |
| M4e | `src/CcDirector.Gateway/Workspaces/WorkspaceStore.cs` | the spawn door's token record no longer compares the token (any claim on a started seat records). |
| M4f | `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` | the spawn door accepts a restore claim from any credential. |
| M5 | `src/CcDirector.Gateway/Workspaces/WorkspaceStore.cs` | TakeRestoreLease grants a second Director a lease another Director holds. |
| M5b | `src/CcDirector.Gateway/Workspaces/WorkspaceStore.cs` | RecordRestoreMark no longer checks that the writer holds a live lease (M5b2 only keeps it compiling). |
| M5b2 | `src/CcDirector.Gateway/Workspaces/WorkspaceStore.cs` | (companion of M5b) |
| M5c | `src/CcDirector.Gateway/Api/WorkspaceEndpoints.cs` | the restore route never gives back a lease it granted when the Director refuses. |
| M6 | `src/CcDirector.ControlApi/Drain/DirectorRestore.cs` | an owner outside the workspace is named without checking it is running. |
| M6b | `src/CcDirector.ControlApi/Drain/DirectorRestore.cs` | a blocked, never-closed owner is named without checking it is running. |
| M6c | `src/CcDirector.ControlApi/Drain/DirectorRestore.cs` | an owner restored in an earlier run is named without checking it is still running. |
| T1 | `src/CcDirector.Gateway.UnitTests/Drain/DirectorRestoreTests.cs` | test-side only, with M1: the intermediate 'PUT ignored' assertion removed, so the run reaches the restore itself. |
| T1r | `src/CcDirector.Gateway.Tests/WorkspaceRestoreRouteTests.cs` | test-side only, with M1: the same, in the route test. |

Ruling map: 1 = M1, M1b, M2, M2b (unit 1a-1c, route R1a-R1b); 2 = M3, M3b (unit 2a-2b, route R2); 3 = M4, M4b-M4f
(unit 3a-3d, route R3a-R3b); 4 = M5, M5b, M5c (unit 4a-4b, route R4a-R4b); 5 = M6, M6b, M6c (unit 5a-5c); 6 = the
command-line runs at the end.

# Unit runs

## 1a (M1)

```
Failed CcDirector.Gateway.Tests.WorkspaceRestoreMarksTests.Save_APutThatClearsOrDropsTheMarks_KeepsWhatTheRestoreWrote [40 ms]
    Assert.Equal() Failure: Strings differ
Failed CcDirector.Gateway.Tests.WorkspaceRestoreMarksTests.Save_APutThatSetsRestoredSessionId_IsIgnored_ButTheDecisionAndHandoverAreKept [44 ms]
    Assert.Null() Failure: Value is not null
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_ASessionPutsAnotherSessionAsTheBossesRestoredId_TheWorkerIsNeverOwnedByIt [44 ms]
    Assert.Null() Failure: Value is not null
Failed!  - Failed:     3, Passed:   284, Skipped:     0, Total:   287, Duration: 2 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

## 1b (M1b)

```
Failed CcDirector.Gateway.Tests.WorkspaceRestoreMarksTests.Save_APutThatClearsOrDropsTheMarks_KeepsWhatTheRestoreWrote [45 ms]
    System.NullReferenceException : Object reference not set to an instance of an object.
Failed CcDirector.Gateway.Tests.WorkspaceRestoreMarksTests.Save_APutThatSetsRestoredSessionId_IsIgnored_ButTheDecisionAndHandoverAreKept [44 ms]
    Assert.Null() Failure: Value is not null
Failed!  - Failed:     2, Passed:   285, Skipped:     0, Total:   287, Duration: 2 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

## 1c (M2b)

```
Failed CcDirector.Gateway.Tests.SessionKeyGuardTests.Workspace_shapes_the_Gateway_does_not_route_stay_refused(method: "POST", path: "/gateway/workspaces/director-restart-2026-09-06/re"···) [< 1 ms]
    POST /gateway/workspaces/director-restart-2026-09-06/restore/now is not a routed workspace shape and must not be authorized
Failed CcDirector.Gateway.Tests.SessionKeyGuardTests.Workspace_shapes_the_Gateway_does_not_route_stay_refused(method: "POST", path: "/gateway/workspaces/director-restart-2026-09-06/re"···) [< 1 ms]
    POST /gateway/workspaces/director-restart-2026-09-06/restore/marks is not a routed workspace shape and must not be authorized
Failed!  - Failed:     2, Passed:   285, Skipped:     0, Total:   287, Duration: 2 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

## 2a (M3)

```
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_AllOwed_ARunningSeatIsReportedOnItsRecord_AndTheRestComeBack [62 ms]
    Assert.Single() Failure: The collection contained 2 items
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.PrepareAsync_EverySeatStillRunning_IsRefused [51 ms]
    Assert.Throws() Failure: No exception was thrown
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.PrepareAsync_ANamedSeatStillRunning_IsRefused_AndStartsOnceItHasClosed [45 ms]
    Assert.Throws() Failure: No exception was thrown
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_ASeatListedUnderAnUnreachableDirector_ComesBackOnlyIfTheDrainRecordedItClosed [46 ms]
    Assert.Single() Failure: The collection contained 2 items
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_ASeatRecordedClosedButStillRunningOnAReachableDirector_IsNotStartedAgain [50 ms]
    Assert.Empty() Failure: Collection was not empty
Failed!  - Failed:     5, Passed:   282, Skipped:     0, Total:   287, Duration: 2 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

## 2b (M3b)

```
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_ASeatListedUnderAnUnreachableDirector_ComesBackOnlyIfTheDrainRecordedItClosed [52 ms]
    Assert.Single() Failure: The collection contained 2 items
Failed!  - Failed:     1, Passed:   286, Skipped:     0, Total:   287, Duration: 2 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

## 3a (M4)

```
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_ATimedOutStartAskedAgain_IsNeverCreatedTwice_UntilForced [45 ms]
    Assert.Contains() Failure: Sub-string not found
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_AnEarlierStartWithARunningCandidate_NamesIt [51 ms]
    Assert.Contains() Failure: Sub-string not found
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_ARelayErrorIsAMaybe_AndIsNotStartedAgainWithoutForce [31 ms]
    Assert.Contains() Failure: Sub-string not found
Failed!  - Failed:     3, Passed:   284, Skipped:     0, Total:   287, Duration: 2 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

## 3b (M4c)

```
Failed CcDirector.Gateway.Tests.WorkspaceRestoreMarksTests.RecordRestoreMark_AFailureAfterTheSeatWasRecordedRestored_DoesNotEraseIt [70 ms]
    Assert.Null() Failure: Value is not null
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_ATimeoutAfterTheGatewayCreatedTheSeat_IsResolvedByItsToken_AndNotCreatedAgain [60 ms]
    Assert.Null() Failure: Value is not null
Failed!  - Failed:     2, Passed:   285, Skipped:     0, Total:   287, Duration: 2 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

## 3c (M4d)

```
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_ARelayErrorIsAMaybe_AndIsNotStartedAgainWithoutForce [23 ms]
    Assert.Contains() Failure: Sub-string not found
Failed!  - Failed:     1, Passed:   286, Skipped:     0, Total:   287, Duration: 2 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

## 3d (M4e)

```
Failed CcDirector.Gateway.Tests.WorkspaceRestoreMarksTests.RecordRestoredByClaim_OnlyTheMatchingTokenRecords [42 ms]
    Assert.False() Failure
Failed!  - Failed:     1, Passed:   286, Skipped:     0, Total:   287, Duration: 2 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

## 4a (M5)

```
Failed CcDirector.Gateway.Tests.WorkspaceRestoreMarksTests.TakeRestoreLease_ASecondDirectorIsRefusedByName_UntilTheFirstFinishesOrLapses [1 s]
    Assert.Throws() Failure: No exception was thrown
Failed!  - Failed:     1, Passed:   286, Skipped:     0, Total:   287, Duration: 2 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

## 4b (M5b + M5b2)

```
Failed CcDirector.Gateway.Tests.WorkspaceRestoreMarksTests.RecordRestoreMark_WithoutTheLease_OrFromAnotherDirector_OrAfterItLapsed_IsRefused [41 ms]
    Assert.Throws() Failure: No exception was thrown
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_WithoutTheLease_StartsNothing [20 ms]
    Assert.Throws() Failure: No exception was thrown
Failed!  - Failed:     2, Passed:   285, Skipped:     0, Total:   287, Duration: 3 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

## 5a (M6)

```
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_AnOwnerOnAnUnreachableDirector_IsNotTakenForRunning [35 ms]
    Assert.Empty() Failure: Collection was not empty
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_AnOwnerOnAnotherDirector_ThatIsNotRunning_FailsTheSeat_AndStartsNothing [34 ms]
    Assert.Empty() Failure: Collection was not empty
Failed!  - Failed:     2, Passed:   285, Skipped:     0, Total:   287, Duration: 2 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

## 5b (M6b)

```
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_AnOwnerThatBlockedTheDrain_ButWasClosedByHandWithoutARecord_FailsTheWorker [47 ms]
    Assert.Empty() Failure: Collection was not empty
Failed!  - Failed:     1, Passed:   286, Skipped:     0, Total:   287, Duration: 2 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

## 5c (M6c)

```
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_AnOwnerRestoredInAnEarlierRun_ThatHasSinceStopped_FailsTheWorker [55 ms]
    Assert.Single() Failure: The collection contained 2 items
Failed!  - Failed:     1, Passed:   286, Skipped:     0, Total:   287, Duration: 2 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

# The critical symptom itself

M1 again, with the test's own intermediate 'the PUT was ignored' assertion removed so the run reaches the restore. Unit:
the Worker is SPAWNED although its boss never came back (it was started under the X the PUT wrote). Route: asking for
the Worker alone no longer fails with 'has not been brought back yet' - it succeeds, under X.

## 1a-symptom (M1 + T1)

```
Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_ASessionPutsAnotherSessionAsTheBossesRestoredId_TheWorkerIsNeverOwnedByIt [1 s]
    Assert.Empty() Failure: Collection was not empty
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 1 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

## R1a-symptom (M1 + T1r)

```
Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.A_session_key_cannot_name_the_owner_of_a_restored_worker_by_writing_the_bosses_restored_id [101 ms]
    Assert.Contains() Failure: Sub-string not found
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 101 ms - CcDirector.Gateway.Tests.dll (net10.0)
```

# Route runs

## R1a (M1)

```
Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.A_session_key_cannot_name_the_owner_of_a_restored_worker_by_writing_the_bosses_restored_id [9 ms]
    Assert.Null() Failure: Value is not null
Failed!  - Failed:     1, Passed:    13, Skipped:     0, Total:    14, Duration: 9 s - CcDirector.Gateway.Tests.dll (net10.0)
```

## R1b (M2)

```
Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.Restore_marks_are_refused_to_a_session_key_to_the_owners_browser_and_to_a_Director_without_the_lease [6 ms]
    Assert.Equal() Failure: Values differ
Failed!  - Failed:     1, Passed:    13, Skipped:     0, Total:    14, Duration: 9 s - CcDirector.Gateway.Tests.dll (net10.0)
```

## R2 (M3)

```
Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.A_captured_seat_still_running_is_refused_and_starts_once_it_has_closed [9 ms]
    Assert.Throws() Failure: No exception was thrown
Failed!  - Failed:     1, Passed:    13, Skipped:     0, Total:    14, Duration: 9 s - CcDirector.Gateway.Tests.dll (net10.0)
```

## R3a (M4b)

```
Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.The_spawn_door_records_the_created_session_on_the_seat_whose_token_it_carries [11 ms]
    Assert.Equal() Failure: Strings differ
Failed!  - Failed:     1, Passed:    13, Skipped:     0, Total:    14, Duration: 12 s - CcDirector.Gateway.Tests.dll (net10.0)
```

## R3b (M4f)

```
Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.A_restore_claim_from_a_session_key_or_a_person_is_refused_and_nothing_reaches_the_Director [11 ms]
    Assert.Equal() Failure: Values differ
Failed!  - Failed:     1, Passed:    13, Skipped:     0, Total:    14, Duration: 10 s - CcDirector.Gateway.Tests.dll (net10.0)
```

## R4a (M5)

```
Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.Two_Directors_on_the_captured_machine_get_one_lease [10 ms]
    Assert.Equal() Failure: Values differ
Failed!  - Failed:     1, Passed:    13, Skipped:     0, Total:    14, Duration: 11 s - CcDirector.Gateway.Tests.dll (net10.0)
```

## R4b (M5c)

```
Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.A_restore_the_Director_refuses_gives_back_the_lease_it_was_granted [6 ms]
    Assert.Null() Failure: Value is not null
Failed!  - Failed:     1, Passed:    13, Skipped:     0, Total:    14, Duration: 9 s - CcDirector.Gateway.Tests.dll (net10.0)
```

# Command line (item 6)

First run: `raise SystemExit(EXIT_ACCEPTED_NOT_WAITED)` replaced by `return`. Second run: the printed status changed
from "accepted, not waited" to "taken". The file was restored after each; the suite then passed (11 passed).

```
>       assert as_json.exit_code == 3, as_json.output
E       assert 0 == 3
FAILED tests/test_director_restore.py::test_no_wait_says_accepted_not_waited_reads_nothing_and_does_not_exit_0
1 failed, 10 passed in 1.20s
        assert as_json.exit_code == 3, as_json.output
        assert out["taken"] is True and out["count"] == 2
>       assert out["waited"] is False and out["status"] == "accepted, not waited"
E       AssertionError: assert (False is False and 'taken' == 'accepted, not waited'
FAILED tests/test_director_restore.py::test_no_wait_says_accepted_not_waited_reads_nothing_and_does_not_exit_0
1 failed, 10 passed in 0.56s
```
