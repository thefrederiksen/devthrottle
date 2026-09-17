## 1 placeholder not resolved (the restarted owner's OLD id is named, as the old command did)
file: src/CcDirector.ControlApi/Drain/DirectorRestore.cs
replaced: '        if (!string.IsNullOrWhiteSpace(boss.RestoredSessionId))\n            return (boss.RestoredSessionId, null);'
with: '        if (!string.IsNullOrWhiteSpace(boss.RestoredSessionId))\n            return (reportsTo, null);'
filter: FullyQualifiedName~DirectorRestoreTests
  Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_AWorkerListedBeforeItsManager_ComesBackAfterIt_OwnedByTheManagersNewId [51 ms]
  Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_AnOwnerRestoredInAnEarlierRun_IsUsedByItsNewId [< 1 ms]
  Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_AThreeLevelChain_ResolvesEachPlaceholderToTheLevelAbove [< 1 ms]
Failed!  - Failed:     3, Passed:    14, Skipped:     0, Total:    17, Duration: 69 ms - CcDirector.Gateway.UnitTests.dll (net10.0)

## 1b same mutation, route test on the real host
file: src/CcDirector.ControlApi/Drain/DirectorRestore.cs
replaced: '        if (!string.IsNullOrWhiteSpace(boss.RestoredSessionId))\n            return (boss.RestoredSessionId, null);'
with: '        if (!string.IsNullOrWhiteSpace(boss.RestoredSessionId))\n            return (reportsTo, null);'
filter: FullyQualifiedName~WorkspaceRestoreRouteTests
  Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.A_Director_restoring_controlled_seats_starts_each_under_its_real_owner [31 ms]
Failed!  - Failed:     1, Passed:     6, Skipped:     0, Total:     7, Duration: 4 s - CcDirector.Gateway.Tests.dll (net10.0)

## 2 the owner pin removed: a session key may name any owner
file: src/CcDirector.Gateway/Api/SpawnOrigin.cs
replaced: 'if (!Guid.TryParse(stated, out var statedId) || statedId != caller.SessionId)'
with: 'if (!Guid.TryParse(stated, out var statedId))'
filter: FullyQualifiedName~WorkspaceRestoreRouteTests
  Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.The_same_create_from_a_session_key_is_refused_and_nothing_reaches_the_Director [5 ms]
Failed!  - Failed:     1, Passed:     6, Skipped:     0, Total:     7, Duration: 4 s - CcDirector.Gateway.Tests.dll (net10.0)

## 3 one seat's failure stops the rest (the failure is thrown, not recorded)
file: src/CcDirector.ControlApi/Drain/DirectorRestore.cs
replaced: '                    failure = $"the Gateway did not start it: {ex.Message}";'
with: '                    throw;'
filter: FullyQualifiedName~DirectorRestoreTests
  Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_AFailedSeatAskedAgain_ComesBack_AndItsFailureIsCleared [< 1 ms]
  Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_AnOwnerThatFailed_FailsItsWorkers_WithTheReason_AndNeverStartsThem [< 1 ms]
  Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_OneSeatRefused_IsReportedOnThatSeat_AndTheRestStillComeBack [< 1 ms]
Failed!  - Failed:     3, Passed:    14, Skipped:     0, Total:    17, Duration: 55 ms - CcDirector.Gateway.UnitTests.dll (net10.0)

## 4 no seniors-first ordering (record order kept)
file: src/CcDirector.ControlApi/Drain/DirectorRestore.cs
replaced: '            .OrderBy(x => x.depth)\n            .ThenBy(x => x.seat.SortOrder)'
with: '            .OrderBy(x => 0)\n            .ThenBy(x => x.seat.SortOrder)'
filter: FullyQualifiedName~DirectorRestoreTests
  Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_AWorkerListedBeforeItsManager_ComesBackAfterIt_OwnedByTheManagersNewId [36 ms]
  Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_AThreeLevelChain_ResolvesEachPlaceholderToTheLevelAbove [< 1 ms]
Failed!  - Failed:     2, Passed:    15, Skipped:     0, Total:    17, Duration: 49 ms - CcDirector.Gateway.UnitTests.dll (net10.0)

## 6 who asked is not stamped from the credential
file: src/CcDirector.Gateway/Api/WorkspaceEndpoints.cs
replaced: '                RequestedBySessionId = askedBy,'
with: '                RequestedBySessionId = null,'
filter: FullyQualifiedName~WorkspaceRestoreRouteTests
  Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.A_session_key_asks_for_a_restore_and_the_order_names_that_session_whatever_the_body_says [2 ms]
Failed!  - Failed:     1, Passed:     6, Skipped:     0, Total:     7, Duration: 4 s - CcDirector.Gateway.Tests.dll (net10.0)

## 7 the guard does not classify the restore route
file: src/CcDirector.Gateway/Util/SessionKeyGuard.cs
replaced: '        if (s.Length == 4 && s[3] == "restore") return verb is "POST";'
with: ''
filter: FullyQualifiedName~WorkspaceRestoreRouteTests
  Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.A_restore_naming_no_Director_is_a_bad_request [7 ms]
  Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.A_session_key_asks_for_a_restore_and_the_order_names_that_session_whatever_the_body_says [1 ms]
  Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.A_restore_onto_a_Director_on_another_machine_is_refused_before_anything_is_sent [2 ms]
  Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.A_restore_the_Director_refuses_is_answered_with_its_reason [1 ms]
Failed!  - Failed:     4, Passed:     3, Skipped:     0, Total:     7, Duration: 4 s - CcDirector.Gateway.Tests.dll (net10.0)

## 8 an authored workspace is restored
file: src/CcDirector.ControlApi/Drain/DirectorRestore.cs
replaced: '        if (!string.Equals(doc.Origin, WorkspaceOrigins.Captured, StringComparison.Ordinal))'
with: '        if (false)'
filter: FullyQualifiedName~DirectorRestoreTests
  Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.PrepareAsync_AnAuthoredWorkspace_IsRefused_BecauseItsOwnersAreTyped [< 1 ms]
Failed!  - Failed:     1, Passed:    16, Skipped:     0, Total:    17, Duration: 52 ms - CcDirector.Gateway.UnitTests.dll (net10.0)

## 9 a seat already back is started again
file: src/CcDirector.ControlApi/Drain/DirectorRestore.cs
replaced: '                        && string.IsNullOrWhiteSpace(s.RestoredSessionId))'
with: ')'
filter: FullyQualifiedName~DirectorRestoreTests
  Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_AnOwnerRestoredInAnEarlierRun_IsUsedByItsNewId [2 ms]
  Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_ASeatAlreadyBack_IsNeverStartedTwice [< 1 ms]
Failed!  - Failed:     2, Passed:    15, Skipped:     0, Total:    17, Duration: 59 ms - CcDirector.Gateway.UnitTests.dll (net10.0)

## 10 the machine check removed
file: src/CcDirector.Gateway/Api/WorkspaceEndpoints.cs
replaced: '                && !string.Equals(doc.Machine, director.MachineName, StringComparison.OrdinalIgnoreCase))'
with: '                && false)'
filter: FullyQualifiedName~WorkspaceRestoreRouteTests
  Failed CcDirector.Gateway.Tests.WorkspaceRestoreRouteTests.A_restore_onto_a_Director_on_another_machine_is_refused_before_anything_is_sent [4 ms]
Failed!  - Failed:     1, Passed:     6, Skipped:     0, Total:     7, Duration: 4 s - CcDirector.Gateway.Tests.dll (net10.0)

## 11 the drain command still names the owner
file: src/CcDirector.ControlApi/Drain/DirectorDrain.cs
replaced: '                ? DrainRestoreCommand.Build(WorkspaceId, seat.SessionId!)'
with: '                ? DrainRestoreCommand.Build(WorkspaceId, seat.SessionId!) + (seat.ReportsTo is null ? "" : " --controlled-by " + seat.ReportsTo)'
filter: FullyQualifiedName~DirectorDrainTests
  Failed CcDirector.Gateway.UnitTests.Drain.DirectorDrainTests.Drain_ARestoreCommandNamesNoOwner_TheDirectorResolvesItFromTheCapture [2 ms]
Failed!  - Failed:     1, Passed:    57, Skipped:     0, Total:    58, Duration: 223 ms - CcDirector.Gateway.UnitTests.dll (net10.0)

## 5 an owner that failed in this run is not recognised as failed
file: src/CcDirector.ControlApi/Drain/DirectorRestore.cs
replaced: '        if (failedHere.TryGetValue(reportsTo, out var why))'
with: '        if (failedHere.TryGetValue(reportsTo, out var why) && why == "never")'
filter: FullyQualifiedName~DirectorRestoreTests
  Failed CcDirector.Gateway.UnitTests.Drain.DirectorRestoreTests.RunAsync_AnOwnerThatFailed_FailsItsWorkers_WithTheReason_AndNeverStartsThem [1 ms]
Failed!  - Failed:     1, Passed:    16, Skipped:     0, Total:    17, Duration: 58 ms - CcDirector.Gateway.UnitTests.dll (net10.0)

## 12 the failure length cap removed
file: src/CcDirector.Gateway/Workspaces/WorkspaceValidation.cs
replaced: '            CapLength($"{where}.restore.failure", restore.Failure, MaxTextFieldChars);'
with: ''
filter: FullyQualifiedName~WorkspaceOwedSeatsAndOriginRulesTests
  Failed CcDirector.Gateway.Tests.WorkspaceOwedSeatsAndOriginRulesTests.A_restore_failure_is_capped_like_every_other_judgment [34 ms]
Failed!  - Failed:     1, Passed:    11, Skipped:     0, Total:    12, Duration: 1 s - CcDirector.Gateway.UnitTests.dll (net10.0)

## 13 an authored workspace may claim a restore attempt
file: src/CcDirector.Gateway/Workspaces/WorkspaceValidation.cs
replaced: '                claimed.Add("a seat restore attempt");'
with: '                { }'
filter: FullyQualifiedName~WorkspaceOwedSeatsAndOriginRulesTests
  Failed CcDirector.Gateway.Tests.WorkspaceOwedSeatsAndOriginRulesTests.An_authored_workspace_cannot_claim_a_restore_attempt [36 ms]
Failed!  - Failed:     1, Passed:    11, Skipped:     0, Total:    12, Duration: 1 s - CcDirector.Gateway.UnitTests.dll (net10.0)

## 14 command line: an old failure read as this run's answer
file: tools/cc-devthrottle/src/machine_ops.py
replaced: fresh = attempted and attempted != before.get(sid.lower(), "")
with: fresh = True
        # The worker failed in an EARLIER run (attempt stamp t0). Until the Director writes a new stamp, that is not
E               "outcome": "failed",
FAILED tests/test_director_restore.py::test_an_old_failure_is_not_this_runs_answer
1 failed, 8 passed in 0.20s

## 15 command line: a failed or pending seat exits 0
file: tools/cc-devthrottle/src/machine_ops.py
replaced: if not ok:
        raise SystemExit(1)
with: if False:
        raise SystemExit(1)
__________ test_one_failed_seat_is_reported_and_the_exit_code_says_so __________
    def test_one_failed_seat_is_reported_and_the_exit_code_says_so(fake):
FAILED tests/test_director_restore.py::test_one_failed_seat_is_reported_and_the_exit_code_says_so
FAILED tests/test_director_restore.py::test_a_seat_with_no_answer_when_the_wait_runs_out_is_pending_and_the_exit_code_says_so
2 failed, 7 passed in 0.19s

