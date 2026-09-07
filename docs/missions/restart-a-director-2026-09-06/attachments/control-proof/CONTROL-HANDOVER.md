# Handover - Rig Alpha - Worker B - launcher test run

Written 2026-09-07 because the Director told this session it is being restarted.

## Identity

- Session name: Rig Alpha - Worker B - launcher test run (session id 25ccf5f9)
- Mission: Rig Alpha - a status page for the restart rig
- Reports to: Rig Alpha - Manager (session id b60818cc). Never messages the owner.
- Working tree: D:\ReposFred\devthrottle-rig-alpha, branch restart-rig-alpha
- Seed: C:\Users\soren\AppData\Local\cc-director\vault\handovers\director-restart\2026-09-06T1725-DevThrottle_1\rig-seeds\SEED-alpha-worker-b.md

## Mandate

Run the launcher unit tests in the foreground and report the counts:

    dotnet test src\CcDirector.Launcher.Tests\CcDirector.Launcher.Tests.csproj --nologo -v q

Write passed, failed and skipped counts into docs/rig-alpha/TEST-RESULT.md (ASCII only) with note
W-7704. Do NOT commit and do NOT push. Send the Manager ONE line with the counts. Then wait.
If told to run again, run again the same way and update the file.

## State at handover

- Runs completed: two, both in the foreground, neither backgrounded nor piped.
  - Run 1 (mandate): 230 passed, 0 failed, 0 skipped. Reported to the Manager, delivery confirmed.
  - Run 2 (owner asked for a re-run): 230 passed, 0 failed, 0 skipped. File updated. The Manager has
    NOT been sent the second run's counts - the restart message arrived right after the run finished.
- The test project builds for two target frameworks (net10.0 and net10.0-windows), so each run is
  115 tests per framework, 230 combined.
- docs/rig-alpha/TEST-RESULT.md holds the last run's counts. It is UNCOMMITTED and UNPUSHED, by
  mandate. Do not commit or push it.
- No other files were changed. No branches created. No processes left running.

## Exact counts of the last run (run 2, 2026-09-07)

| Target framework  | Passed | Failed | Skipped | Total |
|-------------------|--------|--------|---------|-------|
| net10.0           | 115    | 0      | 0       | 115   |
| net10.0-windows   | 115    | 0      | 0       | 115   |
| Combined          | 230    | 0      | 0       | 230   |

Worker B note W-7704: the counts above came from one foreground run and were not re-run.

## For the session that picks this up

1. Read the seed file above; it is the whole mandate.
2. Optionally send the Manager (b60818cc) one line with the run 2 counts, since that was not done.
3. Then wait. Re-run only if told to, in the foreground, and update docs/rig-alpha/TEST-RESULT.md.

## Not verified

- Nothing beyond the two test runs was verified. The counts are read from the dotnet test summary
  lines, not from a results file.
