# Phase 1 review: the Developer's answers

Written by the Developer for phase 1 of the Reclaim the Disk mission, 19 September 2026, in answer to
`review-phase-1.md`. Both findings are accepted. The tests named here are in
`src/CcDirector.Reclaim.Tests`, and each was proven red under an injected revert of its fix before it
was trusted green.

## Finding 1: the saved scan was looked up by a case-sensitive key on a platform whose paths are not

**Accepted.** The review is right that on Windows one folder spelled two ways produced two saved
scans and a false "There is no saved scan" answer, and that this file is the contract between the
Launcher and the Director that phase 5 and phase 6 build on. The code comment that defended
case-preservation was arguing for the right trade on Linux and the wrong one on Windows, exactly as
the review said.

**What the fix does.** The fingerprint in `ScanIndexStore.PathFor` is now platform aware:

- On Windows, the canonical path is folded to one case before the fingerprint is taken, so
  `C:\Some\Folder` and `c:\some\folder` are one fingerprint and therefore one saved scan file. The
  readable label beside the fingerprint was already lower case and is unchanged.
- On every other platform, the fingerprint keeps the exact bytes, because there two spellings are
  two real folders and must never share one file.

Trailing separators and dot segments are still folded first, on every platform, by
`DirectoryScanner.Canonical`, and the existing tests for those are unchanged and still pass. Nothing
else about the file names changed: the same label, the same fingerprint length, the same index
folder. Because only the fingerprint folds, a saved scan's stored `RootPath` still records the
spelling the scan was given, and the listing still shows it.

`PathFor` is a public method this change touched, so it now writes an entry and an exit log line, per
the repository's rule 2 on logging.

**The test that fails without the fix.** `Run_ReportAfterAScanWithThePathCaseFlipped_FollowsThePlatformItRunsOn`
in `RunnerTests.cs` scans the fixture tree and then reports it with the case of its drive letter
flipped. On Windows it must resolve the same saved scan: the same index file, the same sentences,
exit code nought. Without the case fold the report throws "There is no saved scan" and the test is
red. On every other platform the same test asserts the other half of the rule: the report must
refuse, because the second spelling names a different folder and must remain a separate file.

Beside it, `PathFor_TwoSpellingsThatDifferOnlyInCase_FollowThePlatformItRunsOn` in
`ScanIndexStoreTests.cs` pins the same rule at the level of the file names: one file on Windows, two
files elsewhere. This is the guard the review asked to keep, so a later change that folded case on
every platform would be caught rather than quietly merging two real folders into one saved scan.

**The revert proof.** The fix file `src/CcDirector.Reclaim/Indexing/ScanIndexStore.cs` was reverted
with `git stash push` while the tests stayed in place, and both tests above were run: both red. The
fix was restored and both pass. Recorded here as required by the mission's law 4 on proofs.

**Reported, not fixed, because it is outside the two findings:** a default volume on macOS does not
distinguish letter case either, so on that platform two spellings of one folder still produce two
saved files under this fix - loud, and never one file for two folders, but the same false "no saved
scan" answer the review objected to on Windows. The mandate named Windows, and macOS rules arrive in
a later phase; the owner may want the fold extended there when that phase is designed.

## Finding 2: --json was read and then dropped by --version and --help

**Accepted.** The command line reader's own comment calls a flag that is read and dropped a wrong
answer, and that is what happened: `--json` given before `--version` was switched back off in the
request the `--version` branch returned, and `--version --json` returned before the flag was read at
all. The same held for `--help`.

**What the fix does.** `CommandLine.Parse` no longer returns from inside the argument loop when it
meets `--help`, `-h` or `--version`. It remembers which of them was asked for, keeps reading every
flag, and answers only after the loop has finished, building the help or version request from every
flag that was read - so `--json` survives in both orders. When both `--help` and `--version` are
given, the first to appear wins, which is what the command line answered before this change.

Two deliberate consequences of reading the whole command line, both in the direction the AXI
standard points:

- a flag that follows `--help` or `--version` is now judged rather than quietly ignored, so
  `--help --top 5` is a usage error naming the flags that exist, where before it silently dropped
  `--top`;
- help and version are answered before the missing-folder check, so `cc-cleanup-storage scan --help`
  still shows the help page, as it always did.

The version and help answers already carried machine-readable payloads, so no change was needed
there: once the flag survives the read, the entry point prints the payload it was always holding.

`CommandLine.Parse` is a public method this change touched, so its body moved into a private reader
and the public method now writes entry, exit and failure log lines, per the repository's rule 2 on
logging.

**The tests that fail without the fix.** In `CommandLineTests.cs`:

- `Parse_VersionWithTheMachineReadableFlagInEitherOrder_KeepsTheFlag` - `--json --version` and
  `--version --json` both keep the flag.
- `Parse_HelpWithTheMachineReadableFlagInEitherOrder_KeepsTheFlag` - the same for `--help` and for
  `-h`, in both orders.
- `Parse_AFlagAfterHelpThatHelpDoesNotTake_IsAUsageErrorRatherThanQuietlyDropped` - `--top` after
  `--help` is refused rather than dropped.
- `Parse_HelpForACommandGivenNoFolder_StillShowsTheHelpPage` - the old behaviour that must not
  regress.

In `RunnerTests.cs`:

- `Run_VersionWithTheMachineReadableFlagGivenBeforeIt_AnswersInMachineReadableForm` and
  `Run_VersionWithTheMachineReadableFlagGivenAfterIt_AnswersInMachineReadableForm` - the whole path
  the entry point takes: read the command line, run the request, write the machine-readable payload,
  and find the version in it.

**The revert proof.** The fix file
`tools/cc-cleanup-storage/src/CcCleanupStorage/CommandLine.cs` was reverted with `git stash push`
while the tests stayed in place, and every test above was run: all nine that depend on the fix were
red (the tenth in the filter, the help-page regression guard, stayed green because it guards
behaviour the defect and the fix share). The fix was restored and all pass.

## What this leaves alone

No removal code of any kind was added, and nothing outside the two findings was changed. The suite
now holds 128 tests and all are green under `dotnet test src\CcDirector.Reclaim.Tests`. The new tests
branch on the platform they run on rather than failing by design anywhere, so the suite's only
by-design red test on macOS and Linux remains the cloud placeholder test the review already
recorded.
