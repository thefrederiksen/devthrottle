# Review of the phase 1 fix round

Written by the Reviewer for phase 1 of the Reclaim the Disk mission, 19 September 2026. This review
covers commit 5e539e1e9 only, on branch `reclaim/phase1-scan-and-report`, open as pull request
#3122. Its parent 9d8f08640 is the already-reviewed phase 1 work and is not re-reviewed here.

## What I ran

In this worktree, detached at 5e539e1e9:

- `dotnet test src/CcDirector.Reclaim.Tests` - 128 passed, 0 failed, which is the count the
  Developer's answers document claims.
- The revert proof for finding 1: the case fold in `ScanIndexStore.Fingerprint` was removed by hand,
  the project rebuilt, and both named tests run - both red
  (`PathFor_TwoSpellingsThatDifferOnlyInCase_FollowThePlatformItRunsOn`,
  `Run_ReportAfterAScanWithThePathCaseFlipped_FollowsThePlatformItRunsOn`). Restored, rebuilt, and
  the whole suite green again on a real build, not `--no-build`.
- The revert proof for finding 2: `CommandLine.Read` was returned to the early-return form, the
  project rebuilt, and the command line and runner tests run - exactly nine red, and the tenth
  (`Parse_HelpForACommandGivenNoFolder_StillShowsTheHelpPage`) green, which is precisely what the
  answers document says would happen. The answers document is honest about that tenth test.
- The tool itself, built and run, at both 5e539e1e9 and its parent, to compare the answers a caller
  actually receives.

## Verdict

Not approved yet. Three findings, two of them answers a caller sees change for the worse in this
round, and one a test that pins a known wrong answer as the rule.

The two findings the round set out to answer are genuinely fixed on Windows, and I say so first.

## What is right

- **Finding 1 is fixed and the platform branch is honest.** On Windows `Fingerprint` folds the
  canonical path with `ToUpperInvariant` before hashing, and `Label` already lower-cased every
  letter, so both parts of the file name now agree and one folder spelled two ways is one saved
  scan. On every other platform the exact bytes are hashed, so two real folders can never share one
  file. `DirectoryScanner.Canonical` is untouched, so trailing separators and dot segments still
  fold everywhere. The stored `RootPath` still records the spelling the scan was given, as the
  answers document claims.
- **Finding 2's flag now survives in both orders.** Run against the built tool, `--json --version`
  and `--version --json` both print the machine-readable payload with the version in it.
- **No removal code anywhere.** Searching `src/CcDirector.Reclaim`, `tools/cc-cleanup-storage` and
  the suite for `File.Delete`, `Directory.Delete`, `File.Move`, `Directory.Move`, recycle-bin calls
  and `Remove-Item` returns one hit: the test fixture tearing down the tree it built.
- **No attribution and no non-ASCII characters** in the diff or in the commit message.
- **Logging** was added to both public methods the round touched, entry and exit on
  `ScanIndexStore.PathFor` and entry, exit and failure on `CommandLine.Parse`. `PathFor` is called
  twice per run, never in a loop, so the lines cost nothing.

## Findings

### 1. The machine-readable help page now carries no help

`tools/cc-cleanup-storage/src/CcCleanupStorage/Runner.cs:251` - `Help()` returns
`new { command = "help", ok = true }` as its payload and discards the twenty lines of usage text.
Reached now because `CommandLine.cs:211` builds `HelpRequest(indexDirectory, json)`.

Observed, both built and run:

- At 5e539e1e9: `cc-cleanup-storage --help --json` prints `{"command":"help","ok":true}`, exit 0.
- At 9d8f08640: the same command prints the whole usage page, exit 0.

So for the agent this standard exists to serve, this round made `--help --json` strictly less
informative than it was before the fix. It is also the repository's own absence rule: an answer that
reports `ok` and says nothing is not a definitive empty answer, it is a missing one.

The answers document is wrong on this point where it says "The version and help answers already
carried machine-readable payloads, so no change was needed there". Version's payload carries the
version. Help's payload carries no help.

What to do: give the help payload its content - the usage lines, the commands, the flags each
command takes, the exit codes - and pin it with a test that reads a flag name out of the
machine-readable help. Version needs nothing.

### 2. Help followed by a word regressed from the help page to a misleading usage error

`tools/cc-cleanup-storage/src/CcCleanupStorage/CommandLine.cs:135-143` - the positional-argument
checks run inside the loop and return before the `earlyAnswer` check at line 208 is ever reached.

Observed, both built and run:

- At 5e539e1e9: `cc-cleanup-storage --help scan` answers
  `the command cc-cleanup-storage with no command takes no folder, and one was given: scan`,
  exit 2.
- At 9d8f08640: the same command prints the help page, exit 0.

Asking for help about a command is an ordinary thing for a person and an agent to type, and the
answer now calls a command word a folder, which is not a stricter answer but a wrong one. The
answers document declares the deliberate consequence for a FLAG that follows help - "a flag that
follows `--help` or `--version` is now judged rather than quietly ignored" - and a bare word is not
a flag, so this change is neither declared nor tested.

What to do: when help or version has been asked for, stop failing on a positional argument - either
ignore it, or answer help for it when it names a known command. Add a test for `--help scan`.

### 3. The two new tests pin the wrong rule on macOS

`src/CcDirector.Reclaim.Tests/ScanIndexStoreTests.cs:145-152` (the else branch asserting `NotEqual`)
and `src/CcDirector.Reclaim.Tests/RunnerTests.cs:110-113` (the else branch asserting
`FileNotFoundException`).

The branch both tests take is `OperatingSystem.IsWindows()` against everything else, but the rule
being pinned is the file system's case sensitivity, and a default macOS volume does not distinguish
letter case either. On macOS these tests therefore assert, as the correct answer, the same false
"there is no saved scan" that the review raised for Windows.

The Developer did disclose the macOS gap in prose, which is the right instinct. But a green test
asserting the defect is the rule is worse than no test at all: the next seat reads the assertion,
not the paragraph. The continuous integration .NET job runs on Windows only, so nothing runs these
branches today - the defect is latent, not live.

What to do: either fold case on macOS as well
(`OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()`), which is one condition and matches
what both platforms' file systems do, or, if the owner wants macOS left to a later phase, restrict
the else branch to Linux and say in the test that macOS is unresolved.

## Two small things, no action needed to merge

- `src/CcDirector.Reclaim.Tests/RunnerTests.cs:321-340` - `VersionAnswer` collapses three separate
  conditions into one boolean, so a red test says only `Expected: True, Actual: False` and never
  which step failed. Assert each condition inside the helper instead of returning a bool.
- `tools/cc-cleanup-storage/src/CcCleanupStorage/CommandLine.cs:211` - `HelpRequest` is now handed
  the index directory the caller may have overridden, and `Runner.Help()` never reads it. Harmless,
  but it is a field carried for nothing.
