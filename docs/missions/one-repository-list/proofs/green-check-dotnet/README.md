# The .NET half of this mission's check, made green on macOS

**What this is.** This mission's check gates all six of its phases, and on macOS it was red before anyone
touched the mission. A check that is red before the work starts gates nothing. This is the record of every
failure that was in it, which of them was the test's fault and which was the product's, and what was done
about each - so that nobody has to take on trust that a test which mattered was not quietly deleted.

**Nothing here was skipped, no assertion was loosened, and no test was deleted.** Where a test could not run
as written on this system, it was changed to assert the thing that IS true here and to prove it, rather than
to stay silent.

## The counts, run on macOS on 19 September 2026

Before, on a clean `origin/main`:

| Suite | Result |
|---|---|
| `CcDirector.Gateway.UnitTests` | Failed: 7, Passed: 6345, Skipped: 8, Total: 6360 |
| `CcDirector.Core.Tests` | Failed: 0, Passed: 4445, Skipped: 18, Total: 4463 |
| `CcDirector.Avalonia.Tests` | Failed: 7, Passed: 543, Skipped: 0, Total: 550 |

After:

| Suite | Result |
|---|---|
| `CcDirector.Gateway.UnitTests` | **Failed: 0**, Passed: 6355, Skipped: 8, Total: 6363 |
| `CcDirector.Core.Tests` | **Failed: 0**, Passed: 4461, Skipped: 18, Total: 4479 |
| `CcDirector.Avalonia.Tests` | **Failed: 0**, Passed: 550, Skipped: 0, Total: 550 |

The three commands, from the worktree root, with `~/.dotnet` on the path:

```
dotnet test src/CcDirector.Gateway.UnitTests
dotnet test src/CcDirector.Core.Tests
dotnet test src/CcDirector.Avalonia.Tests
```

`CcDirector.Core.Tests` was **already green on this machine**. It is reported here because the mission's
check names it and a run that was not made is not evidence.

**About the skips, which are not failures and were not touched.** The eight in the Gateway suite are the
PostgreSQL-backed tenancy and migration proofs, held back by `PostgresRigGate` when no database rig is
present. The eighteen in Core are Windows-only cases carrying their own stated reasons. Neither set is in
scope here; both are named so this page does not read as though the suites ran everything.

---

## The fourteen failures

Four were the PRODUCT's fault and ten were the TEST's. Taken in order of how much they matter to this
mission.

### 1. PRODUCT - `RulePrimitives.IsPathInside` reports a file inside a repository as outside it

`CcDirector.Gateway.Tests.Rules.RulePrimitivesTests.IsPathInside_follows_a_link_that_stays_inside_the_root`

**This is a real defect and it sits directly in this mission's subject matter.** The mission is about
identifying the same repository across machines and surfaces; this is the product's containment check
getting a path wrong.

`IsPathInside` resolves both paths before comparing them, walking segment by segment so that a link
ANCESTOR is followed - which is right, and is what stops a link inside a repository leading out of it. But
when it found a link it took the link's target and carried on, without resolving that TARGET's own
ancestors. On macOS and on Linux the operating system call behind `Directory.ResolveLinkTarget` follows the
chain of links and leaves the target's directories exactly as they were written, so a link pointing at
`/var/folders/x/repo/real` answers with that string even though `/var` is itself a link to `/private/var`.
The root beside it, walked segment by segment, resolves to `/private/var/folders/x/repo`. One fully
resolved path was then compared against one half-resolved path, and a file plainly inside the repository
answered **false**.

Verified by direct probe on this machine before any change was made: `Path.GetTempPath()` is
`/var/folders/.../T/`, `/var` resolves to `/private/var`, and `ResolveLinkTarget` on a link created inside
that tree returns its target with the `/var` ancestor still unresolved.

**The consequence in production.** A rule scoped with `is_path_inside` is silently narrowed: a file that is
inside the repository is judged outside, so the check answers false and the rule never fires. It fails
closed rather than open - the companion case, a link that genuinely leads OUT of the root, still answers
false correctly - so this is not a way past the containment check. It is the worst shape a defect can take
all the same: nothing fails, something merely stops happening. And rules run on the hosted Gateway, which
is Linux.

**The fix.** `ResolveFinalPath` now resolves a link target's own ancestors as well, with a depth limit of
40 that throws on a circle - which the method's documented contract already promised ("a link that cannot
be resolved throws rather than quietly answering false"). Windows never showed this because its own call
canonicalises the whole path inside the operating system; the fix makes every system give the one answer.

**The test that fails without it.** The existing test above, plus a new one that builds the shape
deliberately instead of relying on where the temporary directory happens to be:
`IsPathInside_follows_a_link_whose_target_is_written_through_a_linked_ancestor`. Watched fail with the
product change reverted and the tests kept: both go red, and the rest of both rule classes stay green.

**What this proof does NOT cover:** the new test can only fail on macOS and Linux. Windows resolves the
whole path inside `GetFinalPathNameByHandle`, so the defect never existed there and no test can make it
appear there.

### 2. PRODUCT - the Gateway compares repository paths by ITS OWN operating system

`CcDirector.Gateway.Tests.Rules.RuleCandidateFilterTests.A_rule_scoped_to_this_sessions_repository_is_a_candidate`

**Also this mission's subject matter, and arguably the more serious of the two.**

`RuleCandidateFilter.PathsAreTheSamePlace` decided whether two repository paths named the same place using
`OperatingSystem.IsWindows()` - the operating system of the machine RUNNING the code. The Gateway runs in a
Linux container and is handed repository paths pushed up by every Director in the fleet, Windows and macOS
alike. It is never the machine the path describes.

So a rule scoped to `D:\ReposFred\scratch` matched a session reported as `d:\reposfred\scratch` while that
code ran on a Windows desktop, and stopped matching it the moment the same code ran on the hosted Gateway.
The separator half was wrong in the same way: `Normalize` replaced forward slashes with
`Path.DirectorySeparatorChar`, which is a forward slash on Linux, so `D:/repos/x` and `D:\repos\x` were two
different places there. The rule silently never fires.

The failure read the other way round too, and that half is the one a Windows developer would have hit: on
Windows the old code compared POSIX paths case-insensitively, so `/Users/dev/repo` and
`/users/dev/repo` - two genuinely different directories on Linux and on a case-sensitive Mac volume -
answered as one place.

**The fix.** The path's own shape decides. A drive-letter path or a `\\server\share` is compared without
regard to case and with its separators unified, because that is what Windows does with it. Anything else is
a POSIX path, compared exactly, with only a trailing separator dropped - a backslash is a legal character
in a POSIX file name and is left alone rather than treated as a separator.

**The tests that fail without it.** Three, and between them they cover both platforms:

- `A_rule_scoped_to_this_sessions_repository_is_a_candidate` (strengthened to mix separators and casing).
  Watched go red on macOS with the fix reverted.
- `A_rule_scoped_to_a_POSIX_repository_does_not_match_a_different_casing_of_it` - **this is the half that
  fails on Windows** and passes on macOS either way.
- `A_rule_scoped_to_a_POSIX_repository_matches_it_written_with_a_trailing_separator`.

**What this proof does NOT cover:** only the first was watched failing, because only macOS was available
here. The Windows half is argued from the old code's own text (`OperatingSystem.IsWindows()` selecting
`OrdinalIgnoreCase` for every path), not from an observed red run. A reviewer with a Windows machine should
confirm it.

### 3. PRODUCT - a repository's folder name read with `Path.GetFileName` on the wrong machine

`CcDirector.Avalonia.Tests.LegacyWorkspaceImportTests.An_imported_workspace_keeps_the_name_agent_colour_arguments_and_order_of_every_seat`

**The same family as the two above, and again this mission's subject matter.** A workspace saved on a
Windows desktop carries `D:\ReposFred\devthrottle_internal`. Opened on a Mac Director, the seat's display
name came out as the whole path instead of `devthrottle_internal`, because `Path.GetFileName` honours only
the separator of the host it is running on and finds none in a drive path.

The surrounding code already knew a path could use either separator - it read
`Path.GetFileName(repo.TrimEnd('\\', '/'))` - but the trim does not help `GetFileName` find the segment.

**The fix.** A new shared `CcDirector.Core.Utilities.RepositoryPaths.FolderName`, which reads the last
segment from the path itself and understands both separators, used at the import site. It is the same
logic the Gateway had already written privately for itself in `ThrottleDefinition.Leaf` - evidence that
somebody else met this and solved it locally rather than once.

**The tests.** The failing test above passes with it. A new `RepositoryPathsTests` in `Core.Tests` covers
Windows paths, share paths, POSIX paths, mixed separators, trailing separators, a bare name, and the
nothing-to-read cases, all of which answer the same on every platform.

**AND A FINDING THAT IS DELIBERATELY NOT FIXED HERE, because it is the mission's to decide, not a
Developer's.** The same wrong call is in **thirteen other places**, all of which turn a repository path
into a name a person reads:

```
src/CcDirector.ControlApi/CatalogReadExecutor.cs:141, :262
src/CcDirector.ControlApi/ControlEndpoints.cs:42
src/CcDirector.ControlApi/SessionWriteExecutor.cs:833
src/CcDirector.ControlApi/Chat/ChatService.cs:418
src/CcDirector.Avalonia/NewSessionDialog.axaml.cs:64, :136, :217, :429
src/CcDirector.Avalonia/LoadWorkspaceDialog.axaml.cs:194
src/CcDirector.Avalonia/SessionViewModel.cs:567
src/CcDirector.Avalonia/RestoreSessionsDialog.axaml.cs:53
```

Four of those are in `NewSessionDialog`, which is the screen this mission is about, and
`CatalogReadExecutor` serves the repository list. They are each a one-line change to
`RepositoryPaths.FolderName`, but they are a sweep across two applications with no failing test pointing at
them, and phase 4 and phase 6 rewrite several of the files. They are reported rather than changed so the
seat that owns the mission decides where that lands.

### 4. PRODUCT - `MicAudioCapture.Dispose()` throws

`MicCaptureConstructionQueriesNoDeviceTests.MicAudioCapture_Constructor_TakesTheResolvedNameAndQueriesNoDevice`
`MicCaptureConstructionQueriesNoDeviceTests.BatchDictationRecorder_ProductionMicrophone_CarriesTheResolvedNameAndQueriesNoDevice`

Both tests construct the capture wrapper and assert the constructor took the resolved device name without
querying a device. **On macOS that assertion passes.** What failed was the `using` block: `Dispose()`
reaches NAudio, which calls into `winmm.dll`, and a `DllNotFoundException` came out of a cleanup path.

A `Dispose` that throws is a defect on any platform. It runs from `using` and from `finally`, so it does
not report a new problem - it REPLACES whatever problem was already being reported with a message about
the wrong thing.

**The fix.** The release of the device is caught and logged in the repository's logging format rather than
raised. This hides nothing: a device that cannot be opened still fails loudly when capture is STARTED,
which is where a caller can act on it.

### 5. TEST - three liveness tests could not start their own child process

`SessionCommandExecutorLivenessTests.Kill_WithNoInjectedCheck_ReadsARealLiveProcessAndReportsItStopped`
`SessionCommandExecutorLivenessTests.Kill_WithNoInjectedCheck_ReadsAnEndedProcessAsGoneAndReportsAlreadyStopped`

The fixture ran `cmd.exe /c ping -n 600 127.0.0.1 > nul` unconditionally, so on macOS and Linux it could
not build a child at all and the tests reported the PRODUCT as broken. The production liveness check reads
a process identifier and nothing else - it is the same platform-neutral code everywhere. **Test's fault.**

Fixed by starting `/bin/sleep 600` where there is no `cmd.exe`. Every assertion is unchanged, and they now
run against a real live process and a real dead one on this machine.

`SessionCommandExecutorLivenessTests.Kill_WithNoInjectedCheck_WillNotCallAnUnreadableLiveProcessAlreadyStopped`

This one deliberately called `Assert.Fail` on a non-Windows host, with a considered message: it builds a
live-but-unreadable process using Windows process security, it refuses to skip, and it said the Unreadable
answer was therefore unproven here.

The instinct was right and the outcome was wrong. **The state is not merely unproven on macOS and Linux -
it is unreachable**, because on those systems the kernel answers "does this process identifier exist" to
everyone; permission governs what you may DO to a process, not whether you may see it. Verified by probe
before changing anything: reading the initial process, which is the superuser's, answers `HasExited=false`
and throws nothing. So the test made a false claim - it reported a product failure where the product has
no third answer to get wrong. **Test's fault.**

Fixed without a skip and without weakening the Windows case. On a non-Windows host it now proves the
property that makes the absence correct, and proves it as a PRESENCE rather than as the absence of a
failure:

1. it must be running as an ordinary user, or the premise cannot be built, and it says so and fails;
2. `kill(1, 0)` must be refused with "operation not permitted" - the operating system positively denying
   us rights over that process;
3. and reading that same denied process must still answer, throwing nothing - so a denied live process is
   Alive here, never Unreadable;
4. and a process that really has ended must throw `ArgumentException`, which the production check reads as
   Gone.

Two answers, both demonstrated against real system calls, and no third. If a future system made that read
throw, this goes red and says the Unreadable answer has become reachable and now needs its own coverage -
which is the alarm the original author wanted and did not get.

### 6. TEST - two recovery tests induced a rename failure in a Windows-only way

`WorkListStorePersistenceTests.Import_RenameAsideFails_NextConstructionRenamesAside_WithoutReimporting`
`CronJobStoreTests.LegacyJson_RenameFailsAfterImport_NextConstructionRecoversWithoutReimporting`

Both prove that a legacy import whose rename-aside fails is recovered on the next construction without
re-importing. Both arranged the failure by holding the file open with `FileShare.Read`, which permits the
import's read and denies the move - **a Windows mechanism**. macOS and Linux do not enforce .NET's sharing
modes, so the rename quietly succeeded, the state the tests exist to build was never built, and both
reported a product failure. The recovery path itself is the same platform-neutral code everywhere.
**Test's fault, in how the failure was induced.**

Fixed with a new `BlockedRename` fixture that uses each system's own real mechanism - on Windows a file
someone else holds open, on macOS and Linux a directory the account may not write, since a POSIX rename
needs write permission on the containing directory rather than on the file. Both are failures the product
genuinely meets.

**The fixture proves the block took.** Its constructor attempts a real rename and requires the operating
system to refuse; a success undoes the probe and throws with the reason, naming the superuser as the likely
cause. Without that, a host where the block did not take would run the test against a rename that succeeded
and report the result as a verdict on the product - which is the exact failure this replaced.

The two legacy files moved into a subdirectory of their own, because on Unix the whole directory is closed
to writing and the test harness's database lives in the directory above it.

### 7. TEST - four Speak dialog tests did not pin the microphone

`SpeakDialogReadyCueBlankingTests.CaptureLive_PlaysTheCue_OnlyAReportedCompletionBlanksItInTheRecorderItWasPlayedInto`
(three cases) and
`SpeakDialogCloseDuringStartupTests.ClosingWhileTheMicrophoneStarts_DisposesTheRecorderAndNeverPublishesIt`

These are tests of the ready cue and of closing mid-startup. All four supply a fake microphone and a
recorder factory - but none set `ResolveMicForTests` or `EnumerateMicsForTests`, which the dialog provides
for exactly this purpose and which every other Speak dialog test in the suite sets. So the dialog ran the
real `winmm` device enumeration on its way to the recorder factory, and on macOS that threw before the
factory was ever reached. **Test's fault: an incomplete fixture.** Fixed by pinning the device.

This also removed a dependency on whatever microphones the developer's machine happens to have, which
these tests never wanted.

**And it closed a FALSE GREEN found next door.** `ClosingDuringThePreflight_ConstructsNoRecorderAtAll`, in
the same file, was passing on macOS - and its assertion is that NO recorder was built. A startup that died
in device enumeration satisfies that without the dialog's guard existing at all, so on this machine it was
certifying nothing. It is pinned too, and it is now passing for the right reason.

---

## A product defect found in passing, reported and NOT fixed

**The Director's Speak dialog is reachable on macOS and cannot work there, and it says so in terms nobody
can act on.** The whole local microphone stack - `MicDevices`, `MicAudioCapture` - is NAudio over
`winmm`, which exists only on Windows; `MicDevices` names Windows in its own documentation throughout.
Nothing gates the dialog on the platform: `MainWindow` and `ExpandedEditorDialog` open it anywhere. On a
Mac Director the user gets `Failed to start recording: Unable to load shared library 'winmm.dll'`.

It is not fixed here because deciding what the Mac Director should do about local dictation is a product
decision, it is nothing to do with the repository list, and it is larger than this task. The `Dispose`
defect in section 4 is fixed because that one is a contract violation on every platform with a contained
fix; the rest is reported.

---

## What was changed

Product:

- `src/CcDirector.Gateway/Rules/RulePrimitives.cs` - resolve a link target's own ancestors, with a depth
  limit that throws on a circle.
- `src/CcDirector.Gateway/Rules/RuleCandidateFilter.cs` - compare repository paths by the path's shape,
  not by the host's operating system.
- `src/CcDirector.Core/Utilities/RepositoryPaths.cs` - new; read a repository's folder name from a path
  written on any machine.
- `src/CcDirector.Avalonia/LegacyWorkspaceImport.cs` - use it for the seat name.
- `src/CcDirector.Avalonia/Voice/MicAudioCapture.cs` - `Dispose` logs a failed device release instead of
  raising it.

Tests:

- `src/CcDirector.Gateway.UnitTests/Rules/RulePrimitivesTests.cs` - one new case for the link-ancestor
  shape.
- `src/CcDirector.Gateway.UnitTests/Rules/RuleCandidateFilterTests.cs` - two new cases for POSIX paths;
  the Windows case strengthened.
- `src/CcDirector.Gateway.UnitTests/SessionCommandExecutorLivenessTests.cs` - a child process this machine
  can start; the unreadable case proves the platform property instead of failing.
- `src/CcDirector.Gateway.UnitTests/Data/BlockedRename.cs` - new fixture, self-proving.
- `src/CcDirector.Gateway.UnitTests/WorkListStorePersistenceTests.cs`,
  `src/CcDirector.Gateway.UnitTests/CronJobStoreTests.cs` - use it.
- `src/CcDirector.Core.Tests/Utilities/RepositoryPathsTests.cs` - new.
- `src/CcDirector.Avalonia.Tests/SpeakDialogReadyCueBlankingTests.cs`,
  `src/CcDirector.Avalonia.Tests/SpeakDialogCloseDuringStartupTests.cs` - pin the microphone.
