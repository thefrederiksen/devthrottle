# Linux gate: pull request #4179

**Verdict: PASSES.** No test fails on the pull request that does not also fail on main.

Linux gate under the owner's decision of 18 October; Windows re-check pending.

- Pull request: #4179, "API mode phase 2 follow-on: a turn whose end was lost gets an explicit ending"
- Branch: `api-mode-lost-turn-end`
- Commit tested: `cdd81b9a6e3d3743b53ac23bdcc9e7bd06eba411`. The branch head already merges main
  `dac07950f`, so `git merge --no-edit origin/main` reported "Already up to date" and made no new
  merge commit. The tested commit is the branch head.
- Main commit for the baseline: `dac07950fb5c0736e5f661161ba9730ed2d492ef`
- Machine: devlinux, .NET 10.0.301, `DOTNET_USE_POLLING_FILE_WATCHER=1`, one suite at a time
- Filter on every suite:
  `Gate!=Quarantine&FullyQualifiedName!~PythonToolsHealAndShimTests&FullyQualifiedName!~ToolReconcilerTests`
- After every suite, `~/.local/bin/cc-devthrottle` still pointed into
  `~/.local/share/cc-director/pyenv/bin`.

## Why these suites

The pull request changes only the Gateway (`src/CcDirector.Gateway`), its contracts
(`src/CcDirector.Gateway.Contracts`), Gateway unit tests and one review document. So it runs
CcDirector.Gateway.UnitTests and CcDirector.Gateway.Tests, plus CcDirector.Core.UnitTests, which
always runs.

## Results

| Suite | Pull request: passed / failed / skipped | Main: passed / failed / skipped |
|---|---|---|
| CcDirector.Core.UnitTests | 2,400 / 5 / 23 | 2,400 / 5 / 23 |
| CcDirector.Gateway.UnitTests | 14,423 / 4 / 2 | 14,375 / 4 / 2 |
| CcDirector.Gateway.Tests | 1,771 / 10 / 1 | 1,771 / 10 / 1 |

CcDirector.Gateway.UnitTests has 48 more tests on the pull request than on main. These are the
pull request's new tests, and they all pass.

## Failing tests, and why none counts

In each suite, the list of failed test names is identical on the pull request and on main. So
every failure below is already on main, and none counts against this pull request. No test needed
a re-run alone.

CcDirector.Core.UnitTests (5, the same on main). They use Windows paths or Windows separators:
- `CcWorktreesPoolSlotsTests.HasSlotLayout_RecognisesOnlyTheShapeThePoolCreates`, three cases (`D:\Repos\devthrottle.worktrees\wt01`, `wt02`, `wt123`)
- `WorktreePoolSettingsTests.For_IgnoresHowThePathWasTyped`
- `RepositorySourceIndexTests.ListFiles_returns_absolute_paths_in_the_platforms_separators`

CcDirector.Gateway.UnitTests (4, the same on main):
- `DefinitionFormatDocumentTests.Document_NamesEveryKeyTheSchemasTake`
- `HeldDeliveryDriverTests.ARecordWriteThatThrowsAfterTheClaimMark_IsPressedAgainByTheDriver_ResolvedWithNoRestart`
- `VoiceAutoOffLineTests.ClearForVoiceOn_WhenTheSaveFails_DoesNotThrowAndTheNextVoiceOnWritesIt`
- `VoiceUnheardCountTests.NotePlayed_AResetWhoseSaveFailed_IsWrittenAgainByTheNextPlay`

CcDirector.Gateway.Tests (10, the same on main):
- `ContextLessRouteCensusTests.The_selfhost_context_less_route_set_adds_only_the_hosted_denied_families`
- `FleetSpawnMissionAttachTests.Gateway_resolved_mission_attaches_directly_without_any_local_lookup`
- `FleetSpawnMissionAttachTests.No_mission_on_the_create_attaches_nothing`
- `FleetSpawnOriginTests.An_agent_spawn_records_who_asked_and_which_session_asked`
- `FleetSpawnOriginTests.A_parent_named_on_a_human_origin_is_dropped`
- `FleetSpawnOriginTests.A_spawn_that_names_no_surface_is_recorded_with_unknown_kind_and_no_parent`
- `FleetSpawnOriginTests.Lineage_is_recorded_even_when_the_new_session_has_no_controller`
- `HostedProcessControlDenyTests.Hosted_director_launch_is_refused`
- `WorkflowSeatTests.A_gateway_resolved_seat_isStamped_andBriefsTheAgent`
- `WorkflowSeatTests.An_unseated_spawn_isUnaffected_andCarriesNoSeatParagraph`

## What this gate does not cover

- Windows. The Windows-only behaviour of these suites is not exercised on Linux, and the failures
  above that come from Windows paths hide whatever those tests would say on Windows.
- The 19 tests that fail on main on Linux. They are the same on both sides, but this gate cannot
  say whether the pull request changes what they would show on a machine where they pass.
- Web and Python suites. The pull request touches neither, so neither was run.
