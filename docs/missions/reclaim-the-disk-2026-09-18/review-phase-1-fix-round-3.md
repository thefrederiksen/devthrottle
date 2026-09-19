# Phase 1, fix round three - review

Written by the Reviewer on 20 September 2026, in a worktree of its own at
`D:/ReposFred/devthrottle-reclaim-review2`, detached at `87e6c41bf` on branch
`reclaim/phase1-scan-and-report`, open as pull request 3122.

Under review is only the round-three delta: `git diff d6360a7d1..87e6c41bf`, two commits
(`1fa5f815c`, `87e6c41bf`). It answers the three findings the round-two review recorded in
`review-phase-1-fix-round-2.md`, and the author's written answers are the "Fix round three"
section at the end of `review-phase-1-answers.md`. The parent commit `d6360a7d1` was reviewed and
approved in round two and is not re-reviewed except as the behaviour baseline this round is measured
against.

## Verdict

**Approved. No findings.**

All three findings are genuinely fixed. Each fix was proven by reverting it by hand in this
worktree, rebuilding, seeing the named test or the named harm appear, restoring, rebuilding, and
seeing green - with one honest exception, finding 3, where the named test cannot be red under a pure
revert and the harm was reproduced the way the author did it, as a demonstration on real builds both
ways. No `--no-build` was used anywhere in this review, including on restores.

The flag-agreement test genuinely asks the command line reader: proven not by reading it alone but by
reverting the reader to its pre-round-three form and watching the test fail with the exact asymmetry
it exists to catch. No behaviour that worked before changed: twenty calls were run against both
builds and every exit code is identical. The text help page changed by exactly the two flag lines the
author claimed, measured by rendering both pages from two separate builds and diffing them. Nothing
outside the three findings was changed, no removal code exists anywhere in the reclaim surface, every
touched file is plain ASCII, and nothing names any assistant, model or vendor.

One observation is recorded below, not as a finding: five usage-error messages now enumerate two more
flag spellings than before, which the author disclosed for one instance and which is the direct and
beneficial consequence of the second finding's fix.

## What was run

- `dotnet build tools/cc-cleanup-storage/src/CcCleanupStorage/CcCleanupStorage.csproj` - succeeded,
  0 warnings, 0 errors, run twice more during the revert proofs.
- `dotnet test src/CcDirector.Reclaim.Tests` - **135 passed, 0 failed, 0 skipped**, on a build the run
  made itself, run three times in total (start, after the last restore, at the end). No `--no-build`
  run was used anywhere in this review, including on restores.
- The built tool at `87e6c41bf` and at its parent `d6360a7d1`, the parent built in a second worktree
  cut for the purpose and removed afterwards. Twenty command lines were run against each build with
  their exit codes and full output captured and compared directory by directory.
- Hand reverts of each fix in this worktree, rebuilt and re-run each time, as described per finding
  below.
- A sweep of `src/CcDirector.Reclaim` and `tools/cc-cleanup-storage` for removal verbs.
- A byte-by-byte sweep of all five files the delta touches for characters outside printable ASCII
  and tab.
- A sweep of the delta, the two commit messages, and the whole reclaim surface for every assistant
  vendor name, agent product name, and both trailer lines a harness adds by default.

At the end of the review the worktree is clean: `git status --short` is empty and `git diff HEAD` is
empty, so every hand revert below was fully undone. The second worktree and every captured file were
removed.

## Finding 1 of the round under review: the page named a command the tool refuses

**Fixed, and proven.**

Every command entry now carries `name`, `word` and `invocation` as three separate things
(`JsonShapes.cs:143-163`, `Runner.cs:224-243`), so nothing has to be inferred from a name any more.
The first command's `word` is empty and its `invocation` is the bare tool, which the entry says
plainly.

**Revert proof.** The first command's `word` was set back to `saved-scans` by hand, the project
rebuilt, and `Run_HelpInMachineReadableForm_EveryCommandSaysHowItIsCalledAndTheReaderTakesThatWord`
run: **red** - the entry's own invocation does not contain the word the page hands out, and the word
is one the reader refuses. The fix was restored with `git checkout`, the project rebuilt, and the same
test ran **green**.

The test takes the page at its word the way a machine would: it reads each `word` out of the serialized
payload and hands it to `CommandLine.Parse`, the very thing that would refuse it, and requires the
request's own `CommandWord` to be that word.

## Finding 2 of the round under review: the page missed two flags the reader takes

**Fixed, and proven. And the test really asks the reader.**

The root cause is addressed, not the symptom: `-h` and `--version` are now inside the flag lists,
which are the one source of what each command takes, and the reader judges every flag against the
command's list first, help and version included (`CommandLine.cs:168-179`). Nothing decides which
command takes which flag a second time: the `--version` special case for the bare tool is gone, and
the early-answer branches only record which of help or version was asked for, after the list has
already ruled.

**The flag-agreement test does not compare the lists with themselves.**
`Run_HelpInMachineReadableForm_EachCommandsFlagsAreExactlyTheOnesTheReaderTakes` takes every flag
spelling this tool has anywhere, hands each one to `CommandLine.Parse` for each command, and counts a
flag as taken only when the reader builds a request - so both halves of the equality come from live
parser calls, not from the lists the page prints. This is not a reading of the test; it is proven by
the revert below, in which the "taken" side and the "named" side genuinely disagreed.

**Revert proof.** `CommandLine.cs` was restored to its pre-round-three form with
`git checkout d6360a7d1 --`, the project rebuilt, and the test run: **red**, reporting the defect
itself, in the author's exact words:

```
Expected: ["--help", "--index-directory", "--json", "--version", "-h"]
Actual:   ["--help", "--index-directory", "--json"]
```

The reader takes five flags with no command word while the page names three of them. The other three
machine-readable help tests stayed green under this revert, so the reverts are independent. The fix
was restored, the project rebuilt, and all four ran **green**.

Behaviour verified on the built tool: `-h`, `scan -h` and `report -h` all print the page and exit 0;
`--version` and `--json --version` answer the version; `scan --version` and `report --version` are
still refused with exit 2; the narrow skip from round two (a bare word after help or version is
passed over, an unknown flag after it is still refused) is untouched.

## Finding 3 of the round under review: the usage lines and the commands were coupled only by position

**Fixed, and proven by demonstration, both ways, on real builds.**

The usage lines are now read off the commands themselves (`Runner.cs:265-283`), so a command cannot be
added without its line. The help columns widen for a longer entry and never fall below the widths the
page has always used, so today's page is unchanged to the character.

**Revert proof, and its limit, stated plainly.** The named test,
`Run_HelpInMachineReadableForm_TheUsageLinesAreTheCommandsOwnInvocationsOneForEach`, is a standing
pin: under a pure revert with today's three commands, both lists still agree and the test stays
green, so it cannot be made red that way, and I do not claim it was. The finding's own stated harm
was reproduced instead, exactly as the author did it:

- `Runner.cs` and `JsonShapes.cs` were restored to their pre-round-three structure, a fourth command
  was added with no fourth usage line beside it, and the project rebuilt. `cc-cleanup-storage --help`
  answered `error: failed` / `message: Index was outside the bounds of the array.` and exited **1** -
  the one answer that must never fail, failing.
- The fix was restored, the same fourth command was added in the five-field form, and the project
  rebuilt. `cc-cleanup-storage --help` printed the whole page including the fourth command's usage
  line, and exited **0**. No second edit anywhere was needed to make that line appear.

The fourth command was then removed and the tree restored; `git diff HEAD` is empty and the full
suite is green.

## No behaviour that worked before has changed

Twenty command lines - every help and version spelling in both orders, the word-after-help and
word-after-version cases, the unknown-flag refusals with and without a command word, the
saved-scans-word refusal and the scan-without-folder refusal - were run against the parent build and
this round's build. **Every exit code is identical.** Every answer is identical except:

1. The text help page, which changed by exactly the two flag lines the author claimed and nothing
   else: the `-h` line is new, and `--version` now says it is taken with no command word only. Measured
   by rendering the page from both builds and diffing the captured output.
2. The machine-readable help payload, which gained the `word` and `invocation` fields and the flags
   the reader takes - the three findings themselves.
3. Five usage-error messages, of three distinct shapes, now enumerate `-h` and `--version` where the
   reader takes them: the no-command unknown-flag message, the scan unknown-flag message and the
   report unknown-flag message. The author disclosed one instance of this (`scan --version` now
   naming `-h`); the full extent is five messages, and I record it here rather than raise it, because
   it is the same single mechanism as the finding's fix - the messages print the same lists that are
   now the one source - and every one of them now names flags the reader genuinely takes, where
   before each named a list that was missing working flags. Nothing that worked before works
   differently.

## Scope: nothing beyond the three findings

- The delta touches five files. The answers document and `RunnerTests.cs` are purely additive (108 and
  128 lines, no deletions); the deletions in `CommandLine.cs`, `JsonShapes.cs` and `Runner.cs` are
  all inside the three findings and all were read in full.
- Every line of the delta was read. Nothing outside the three findings was changed. `Program.cs`, the
  engine, the index store and every other file of the reclaim surface are untouched by this round.
- The Delivery Lead building this round is unusual for the method's seats, but the owner's explicit
  override is recorded in `handover-delivery-lead-2.md` on the mission branch, as the answers claim,
  and this review comes from a different agent family, which is the mechanism that override still
  requires.
- Only this tool and its tests consume the changed code; `HelpCommandJson` has no other reader in the
  repository.

## Removal code, ASCII, attribution

- **No removal code anywhere.** The sweep of `src/CcDirector.Reclaim` and `tools/cc-cleanup-storage`
  for `File.Delete`, `Directory.Delete`, `File.Move`, `Directory.Move`, `.Delete(`, recycle,
  quarantine, purge, trash, unlink and remove returns three hits, all three the ordinary English
  prose that says the tool never deletes, moves or changes anything. Nothing deletes, moves or holds
  anything.
- **ASCII only.** All five files the delta touches were swept byte by byte; no character outside
  printable ASCII and tab appears in any of them.
- **No attribution of any kind.** The two commit messages, the full delta, and the whole reclaim
  surface were swept for every assistant vendor name, agent product name, `Co-Authored-By`, and
  `Generated with`: no hits. The only vendor-adjacent strings in the surface are pre-existing
  references to the repository's own `CLAUDE.md` instruction file, which name a file in this
  repository and were not added by this round.
- Plain English throughout: no abbreviations in the delta's code, comments, tests, documents or
  commit messages.

## What this review does not cover

- The author's own claim that no `--no-build` run was used in their round: that is self-testimony and
  cannot be verified from here. What is verified is that this review's own builds and restores used
  none.
- The parent commit `d6360a7d1` and everything before it, except as the behaviour baseline, which
  the round-two review approved.
- Any suite other than `src/CcDirector.Reclaim.Tests`. The changed code has no other consumer in the
  repository, so the repository gate was not run.
- This round touches no platform-conditional code, so nothing here is unreachable on this machine;
  the macOS limit recorded in round two belongs to the earlier round and is unchanged by this delta.
- The worktree is three commits behind `origin/main`, which is expected for a review checkout pinned
  to a pull request head; none of the three commits on main touch the reclaim surface, which exists
  only on this branch.
