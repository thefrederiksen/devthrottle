# Phase 4 gate: the Director pushes its API-mode events (pull request #4090)

Run by the phase 4 Tech Lead's gate seat on SORENLAPTOP (Windows), 17 October 2026.

- Commit tested: `1a0b55a7a`, a clean local merge of origin/main at `ba4a44fa4` (which includes the transport
  test hang fix, #4102, at `7a66635a7`) with the pull request branch at `38712a0eb`. Nothing was pushed from the gate.
  The only change to the branch after it is one paragraph in `event-model.md` (holes start a new generation).
- Command: `.\scripts\test-local.ps1 -Parked` (Debug). Every suite ran all of its tests.
- Time: 20 minutes 19 seconds; the rerun of the failed tests (`-RerunFailed`, once) 29 seconds.
- Run folder `C:\Users\soren\AppData\Local\Temp\cc-test-local-bfd2f1e6`; rerun folder `cc-test-local-298f1a0e`.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| Core.UnitTests | 2,157 | 0 | 0 |
| Avalonia.Tests | 1,065 | 0 | 0 |
| Engine.Tests | 85 | 0 | 0 |
| HostedAgent.Tests | 85 | 0 | 0 |
| Launcher.Tests | 214 | 0 | 0 |
| Terminal.Avalonia.Tests | 71 | 0 | 0 |
| Reclaim.Tests | 305 | 0 | 0 |
| DeliveryQualification.Tests | 17 | 0 | 0 |
| cc-director-setup.Tests | 39 | 0 | 0 |
| cc-director-setup-engine.Tests | 884 | 0 | 0 |
| cc-director-setup-cli.Tests | 74 | 0 | 0 |
| Gateway.Tests shard 1 of 4 | 412 | 0 | 0 |
| Gateway.Tests shard 2 of 4 | 478 | 2 | 0 |
| Gateway.Tests shard 3 of 4 | 407 | 6 | 3 |
| Gateway.Tests shard 4 of 4 | 469 | 0 | 0 |
| Gateway.UnitTests | 13,956 | 0 | 0 |
| Core.Tests | 4,537 | 1 | 0 |

Totals: 24,859 passed, 9 failed, 3 skipped (the three skipped tests are Linux-only).

The nine failures, every one with the error "This host cannot create a FILE symbolic link (A required privilege
is not held by the client)", and every one failing again on the rerun with the same message:

1. `PathContainmentLinkEscapeTests.ResolveSessionFile_fileSymbolicLinkUnderTheRootEscapingIt_isRefused`
2. `PathContainmentLinkEscapeTests.ResolveScreenshot_fileLinkPlantedInsideTheScreenshotsFolder_isRefused`
3. `SessionFileAccessTests.Decide_symbolicLinkFromANamedPathToASecret_isRefused`
4. `SessionFileAccessTests.Decide_servesTheRealPathNotTheLink`
5. `SessionFileAccessTests.Decide_symbolicLinkInsideTheWorkingFolderToAProtectedRoot_isRefused`
6. `SessionFileAccessTests.Decide_symbolicLinkFromANamedPathToAnOrdinaryFile_isAllowed`
7. `SessionFileAccessTests.Decide_symbolicLinkToADotfileUnderHome_isRefused`
8. `SessionFileAccessTests.Decide_symbolicLinkInsideANamedFolderToAnUnnamedFile_isRefused`
9. `Skills.SkillRound7SharedRootCollisionTests.A_dangling_link_at_a_wanted_name_is_left_untouched_and_the_other_skills_are_placed`

None is touched by the pull request. They fall under the Delivery Lead's standing ruling (decision 14): a test
failing with the symbolic link permission error, and failing again on rerun, does not block. No test failed once
and passed on the rerun; nothing hung or crashed.

A first run on the older base `8cd44458a` was stopped when #4102 merged to main; that run's build had also failed
at the Gateway project's `npm ci` step, which succeeded by hand, and the second run built cleanly. Not a code defect.

**Verdict: passes the gate under the standing rulings.**
