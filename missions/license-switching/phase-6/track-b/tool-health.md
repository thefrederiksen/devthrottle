# Tool health: why the license tool went missing, and the machine that says it cannot switch

Phase 6, track B. The proof for Part A of the "tool health and Director restart" piece.

## What happened on 16 October 2026

A threshold switch reached a Windows machine at 09:31:50 (machine local time; 13:31:50 UTC) and failed with
"cc-devthrottle is not installed in this machine's Python tool environment". The retry a minute later worked.

## The cause, with the evidence - it was ours

The machine's own Director log for that morning has the whole story. DevThrottle v2.22.0 was published at
13:28 UTC; the Director's hourly tools refresh saw it at 09:30:55 and started rebuilding the shared Python tool
environment (`<machine root>/pyenv`), which is where the switch looks for `cc-devthrottle`:

```
09:30:55.261 [ToolUpdater] refreshing Python tools bundle: 2.21.0 -> 2.22.0
09:31:09.652 [PythonToolsInstaller] setting the current tools environment aside
09:31:09.679 [PythonToolsInstaller] creating the shared Python venv
09:31:24.135 [PythonToolsInstaller] installing 12 tools offline from the wheelhouse (96 wheels)
09:31:31.998 [LicenseUsageReporter] no report sent FAILED: cc-devthrottle is not installed in this machine's Python tool environment (...)
09:31:52.097 [GatewayStreamClient] Command FAILED: verb=license-switch, ... error=cc-devthrottle is not installed in this machine's Python tool environment (...)
09:31:56.495 [PythonToolsInstaller] restored <machine root>\pyenv from <machine root>\pyenv.prev-...
09:31:57.243 [PythonToolsInstaller] FAILED: offline pip install failed (2): ... The previous tools were put back
09:32:53.222 [CcDevthrottleLicenseCommandLine] SwitchAsync: outcome=switched
```

So the tool was absent for 47 seconds (09:31:09.652 to 09:31:56.495) and the switch arrived 41 seconds into
it. The next refresh, at 10:32, succeeded and opened the same kind of window again: the Director log has another
"not installed" at 10:32:31, and the new launcher's file time is 10:33:43 - about 90 seconds with no tool.

**Why the window existed.** `PythonToolsInstaller` built the new venv AT ITS REAL PATH. A venv writes its own
absolute path into every console script, so it could not be built anywhere else and renamed in, the way the base
Python already was. To build it in place, the installer first renamed the live venv aside and took the shims off
- and from then until pip finished, there was no `cc-devthrottle` at all, neither in the venv nor on the PATH.
Every machine on the fleet opens this window at the same moment: when a release is published.

**The fix, at the source.** The new venv is now built beside the live one, in `pyenv.staging-<id>`, from the
staged base Python in `python.staging-<id>`. When pip is done and every console script is present, the absolute
paths it wrote are pointed at the final folders (`VenvRelocation`: the first line of every console script, the
three paths in `pyvenv.cfg`, and off Windows the interpreter links), and only then is it swapped in with four
directory renames. The live tools - venv, shims and base Python - are never touched while the slow work runs.
A failure before the swap changes nothing installed, so nothing has to be put back. No retry was added anywhere.

What is left is the swap itself: four renames, a few tens of milliseconds. Windows has no atomic replace of a
directory, so that cannot be zero; it is roughly a thousand times shorter than before.

## The proof

### The test that watches the window

`PythonToolsInUseTests.InstallAsync_WhilePipRuns_TheInstalledToolAndItsShimAreStillThere` - while the fake pip
is running, it checks that the installed console script still has its old contents, that its shim is still in
`bin`, and that the base Python is still there. **Revert proof:** with the old `PythonToolsInstaller.cs` put
back over the new tests, it fails with "the installed console script was gone while pip ran"; with the fix it
passes.

### A measured reproduction with a real Python, a real venv and a real pip

`venv-window-proof/` (beside this file) runs the real setup engine against an isolated folder: it installs a
one-tool bundle, then installs the next version while a watcher checks the `cc-devthrottle` launcher once a
millisecond, recording every stretch it is missing, and finally runs the installed launcher and its shim.

| Run | Longest time with no launcher | Launcher and shim afterwards |
|---|---|---|
| Windows, the installer before this change | **48,052 ms** (and a 91 ms probe) | both start, print the new version |
| Windows, this change | **46 ms** (the swap) and 15 ms (the in-use probe's rename and back) | both start, print the new version |
| Linux (WSL Ubuntu, the v2.22.0 Linux bundled Python), this change | **none seen** | both start, print the new version |

The bundle here has one small wheel; the real one has 96, which is why the field windows were 47 and 90 seconds.
The Linux run also showed the rewritten venv is right where it matters: `bin/python3` links to the final
`python/bin/python3`, the launcher's first line is the final `pyenv/bin/python3`, and `pyvenv.cfg` names only
final folders.

How to run it: `dotnet run -c Release -- first <a base Python folder> <an empty scratch folder>`, then the same
with `second` and the same two folders. The installs are two runs because of finding 1 below.

### The other tests (each in one sentence)

`VenvRelocationTests`:
- `Relocate_WindowsLauncher_RewritesOnlyTheInterpreterLineAndKeepsTheProgramAndArchive` - a Windows console script
  (launcher program, interpreter line, zip archive) comes out with only the interpreter line changed.
- `Relocate_Config_NamesTheFinalPythonAndVenvAndKeepsEverythingElse` - `pyvenv.cfg` names the final base Python and
  venv and nothing else in it changes.
- `Relocate_AScriptThatDoesNotNameTheStagedVenv_FailsAndChangesNothing` - a console script that names some other
  interpreter stops the install before any file is rewritten, because swapping it in would leave a tool that
  cannot start.
- `Relocate_NoConfig_Fails` - a venv with no `pyvenv.cfg` is refused.
- `Replace_NoOccurrence_ReturnsTheSameArraySoNothingIsRewritten` and `Replace_SeveralOccurrences_ReplacesEach` -
  the byte replacement touches only files that name the staging folder, and every occurrence in them.

`PythonToolsInUseTests` (changed to the new truth):
- `InstallAsync_PipFails_LeavesTheInstalledToolsUntouched` - a pip failure leaves the previous Python, venv, shim
  and recorded version as they were, and no staging folder behind.
- `InstallAsync_AnExceptionMidInstall_FailsWithItInsteadOfEscaping` - an exception while building is reported as a
  failed update naming the error, and the installed tools still work.
- `InstallAsync_Succeeds_InstallsTheNewToolsAndRemovesLeftoversButNeverAProbe` - now also checks that the installed
  console script and `pyvenv.cfg` name the final folders, not the staging ones.
- `InstallAsync_AToolStartsInTheVenvAfterTheCheck_HoldsAndChangesNothing` and
  `InstallAsync_AToolStartsInThePythonAfterTheCheck_PutsTheVenvBackAndHolds` - a tool that starts using the trees
  after the in-use check still makes the update hold at the swap, with the previous tools working.

`PythonToolsHealAndShimTests.InstallAsync_FailedVenvRebuild_LeavesTheInstalledToolAndItsShimAlone` - replaces the
test that a failed rebuild REMOVES the shim: now a failed rebuild leaves both the tool and its shim exactly as they
were, so a shim still never points at a missing target (issue #577), and no version is recorded.

## The Director says when it cannot switch

Contract section 10.1 (added to `contract.md`). The usage report has two new fields, `CanSwitch` and
`CannotSwitchReason` (`tool-missing`). The Director asks `ILicenseCommandLine.IsInstalled` on every report - it is
the same `_resolveTool()` every license call makes before it starts the tool, never a second lookup. While the tool
is missing, the report carries the last report's holdings with their own old read times (the contract's rule for a
failed read), because the holdings come from the tool itself.

**The rule for an old Director.** No field (`CanSwitch` null) means "this Director does not say". The Gateway
stores no reason for it and treats the machine exactly as before the field existed: same status, still switched,
nothing in the log. So nothing that works today stops working.

Tests (each in one sentence):

`LicenseCommandLineTests.IsInstalled_AsksTheSameLookupEveryCallMakes` - one lookup function answers both the
report's question and the call's: missing, the report says no and a call fails "not installed"; present, it says yes.

`LicenseUsageReporterTests`:
- `TickAsync_ToolPresent_ReportSaysASwitchCanRun` - with the tool in place the report says a switch can run.
- `TickAsync_ToolGoesMissing_SendsAtOnceThatASwitchCannotRunWithTheLastHoldings` - the next 30-second tick, not the
  next interval, says it cannot and why, with the last holdings and their old read times, and asks the missing tool
  nothing.
- `TickAsync_ToolStillMissing_SendsNothingMoreUntilTheInterval` - one report says it; then only the interval report
  repeats it.
- `TickAsync_ToolBack_TheNextTickSaysASwitchCanRunAgain` - the report flips back on the next tick once the tool is there.
- `TickAsync_ToolMissingBeforeAnyReport_SendsNothing` - with no report sent yet there is nothing true to send (an
  empty list would read as every holding removed); the waiting "connected" report goes once the tool is back.

`LicenseCannotSwitchTests` (Gateway; real store on a throwaway SQLite file, then the read model, snapshot, fold and
engine):
- `AMachineThatCannotSwitch_IsShownSo_WithWhy` - status `cannot-switch`, "Cannot switch: the DevThrottle tool is
  missing from its Python tool environment", warning tone.
- `AMachineThatCannotSwitch_IsNotSentOne_AndTheSkipNamesWhy_WhileTheOthersAreSwitched` - the other machine on the
  tired account is switched; this one is skipped with that reason.
- `TheOnlyMachineCannotSwitch_NothingIsSent_AndNoRoomIsNotClaimed` - no switch, and no false "no account had room" email.
- `TheLog_GetsOneEntryWhenItStarts_AndOneWhenItEnds_NeverOnePerReport` - three reports saying it cannot and two
  saying it can give exactly one "Cannot switch" and one "Can switch again" entry.
- `AReportSayingTheToolIsBack_GetsTheMachineSwitchedOnTheNextCheck` - left behind while it could not switch, it is
  switched on the first check after a report says it can. No second retry mechanism: it is simply a machine on a
  tired account again.
- `ADirectorThatSendsNoField_BehavesExactlyAsBefore` - reporting, switched, nothing logged.
- `ADirectorThatStopsSaying_IsTreatedAsBefore_ButTheLogDoesNotClaimItCanSwitchAgain` - back to the old rule, with no
  "can switch again" entry, because nothing said it can.
- `AReasonThisGatewayDoesNotKnow_IsStoredAsNotKnown_AndTheMachineIsStillNotSentASwitch` (two cases: an unknown word,
  and none) - the word is never stored as given, and the machine is still not sent a switch.

The alarm call is left out, as the brief says: Track A builds the one license alarm. The detection and the visible
status are here for it to call.

## The bin folder window seen on 17 October 2026

**What was seen.** On the machine the Delivery Lead watched, the `cc-*` commands vanished from the Director's `bin`
folder from about 14:34 to 14:37 UTC, while a pip install ran from a `cc-pytools-<id>` temporary folder. Only
`cc-linkedin` was left. That machine runs a release from before this pull request.

**What causes it, on origin/main.** `cc-pytools-<id>` is the folder `PythonToolsInstaller.InstallAsync` extracts the
tools bundle into (step 2), and the pip install that reads from it is step 5. On main the installer, in order:

1. Calls `RemoveManagedShims(manifest.Scripts)` immediately before setting the venv aside ("setting the current
   tools environment aside", line 329 of `PythonToolsInstaller.cs` on main). This deletes `bin\<script>.cmd`,
   `bin\<script>.exe` and the bare `bin\<script>` of EVERY script in the bundle.
2. Sets the venv aside, creates a new one, and runs pip into it.
3. Writes the shims again only after pip and the health check (`WriteShims`, line 452 on main).

So **the `bin` folder is empty of every bundle command for the whole pip install**, the same minutes the venv is
gone. `cc-linkedin` survives because it is not one of the bundle's console scripts, and `RemoveManagedShims` walks
only the bundle's list. This is the same window as the one in "The cause", seen from the `bin` folder instead of
the Python environment.

**What this pull request changes - the same code path.** With this change the only `RemoveManagedShims` calls are:

- **After the swap** (step 9), and only for scripts the PREVIOUS bundle had and the new one does not:
  `RemoveManagedShims(restoreScripts.Except(manifest.Scripts))`. A command that is still shipped is never deleted.
  It is rewritten in place by `WriteShims`, which overwrites each file rather than deleting it first.
- **In the roll-back after a failure that came after the swap**, where the shims are removed and immediately written
  again for the restored venv.

Nothing touches `bin` before or during pip. The test
`InstallAsync_WhilePipRuns_TheInstalledToolAndItsShimAreStillThere` checks the `bin` shim itself
(`bin\<tool>.cmd`), not only the venv, at the moment pip runs. On main's order that assertion fails, because the
shim was deleted first.

**What it does not cover, said plainly:**

- **The swap itself.** For the four renames (46 ms measured), the shims are in `bin` but the venv they start is
  not. A command started in that instant fails with "not found" from inside the shim, not from the shell.
- **The rewrite.** `WriteShims` overwrites each shim with `File.WriteAllText`. A command started in the instant
  one file is being written could read it half written. That instant is a single small file write per shim, and it
  is not measured here.
- **The roll-back.** After a failure that came after the swap, the shims are removed and written again, a moment
  apart, on purpose: the restored venv may not have every script.
- **The update that installs this fix.** The tools refresh is run by whichever build of the setup engine is running
  when it starts. If that is a build from before this pull request, that refresh has the old three-minute window
  one last time. Every refresh after it does not.
- **Commands outside the bundle.** Anything else that writes or removes files in `bin` (the uninstaller, other
  components such as `cc-linkedin`) is not changed by this pull request.

## A crash in the middle of the swap

The swap is four directory renames (step 8 of `PythonToolsInstaller.InstallAsync`): the venv aside, the base Python
aside, the staged Python in, the staged venv in. A process that dies between the first and the fourth leaves one of
these states. This is read from the code, not from a test; the review asked for it in words.

| Dies after | What is on disk | The tool |
|---|---|---|
| The venv rename aside | no `pyenv`; `pyenv.prev-<id>` is the working old venv; `python` is the old one; both `.staging-` folders hold the new runtime, already pointed at its final place | missing |
| The staged Python rename in | `python` is the NEW Python; no `pyenv`; `pyenv.prev-<id>` is the old venv | missing |
| The staged venv rename in, before the version is recorded | the new runtime is in place and works; the shims are the previous bundle's | present |

**The next tools refresh repairs every one of them, with nothing done by hand.**

- **It runs again.** The bundle version is recorded only at step 10, after the swap and the shims, so in every row
  above the recorded version is still the previous one. `ToolUpdater` therefore sees the machine behind
  (`versionBehind`) and runs the installer again. It would also run it if the version had been recorded and the
  venv were unhealthy (`unhealthy`, the self-heal).
- **It cleans up.** `RemoveLeftoverTrees` removes every `.staging-`, `.prev-`, `.old-` and `.failed-` folder, and
  it runs only after a verified install (step 11), so the old venv in `pyenv.prev-<id>` is never deleted before a
  working replacement exists.
- **Until then**, in the first two rows the machine has no `cc-devthrottle`. Its Director reports exactly that
  (`CanSwitch` false, reason `tool-missing`), and the Gateway sends that machine no switch.

**The repair depends on the Director being alive to run the refresh.** A crash of the installer is most likely a
crash of the Director itself, since the refresh runs inside it. Pull request #4118 makes the installed launcher start
a crashed Director again on Windows and macOS, and #4117 gives the owner the same on Linux. The refresh then runs at
the Director's next tools check.

## The gate

```
.\scripts\test-local.ps1   (commit c4855f2ea, the code of this pull request; only the two documents were uncommitted)
  CcDirector.Core.UnitTests                outcome=Completed    total=2133   executed=2133
  CcDirector.Avalonia.Tests                outcome=Completed    total=1063   executed=1063
  CcDirector.Engine.Tests                  outcome=Completed    total=85     executed=85
  CcDirector.HostedAgent.Tests             outcome=Completed    total=85     executed=85
  CcDirector.Launcher.Tests                outcome=Completed    total=214    executed=214
  CcDirector.Terminal.Avalonia.Tests       outcome=Completed    total=71     executed=71
  CcDirector.Reclaim.Tests                 outcome=Completed    total=305    executed=305
  CcDirector.DeliveryQualification.Tests   outcome=Completed    total=17     executed=17
  cc-director-setup.Tests                  outcome=Completed    total=39     executed=39
  cc-director-setup-engine.Tests           outcome=Completed    total=891    executed=891
  cc-director-setup-cli.Tests              outcome=Completed    total=74     executed=74
RESULT: GREEN
```

```
.\scripts\test-local.ps1 -Parked   (commit 2dd094eef, tree clean, on the base 08dec1140)
  every suite above, plus:
  CcDirector.Gateway.Tests.shard1of4       outcome=Completed    total=412    executed=412
  CcDirector.Gateway.Tests.shard2of4       outcome=Completed    total=480    executed=480
  CcDirector.Gateway.Tests.shard3of4       outcome=Completed    total=403    executed=403
  CcDirector.Gateway.Tests.shard4of4       outcome=Completed    total=469    executed=469
  CcDirector.Core.Tests                    outcome=Completed    total=4538   executed=4538
  CcDirector.Gateway.UnitTests             outcome=Failed       total=13840  executed=13840   (6 failed)
```

The six failures were five `PromptOnlyWhenWaitingForInputTests` and one `FleetManagerEventServiceTests` test -
prompt delivery, nothing this change touches. `-RerunFailed` failed them again, and the same six PASS on
origin/main at 6c783e3d4: they were fixed on main after this branch's base, by #4107 ("the scripted terminal
draws the composer a send now reads before typing"). After rebasing onto that main, those tests plus every
license test pass:

```
dotnet test src/CcDirector.Gateway.UnitTests --filter "PromptOnlyWhenWaitingForInputTests|FleetManagerEventServiceTests|CcDirector.Gateway.Tests.Licenses"
Passed!  - Failed:     0, Passed:   972, Skipped:     0, Total:   972
```

## What the live proof should look for

- On the next release, each Director's log shows `setting the current tools environment aside` is GONE and
  `pointing the new tools environment at its final location` then `swapping in the new tools environment` appear,
  with no `cc-devthrottle is not installed` line between `refreshing Python tools bundle` and `installed`.
- After the update, `cc-devthrottle --version` works on the machine through the shim, and
  `<machine root>/pyenv/pyvenv.cfg` names `<machine root>/python`, not a `.staging-` folder.
- After the Gateway is deployed, the Licenses page shows every machine as before (no machine reports
  `cannot-switch` while its tool is present). To see the new status without harming a machine, rename the
  `cc-devthrottle` launcher on a test machine for a minute: within 30 seconds its card says "Cannot switch: the
  DevThrottle tool is missing from its Python tool environment" and the log gets one "Cannot switch" entry; put it
  back, and within 30 seconds the card is back to "Reporting" with one "Can switch again" entry.

## Findings, not fixed here

1. **The install lock can be released on the wrong thread.** `SharedInstallLock` is a named mutex taken in
   `InstallAsync` and released by `using` after an `await`, so the release can run on another thread, fails
   silently (`catch { }`), and the mutex stays owned. A second install in the same process can then wait the full
   15 minutes for a lock its own first install still holds: the Linux measured run above hung exactly so, and the
   proof was split into two processes to get past it. A Director runs the refresh hourly in one process.
2. **The pip failure on 16 October is unexplained.** Its output was cut to 600 characters, which held only pip's
   "Processing" lines, so the reason (exit 2) is lost. The trim should keep the END of pip's output, where the
   error is.
3. **Test runs write to the real setup log.** `%LOCALAPPDATA%/cc-director/logs/setup` holds many logs with
   "The Windows setup wizard crashed: NullReferenceException: boom" and reports to `gateway.example.test`: test
   output landing in the owner's real log folder.
