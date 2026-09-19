# Phase 1, fix round two - review

Written by the Reviewer on 19 September 2026, in a worktree of its own at
`D:/ReposFred/devthrottle-reclaim-review2`, detached at `d6360a7d1` on branch
`reclaim/phase1-scan-and-report`, open as pull request 3122.

One commit is under review: `d6360a7d1`. Its parent `5e539e1e9` was reviewed in
`review-phase-1-fix-round.md` and is not re-reviewed except where this round touches it.

## Verdict

**Approved, with three findings recorded against the new machine-readable help page.**

The three findings this round answers are genuinely fixed. Each was proven by reverting the fix by
hand in this worktree, rebuilding, seeing the named test red, restoring, rebuilding, and seeing it
green. The suite is fully green on a real build. Nothing beyond the three findings was smuggled in,
there is no removal code anywhere in the reclaim surface, and there is no attribution of any kind.

The three findings below are new, and they are about the help page this round created rather than
about the three it answered. None of them breaks a command, loses data, or leaves a caller without a
clear way forward - each one ends in a plain exit-2 message naming the valid values. Finding 1 is a
few lines and I would fix it before merging if another round is cheap; if the Delivery Lead prefers
to merge now, all three are safe to carry into phase 2 as follow-up work.

## What was run

- `dotnet build tools/cc-cleanup-storage/src/CcCleanupStorage/CcCleanupStorage.csproj` - succeeded,
  0 warnings, 0 errors.
- `dotnet test src/CcDirector.Reclaim.Tests` - **132 passed, 0 failed, 0 skipped**, on a build the
  run made itself. No `--no-build` run was used anywhere in this review, including on restores.
- The built tool was run at `d6360a7d1` and at its parent `5e539e1e9`, from a second worktree cut
  detached at the parent and built separately. That worktree has been removed.
- A sweep of `src/CcDirector.Reclaim` and `tools/cc-cleanup-storage` for removal verbs.
- A sweep of every file the commit touches for non-ASCII bytes and for attribution strings.

At the end of the review the worktree is clean: `git status --short` is empty and `git diff HEAD` is
empty, so every hand revert below was fully undone.

## Finding 1 of the round under review: the machine-readable help page carried no help

**Fixed, and proven.**

What a caller receives, measured at both commits:

| Call | At `5e539e1e9` | At `d6360a7d1` |
|---|---|---|
| `--help --json` | `command` and `ok`, and nothing else | the whole page: `usage`, `commands` with each one's flags, `flags` with purposes, `exitCodes` with meanings, `notes` |

The page is written once as data in `Runner.Help()` (`Runner.cs:222-262`) and both surfaces render
from it, so the text page and the payload cannot drift apart. The flag lists come from
`CommandLine.SavedScansFlags`, `ScanFlags` and `ReportFlags` (`CommandLine.cs:71-77`), which are the
same lists the reader refuses unknown flags against.

**The text page is unchanged but for one space, disclosed by the Developer.** Diffed byte for byte
between the two commits, the only difference is the first usage line, which now aligns with the
other two instead of ending one space short. That is an improvement and it was written down.

**Revert proof.** The payload in `Runner.Help()` was replaced by hand with the old two-field
anonymous answer, the project rebuilt, and
`Run_HelpInMachineReadableForm_CarriesThePageAndAFlagNameCanBeReadOutOfIt` run: **red**
(`KeyNotFoundException` at `RunnerTests.cs:262`, the `usage` field absent). The fix was restored,
rebuilt, and the same test ran **green**.

## Finding 2 of the round under review: a bare word after help or version became a usage error

**Fixed, and proven.**

| Call | At `5e539e1e9` | At `d6360a7d1` |
|---|---|---|
| `--help scan` | exit 2, "takes no folder, and one was given: scan" | exit 0, the help page |
| `-h scan` | exit 2, the same wrong answer | exit 0, the help page |
| `--version scan` | exit 2, the same wrong answer | exit 0, `cc-cleanup-storage 1.0.0` |
| `--json --version` | exit 0, the version payload | unchanged |
| `--help --top` | exit 2, "there is no flag --top ..." | unchanged |

The last row matters: the skip is narrow. A positional argument is passed over only when help or
version has been asked for (`CommandLine.cs:150-151`); flag validation is untouched, so an unknown
flag after `--help` is still refused with the valid values. A positional argument with no help and no
version asked for is judged exactly as before.

**Revert proof.** The two-line skip at `CommandLine.cs:150-151` was deleted by hand, the project
rebuilt, and the filter run over the three named tests plus the finding-1 test:
`Parse_HelpFollowedByAWord_ShowsTheHelpPageRatherThanAUsageError` (both of its cases) and
`Parse_VersionFollowedByAWord_AnswersTheVersionRatherThanAUsageError` were **all three red**, while
the finding-1 test stayed green, confirming the reverts are independent. The fix was restored,
rebuilt, and all four ran green.

## Finding 3 of the round under review: the platform tests pinned the wrong rule on macOS

**Fixed as far as this machine can show, and the limit is real. I judge the choice sound.**

The fold in `ScanIndexStore.Fingerprint` is now
`OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()` (`ScanIndexStore.cs:309`), and both
platform tests branch on the same condition, so neither asserts the false "there is no saved scan"
answer as the rule on macOS.

**Revert proof, and what it does not cover.** Two reverts were run:

- The fold was removed on every platform. Both
  `PathFor_TwoSpellingsThatDifferOnlyInCase_FollowThePlatformItRunsOn` and
  `Run_ReportAfterAScanWithThePathCaseFlipped_FollowsThePlatformItRunsOn` went **red**. Restored and
  rebuilt: green.
- The fold and both test conditions were reverted to the parent's Windows-only rule. Both tests
  stayed **green** on this Windows machine.

The second revert is the honest limit, and I state it plainly: **the macOS half of this round's
change has no executable proof on this machine.** `OperatingSystem.IsMacOS()` is false here, so the
added disjunct is dead code in every run performed, in the engine and in both tests. What the first
revert proves is the Windows fold, which already existed at the parent. The macOS half is a reasoned
change awaiting a macOS run. The Developer recorded this same limit in the answers document, which is
the right thing to have done.

**On the choice itself, judged against the mission.** The Developer folded case on macOS rather than
leaving macOS unresolved, on the reasoning that this is engine truth about a file system and not a
macOS rule. I agree, for three reasons drawn from the mission documents:

- The phase mandate says the engine is "platform-neutral ... nothing Windows-specific in it: the
  Windows rules are a separate phase", and that "it works on Windows, macOS and Linux"
  (`mandate-phase-1.md:26,61`). Knowing how a platform's file system compares two path spellings is
  what a platform-neutral engine has to know to be correct on all three.
- The mission's "Out" list excludes "macOS and Linux rules" (`mission.md:214`). A rule in this
  mission is a statement about what is disposable - orphaned installer packages, package caches. A
  case fold is not one of those, so it is not what that line excludes.
- The alternative the review offered would have left a green test asserting the false answer as the
  rule on macOS, which the previous review itself called worse than no test.

**One boundary, accepted rather than raised as a finding.** The check asks the platform, not the
volume, so a macOS volume deliberately formatted case-sensitive is folded as though it were not, and
two genuinely different folders would then share one saved scan - a silent wrong merge, which is a
worse failure than the missing scan this fixes. The Developer stated this boundary. It is not a new
risk: the identical exposure already exists on Windows and was accepted when the previous review
demanded the Windows fold, and `Label()` lowercases the readable part of the file name too
(`ScanIndexStore.cs:283-300`), so the label does not separate the two either. The mission's platforms
are the defaults, and case-sensitive is not the default on either. **Recommendation for the macOS
phase: ask the volume rather than the platform, and cover it with a test on a case-sensitive volume.**

## Scope: nothing beyond the three findings

- **No removal code anywhere.** A sweep of `src/CcDirector.Reclaim` and `tools/cc-cleanup-storage`
  for `File.Delete`, `Directory.Delete`, `File.Move`, `Directory.Move`, recycle, quarantine, holding,
  purge and trash returns three hits, all three of them the ordinary English word "hold" in prose
  about cloud placeholder files and about scan depth. Nothing deletes, moves or holds anything.
- Every file the commit touches is traceable to one of the three findings. Making the three flag
  lists public (`CommandLine.cs:70-77`) serves finding 1 and is documented where it happens.
- The one change nobody asked for is the one-space alignment of the first usage line, which came with
  the rewrite and was disclosed in the answers document. I do not count it as scope creep.
- Only this tool and its tests consume any of the changed code; the sole other reference to
  `CcCleanupStorage` or `CcDirector.Reclaim` in the repository is the solution file.

## Style

- Plain English throughout, no abbreviations in code, comments, tests or the answers document.
- **ASCII only.** Every file the commit touches was swept byte by byte; no character outside
  printable ASCII and tab appears in any of them.
- **No attribution of any kind.** The commit message, the diff and the answers document were swept
  for every assistant vendor name, every agent product name, and both of the trailer lines a harness
  adds by default: no hits.
- **Logging on public methods touched.** `CommandLine.Parse` writes entry, done and failure lines;
  `Runner.Run` writes entry and done. The three new public members are properties, not methods, and
  `Runner.Help()` is private and reached through `Runner.Run`, which logs. Nothing is missing.

## New findings, against the help page this round created

### Finding 1 - the machine-readable help page names a command the tool refuses

`tools/cc-cleanup-storage/src/CcCleanupStorage/Runner.cs:226`, with the shape at
`JsonShapes.cs:138`.

The payload carries an array called `commands`, whose first entry has a `name` of `saved-scans`. The
other two entries' names, `scan` and `report`, are command words that can be typed. This one cannot:

```
> cc-cleanup-storage saved-scans
error: usage
message: there is no command saved-scans; the commands are scan and report.
```

An agent reading a field called `name`, inside an array called `commands`, beside two entries where
that field IS the command word, will type it and be refused. The `usage` array does carry the bare
form `cc-cleanup-storage`, but nothing in the payload ties a usage line to a command - they are two
separate arrays that happen to be in the same order. This is the same class of defect as the finding
this round fixed: the machine-readable page telling a machine something the tool will not do.

The error it produces is a clean exit 2 naming the valid commands, so the caller recovers in one
step. The fix is small: give `HelpCommandJson` the invocation line as its own field, so each command
carries how it is called and the parallel arrays disappear.

### Finding 2 - the page misses two flags the reader takes

`tools/cc-cleanup-storage/src/CcCleanupStorage/Runner.cs:226,237-238`, against
`CommandLine.cs:165,171`.

The commit message claims the page "can never name a flag the reader refuses or miss one it takes".
The first half holds. The second does not, in two places:

- **`-h` is taken everywhere and named nowhere.** `CommandLine.cs:165` accepts `-h` as an equal of
  `--help`; it was verified to work (`-h scan` prints the page, exit 0). It appears in neither the
  global `flags` array nor any command's flag list. This was also true of the text page before this
  round, so it is inherited rather than introduced - but it contradicts the claim the round is built
  on, and the page is now the single source both surfaces render from, so it is one edit to fix both.
- **`--version` is taken with no command word and omitted from that command's flags.**
  `CommandLine.cs:171` accepts `--version` when the command is `SavedScans`, bypassing the allow-list
  entirely, so `SavedScansFlags` at `CommandLine.cs:71` does not contain it and the page's first
  command's flag list therefore does not either - while the global `flags` array does list
  `--version`. A machine reading the per-command list concludes the bare tool does not take
  `--version`, which is wrong. The per-command lists are right for `scan` and `report`, where
  `--version` genuinely is refused (verified: `scan --version` is exit 2).

Both are wrong answers to a machine rather than untidiness, which is the standard
`docs/axi-standard.md` sets and the standard the round invoked.

### Finding 3 - the two help arrays are coupled only by position

`tools/cc-cleanup-storage/src/CcCleanupStorage/Runner.cs:248-252` and `272`.

`usage` is a bare three-element array and the text renderer walks it as `usage[at]` while bounded by
`commands.Count`. A fourth command added to `commands` without a fourth line added to `usage` throws
`IndexOutOfRangeException` from the help page - the one command that must never fail. This is not a
defect today, both lists hold three, and fixing finding 1 by moving the invocation line onto
`HelpCommandJson` removes this as a side effect.

## What this review does not cover

- The macOS half of the round's finding 3, as stated above: dead code in every run performed here.
- The parent commit `5e539e1e9` and the original phase 1 commit, except where this round touches
  them.
- Any suite other than `src/CcDirector.Reclaim.Tests`. The changed code has no other consumer in the
  repository, so I did not run the repository gate.
