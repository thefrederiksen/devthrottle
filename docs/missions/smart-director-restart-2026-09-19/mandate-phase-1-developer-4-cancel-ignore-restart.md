# Mandate - Smart Director Restart - Developer, phase 1, task 4: Cancel and keep working, Shut down and ignore all, the operating system shutdown record, and the restart purpose

You are a Developer on the Smart Director Restart mission. You were opened by the Tech Lead of phase 1
(session f42438de, the third seat) and you report to it, never to the owner and never to the fleet. You have no
transcript; this file is your history.

Your worktree and branch are named in the one-line prompt that opened you. It was cut from
`origin/main` AFTER task 3 merged. Work only there. Never touch `D:/ReposFred/devthrottle`.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Developer".
2. In `docs/missions/smart-director-restart-2026-09-19/`: `mission.md` (sections 3, 5, 7 and 8),
   `phase-1-interface.md` (a contract phase 2 is already building against - its names and shapes are not
   yours to change; if one cannot be built as written, `cc-devthrottle session raise`),
   `proof-phase-1-task-1.md`, `proof-phase-1-task-2.md` and `proof-phase-1-task-3.md` (what is already
   landed: the contract types, the record marks, the restart cycle over the real drain, and the smart
   shutdown run for the path that goes all the way down).
3. The smart shutdown run task 3 built, all of it, and its tests. `src/CcDirector.ControlApi/Drain/
   DirectorRestore.cs` and its tests. `src/CcDirector.ControlApi/Restart/DirectorRestartCycle.cs` and
   `DirectorDrainRestartStep.cs`. REUSE them; do not rewrite or copy them.
4. `docs/CodingStyle.md`.

## Your one task

Finish `ISmartShutdown` and `ISmartShutdownRun`: the four things task 3 left out.

1. **Cancel and keep working** (mission 5.3 item 7). Honoured only while the snapshot says `CanCancel`;
   otherwise ignored and logged. Once. It stops closing sessions; tells each session still open that the
   restart is off (a new short text beside the others in `DrainMessages`); brings back every session
   already closed from the handover it just wrote, through the EXISTING restore, against the SAME
   Director, leads first, under its real owner; marks the record cancelled (the mark task 1 added) and
   saves it. Rows end `KeptRunning` or `BroughtBack`; the phase is `Cancelling`, then `Finished`; the
   outcome is `Cancelled`. `CanCancel` becomes true in the snapshots from the moment the record exists
   until the limit is reached or Shut down now is pressed; from then on it is false, because sessions
   are being ended without a handover and there is nothing honest to bring them back from. A session
   that cannot be brought back is a fact in its row's `Detail` and in the result's `Detail`; it never
   stops the others.
2. **Shut down and ignore all** (`ShutDownIgnoringAllAsync`, mission 5.3 item 8). Writes the record
   first (names, repositories, conversation ids; marked as coming from an ignore-all, so nothing is
   offered back from it), then ends every session. With the Gateway unreachable it STILL ends the
   sessions, because the owner chose to discard them: `RecordWritten` is false and `RecordRefusal` says
   why. That is the owner's stated choice, not a fallback; say so in the comment.
3. **The operating system shutdown record** (`RecordAndLetEndAsync`, mission 10.5). Writes the record
   (names, repositories, conversation ids, no handovers) and returns. It ends nothing itself.
4. **The restart purpose.** With `Purpose = Restart`, once the Director is verifiably empty the run goes
   to phase `Restarting` and asks the launcher through the existing restart cycle's launcher step - the
   same capability re-check the cycle does, never a second way of asking. Outcomes `RestartAccepted`,
   or `RestartRefused` with the launcher's reason (the record stands). `Start` with `Purpose = Restart`
   when `CheckAsync` says `CanRestart` is false throws `InvalidOperationException` with the reason,
   before anything is touched.

The old drain path and the restart cycle's own drain (task 2) behave exactly as they do today. If you
find yourself editing an existing test to make it pass, stop: that is old behaviour changing.

## Tests

Always written, on the existing `DrainTestRig`, in the namespace `CcDirector.Gateway.UnitTests.Drain`.
Names: `MethodName_Scenario_ExpectedResult`. At least:

- cancel while collecting, with one session already closed and two still open: the closed one is
  brought back through the real restore against the same Director, the two open ones are told the
  restart is off and end `KeptRunning`, the saved record is marked cancelled, the outcome is
  `Cancelled`, and no session was ended or interrupted after the cancel;
- cancel with a lead and a session under it both closed: the lead comes back first;
- cancel after the limit, and cancel after Shut down now: ignored, the run still ends `Emptied`;
- cancel pressed twice: honoured once;
- a session that cannot be brought back: the others still come back, and the row and the result say why;
- ignore all: the record is saved BEFORE the first session is ended (assert the ORDER of calls on the
  rig), it carries every conversation id and the ignore-all mark, and every session is ended;
- ignore all with the Gateway unreachable: `RecordWritten` false with the reason, every session still
  ended;
- the operating system shutdown record: the record is saved with every conversation id, and neither
  `InterruptAsync` nor `EndAsync` is ever called;
- restart purpose: the launcher is asked only after every session is verifiably absent (assert the
  ORDER on the rig) and the outcome is `RestartAccepted`; a launcher that refuses gives `RestartRefused`
  with its reason and the record stands; `CanRestart` false refuses the start and touches no session;
- close purpose: the launcher is never asked.

Each test watches the REAL run through the rig; a test that hand-builds a snapshot or a result proves
nothing about the engine. For each new test write one plain sentence saying what it shows. Do revert
proofs on at least the cancel test and the ignore-all order test: commit first, mutate, REBUILD, see
red, restore with `git checkout -- <literal path>`, REBUILD, see green. Never `--no-build` on the
restore run. Run the WHOLE check on the mutated build, not a filter, and report every red test.

## The check

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

The baseline is the count after task 3: the Tech Lead's own run on the merged result gave 518 passed,
0 failed (`origin/main` at `aa8b22912`, pull request 3193). Also read `review-phase-1-3.md` and its answers:
the engine must keep reading the host's CURRENT Gateway client, and a `Changed` handler is called on the
engine thread and must never be waited on from inside a lock. Run it yourself on your untouched
worktree first and write the count down. Read the COUNT, not the colour. Foreground only.

## Rules

- Do NOT open a pull request. When the work is committed and pushed to your branch, write
  `docs/missions/smart-director-restart-2026-09-19/proof-phase-1-task-4.md` (the command, the counts
  before and after, each new test in one plain sentence, the revert proofs, what you could not reach),
  commit it, push, and then `cc-devthrottle session report "<one paragraph>"`. I send your code to a
  Reviewer. Its findings go to a FRESH Developer on a new mandate, not back to you.
- You get ONE turn. On this Director a session that has stopped is never woken: no message, no report
  and no answer reaches it. So finish EVERYTHING - the code, the tests, the revert proofs, the proof
  file, the commit and the PUSH - before your turn ends. The Tech Lead is polling for the pushed proof
  file, not for a message. Never end your turn to wait for anything.
- No fallback programming. Every public method logs entry, exit and errors with `FileLog.Write`.
  Try-catch at entry points only.
- Nothing in the background. No sub-agents inside your session.
- A safety hook on this machine stops any shell command that runs `rm` (or any delete) on a path built
  from a variable, such as `rm $folder/$file`, and asks the owner. Nobody but the owner can answer, so
  the seat hangs for good - it ended the first Tech Lead of this phase. Never write such a command.
  Delete files by LITERAL path, one per `rm`. Never leave a stash behind.
- Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", in any
  commit, file or comment. ASCII only in everything. Plain English, no abbreviations.
- Never destroy, deploy, or send anything outward. Never restart, drain, stop or message a session
  that is not yours. Never run any of this against a real Director; tests only.
- If something is undecidable inside this mandate: `cc-devthrottle session raise "<the question, with
  your recommendation>"`, then BUILD YOUR RECOMMENDATION and carry on, and write the decision under its
  own heading in the proof. Never stop to wait for the answer: it cannot reach you.
