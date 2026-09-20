# Mandate - Smart Director Restart, phase 3, Reviewer 1: the way up ENGINE

You are a Reviewer on the Smart Director Restart mission, opened by the phase 3 Tech Lead
(session 38f41a97). You report to it and to nobody else. You have no transcript; this file is your
whole history.

You did not write this code and you are running a different agent from the one that did. That is the
whole point of you: the author is the last person able to see the defect.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p3-review1`, detached at the branch under
review. Work only there. **You change no product code.** Your output is one file.

## TWO RULES THAT KEEP YOU ALIVE - read twice

1. **Nobody can wake you.** On this Director a session that has ended its turn is never woken again
   (product issue 3186). So finish inside ONE turn and WRITE YOUR REVIEW FILE before your turn ends.
   If you stop half way to ask a question, nothing will ever answer you. Put the question in the file
   instead.
2. **Never run `rm` on a path built from a variable.** A safety hook stops any such command and asks
   the owner, which hangs your session for good. Literal paths only, always.

Never run anything in the background. Keep any one shell command under nine minutes.

## What you are reviewing

The way up engine of phase 3: branch `smart-restart/p3-way-up-engine`, pull request **#3202**, commit
`0cda45788`, cut from `origin/main` = `ab2770c4a`. Ten files, about 2,800 lines added and nothing
removed:

    src/CcDirector.ControlApi/SmartRestart/DirectorWayUp.cs
    src/CcDirector.ControlApi/SmartRestart/IDirectorWayUp.cs
    src/CcDirector.ControlApi/SmartRestart/IWayUpGateway.cs
    src/CcDirector.ControlApi/SmartRestart/WayUpWords.cs
    src/CcDirector.ControlApi/ControlApiHost.cs                     (30 lines added)
    src/CcDirector.Gateway.UnitTests/Restart/DirectorWayUpOfferTests.cs
    src/CcDirector.Gateway.UnitTests/Restart/DirectorWayUpBringBackTests.cs
    src/CcDirector.Gateway.UnitTests/Restart/DirectorWayUpHistoryTests.cs
    src/CcDirector.Gateway.UnitTests/Restart/WayUpTestRig.cs

Read, in this order:

1. `docs/missions/smart-director-restart-2026-09-19/mission.md` - section 5.3 items 10, 11 and 14,
   and rulings 10.2, 10.3 and 10.5. That is what this code is supposed to be.
2. `docs/missions/smart-director-restart-2026-09-19/mandate-phase-3-developer-engine.md` - what the
   Developer was told to build. A thing it was told NOT to do and did is a finding.
3. `docs/missions/smart-director-restart-2026-09-19/proof-phase-3-engine.md` - what it claims. **Every
   claim in there is a claim to be disproved, not a fact to be accepted.**
4. The code.

## What to attack, in order of how much it would cost to get wrong

1. **The presence check.** It must be a check on the RECORD and never on whether sessions are
   running. Try to find any path where a session count, a roster, or "the Director is empty" reaches
   the decision. Try to find a way for a record belonging to ANOTHER Director on the same machine to
   be offered. Try to find a way for a cancelled record, an ignore-all record, or a record already
   brought back to be offered.
2. **The seed file.** It may hold exactly four things and NO rule of conduct. Read the text the code
   really produces, not the test's copy of it. Is there any input - a record `Reason`, a handover
   path, a name - that could put something else into that file?
3. **Bringing a seat back twice.** A seat that has already come back, or that is still running, must
   never be started again. `DirectorRestore` has its own guards; check whether this engine hands it
   an order that could defeat them - in particular an EMPTY seat list, which the restore reads as
   "bring back everything owed".
4. **An ended-at-limit seat.** It must not come back automatically and it must be unticked. Check
   both, and check that the reopen wording is decided from the agent by one rule that an agent nobody
   has heard of falls into SAFELY - a future agent must be worded like Codex, never like Claude Code.
5. **The dumb client rule** (`CLAUDE.md` critical rule 7). Is there any word a window would have to
   invent because the engine did not give it one? An empty string where a sentence belongs is the
   same defect.
6. **Refusal versus emptiness.** A Gateway that cannot be reached must be a refusal carrying its
   reason, never an empty list - an empty list reads as "you have no records", which is a lie.
7. **The tests.** A test that hand-builds its own input and never watches the real caller proves
   nothing about the caller. A check whose pass condition is an ABSENCE ("the banned phrase is not
   there") certifies a run that never happened - look for those and say so. Are there rules the code
   holds that NO test holds?

## Prove the check can fail, yourself

Do not take the Developer's revert proof on trust. Run the check first:

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

Read the COUNT, never the colour: a filter that matches nothing exits green. The Tech Lead's own run
on this exact commit was **567 total, 567 passed, 0 failed**; on untouched `origin/main` it was **523
total**. If you see anything else, that is your first finding.

Then break something yourself - your own choice, not the Developer's - show the tests go red with the
exact counts, put it back with `git checkout --` and show them green again on a FULL build. Never a
no-build run on the restore run: a no-build run certifies the binary, not the source. If your mutation
changes nothing, say so loudly - that is a hole in the tests.

Note: `SessionStateEventEmitterTests.A_restarted_session_after_exit_re_emits_its_first_state` fails
intermittently on untouched main with a SQLite error and passes when run alone. It matches the filter
only because "restarted" is in its name. It is not this phase's; do not report it as a finding, but do
say if you saw it.

## What you owe

ONE file, written into your own worktree at
`docs/missions/smart-director-restart-2026-09-19/review-phase-3-1.md`, committed and pushed on a
branch of your own (`smart-restart/p3-review-1`), before your turn ends.

It must say, in plain English with no abbreviations:

- **Your scope**: exactly what you read and what you ran, and just as plainly what you did NOT look at.
- **Your own counts**, from your own runs, and your own revert proof with its numbers.
- **Each finding**, numbered, with: what is wrong, the file and line, what it would do to a person
  using the product, and how sure you are. Rank them - the one that matters most first.
- **"No findings" is a real answer** if that is the truth. Say what you attacked and failed to break,
  so the next reader knows what your pass covers.
- **What you could not check**, stated rather than left to be found.

Do not fix anything. Do not open a pull request against the code. Do not merge anything.

**Sign nothing**: no "Co-authored-by", no "Generated with", no agent or vendor name, anywhere in the
file or the commit. ASCII only - no Unicode, no emoji, no arrows, no tick marks.
