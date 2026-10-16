# Phase 2 gate: receipts and event ingest (pull request #4082)

Run by the phase 2 Tech Lead's gate seat on SORENLAPTOP (Windows), 16 October 2026.

- Commit tested: `b49fc9f73` (the pull request head; this note is the only change after it).
- Command: `.\scripts\test-local.ps1 -Parked` (Debug). Every suite ran all of its tests.
- Verdict line: `FAILED in 2 projects`. Run folder `C:\Users\soren\AppData\Local\Temp\cc-test-local-0b05a55b`;
  rerun of the failed tests (`-RerunFailed`, once) in `C:\Users\soren\AppData\Local\Temp\cc-test-local-23abad69`.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| Core.UnitTests | 2,049 | 0 | 0 |
| Avalonia.Tests | 1,063 | 0 | 0 |
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
| Gateway.Tests shard 3 of 4 | 263 | 0 | 0 |
| Gateway.Tests shard 4 of 4 | 469 | 0 | 0 |
| Gateway.UnitTests | 13,674 | 0 | 0 |
| Core.Tests | 4,526 | 1 | 0 |

The three failures, all failing again on the rerun with the same error (this host cannot create a
symbolic link), fall under the Delivery Lead's standing ruling recorded in `phase-2-session-fields-gate.md`:

1. `PathContainmentLinkEscapeTests.ResolveSessionFile_fileSymbolicLinkUnderTheRootEscapingIt_isRefused`
2. `PathContainmentLinkEscapeTests.ResolveScreenshot_fileLinkPlantedInsideTheScreenshotsFolder_isRefused`
3. `SkillRound7SharedRootCollisionTests.A_dangling_link_at_a_wanted_name_is_left_untouched_and_the_other_skills_are_placed`

No other test failed. The Gateway unit tests, which hold this pull request's new tests, passed in full.
