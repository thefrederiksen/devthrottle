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

## Fix round two

Written by the Developer on 19 September 2026, in answer to `review-phase-1-fix-round.md`. All three
findings are accepted. The tests named here are in `src/CcDirector.Reclaim.Tests`, and each was
proven red under an injected revert of its fix before it was trusted green.

### Finding 1: the machine-readable help page now carries no help

**Accepted.** The review is right, and the sentence in the round-one answers above that said "the
version and help answers already carried machine-readable payloads, so no change was needed there"
was wrong about help: version's payload carried the version, help's payload carried two fields and
none of the page. For the agent this standard exists to serve, `--help --json` had become strictly
less informative than it was before the fix round.

**What the fix does.** `Runner.Help()` now writes the page once, as data, and renders both surfaces
from it: the text lines the terminal prints and a new `HelpJson` payload. The payload carries the
usage lines, the commands with the flags each one takes, every flag with what it does, every exit
code with what it means, and the closing notes of the page. The flags each command takes come from
the command line reader's own lists, which are public now, so the page can never name a flag the
reader refuses or miss one it takes - the same single source the AXI standard asks a help page to
agree with the parser on. Version needed nothing and is unchanged.

The text page prints the same words as before. One whitespace change came with the rewrite: the
three usage lines are now aligned to the same column, where the old page's first usage line ended one
space short of the other two.

**The test that fails without the fix.**
`Run_HelpInMachineReadableForm_CarriesThePageAndAFlagNameCanBeReadOutOfIt` in `RunnerTests.cs` reads
the answer the way a machine does, out of the serialized payload: it finds the scan command, reads the
flag name `--folder-depth` out of the flags that command takes, finds a flag with its purpose, an
exit code with its meaning, and the usage lines.

**The revert proof.** The payload in `Runner.Help()` was reverted by hand to the two-field answer the
review found, the project rebuilt, and the test above run: red. The fix was restored and the suite is
green again.

### Finding 2: help followed by a word regressed from the help page to a misleading usage error

**Accepted.** The round-one answers declared the consequence of reading the whole command line for a
FLAG that follows help, and a bare word is not a flag. Answering "takes no folder, and one was given:
scan" to a caller asking for the help page calls a command word a folder, which is a wrong answer,
not a stricter one.

**What the fix does.** When help or version has been asked for, a positional argument is no longer
judged: the reader moves past it and answers the help page or the version. The review offered two
shapes for this and I chose the simpler one, ignoring the word, for this reason: this tool has
exactly one help page, which answers for every command, so there is nothing more to say when the word
names a known command - the page the caller gets is the page they were asking for either way. The
positional argument is still judged exactly as before when help and version have NOT been asked for,
so `cc-cleanup-storage --json C:\some\folder` remains the usage error it always was.

**The tests that fail without the fix.** In `CommandLineTests.cs`:
`Parse_HelpFollowedByAWord_ShowsTheHelpPageRatherThanAUsageError` covers `--help scan` and `-h scan`,
and `Parse_VersionFollowedByAWord_AnswersTheVersionRatherThanAUsageError` covers `--version scan`,
which regressed the same way.

**The revert proof.** The skip was removed by hand, the project rebuilt, and all three tests above
run: all red. The fix was restored and the suite is green again.

### Finding 3: the two new tests pin the wrong rule on macOS

**Accepted, and I chose to fold case on macOS as well.** The fold in `ScanIndexStore.Fingerprint` is
now `OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()`, and both platform tests branch on the
same condition.

**Why this choice and not leaving macOS unresolved.** The mission is Windows first, then macOS, then
Linux, and the fold is engine truth about the platform's file system, not a macOS rule - the mission's
"Out" list excludes macOS rules, and no rule exists in this phase at all. A default macOS volume does
not tell folders apart by letter case, exactly as on Windows, so folding there is the same honest
answer the review asked for on Windows. Restricting the fold to Windows and saying in the tests that
macOS is unresolved would leave a green test asserting the false "there is no saved scan" answer as
the rule, which the review itself calls worse than no test, and that test would be waiting for the
macOS phase to turn red. Folding now costs one condition and makes both tests pass on macOS rather
than assert a falsehood there.

The boundary, stated honestly: a macOS volume formatted to be case sensitive, which is not the
default, is folded like a default volume, because the check asks the platform and not the volume. The
mission's platforms are the defaults, and the same boundary already existed on Windows, where
case-sensitive volumes are also possible.

**The tests.** `PathFor_TwoSpellingsThatDifferOnlyInCase_FollowThePlatformItRunsOn` in
`ScanIndexStoreTests.cs` and `Run_ReportAfterAScanWithThePathCaseFlipped_FollowsThePlatformItRunsOn`
in `RunnerTests.cs` now assert one file for one folder spelled two ways on Windows and macOS, and two
files on Linux and every other platform.

**The revert proof, and its limit.** The fold was removed by hand on every platform, the project
rebuilt, and both tests run: both red on this Windows machine, which is the branch it can execute.
The macOS half of the condition cannot be executed on this machine, and I record that rather than
claim it: the proof that it is the right rule is that the fold condition and the tests' branch are
the same single statement, written in one place each, and the tests now state the macOS rule rather
than the false one. The continuous integration job runs on Windows only, so a macOS run of the suite
is the day that half is executed.

### What this leaves alone

No removal code of any kind was added, and nothing outside the three findings was changed. The two
small things the review listed as needing no action to merge - the `VersionAnswer` helper collapsing
its conditions into one boolean, and `HelpRequest` carrying an index directory that `Runner.Help()`
never reads - are left exactly as they are, reported here rather than fixed. The suite now holds 132
tests and all are green under `dotnet test src\CcDirector.Reclaim.Tests`.

## Fix round three

Answering the three findings the round-two review recorded against the machine-readable help page
it approved. The review called all three non-blocking and offered the choice of carrying them into
phase 2; they are answered here instead, because the help page is the thing an agent reads before
it does anything else with this tool, and a page that misdescribes the tool is the defect this
whole round exists to remove.

Written by the Delivery Lead, which built this round under the owner's explicit override recorded
in `handover-delivery-lead-2.md`. The round-three change therefore needs a Reviewer from a
different agent family, and the Delivery Lead sends it - the seat being judged never arranges its
own review.

### Finding 1: the page named a command the tool refuses

**Accepted.** `commands` carried an entry whose `name` was `saved-scans`, beside two whose name IS
the word that is typed. A machine reading that field would type `saved-scans` and be refused.

**What the fix does.** Every command entry now carries three separate things instead of one
overloaded one: `name` is the name the command answers under, which is the value the `command`
field carries in every answer it gives; `word` is the word that is typed, and it is empty for the
tool run with no command word; `invocation` is the whole line the command is called with. Nothing
has to be inferred from a name any more, and the name that is not a word cannot be mistaken for
one, because the entry beside it says plainly that there is no word.

**The test that fails without the fix.**
`Run_HelpInMachineReadableForm_EveryCommandSaysHowItIsCalledAndTheReaderTakesThatWord` in
`RunnerTests.cs` reads each entry out of the serialized payload, the way a machine does, and hands
the `word` it finds to the command line reader - the very thing that would refuse it. An empty word
must come with the bare tool as its invocation.

**The revert proof.** The first command's `word` was set back to `saved-scans` by hand, the project
rebuilt, and the test run: **red**. The fix was restored, rebuilt, and the test ran **green**.

### Finding 2: the page missed two flags the reader takes

**Accepted.** `-h` was taken by every command and named nowhere; `--version` was taken with no
command word and left out of that command's flag list while the global list named it.

**The root cause, and why the fix is not an addition to the page.** Both flags worked because they
were answered *before* the command's flag list was consulted, which let them work while sitting
outside it. Adding them to the page's text would have left that arrangement in place and fixed the
symptom. The fix is that the lists now hold every flag the reader takes, and the reader judges
every flag against the command's list first, help and version included. Which command takes which
flag is now one statement, in the lists, and nothing decides it a second time.

**Behaviour is unchanged.** Verified on the built tool: `-h`, `scan -h` and `report -h` all print
the page and exit 0; `--version` and `--json --version` answer the version; `scan --version` is
still refused with exit 2, now naming `-h` among the flags scan takes. The narrow skip the previous
round added - a bare word after help or version is passed over, an unknown flag after it is still
refused - is untouched.

**The test that fails without the fix.**
`Run_HelpInMachineReadableForm_EachCommandsFlagsAreExactlyTheOnesTheReaderTakes` in
`RunnerTests.cs`. It does not compare the lists with themselves, which would prove nothing. It
takes every flag spelling this tool has anywhere, asks the reader one at a time whether it takes
that flag for that command, and then requires the page to name exactly the flags the reader took -
no fewer, so a working flag is never hidden, and no more, so the page never sends a caller to a
refusal.

**The revert proof.** `CommandLine.cs` was restored to the pre-round-three reader by hand, the
project rebuilt, and the test run: **red**, reporting the defect itself - the reader takes
`--help, --index-directory, --json, --version, -h` with no command word while the page names three
of them. The fix was restored, rebuilt, and the test ran **green**.

### Finding 3: the usage lines and the commands were coupled only by position

**Accepted.** `usage` was a separate literal array and the text renderer walked it as `usage[at]`
while bounded by `commands.Count`, so a fourth command added without a fourth usage line beside it
threw `IndexOutOfRangeException` from the help page - the one answer that must never fail.

**What the fix does.** The usage lines are read off the commands themselves, so there is one list
and a command cannot be added without its line. The help columns also widen for a longer entry
rather than running into the words beside them, and their widths never fall below the ones the page
has always used, so today's page is unchanged to the character.

**The tests.** `Run_HelpInMachineReadableForm_TheUsageLinesAreTheCommandsOwnInvocationsOneForEach`
in `RunnerTests.cs` requires one usage line per command, in order, each equal to that command's own
invocation. It is the standing pin: two lists that can fall out of step cannot satisfy it.

**The revert proof, which is a demonstration rather than a test run, and why.** A test cannot add a
fourth command, because the command list is written inside `Runner.Help()`. Exposing it to a test
would change the design to suit the proof. So the finding's own stated harm was reproduced by hand,
both ways, on a real build each time:

- `Runner.cs` and `JsonShapes.cs` were restored to the pre-round-three structure, a fourth command
  was added with no fourth usage line, and the project rebuilt. `cc-cleanup-storage --help` answered
  `error: failed` / `message: Index was outside the bounds of the array.` and exited **1**.
- The fix was restored, the same fourth command added, and the project rebuilt.
  `cc-cleanup-storage --help` printed the whole page including the fourth command's usage line, and
  exited **0**. No second edit anywhere was needed to make that line appear.

The fourth command was then removed, `git diff HEAD` is empty, and the suite is green.

### What changed in the text page, disclosed

The text page is byte-identical to the round-two page but for the two lines the finding asked for,
measured by rendering both pages from two builds and diffing them: `-h` is now listed, and
`--version` now says it is taken with no command word only. Nothing else moved - the column
positions are the same, because the widths are floored at the ones the page already used.

### What this leaves alone

No removal code of any kind exists in this phase, and nothing outside the three findings was
changed. The suite now holds 135 tests, all green under `dotnet test src\CcDirector.Reclaim.Tests`
on a build the run made itself; no `--no-build` run was used anywhere in this round, including on
the restores.
