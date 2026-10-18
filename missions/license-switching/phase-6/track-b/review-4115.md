# Review of pull request 4115 - License tool health (Track B, phase 6)

Reviewer: a separate review session, a different agent family from the Developer, opened by the Track B Tech Lead (session 111). Read only. Nothing in the worktree was edited, built or committed.

## Scope

**What I read.** The whole diff of 4115 against its base 6c783e3d4, which is origin/main at the time of this review (both Track B worktrees sit on it): `PythonToolsInstaller.cs`, the new `VenvRelocation.cs`, `LicenseCommandLine.cs`, `LicenseUsageReporter.cs`, both contract files, `LicenseStore.cs`, `LicenseDisplayFold.cs`, `LicenseDecisionEngine.cs`, `LicenseDecisionModel.cs`, `LicenseReadModel.cs`, `LicenseSnapshotSource.cs`, the entity and context changes, both migrations and their snapshots, every changed test file, the proof file `tool-health.md`, the `venv-window-proof` program, and the changes to `contract.md` and `cockpit-api.md`. Outside the diff, to check the claims: the installer's `SetDirAside`, `RestoreDirOver`, `ToolTreesAreFree`, `RemoveLeftoverTrees` and `FailAndRollBack`; `ToolUpdater`'s decision to run the installer; `ExecutableResolver`; `DirectorHub.PushLicenseUsage`; `LicenseUsageIngest`'s refusal rules; every implementer of `ILicenseCommandLine` in the repository; the `client-core` licenses client and both app shells; the macOS packaging script.

**What I ran.** Nothing from this pull request. A reviewer never builds into the worktree, so I did not run the .NET gate, and I did not run the window proof (it needs a real base Python folder and would re-run the Developer's own harness). Every test claim below is checked by reading the test, not by running it. I swept both diffs for real names, machine names, addresses, email domains and user paths.

**What I could not reach.** The 16 October Director log the proof quotes; the Windows and Linux measured runs; whether the real Windows console-script launcher tolerates a first line of a different length (the Windows measured run, which executed the installed launcher after the swap, is the only evidence, and it is the Developer's).

## Verdict

**No defect found that must change before merge.** Two findings for the Tech Lead's judgment, both about the strength of the evidence rather than the code, and several observations. Within the scope above the code does what the pull request says: the live tools are untouched through the slow steps, the Director's check is the same lookup as the switch, an old Director behaves exactly as before, the Gateway folds the verdict and the clients render it, and the log gets one row at each edge.

## The questions the brief asked, answered from the code

### Does "build beside and swap" really leave no moment without the tool?

**No - and the pull request does not claim it does.** The title says "never take the tools away during an update"; the proof says what is true: the window shrank from the whole pip install (47 and 90 seconds in the field, 48,052 ms in the measured run) to the swap itself (46 ms measured), plus the in-use probe's rename-and-back at the start (15 ms, which existed before this change). The code confirms the order: the venv is renamed aside, then the base Python, then the staged Python is renamed in, then the staged venv (`PythonToolsInstaller.cs` step 8). From the first rename to the fourth there is no `cc-devthrottle`. Windows has no atomic directory replace, so this cannot be zero. On Linux and macOS the renames are microseconds and the measured Linux run saw no absence at one probe a millisecond.

### Windows: a rename over a folder with running `python.exe` or `cc-devthrottle.exe` files open

Windows refuses to rename a directory with an open handle inside it. The code treats the refusal as the signal it is: the venv rename aside returns false and the update is HELD with nothing moved (step 8, `SetDirAside`); the base Python rename aside returning false goes through `FailAndRollBack`, which puts the venv back. Both cases are tested (`InstallAsync_AToolStartsInTheVenvAfterTheCheck_HoldsAndChangesNothing`, `InstallAsync_AToolStartsInThePythonAfterTheCheck_PutsTheVenvBackAndHolds`), and the tests now also assert no staging folder is left behind. A process already running from the old venv keeps running after the swap: its open file keeps the `.prev-` folder's files alive, `TryDeleteDir` of that folder is best-effort, and `RemoveLeftoverTrees` removes it after a later verified install. Correct.

The cost of a hold is now a wasted minute of pip work (the venv is built before the hold is discovered), where before the hold came first. That is the right trade, since the alternative was the window.

### Windows: antivirus holds

A scanner holding a freshly written file inside `pyenv.staging-<id>` at the moment of the fourth rename throws `IOException` out of `Directory.Move`; that is caught by the `IOException` catch and goes through `FailAndRollBack`, which restores both backups. A scanner holding a file in the live venv at the first rename produces a hold. A scanner holding a file during the relocation rewrite makes `Relocate` return false, and the update fails with the installed tools untouched. All three paths end with the previous tools in place. Correct.

### A crash halfway through the swap - what is left, and does the next update recover?

**Not covered by the proof or by any test; from the code, the next hourly refresh recovers it.** The cases, by where the process dies:

- After the venv rename aside, before the Python rename: no `pyenv`; `pyenv.prev-<id>` holds the working venv; `python` is the old one; both staging folders hold the new runtime, already relocated.
- After the staged Python is renamed in, before the staged venv is: `python` is the NEW Python; no `pyenv`; `pyenv.prev-<id>` is the old venv, whose `pyvenv.cfg` now names a Python it was not built from.
- After both renames, before the version is recorded: the new tools are in place and WORK; the shims are the previous bundle's; the recorded version is still the old one.

In every case the recorded bundle version is still the previous one (it is written only at step 10, after the swap), so `ToolUpdater` sees the machine behind and runs the installer again at the next hourly refresh (`ToolUpdater.cs`, `versionBehind`). If instead the version was recorded and only the backups were not deleted, nothing is missing and `RemoveLeftoverTrees` removes them after the next verified install. `RemoveLeftoverTrees` runs only after a verified install (step 11), so the `.prev-` backup is never deleted before a replacement exists. In the first two cases the machine has no tool for up to an hour; the Director's new field reports exactly that, and the Gateway sends it no switch. The recovery depends on the Director being alive to run the refresh, which is what pull request 4117 is about.

Finding 2 below asks for this to be written into the proof, because it is the question an operator will ask the day it happens.

### Does the Director's check use the SAME lookup as the switch?

**Yes.** `IsInstalled => _resolveTool() is not null` (`LicenseCommandLine.cs:181`) and every call goes through `RunForJsonAsync`, whose first line is `var exe = _resolveTool() ?? throw ... not installed` (line 237). One function, asked afresh each time; `ExecutableResolver.Resolve` with a rooted path never searches PATH (lines 35-42). The test `IsInstalled_AsksTheSameLookupEveryCallMakes` counts three asks through one injected function. The reporter asks it on every tick before any tool call.

### Does an old Director that sends no field behave exactly as today?

**Yes.** `CannotSwitchReasonOf` answers null unless `CanSwitch == false`; a null reason changes nothing in the fold, the engine, the read model or the log (`ADirectorThatSendsNoField_BehavesExactlyAsBefore`, `ADirectorThatStopsSaying_IsTreatedAsBefore_ButTheLogDoesNotClaimItCanSwitchAgain`). The reverse direction is safe too: a NEW Director reporting to an OLD Gateway sends two extra JSON properties, and the hub deserializes with the default System.Text.Json settings, which ignore unknown members; nothing in the Gateway sets `UnmappedMemberHandling` to disallow them.

### Does the Gateway fold decide and the client only render?

**Yes.** Status, text and tone are computed in `LicenseDisplayFold` (one new arm, placed before "signed out" with the reason written beside it) and stamped on the DTO; the skip reason is a sentence from `LicenseStore.CannotSwitchSentence`. No file under `packages/client-core/src`, `apps/cockpit/src` or `apps/mobile/src` branches on a machine status value; they render `statusText` and `statusTone`. `cockpit-api.md` lists the new status and the two new log kinds and keeps its "never branch on a kind you do not know" rule.

### One log entry per change?

**Yes, on both sides.** Gateway: a `cannot-switch` row only on the transition from no reason to a reason, a `can-switch-again` row only on the transition from a reason to none AND `CanSwitch == true`; the reason is persisted on the machine row, so a Gateway restart does not re-log (`TheLog_GetsOneEntryWhenItStarts_AndOneWhenItEnds_NeverOnePerReport`). Director: one `FileLog` line when the tool goes missing and one when it is back (`_toolMissingLogged`).

## Findings (for the Tech Lead's judgment; none is a code defect I can prove)

### Finding 1 - the Gateway unit suite was not run on the commit that merges

The proof's `-Parked` run is at 2dd094eef on the base 08dec1140; it failed six prompt-delivery tests the proof attributes to the base and to #4107. The branch was then rebased onto 6c783e3d4 and only a FILTERED run of 972 tests (the six plus the license namespace) was made. The default gate at c4855f2ea is also a different commit from the head c15d3892b. So the 13,840-test Gateway unit suite has never run on the code that merges, and the repository's rule for a Gateway change is `-Parked`. The expected harm is low (the rebase brought in #4107, #4112 and release notes), but the evidence is for a different commit, and "the same six pass on origin/main" is self-testimony I could not check. The Tech Lead may accept it or ask for one `-Parked` run on the head.

### Finding 2 - the proof does not say what a crash during the swap leaves behind

The brief for this review asked it; the proof answers the pip-failure and the in-use cases and is silent on a process that dies between the first and the fourth rename. My reading above says the next hourly refresh recovers it because the version is recorded last and leftovers are removed only after a verified install. That is a property worth one paragraph in `tool-health.md`, and ideally one test that kills the installer after the first rename (a `RunProcess` fake that throws cannot reach it, because the renames are not process runs; it would need a seam or a hand-run). I am not asking for the test; I am asking that the proof state the answer, so the next operator does not have to derive it.

## Observations (no action required)

1. **A Director that restarts while the tool is missing reports nothing.** `SendWithoutToolAsync` sends nothing when no report has been sent yet, by design (an empty holdings list would read as every holding removed). The Gateway then shows that machine as "Not reporting" after six minutes rather than "Cannot switch". It is still skipped, so no wrong switch is sent; only the status is less specific. The design reason is written in the code and the contract.
2. **The "not-known" reason shows its code word in one place.** `LicenseDisplayFold.ReasonText` maps `tool-missing` to a sentence and lets any other word through as it is, so a `cannot-switch` log row from a Director with an unknown reason word has `reasonText` "not-known". The row's summary and the machine status both carry a proper sentence, so the harm is one raw word in one field.
3. **The relocation matches the staged path byte for byte, ordinal.** Windows paths are case-insensitive; if the venv's console scripts ever carried the path in a different case from `InstallLayout`, every update would refuse with "does not name the staged environment" - safely, with the tools untouched, but permanently. Both strings come from the same `InstallLayout` value (the installer launches the staged interpreter by that path and Python reports it as launched), and the Windows measured run passed, so I do not expect it; I note it because the failure would be silent in the field until someone read the setup log.
4. **A 46 ms blip can still produce two log rows.** A report that lands inside the swap sees the tool missing and the next tick sees it back: one `cannot-switch` and one `can-switch-again` row for a blink. At one report every 30 seconds and one update an hour per release, this is rare and honest.
5. **The install lock finding is real and pre-existing.** `using var heldLock = installLock;` is disposed after `await`s, so the named mutex can be released from another thread; the Developer's write-up (finding 1 in the proof) matches the code. Not this pull request's to fix.
6. **No real name, machine name, address or user path** in either diff. Placeholders only.

## Tests, read not run

Each test named in the proof exists, asserts what the proof's sentence says, and the revert claim for `InstallAsync_WhilePipRuns_TheInstalledToolAndItsShimAreStillThere` is consistent with the old code (which renamed the venv aside before pip). The fake runner now creates the venv wherever the installer asks and writes the console script's first line as `#!<interpreter>`, so the relocation is exercised by the install tests, not only by `VenvRelocationTests`. `PythonToolsHealAndShimTests` was changed from "a failed rebuild removes the shim" to "a failed rebuild leaves tool and shim alone", which is the new truth and still satisfies the rule that a shim never points at a missing target.
