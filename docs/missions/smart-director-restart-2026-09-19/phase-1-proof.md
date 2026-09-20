# Proof - Smart Director Restart, phase 1: the engine

What phase 1 owed the Delivery Lead: the check, its counts before and after, what each task built, what
the new tests prove, the pull request numbers, the reviews and their findings, and what the phase could
not reach.

Written on 20 September 2026 by the Developer seat that finished task 4. Phase 1 had no Tech Lead left
to write it: the third Tech Lead seat and the task 4b Developer both died at 06:47 that morning on the
account's monthly spend limit. This file takes its facts from `phase-1-status.md` (the Tech Lead's own
running record, in the phase 1 worktree), the four task proofs and the four reviews beside this file. It
does not rewrite them; each names the tests one by one and this file does not repeat that.

## The check

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

It is the mission document's own check (section 7), confirmed in phase 0. Every run below was in the
foreground and built from source in the same command. The count is what is read, never the colour.

| When | Passed | Failed | Who ran it |
|---|---|---|---|
| Before phase 1: `origin/main` at `2092941b6` | 418 | 0 | task 1 Tech Lead; re-run by the task 2 Developer |
| After task 1 (and its review answer) | 477 | 0 | the task 1 Developers |
| After task 2, from its own baseline of 418 | 428 | 0 | the task 2 Developer |
| Before task 3: `origin/main` at `9f79e92dc` | 487 | 0 | the task 3 Developer |
| After task 3 | 517 | 0 | the task 3 Developer, and the Reviewer |
| After task 3b, the engine branch merged with `origin/main` | 518 | 0 | the phase 1 Tech Lead |
| After task 4 | 540 | 0 | the task 4 Developer, the Tech Lead, and the Reviewer |
| Before task 4 merged: `origin/main` at `217b79f63` | 580 | 0 | me, in a worktree of its own |
| After task 4 and its review answers, merged with that `origin/main` | 606 | 0 | me |

The last two rows are the only pair measured against each other on the same day and the same machine:
606 - 580 = 26, the 22 cases of task 4 plus the 4 that answer its review. The earlier pairs were each
measured by the seat that built that task, against the `origin/main` of that hour.

**The phase added 126 test cases in all, and that figure is arithmetic over the four proofs, not one
measurement**: 59 for task 1 (57, plus 2 for its review answer), a net 10 for task 2 (11 new, 1 deleted
because it tested a stand-in that no longer exists), 31 for task 3 (30, plus 1 for its review answer),
and 26 for task 4. The baseline moved under the phase repeatedly - phases 2, 3 and 4 ran beside it and
their tests match the same filter - which is why no single before-and-after pair spans the whole phase.

Also run on the merged result of the last task, by me:

- `RetiredMessagingWordsTests` in `CcDirector.Core.UnitTests`: 5 passed, 0 failed. The phase has run this
  since task 3c, because a comment written by task 1 tripped the retired-words sweep and turned the
  default local gate on main red.
- `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"`: 47 passed,
  0 failed - phase 2's screens, which are on main now.
- `dotnet build src/CcDirector.Avalonia`: 0 warnings, 0 errors.

## What each task built, in plain words

**The interface first.** Before any code, the phase 1 Tech Lead settled the ONE interface between this
engine and phase 2's screens and merged it as `phase-1-interface.md` (pull request 3172), so phase 2
could build against a fake while phase 1 built the real thing. It is still the contract, amended twice
since by review answers, each amendment noted in the file itself.

**Task 1 - the contract types, the record marks, the two session verbs, the new request text** (pull
request 3182). The types of the interface exactly as written, with a time allowed that refuses anything
but 5, 10, 15, 30 or 60 minutes - on the value, so a copy cannot smuggle a bad one in. Three new marks on
the record: the drain state `ended-at-limit`, what kind of shutdown wrote the record (a smart shutdown or
an ignore-all), and when it was cancelled. Two new verbs on the drain's session seam: interrupt a
session, and ask whether it is mid-turn - "mid-turn" answered from the same fact the deletion reaper
uses, so the word means one thing in the product. And the new request text: the promise that a session
will not be killed is gone, and in its place it is told how many minutes it has and to write the exact
next action first, so that whatever is on disk when time is up is already useful.

**Task 2 - the restart cycle drains through the real drain** (pull request 3181, defect #3169). The
restart cycle was wired to a stand-in that refused every drain; it now runs the real one. The stand-in
was deleted, and so was every sentence that claimed the build could not drain - the claim was swept, not
only the symbol. The rule that names a record moved into one place, so the desktop drain and the cycle
name their records the same way.

**Task 3 - the run: the time allowed, two stages, the limit, Shut down now, progress per session** (pull
request 3193). The smart shutdown itself. Sessions are asked; at two thirds of the time allowed the ones
still mid-turn are interrupted and told to hand over now; at the limit every session still present is
ended, recorded as ended at the limit with its conversation id, and the record is saved before and after
the ending. "Shut down now" jumps to the limit. Throughout, one immutable snapshot per change is raised
for a screen to render, with the state names, the phase label and the count sentence all coming from the
engine, so a new state is one edit here and no new branch in a window. Whether the Director is empty is
asked of the live session list at the end, never summed from rows that each looked closed.

**Task 4 - cancel and keep working, shut down and ignore all, the operating system record, the restart
purpose** (pull request 3212). "Cancel and keep working" stops the closing, takes back the close of any
session the reaper has not yet taken, tells every session still open that the restart is off, marks the
record cancelled and saves it, and only then brings every already-closed session back through the one
existing restore, leads first, against the same Director. "Shut down and ignore all sessions" writes the
record BEFORE it ends anything, and still ends everything when the Gateway is down, saying why. The
operating system shutdown writes the record and ends nothing. The restart purpose asks this Director's
own launcher, through the restart cycle's own re-check-and-ask step, and only after the drain has
answered that nothing at all is left.

One thing task 4 found that phase 3 needed: a real Director cannot run the restore directly, because the
Gateway grants the restore lease only at its own restore door. The new `GatewaySmartShutdownBringBack`
asks that door for this Director's own id and the Gateway relays the order back down.

## What the new tests prove

Each task proof lists its tests one sentence each; this is what they amount to.

- **Every engine test runs the real engine over the real drain over the existing `DrainTestRig`.** No test
  builds a snapshot or a result by hand, and the cancel tests run the real restore over the very record
  the run itself saved. That is deliberate: a test that hand-builds its input never watches the caller.
- **The states and the words are the engine's.** Tests assert that every row label, phase label and count
  sentence comes from the engine's own word list, so a screen that renders them verbatim cannot be wrong
  about what a state means.
- **Order is asserted where order is the whole point** - on a shared journal, not by reading the end
  state: the record is saved before the first session is ended; the launcher is asked only after the last
  session is gone; the cancelled record is saved before the first session is brought back.
- **The failure cases are tested as first-class cases**, not as an afterthought: a wedged session that
  cannot take a prompt, a session that never answers, a Gateway that cannot be reached, a launcher that
  refuses, a machine that fails the re-check, a session that cannot be brought back, a button pressed
  twice, a button pressed too late.
- **Every task ran at least one revert proof**: the fix was undone, the whole suite REBUILT and re-run with
  no narrowed filter, the exact red tests recorded, then restored, REBUILT and re-run green. Four of those
  proofs are in the task proofs; two more are in `review-phase-1-4-answers.md` for the review answers.

## The reviews

Every task was read by a Reviewer seat that did not build it, running a different agent from the builder.
The phase status records Pi for tasks 3 and 4 - Codex was unavailable from 20 September on its own usage
limit - and does not name the agent for tasks 1 and 2. Each review states its scope and what it could not
reach; each finding was answered by a Developer, never by the Reviewer.

| Review | Findings | Answered in |
|---|---|---|
| `review-phase-1-1.md` (task 1) | 1: the short hand-over-now message left out the lines that let a session leave a question for the owner or say it is blocked, and for some sessions it is the only message they ever see | `review-phase-1-1-answers.md` - accepted, fixed, 2 tests |
| `review-phase-1-2.md` (task 2) | none within its scope; it recorded two candidates it had weighed and rejected | `review-phase-1-2-answers.md` - both declined with the reason, by the Tech Lead, because the Developer was gone |
| `review-phase-1-3.md` (task 3) | 2: the engine kept the Gateway client it was made with, so it could refuse a smart shutdown the Director could in fact perform; and a screen handler that blocks stalls the whole run, with nothing saying it must not | `review-phase-1-3-answers.md` - both accepted; the first fixed with a test that watches the host, the second written into the contract as a rule phase 2 must keep |
| `review-phase-1-4.md` (task 4) | 2: a session that appears during an ignore-all is in neither the record nor the end list; and a Gateway failure at the launcher ask ends a finished run as `Failed` instead of the `RestartRefused` the contract defines for it | `review-phase-1-4-answers.md` - both accepted and fixed, 4 tests, a revert proof each |

Six findings in four reviews, all narrow, none about safety, all answered. Review 2 returned none and is
answered all the same, because it recorded two candidates it had weighed and rejected and they are worth
an answer rather than a rediscovery. Two of them changed the
contract phase 2 builds against, which is why `phase-1-interface.md` carries amendments.

## What the phase could NOT reach

This is the part that matters most to whoever comes next.

- **Nothing in phase 1 was ever run against a real Gateway, a real Director or a real launcher.** Every
  proof is the rig. Two facts the real-Director cancel rests on are readings of the Gateway's routes, not
  runs: that the restore door accepts a Director's own credential, and that the Gateway relays the restore
  order back down to that Director. The composition of `GatewaySmartShutdownBringBack`, the door and the
  relayed restore is not exercised end to end by any test. Phase 5 is where that is answered.
- **The new record marks need a Gateway that carries them.** The Gateway refuses a drain state it does not
  know, so a real Director cannot save `ended-at-limit`, the shutdown kind or the cancelled mark until a
  Gateway carrying them is deployed. The isolated rig has its own Gateway. Reported to the Delivery Lead
  when the interface was settled; it is a deploy, and phase 1 never deploys.
- **The roster may lag.** The restore refuses a seat whose captured session is still on the Gateway's
  roster. A session closed seconds before a cancel may still be listed and be refused as "still running",
  and the owner would have to bring it back from the history. Disclosed, not solved, not tested - the
  cancel tests close their sessions well before pressing.
- **Two small windows** where a cancel is accepted by the run but the drain can no longer act on it -
  between the limit being reached and its first snapshot, and between the last session going and the run
  finishing. Both end with the result saying in plain words that the cancel came too late. Neither has a
  test; neither could be made deterministic on the rig.
- **A session that appears after the ignore-all path's one further pass** is still ended by the closing
  application with no trace. Knowingly accepted: the alternative is a loop that can never end.
- **Nothing outside the engine calls the ignore-all path yet**, so nothing shows the sentence it now
  returns about a session that was ended and is in no record. Whoever wires that button must show it.
- **The three measurements this phase guessed at are phase 3's to make**, not phase 1's: how long an
  interrupted session really needs to write a handover (three minutes was the Architect's guess), whether
  the two-thirds point is right, and whether a saved conversation can be reopened at all.
- **The parked suites, the web tests and the Python tests were never run** by this phase, and the whole
  `CcDirector.Gateway.UnitTests` project was run only twice, on unmerged code (6,519 and 6,569 passed).
  One unexplained failure was seen once in a whole-project run during task 1
  (`GovernanceAuditLogTests.The_trail_is_tenant_scoped`), passed on its own straight afterwards, and was
  never explained. It names no drain, workspace or smart shutdown type.
- **Two `LauncherDeclaredCapabilitiesTests` were failing on untouched `origin/main`** on this machine
  during the phase, in the default local gate. Not this phase's, not looked into, reported to the Delivery
  Lead and recorded here so it is not rediscovered as ours.

## The pull requests

| Number | What |
|---|---|
| 3172 | the phase 1 interface, merged before any code so phase 2 could build against it |
| 3181 | task 2: the restart cycle drains through the real drain (defect #3169) |
| 3182 | task 1: the contract types, the record marks, the two session verbs, the new request text |
| 3193 | task 3: the run - the time allowed, two stages, the limit, Shut down now, progress per session |
| 3212 | task 4: cancel and keep working, ignore all, the operating system record, the restart purpose, the answers to its review, and this proof |

Phase 1's own mandate is `mandate-phase-1.md`; the mission document is `mission.md`.
