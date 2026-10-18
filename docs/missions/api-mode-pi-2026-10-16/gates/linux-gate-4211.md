# Linux gate: pull request #4211

**Verdict: PASSES on everything Linux can measure.** No test fails on the pull request that does
not also fail on main. The two test classes this pull request fixes, `ToolReconcilerTests` and
`PythonToolsHealAndShimTests`, are Windows-only and cannot be judged on Linux; the Windows re-check
must run them.

Linux gate under the owner's decision of 18 October; Windows re-check pending.

- Pull request: #4211, branch `fix/non-default-root-keeps-global-bin`
- Commit tested: `342f1277dba466b35463d07a3ca05d8dc8a6d510`. The branch head already merges main
  `dac07950f`, so `git merge --no-edit origin/main` reported "Already up to date" and made no new
  merge commit. The tested commit is the branch head.
- Main commit for the baseline: `dac07950fb5c0736e5f661161ba9730ed2d492ef`
- Machine: devlinux, .NET 10.0.301, `DOTNET_USE_POLLING_FILE_WATCHER=1`, one suite at a time
- Filter on every .NET suite: `Gate!=Quarantine`, plus
  `FullyQualifiedName!~PythonToolsHealAndShimTests&FullyQualifiedName!~ToolReconcilerTests` on
  main and on the comparable branch run (see below)
- The setup engine suite needs `-p:EnableWindowsTargeting=true` to build on Linux, on both sides.
- Python: pytest 9.1.1 with typer 0.27.3 in a throwaway environment, the continuous integration
  job's unpinned set, run from `tools/cc-devthrottle` with `FORCE_COLOR=1`, one file at a time.
- After every suite, `~/.local/bin/cc-devthrottle` still pointed into
  `~/.local/share/cc-director/pyenv/bin`.

## Why these suites

The pull request changes Core (`src/CcDirector.Core`) and its tests, the desktop app's
`Program.cs`, the setup engine (`tools/cc-director-setup-engine`) and the Python tool
`tools/cc-devthrottle/src/setup_ops.py`. So it runs CcDirector.Core.UnitTests,
CcDirector.Core.Tests, CcDirector.Avalonia.Tests (it builds on Linux), CcDirector.Setup.Engine.Tests,
and the pytest files `test_setup_ops.py` and `test_setup_machine_root.py`.

## Results

| Suite | Pull request: passed / failed / skipped | Main: passed / failed / skipped |
|---|---|---|
| CcDirector.Core.UnitTests | 2,413 / 5 / 23 | 2,400 / 5 / 23 |
| CcDirector.Core.Tests | 4,520 / 8 / 12 | 4,519 / 8 / 12 |
| CcDirector.Avalonia.Tests | 1,093 / 0 / 0 | 1,093 / 0 / 0 |
| CcDirector.Setup.Engine.Tests, the two classes left out on both sides | 866 / 27 / 0 | 858 / 27 / 0 |
| `test_setup_ops.py` | 41 / 0 | 34 / 0 |
| `test_setup_machine_root.py` | 9 / 0 | 9 / 0 |

The extra passing tests on the pull request are its new tests.

## Failing tests, and why none counts

In each row of the table above, the list of failed test names is identical on the pull request
and on main. No test needed a re-run alone.

- CcDirector.Core.UnitTests (5): the same Windows-path tests that fail on main on Linux
  (`CcWorktreesPoolSlotsTests.HasSlotLayout_RecognisesOnlyTheShapeThePoolCreates`, three cases;
  `WorktreePoolSettingsTests.For_IgnoresHowThePathWasTyped`;
  `RepositorySourceIndexTests.ListFiles_returns_absolute_paths_in_the_platforms_separators`).
- CcDirector.Core.Tests (8): all in `LinkDetectorExactTextTests` (bare file names, Git Bash paths,
  one-letter folders, quoted paths after web addresses), the same on main.
- CcDirector.Setup.Engine.Tests (27): `InstalledRoleDetectorTests` (4), `InstallLayoutTests` (4),
  `PythonToolsCrashSafeTests` (2), `PythonToolsInUseTests` (11),
  `StandbySlotProvisionerTests.ReadHealthGate_InstancesThatCannotBeListed_IsUnreadableNeverClear`,
  `ToolUpdaterTests` (2), and `UninstallerTests.TheInstallersPathStep_AppendsTheMasterUnlessItIsAlreadyThere`
  (3 cases with `C:\` paths), the same on main.

## The two classes this pull request fixes

On this branch only, the two classes that write `~/.local/bin` links were also run, because the
pull request fixes them. Both are marked `[SupportedOSPlatform("windows")]`. They put the fake
venv scripts in `pyenv/Scripts` and look for `.cmd` launchers, while on Linux the product reads
`pyenv/bin`. That attribute does not skip a test at run time, so on Linux they fail because of the
tests' Windows assumptions, and the failures say nothing about the product:

- `ToolReconcilerTests`: the test host crashes with an unhandled
  `DirectoryNotFoundException` on `.../cc-reconcile-<id>/local/pyenv/bin/cc-pdf`. It crashed in the
  full-suite run, which aborted after 10 tests, and again when the class was run alone.
- `PythonToolsHealAndShimTests`, run alone: 8 passed, 9 failed. The failures are
  `DirectoryNotFoundException` on `.../cc-pyheal-<id>/local/pyenv/bin/cc-pdf` and assertions that
  a `.cmd` launcher survived the purge.

Neither class may be run on main here (it writes the real `~/.local/bin` links), so there is no
Linux baseline for them. Because they cannot be judged on Linux, they do not block this gate. They
are the main thing the Windows re-check must run.

Also seen: on Linux, running the whole setup engine suite with `ToolReconcilerTests` included
aborts the run after 10 tests, because that class crashes the test host. Any Linux run of this
suite has to leave that class out.

## A slip during this gate, recorded so it is not repeated

The first two attempts at the setup engine suite looked the project up by its folder name,
`cc-director-setup-engine.Tests`, but the project file is `CcDirector.Setup.Engine.Tests.csproj`.
The lookup came back empty and `dotnet test` ran the whole solution, with several suites at once,
against the one-suite-at-a-time memory rule. Memory stayed above 8 GB available and nothing was
killed. Those results were thrown away and are not used above, and the runner now refuses to start
when it finds no project.

## What this gate does not cover

- Windows, including the two classes above, which this pull request exists to fix.
- The failures listed above that occur on main on Linux. They are the same on both sides, but this
  gate cannot say what the pull request does to them on a machine where they pass.
- The other Python test files in `tools/cc-devthrottle/tests`, and the web suites. Only the two
  pytest files the pull request concerns were run.
