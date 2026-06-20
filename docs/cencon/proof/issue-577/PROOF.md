# Proof - Issue 577: self-healing cc-* tool install

Self-healing of the shared-venv Python tools install: a bounded pip step, atomic shims, version-record
gating, heal-on-unhealthy auto-update, and a self-checking shim body. Closes issues 445 and 452.

All work is in `tools/cc-director-setup-engine` and its test project. Unit tests are the proof for every
unit-testable acceptance criterion. The end-to-end "fresh install works" / "half-install repaired by
update" outcome requires a clean machine (or the opt-in `CC_PYBUNDLE_DIR` live harness) and is left for a
human / QA - see the bottom section.

## Source changes (the five required changes)

1. Bounded process run.
   - `ProcessRunner.cs`: `Run` now takes a `TimeSpan timeout` (named defaults
     `ProcessRunner.DefaultTimeout` = 15 min). On expiry it kills the whole process tree
     (`Kill(entireProcessTree: true)`) and returns `(TimeoutExitCode, "TIMEOUT: ...")` carrying the
     partial output. Existing two-argument and three-argument overloads still work (they pass the
     default).
   - `PythonToolsInstaller.cs`: named bounds `VenvCreateTimeout` (5 min) and `PipInstallTimeout` (15 min)
     are threaded into the venv-create and offline-pip `ProcessRunner.Run` calls. A pip timeout is
     surfaced as a loud "timed out and was killed" failure.

2. No stale shims on a partial install (atomic shim/venv).
   - `PythonToolsInstaller.InstallAsync`: new `RemoveManagedShims(manifest.Scripts)` runs up front (before
     the venv reset). Shims are (re)written only after pip succeeds AND `VenvHasAllTools` passes. A
     failed/interrupted run therefore never leaves a `bin\<name>.cmd` whose `pyenv\Scripts\<name>.exe`
     target is missing. Added a post-venv-create guard that fails loud if the venv produced no interpreter.

3. Version record gated on health.
   - `PythonToolsInstaller.InstallAsync`: a final `VenvHasAllTools(manifest.Scripts)` assertion now guards
     the shim write AND the `im.Set(ComponentId, manifest.BundleVersion)` record. A half-built venv never
     records a version that would suppress future repair. On a healthy install the expected script list is
     also persisted via `PythonToolsState.SaveScripts` (sidecar `python-tools-scripts.json`).

4. Self-healing auto-update.
   - `ToolUpdater.RefreshPythonToolsAsync`: the gate now reinstalls when the recorded version is current
     but the on-disk venv is UNHEALTHY, not only when the release is newer. Health is probed with the same
     `PythonToolsInstaller.VenvHasAllTools(layout, scripts)`, using the script list persisted by the prior
     install (`PythonToolsState.LoadScripts`). `InstallAsync` early-outs cheaply on a healthy venv, so
     calling it on an unhealthy-but-current machine is safe.

5. Self-checking shim (defense-in-depth).
   - `PythonToolsInstaller.BuildWindowsShimBody`: the Windows shim now checks the target exe exists FIRST;
     when missing it prints "cc-* tools are not fully installed - run the repair: Home > Fix it, or
     cc-director-setup-cli repair-tools" to stderr and exits non-zero, instead of cmd.exe's raw "is not
     recognized".

### How `VenvHasAllTools` was exposed to `ToolUpdater`

It was a `private` instance method on `PythonToolsInstaller`. It is now also a `public static bool
VenvHasAllTools(InstallLayout layout, IReadOnlyList<string> scripts)` (the instance form delegates to it),
plus a `public static string ConsoleScriptPath(InstallLayout, string)`. `ToolUpdater` calls the static
form directly. The script list it needs is persisted by the installer (new `PythonToolsState` sidecar) so
the updater can probe health offline, without re-downloading the bundle. `ProcessRunner` internals were
exposed to the test project via `InternalsVisibleTo` (added to the engine csproj).

## Acceptance criterion -> test mapping

| Acceptance criterion | Test |
|---|---|
| `VenvHasAllTools` false when a script exe is missing | `PythonToolsHealAndShimTests.VenvHasAllTools_OneScriptMissing_ReturnsFalse` |
| `VenvHasAllTools` true when all present | `PythonToolsHealAndShimTests.VenvHasAllTools_AllScriptsPresent_ReturnsTrue` |
| `VenvHasAllTools` false on empty list (forces rebuild) | `PythonToolsHealAndShimTests.VenvHasAllTools_EmptyScriptList_ReturnsFalse` |
| Update reinstalls on unhealthy-but-current venv | `PythonToolsHealAndShimTests.RefreshPythonTools_UnhealthyCurrentVenv_TriggersReinstall` |
| Update does NOT reinstall on healthy current venv | `PythonToolsHealAndShimTests.RefreshPythonTools_HealthyCurrentVenv_DoesNotReinstall` (plus existing `BundleRefreshGateTests`) |
| Version NOT recorded when venv incomplete (im.Set gated) | `PythonToolsHealAndShimTests.InstallAsync_IncompleteVenv_DoesNotRecordVersion` |
| Failed/interrupted install leaves no managed shim with absent target | `PythonToolsHealAndShimTests.InstallAsync_FailedVenvRebuild_LeavesNoManagedShim` |
| `ProcessRunner.Run` enforces the timeout (kill + partial output) | `ProcessRunnerTests.Run_ProcessExceedingTimeout_IsKilledAndReturnsTimeoutFailure` |
| Fast process within timeout returns real exit code | `ProcessRunnerTests.Run_FastProcessWithinTimeout_ReturnsRealExitCode` |
| Generated Windows shim body exits non-zero + repair guidance when target missing | `PythonToolsHealAndShimTests.WindowsShimBody_TargetMissing_ExitsNonZeroWithRepairGuidance` |

## Build / test results

- `dotnet build cc-director.sln`: Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test tools/cc-director-setup-engine.Tests`: Passed 166, Failed 0, Skipped 0.
- `dotnet test tools/cc-director-setup-cli.Tests` (affected, references the engine): Passed 17, Failed 0.

## End-to-end criterion - what a human must run on a clean box

The unit tests prove the LOGIC. The user-visible "fresh install works" + "half-install is repaired by a
normal auto-update" outcomes can only be proven on a clean machine (or via the opt-in
`PythonToolsInstallerTests` live harness). An agent cannot perform a real install, so this is NOT
fabricated here. To close the end-to-end acceptance criterion, a human/QA must:

1. Build the bundle assets: `scripts/build-python-bundle.ps1` (produces `cc-python-win-x64.zip` +
   `cc-tools-pyenv-win-x64.zip`).
2. Fresh-install path: on a clean machine, run a normal CC Director install and confirm every `ship:true`
   python tool runs: `cc-vault --version`, `cc-html --version`, `cc-pdf --version`, `cc-word --version`
   all succeed (each tool's `pyenv\Scripts\<name>.exe` is present, and its `bin\<name>.cmd` runs it).
   - Optionally drive the same path via the live engine harness:
     `set CC_PYBUNDLE_DIR=<dir with the two zips>` then run
     `dotnet test tools/cc-director-setup-engine.Tests --filter PythonToolsInstallerTests` (no-ops without
     the env var).
3. Half-install repair path: inject the field failure - empty `%LOCALAPPDATA%\cc-director\pyenv\Scripts`
   (delete the console-script exes) while KEEPING `bin\*.cmd` and `config\setup\installed.json`. Trigger a
   normal auto-update (or let the resident Director's update tick fire). Confirm the venv is rebuilt and the
   four tools above work again - the version-gated update no longer skips the unhealthy machine.

Expected, with this change in place: step 2 yields working tools; step 3 self-heals on the next update.
