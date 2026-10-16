# Phase 2 gate: the session connection fields (pull request #4077)

Run by the phase 2 Tech Lead's gate seat on SORENLAPTOP (Windows), 16 October 2026.

- Commit tested: `c5b548755` (the pull request head; this note is the only change after it).
- Command: `.\scripts\test-local.ps1 -Parked`
- Verdict line: `RESULT: FAILED in 2 project(s)`, exit code 1. Run folder `C:\Users\soren\AppData\Local\Temp\cc-test-local-af71fc9d`.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| Core.UnitTests | 1,905 | 0 | 0 |
| Avalonia.Tests | 1,063 | 0 | 0 |
| Engine.Tests | 85 | 0 | 0 |
| HostedAgent.Tests | 85 | 0 | 0 |
| Launcher.Tests | 214 | 0 | 0 |
| Terminal.Avalonia.Tests | 71 | 0 | 0 |
| Reclaim.Tests | 305 | 0 | 0 |
| cc-director-setup.Tests | 39 | 0 | 0 |
| cc-director-setup-engine.Tests | 884 | 0 | 0 |
| cc-director-setup-cli.Tests | 74 | 0 | 0 |
| Gateway.Tests shard 1 of 4 | 412 | 0 | 0 |
| Gateway.Tests shard 2 of 4 | 478 | 2 | 0 |
| Gateway.Tests shard 3 of 4 | 263 | 0 | 0 |
| Gateway.Tests shard 4 of 4 | 469 | 0 | 0 |
| Gateway.UnitTests | 13,456 | 0 | 0 |
| Core.Tests | 4,526 | 1 | 0 |

The three failures, each failing again on `-RerunFailed` (verdict `RESULT: FAILED AGAIN`, folder
`C:\Users\soren\AppData\Local\Temp\cc-test-local-5de75bfe`) with the same error:

1. `PathContainmentLinkEscapeTests.ResolveSessionFile_fileSymbolicLinkUnderTheRootEscapingIt_isRefused`
2. `PathContainmentLinkEscapeTests.ResolveScreenshot_fileLinkPlantedInsideTheScreenshotsFolder_isRefused`
3. `SkillRound7SharedRootCollisionTests.A_dangling_link_at_a_wanted_name_is_left_untouched_and_the_other_skills_are_placed`

Error: "This host cannot create a (file) symbolic link ... On Windows that needs Developer Mode or
elevation." SORENLAPTOP has neither. None of the three touches what this pull request changes (two
string fields on `SessionDto` and their documentation).

**Delivery Lead ruling (16 October 2026), standing for this mission on SORENLAPTOP:** exactly these
three tests, failing with that same symbolic-link permission error on rerun, do not block a merge; any
other failure does. The tests are not deleted or changed by this mission; the question goes to the
owner in the mission's final report.
