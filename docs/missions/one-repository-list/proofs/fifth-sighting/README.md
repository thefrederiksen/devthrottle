# The fifth sighting: a session's display name read from the machine, not from the path

**Status:** fixed. **Suite:** `CcDirector.Avalonia.Tests`. **Date:** 2026-09-20.

## What was red

On macOS, on `origin/main`, one test failed:

    CcDirector.Avalonia.Tests.SmartRestart.SmartShutdownSessionReaderTests
        .Read_NamedAndUnnamedSessions_UseTheNameTheRailShows
    Expected: "devthrottle"
    Actual:   "D:\ReposFred\devthrottle"

Measured baseline on this machine before the fix: **644 passed, 1 failed, 645 total**.

## The line

`src/CcDirector.Avalonia/SmartRestart/SmartShutdownSessionReader.cs`, the name the Smart shutdown
dialog shows for a session that the owner never named:

    session.CustomName ?? Path.GetFileName(session.RepoPath.TrimEnd('\\', '/'))

`Path.GetFileName` honours only the separator of the host it is running on. Handed a Windows path on
macOS or Linux it finds no separator at all and hands back the whole path, so the dialog would offer
to shut down a session called `D:\ReposFred\devthrottle`.

## Why it was red on macOS and green on Windows

On Windows, `Path.GetFileName` understands **both** separators, so it answers correctly for every
ordinary repository path, Windows-shaped or POSIX-shaped. The defect is invisible there. On macOS
and Linux it understands only `/`. The test hard-codes a Windows path, so the same assertion passes
on one platform and fails on the other. `main` was therefore green on the machines most people run
the gate on and red here.

## Which is at fault - both, and separately

- **The test is at fault for the failure.** It asserted a host-dependent function's Windows answer
  from a hard-coded Windows path. A test written that way can only ever pass on Windows; it does not
  describe behaviour, it describes a machine. That is why nobody noticed.
- **The line is at fault as a latent product defect.** It is not reached wrongly in production
  today - a Director's own sessions carry that Director's own paths, so the reading host and the
  writing host are the same machine. But it decides a folder name from the machine running it, and
  the moment a path arrives from elsewhere (a workspace saved on another desktop, anything that
  comes down from the Gateway) it is wrong.

## The fifth sighting of one family, and the helper already existed

This is the **fifth** product defect this mission has found of exactly one shape: code deciding case,
separators, or a folder name from the machine RUNNING it rather than from the path's own shape. Four
were found and fixed before it.

The useful part for whoever reads this later: **the fix was already on `main` and was not used.**
`CcDirector.Core.Utilities.RepositoryPaths.FolderName` handles both separators, a trailing separator,
a drive root, a UNC path and a POSIX path, and carries its own tests in
`src/CcDirector.Core.Tests/Utilities/RepositoryPathsTests.cs`. It was written by this mission, for
this exact family, and then a new file shipped the defect anyway. The helper being available is not
enough on its own; the habit of reaching for `Path.GetFileName` outlives it.

At least seventeen more lines across eleven files carry the same defect, catalogued in
`../green-check-dotnet/`. They are deliberately deferred by the Delivery Lead. This one was taken
only because it was red on `main` and blocking the mission's check.

## The change

    session.CustomName ?? RepositoryPaths.FolderName(session.RepoPath)

The `TrimEnd('\\', '/')` is gone. It is genuinely dead: `RepositoryPaths.FolderName` trims a trailing
separator of either kind itself, and its own tests prove `D:\repos\x` and `D:\repos\x\` answer alike.

On the test side, `Read_NamedAndUnnamedSessions_UseTheNameTheRailShows` keeps asserting the Windows
path's correct answer - which now holds on every platform rather than on one - and gains a POSIX path
asserting the same answer.

### A second test, because the first cannot fail on Windows

Swapping a test that only passes on Windows for one that only passes on macOS would be the same
defect mirrored. So it is worth being plain about what each case can and cannot catch:

- The Windows-path case fails on **macOS and Linux** if the line regresses. It **cannot** fail on
  Windows, because `Path.GetFileName` is correct there for ordinary paths. Neither can the POSIX
  case, for the same reason.
- `Read_DriveRootRepositoryPath_IsItsOwnName` is the case that fails on **Windows**. A drive root is
  the one input where the two implementations disagree there: `Path.GetFileName(@"D:\")` answers the
  empty string on Windows - the rail would show a session with no name at all - while
  `RepositoryPaths.FolderName` answers `D:` on every platform. On macOS this case passes under the
  old line too, which is exactly why it had to be added rather than assumed.

Together the two fail on every platform if the line regresses. Nothing else was added.

## Proof

**Predicted symptom, written before the revert was run:** exactly one failure on this macOS machine -
`Read_NamedAndUnnamedSessions_UseTheNameTheRailShows`, `Expected: devthrottle` /
`Actual: D:\ReposFred\devthrottle` - and `Read_DriveRootRepositoryPath_IsItsOwnName` passing here,
because it is the Windows-side case.

**Observed on the revert** (new tests in place, product line put back):

    Failed  ...SmartShutdownSessionReaderTests.Read_NamedAndUnnamedSessions_UseTheNameTheRailShows
    Expected: "devthrottle"
    Actual:   "D:\ReposFred\devthrottle"
    Failed!  - Failed: 1, Passed: 6, Skipped: 0, Total: 7

The symptom matches the prediction, and the drive-root case passed on macOS as predicted.

**With the fix restored:**

    CcDirector.Avalonia.Tests   Passed!  - Failed: 0, Passed:  646, Skipped:  0, Total:  646  (43 s)
    CcDirector.Core.Tests       Passed!  - Failed: 0, Passed: 4485, Skipped: 18, Total: 4503  (6 m 21 s)

`CcDirector.Core.Tests` was run because this change adds a caller of a Core helper.

**What this proof does not cover.** Every number here was measured on macOS. The Windows behaviour
described above - `Path.GetFileName(@"D:\")` answering empty, and both separators being understood -
is stated from the documented platform rule and from `RepositoryPathsTests`, not from a Windows run
on this branch. The drive-root test is the case that would prove it, and it has not yet been run on
Windows. Nothing in the running application was exercised: the Smart shutdown dialog has no caller
yet, so this is a unit-level proof of the reader only.
