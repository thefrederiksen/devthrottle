# Mandate - Smart Director Restart - Developer, phase 1, task 3: the smart shutdown run - time allowed, two stages, the limit, Shut down now, progress per session

You are a Developer on the Smart Director Restart mission. You were opened by the Tech Lead of phase 1
(session 1c3174ba, the second seat; the first seat that wrote this file is gone) and you report to it, never to the owner and never to the fleet. You have no
transcript; this file is your history.

Your worktree and branch are named in the one-line prompt that opened you. It was cut from
`origin/main` AFTER task 1 merged. Work only there. Never touch `D:/ReposFred/devthrottle`.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Developer".
2. In `docs/missions/smart-director-restart-2026-09-19/`: `mission.md` (sections 3, 5 and 7, and 8),
   `phase-1-interface.md` (a contract phase 2 is already building against - its names and shapes are not
   yours to change; if one cannot be built as written, `cc-devthrottle session raise`), and
   `proof-phase-1-task-1.md` (what task 1 already landed: the contract types, the record marks, the
   `InterruptAsync` and `EndAsync` verbs, the new request texts).
3. `src/CcDirector.ControlApi/Drain/DirectorDrain.cs`, all of it, and its tests. REUSE it (mission
   5.1): the capture first, the chain, leaf first closing, the poll, the 500 byte floor, the re-read at
   close, the secret sweep, the owner questions. Do not rewrite it and do not copy it.
4. `docs/CodingStyle.md`.

## Your one task

Build the run behind `ISmartShutdown.Start` and `ISmartShutdownRun` for the path that goes all the way
down. NOT in this task: `CancelAndKeepWorking` (it sets nothing yet and `CanCancel` is false in every
snapshot until the next task), `ShutDownIgnoringAllAsync`, `RecordAndLetEndAsync`, and
`Purpose = Restart` (the next task). `CheckAsync` IS in this task.

1. **The time allowed as an option.** The drain gains options the old path does not set (mission
   section 8): with none of them set, the old drain behaves exactly as it does today - ninety minutes,
   never forces - and the existing tests stay green untouched. If you find yourself editing an existing
   test to make it pass, stop: that is the old behaviour changing.
2. **Stage one at two thirds of the time allowed.** Every session still present that has not handed
   over and is mid-turn is interrupted (`InterruptAsync`) and sent the short "hand over now: the exact
   next action first" message. A session that is NOT mid-turn and has not answered is sent the short
   message without an interrupt. How the engine knows "mid-turn" goes through the session control
   seam - add the question to the seam if it is not there; do not reach around it. An interrupt that
   does not land is a fact for the row's `Detail`, not a reason to stop.
3. **The limit.** Every session still present is ended (`EndAsync`), its seat is recorded as
   `ended-at-limit`, and its conversation id is on the record. Whatever handover is on disk at that
   moment is kept and recorded with the seat - a half-written handover is the point of "write the
   exact next action first". The record is SAVED to the Gateway before the first session is ended, and
   again after. Then the engine waits for every session to be verifiably absent, as the drain already
   does. With the limit reached the run ends `Emptied`: a smart shutdown always empties the Director.
4. **Shut down now** jumps to the limit from any earlier phase. Once.
5. **Progress per session**, as `phase-1-interface.md` section 3 says: a complete immutable snapshot on
   every change and at least once per poll; rows ordered leads first with each lead's sessions under
   it; the labels (`StateLabel`, `PhaseLabel`, `CountLabel`) worded by the engine in plain English. A
   handler that throws must never reach the engine: catch it, log it, carry on. Keep the existing
   `DrainProgress` callback working for the old path.
6. **`CheckAsync`** and **`ControlApiHost.CreateSmartShutdown()`** (never null). A Gateway that cannot
   be reached refuses the smart shutdown with the reason, before anything is touched. `CanRestart`
   comes from the existing restart eligibility answer. `Start` with a run or a drain already under way
   throws `InvalidOperationException` with the reason.
7. The record says it came from a smart shutdown (the mark task 1 added).

Two facts task 1 found, which bind you:

- Pi refuses an interrupt by design, and the seam hands that refusal back. A session that cannot be
  interrupted is still sent the short message at two thirds; its row stays as it was with the refusal
  in `Detail` (it is NOT shown as `Interrupted`, because it was not); it is ended at the limit like any
  other session still present.
- The conversation id on a captured seat is put back from the capture on every save. So it is read at
  capture time, and your test for `ended-at-limit` asserts the id that is on the SAVED record.

Where the old drain's comments state the never-force rule as a fact about the whole class, make them
say what is now true: it holds for the old path, and the smart shutdown replaces it by the owner's
ruling (mission 5.3 item 5).

## Tests

Always written, on the existing `DrainTestRig` with its clock and delay seams, so ten minutes run in
milliseconds. In the namespace `CcDirector.Gateway.UnitTests.Drain`. Names:
`MethodName_Scenario_ExpectedResult`. At least:

- a session that hands over in time is closed and never interrupted or ended;
- a session mid-turn at two thirds is interrupted and sent the short message, then hands over, and is
  closed and NOT ended at the limit;
- a session that never answers is ended at the limit, recorded `ended-at-limit` with its conversation
  id, and the record was saved BEFORE it was ended (assert the ORDER of calls on the rig);
- a lead with a session under it: leaf first still holds, and the rows come out lead first;
- a wedged session (the request is refused) shows `NotDelivered` with the reason at once, and is ended
  at the limit;
- Shut down now from the collecting phase ends every session still present, at once;
- the snapshots: every state in the interface is reached by some test through the REAL run, the count
  label matches the rows, `Changed` is raised at least once per poll, a throwing handler does not stop
  the run;
- the old path: with no new option set, neither `InterruptAsync` nor `EndAsync` is ever called, and
  the ninety minute deadline still ends as unreachable;
- the Gateway unreachable: `CheckAsync` refuses with the reason and `Start` touches no session;
- each of the five allowed times puts the two thirds point and the limit where they belong.

Each test watches the REAL run through the rig; a test that hand-builds a snapshot proves nothing about
the engine. For each new test write one plain sentence saying what it shows. Do revert proofs on at
least the two-thirds test and the save-before-end test: commit first, mutate, REBUILD, see red,
restore, REBUILD, see green. Never `--no-build` on the restore run.

## The check

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

The baseline is in `proof-phase-1-task-1.md` (the count after task 1). Run it yourself on your untouched
worktree first and write the count down. Read the COUNT, not the colour. Foreground only.

## Rules

- Do NOT open a pull request. When the work is committed and pushed to your branch, write
  `docs/missions/smart-director-restart-2026-09-19/proof-phase-1-task-3.md` (the command, the counts
  before and after, each new test in one plain sentence, the revert proofs, what you could not reach),
  commit it, push, and then `cc-devthrottle session report "<one paragraph>"`. I send your code to a
  Reviewer; you will get its findings back and answer every one in `review-phase-1-3-answers.md`.
- No fallback programming. Every public method logs entry, exit and errors with `FileLog.Write`.
  Try-catch at entry points only (the run's own top, the event raise).
- Nothing in the background. No sub-agents inside your session.
- A safety hook on this machine stops any shell command that runs `rm` (or any delete) on a path built
  from a variable, such as `rm $folder/$file`, and asks the owner. Nobody but the owner can answer, so
  the seat hangs for good - it ended the first Tech Lead of this phase. Never write such a command.
  Delete files by LITERAL path, one per `rm`. Never leave a stash behind. For a revert proof, restore
  with `git checkout -- <literal path>` after committing first.
- Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", in any
  commit, file or comment. ASCII only in everything. Plain English, no abbreviations.
- Never destroy, deploy, or send anything outward. Never restart, drain, stop or message a session
  that is not yours. Never run any of this against a real Director; tests only.
- If something is undecidable inside this mandate: `cc-devthrottle session raise "<the question, with
  your recommendation>"` and carry on with the rest.
