# Phase 2 gate: take the wheel, hand back and stop the turn (pull request #4079)

Run by the phase 2 Tech Lead's gate seat on SORENLAPTOP (Windows), 16 October 2026.

- Commit tested: `0d90541ef` (the pull request head; this note is the only change after it).
- Command: `.\scripts\test-local.ps1 -Parked` (Debug). Every suite ran all of its tests.
- Verdict line: `FAILED in 3 projects`. Run folder `C:\Users\soren\AppData\Local\Temp\cc-test-local-cf20dd03`;
  rerun of the failed tests (`-RerunFailed`, once) in `C:\Users\soren\AppData\Local\Temp\cc-test-local-412efab8`.

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
| Gateway.Tests shard 3 of 4 | 262 | 1 | 0 |
| Gateway.Tests shard 4 of 4 | 469 | 0 | 0 |
| Gateway.UnitTests | 13,521 | 0 | 0 |
| Core.Tests | 4,526 | 1 | 0 |

The four failures:

1. `PathContainmentLinkEscapeTests.ResolveScreenshot_fileLinkPlantedInsideTheScreenshotsFolder_isRefused`
2. `PathContainmentLinkEscapeTests.ResolveSessionFile_fileSymbolicLinkUnderTheRootEscapingIt_isRefused`
3. `SkillRound7SharedRootCollisionTests.A_dangling_link_at_a_wanted_name_is_left_untouched_and_the_other_skills_are_placed`

   All three failed again on the rerun with the same error: this host cannot create a symbolic link.
   They fall under the Delivery Lead's standing ruling recorded in `phase-2-session-fields-gate.md`.

4. `SessionStopEndpointTests.A_second_stop_straight_after_the_first_succeeds_quietly` (Gateway.Tests,
   `SessionStopEndpointTests.cs` line 158): the FIRST `POST /sessions/{sid}/stop` answered 503 Service
   Unavailable instead of 200. It **passed on the rerun**. It tests the session stop route, which this
   pull request does not change: the pull request's only edit to `GatewayEndpoints.cs` adds three new route
   lines (`/wheel/take`, `/wheel/hand-back`, `/turn/stop`) after the escape route. Whether the 503 is a race
   in the stop route or a test that failed on correct code is outside this mission; it is reported to the
   Delivery Lead for the owner, and the test is not touched here.
