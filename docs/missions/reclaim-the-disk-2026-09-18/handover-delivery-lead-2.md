# Handover to the Delivery Lead, third seat

From the Delivery Lead, second seat, 19 September 2026. The owner has ruled: the seat that takes
this over FINISHES THE MISSION, and it may also build and review code itself - the owner's exact
instruction was that it "can also review the code and implement improvements if it finds any but
its clear job to finish this mission". That is an explicit override of the method's rule that a
Delivery Lead never builds and never reads diffs. It is recorded here per the method's own rule on
overrides: it names what it replaces, it lives in the mission's record, and it dies with the
mission.

## The mission

"Reclaim the Disk" - issue #3120, Gateway mission id 405ddcb3-79f5-4330-b5cf-3331cddeb2f2.
Build `cc-cleanup-storage`: a tool that scans a disk, says what fills it, recommends only what it
can PROVE is safe to remove (three proof kinds: a system record, the owner's own cleanup command,
or "we made it and it is old"), moves removals to an undoable holding folder, and refuses
everything it cannot prove disposable. **No removal is ever run on the owner's machine by this
mission - removal is proven on fixture trees the tests build.** The whole mandate is the mission
document, on main: `docs/missions/reclaim-the-disk-2026-09-18/mission.md`. It is your authority.
Phases 1 and 2 need no Tech Lead. Phase 3 (removal) does, and its Reviewer reads it BEFORE merge.

## Where phase 1 stands - everything is pushed, nothing is merged

Branch `reclaim/phase1-scan-and-report`, open as **pull request #3122** against main, mergeable.
Commit chain: `9d8f08640` (phase 1: scan and report) - `5e539e1e9` (fix round one) -
`d6360a7d1` (fix round two, the current tip).

The review record, all on its own branches, all readable with `git show <branch>:<path>`:

- `reclaim/phase1-review` - the first review. Two findings. Both fixed in round one.
- `reclaim/phase1-review-fixround` - the review of round one. NOT approved, three findings. All
  fixed in round two.
- `reclaim/phase1-review-fixround2` - the review of round two. **APPROVED**, with three new
  non-blocking findings in the help page. These three are OPEN - they were sent to the Developer
  seat but it would not wake, and no round three was built.

The three open findings (full detail in the review document on `reclaim/phase1-review-fixround2`,
path `docs/missions/reclaim-the-disk-2026-09-18/review-phase-1-fix-round-2.md`):

1. The machine-readable help page lists a command named `saved-scans` which the tool refuses with
   "there is no command saved-scans" (`Runner.cs:226`). The page must name only commands the tool
   takes. The Reviewer notes that fixing this by putting the invocation line on the command's own
   help entry removes finding 3 as well.
2. The page misses `-h`, which the reader takes everywhere, and omits `--version` from the
   saved-scans flag list although the reader takes it there (`Runner.cs:226,237-238` against
   `CommandLine.cs:165,171`). The page must agree with the parser everywhere - that agreement is
   the point of the round-two design, which draws the flag lists from the reader's own public
   lists.
3. Usage lines and command entries are coupled only by position in the help data, so a fourth
   command would throw from the help page (`Runner.cs:248-252,272`). Fix by structure, not by
   careful ordering.

**Your first move: build fix round three to answer these three**, in the Developer worktree
`D:\ReposFred\devthrottle-reclaim-phase1`, extend
`docs/missions/reclaim-the-disk-2026-09-18/review-phase-1-answers.md` with a "Fix round three"
section, prove each new test red under an injected revert, suite green, push. Then the gate and
merge (below).

## The gate and the merge rule

- The gate is `.\scripts\test-local.ps1`, run it in a worktree of your own - never the developer's
  tree. I ran it green at `d6360a7d1` (nine suites, 2,318 tests, `CcDirector.Reclaim.Tests` at
  132). Re-run it at the tip after round three.
- The gate's COVERAGE GAP line names parked suites (Core, Gateway, Gateway.UnitTests). The whole
  pull request touches only `src/CcDirector.Reclaim*`, `tools/cc-cleanup-storage`, the mission
  documents and four registration files (solution, `test-local.ps1`, `build-all-tools.ps1`,
  `tools/registry.json`) - nothing the parked suites cover. State that reasoning in the pull
  request; the rule allows it.
- Merge on local green plus a review by a different agent family, then squash-merge
  (`gh pr merge 3122 --squash --delete-branch`). The phase 1 code has been reviewed by two
  families across three reviews. If you build round three yourself you are the author, so the
  round-three delta needs a reviewer from a different family (ClaudeCode reviewing Pi's work, or
  the reverse).
- Pull request #3122's continuous integration is red with two failures, both accounted for and
  neither caused by this branch: `FleetManagerRoutesHostTests.The_session_list_pins...` is
  **known issue #3107, which fails on main's own run at the parent commit**, and
  `FleetPreferenceStoreTests.Add_Blank_IsRefused` is the known SQLite disposed-object teardown
  race. The mission's comparative criterion (a failure is ours only if the parent does not also
  show it) is satisfied for both. Do not let either block the merge; chase neither into this
  mission's scope.

## After phase 1 merges, in order (all in the mission document, section 6)

2. **Rules and recommendations** - rule contract, classifier, `recommend` command, first three
   rules (orphaned Windows installer packages; package caches by their own commands; DevThrottle
   test scratch folders in Temp). No removal code exists yet, so this phase cannot delete
   anything. Proof includes a read-only `cc-cleanup-storage recommend C:\ --json` on the owner's
   machine: exit 0, the installer rule with all three controls greater than zero, the unseen-gap
   line. Exit code and parsed JSON decide.
3. **Removal with holding** - `reclaim`, the holding folder, the whole ten-item refusal list. Seat
   a Tech Lead. Every refusal test proven red with the refusal removed. The Reviewer reads this
   phase BEFORE it merges.
4. **The remaining Windows rules** - may run parallel with phase 3 once phase 2 is merged.
5. **Background scan and rules as data** - the Launcher hosts the scan, rule files versioned and
   refreshed from the Gateway. Ends at MERGED. The Gateway deploy is the owner's decision, not
   yours - never deploy it yourself.
6. **The screen** - the Director and Cockpit page rendering the saved report. The engine writes
   the sentences; the screens decide nothing (critical rule 7).

Then the QA report on issue #3120 - the flow AND the failure cases - and the owner gets one page.
Report to the owner once, at the report; something genuinely undecidable is the only other thing
that reaches him.

## Fleet mechanics that will bite you

- **The `cc-devthrottle` command.** Its shims live in
  `C:\Users\soren\AppData\Local\cc-director\instances\default\bin`. That folder is emptied and
  repaired by Director starts during the one-tools-folder migration; if the command vanishes from
  your path mid-session, call it by its full path there. Do NOT use
  `C:\Users\soren\AppData\Local\cc-director\bin` - that holds a stale v1.3.0 that predates the port
  removal and asks for `CC_DIRECTOR_API`.
- **Snoozed seats do not wake for messages.** A session that has finished its turn and gone
  snoozed does not receive queued messages - my messages to two such seats sat undelivered for an
  hour. When a seat goes quiet: check its screen with `session buffer <id>`, and if it is idle at
  its prompt with your message undelivered, stop it (`session stop <id> --reason "..."`) and
  re-seat a fresh one. Verify the worktree is clean before you stop any seat.
- **Agent budgets, as of 19 September.** ClaudeCode hit its monthly spend limit and resets weekly
  at 3 PM Toronto (a seat parked on it resumes on its own when the limit lifts - do not spawn work
  onto it before then). Codex hit its usage limit and is out until 22 September 4:46 AM. Pi
  (GLM 5.3) has been the workhorse and works now. The review rule needs a different family from
  the author: Pi's work needs a ClaudeCode or Codex reviewer; ClaudeCode's work needs a Pi or
  Codex reviewer. Plan rounds so a reviewer family is actually available.
- **One worktree per workstream, cut from origin/main after the phase before it merges.** Never
  build in the shared checkout `D:\ReposFred\devthrottle` - it runs far behind and reading it has
  produced fiction twice. Read shipped code with `git show origin/main:<path>`.
- **Naming:** every seat is `Reclaim the Disk - <Role> - <what this seat does>`, attached to the
  mission (`--mission 405ddcb3-79f5-4330-b5cf-3331cddeb2f2`). No repository names, no ids.
- **Never sign anything.** No co-authored-by, no "Generated with", no mention of any assistant or
  vendor in commits, pull requests, issues, comments, code or documents. Check before every
  `git commit` and every `gh` call.
- **Plain English, no abbreviations**, everywhere: "pull request", not "PR"; "the local gate", not
  "CI". ASCII only in code, output and documents.

## What I left where

- `D:\ReposFred\devthrottle-reclaim-phase1` - the phase 1 worktree, on
  `reclaim/phase1-scan-and-report` at `d6360a7d1`, clean. It is yours to finish round three in, or
  hand to a Developer seat.
- `D:\ReposFred\devthrottle-reclaim-review2` - the review worktree, detached at `d6360a7d1`, clean.
  Both fix-round review documents were landed from it onto the branches named above.
- `D:\ReposFred\devthrottle-reclaim-gate1` - my gate worktree, detached at `d6360a7d1`, clean.
  Update it to the tip and re-run the gate there; remove it when the mission ends.
- `D:\ReposFred\devthrottle-reclaim-the-disk` - the mission record worktree, on branch
  `mission/reclaim-the-disk`, clean. The mission document, the first handover and this one are the
  record. Land the reviews, answers and proofs on main as you merge - the record is part of the
  work.
- My two remaining seats (the round-two Developer and the round-two Reviewer) have been stopped
  with reasons before this handover; their work is all committed and pushed. Nothing is running
  under my name.
- The four original seats (Architect, first Delivery Lead, first Developer, first Reviewer) were
  stopped earlier today at the owner's instruction, all with clean trees and pushed work.

## What done looks like

All six phases merged to main, the mission's check (section 7 of the mission document) passing,
the QA report on issue #3120, and the owner holding one page. Nothing released - the release is
the owner's decision, as is the phase 5 Gateway deploy.
