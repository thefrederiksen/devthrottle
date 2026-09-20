# Session Cards - the Windows test gate

Measured 2026-09-19 on SOREN_NORTH (Windows 11), by the Windows gate Worker session 78f84d2f.
Nothing in either tree was changed. This is a measurement, not a fix.

## Verdict

**RED. The default gate exits 1 on the mission branch - and exits 1 on origin/main with the identical
two failures.** The two failures are not caused by the Session Cards work. They are not a pass either:
the gate is red on this machine from inside any Director session, on the release baseline too.

## 1. The command and its exit code

Run from the root of a worktree cut at `origin/mission/session-cards` (commit `af4671a0e`):

    .\scripts\test-local.ps1

No flag is needed; the default run is the default set. **Exit code: 1.**

## 2. Did the solution build

**Yes, whole.** All three builds the script performs (the solution and the two installer test
projects) reported `Build succeeded. 0 Warning(s) 0 Error(s)`. No project failed to build. All nine
test projects started and all nine wrote a result file with every test executed.

## 3. Counts - mission branch (`af4671a0e`)

| Suite | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| CcDirector.Core.UnitTests | 706 | 706 | 0 | 0 |
| CcDirector.Avalonia.Tests | 595 | 595 | 0 | 0 |
| CcDirector.Engine.Tests | 63 | 63 | 0 | 0 |
| CcDirector.HostedAgent.Tests | 88 | 88 | 0 | 0 |
| CcDirector.Launcher.Tests | 197 | 195 | **2** | 0 |
| CcDirector.Terminal.Avalonia.Tests | 30 | 30 | 0 | 0 |
| CcDirector.Reclaim.Tests | 135 | 135 | 0 | 0 |
| cc-director-setup.Tests | 25 | 25 | 0 | 0 |
| cc-director-setup-engine.Tests | 614 | 614 | 0 | 0 |
| **Whole run** | **2453** | **2451** | **2** | **0** |

### The two failing tests

Both are in `src\CcDirector.Launcher.Tests\LauncherDeclaredCapabilitiesTests.cs`.

1. `CcDirector.Launcher.Tests.LauncherDeclaredCapabilitiesTests.An_unarmed_launcher_declares_no_restart_signal_and_that_is_a_NO_not_an_unknown`
   - Line 178: `Assert.False(LauncherDeclaredCapabilities.Describe().RestartSignalArmed)`
   - Message: `Assert.False() Failure  Expected: False  Actual: True`
2. `CcDirector.Launcher.Tests.LauncherDeclaredCapabilitiesTests.Describing_the_launcher_asks_the_signal_and_never_raises_it`
   - Line 160: `Assert.False(declaration.RestartSignalArmed)`
   - Message: `Assert.False() Failure  Expected: False  Actual: True`

## 4. The baseline - origin/main (`9eb3a13bc`)

Same command, second worktree cut at `origin/main`. **Exit code: 1. Build succeeded whole.**

| Suite | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| CcDirector.Core.UnitTests | 764 | 764 | 0 | 0 |
| CcDirector.Avalonia.Tests | 554 | 554 | 0 | 0 |
| CcDirector.Engine.Tests | 63 | 63 | 0 | 0 |
| CcDirector.HostedAgent.Tests | 88 | 88 | 0 | 0 |
| CcDirector.Launcher.Tests | 197 | 195 | **2** | 0 |
| CcDirector.Terminal.Avalonia.Tests | 30 | 30 | 0 | 0 |
| CcDirector.Reclaim.Tests | 193 | 193 | 0 | 0 |
| cc-director-setup.Tests | 25 | 25 | 0 | 0 |
| cc-director-setup-engine.Tests | 619 | 619 | 0 | 0 |
| **Whole run** | **2533** | **2531** | **2** | **0** |

The two failing tests on main are **the same two tests with the same assertion message.**

## 5. Whose failure is it - observed, not inferred

The failure belongs to the repository plus this machine's session environment, not to the mission.

What was observed, on the mission worktree's already-built binaries, same test class, same minute:

- With `CC_DIRECTOR_ROOT` as every Director session has it
  (`C:\Users\soren\AppData\Local\cc-director\instances\default`): 2 failed, 15 passed of 17.
- With `CC_DIRECTOR_ROOT` removed from the process: 0 failed, 17 passed of 17.
- The whole Launcher suite with the variable removed: 197 passed, 0 failed, under both target frameworks.

What the code says: `LauncherDeclaredCapabilities.RestartSignalArmedNow()` asks the Windows kernel whether
anything is listening on the restart signal named for the serving root. The tests assume "this test
process armed nothing, so the answer is no". Inside a session the root is the live instance, whose real
launcher is running and listening - so the kernel honestly answers yes. The test reads the machine, not
just its own process. On a Mac the kernel cannot be asked, which is why this never showed there.

This is the same family as the known instance-home test problem (issue 2740): a test whose answer
depends on the session's environment.

## 6. Things the reader should not miss

- **The mission branch is 12 commits behind origin/main** (and 10 ahead). The count differences above
  (Core.UnitTests 706 against 764, Reclaim 135 against 193, Avalonia 595 against 554) are that distance,
  not lost tests. But it means this run did NOT test the mission joined with current main. A gate on the
  rebased or merged result is still owed before landing.
- **The script printed a COVERAGE GAP**: the change touches code covered by the three parked suites
  (`CcDirector.Core.Tests`, `CcDirector.Gateway.Tests`, `CcDirector.Gateway.UnitTests`), which the default
  run does not execute. I was asked for the default gate and ran only that. `-Parked` was not run.
- **The Launcher suite runs twice** (once per target framework) and the second run overwrites the first
  run's result file. Both runs reported 2 failed of 197, so nothing was hidden this time, but the script
  counts the suite once.
- The default run executes no web tests and no Python tests.

## 7. Evidence kept on this machine

- Mission run logs and result files: `C:\Users\soren\AppData\Local\Temp\cc-test-local-6055f97c`
- Baseline run logs and result files: `C:\Users\soren\AppData\Local\Temp\cc-test-local-7c088b5a`
- Worktrees: `D:\ReposFred\devthrottle-gate-sessioncards` and `D:\ReposFred\devthrottle-gate-baseline-main`
