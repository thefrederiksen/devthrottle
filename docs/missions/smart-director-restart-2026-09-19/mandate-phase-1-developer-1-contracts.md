# Mandate - Smart Director Restart - Developer, phase 1, task 1: the contract types, the record marks, the two new session verbs, the new request text

You are a Developer on the Smart Director Restart mission. You were opened by the Tech Lead of phase 1
(session 5a54dc21) and you report to it, never to the owner and never to the fleet. You have no
transcript; this file is your history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p1-contracts`, branch
`smart-restart/p1-contracts`, cut from `origin/main`. Work only there. Never touch
`D:/ReposFred/devthrottle`.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Developer".
2. In `docs/missions/smart-director-restart-2026-09-19/`: `mission.md` (sections 5 and 8 above all) and
   `phase-1-interface.md`. The interface document is a contract another phase is already building
   against: the names and shapes in it are not yours to change. If one cannot be built as written, stop
   and tell me with `cc-devthrottle session raise`.
3. `docs/CodingStyle.md`.

## Your one task

This is the foundation the next tasks build on. It adds types, verbs and text. It changes NO behaviour
of the existing drain: everything new is reached only through options or calls the old path never
makes (mission section 8), so it is safe to merge today.

1. **The contract types.** New folder `src/CcDirector.ControlApi/SmartRestart/`, namespace
   `CcDirector.ControlApi.SmartRestart`: every interface, record, enum and the static
   `SmartShutdownTimes` exactly as written in `phase-1-interface.md` sections 2 and 3. Types only. No
   engine, no `ControlApiHost.CreateSmartShutdown()` yet - that is the next task. `SmartShutdownRequest`
   refuses, with a clear message, a `TimeAllowed` that is not one of the five allowed values.
2. **The record marks**, in `src/CcDirector.Gateway.Contracts/WorkspaceDtos.cs`, and accepted by
   `src/CcDirector.Gateway/Workspaces/WorkspaceValidation.cs`:
   - a drain state `ended-at-limit` (added to `WorkspaceDrainStates.All`): the engine ended the session
     at the limit; its conversation id is already on the seat (`ClaudeSessionId`);
   - a way for a record to say how it came about: a smart shutdown, or a shut down that ignored all
     sessions. Do NOT add values to `WorkspaceOrigins` - the store branches on `captured`. Add a
     separate nullable field on the document with its own closed list of values, validated like the
     others. Phase 3 finds records by it;
   - a way for a record to say it was cancelled, and when.
   Read how the document round-trips fields an older build does not know (`Unknown`) and make sure the
   new fields survive a round trip through an older and a newer reader; say in your proof what you
   found.
3. **Two new verbs on `IDrainSessionControl`** (`src/CcDirector.ControlApi/Drain/IDrainSessionControl.cs`),
   with the real implementations on `SessionManagerDrainControl` and the matching fakes on the
   `DrainTestRig`:
   - `InterruptAsync(sessionId)`: interrupts the session's turn through the Director's existing
     interrupt path (`SessionCommandExecutor.InterruptAsync` and the agent drivers). It answers with a
     `DrainDelivery`, like `SendAsync`, so "gone" and "could not" stay two facts;
   - `EndAsync(sessionId, reason)`: ends the session now, turn or no turn, through the session
     manager's existing way of killing a session. Find it; do not write a second one.
   The class comment on `SessionManagerDrainControl` says the seam holds no such verbs, on purpose.
   That is no longer true for this feature (mission 5.3 item 5 replaces the never-force rule FOR THE
   SMART SHUTDOWN ONLY). Rewrite the comment so it says what is now true: the old drain path never
   calls these two verbs, and a test proves it (run the existing drain on the rig and assert neither
   verb was called).
4. **The new request text**, in `src/CcDirector.ControlApi/Drain/DrainMessages.cs`: a second message
   for the smart shutdown. The sentence "you will not be killed" is not in it; in its place: you have N
   minutes; write the exact next action first and the rest after, so that whatever is on disk when
   time is up is already useful. Plus the short second-stage message: "hand over now: the exact next
   action first". Plus the message for a cancel: the restart is off, carry on. The OLD message stays
   as it is for the old path. Keep the closing block (`DrainReportBlock`) exactly as it is.

## Tests

Always written. On the existing `DrainTestRig`, in the namespaces `CcDirector.Gateway.UnitTests.Drain`
(and the Gateway's own workspace validation tests for the marks). Names:
`MethodName_Scenario_ExpectedResult`. For each new test write one plain sentence saying what it shows.
A test must watch the real caller, not a hand-built input: if reverting your change leaves the test
green, the test proves nothing - try it on at least one test per item and say which.

## The check

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

Baseline on untouched `origin/main` at `2092941b6`: 418 passed, 0 failed. Read the COUNT, not the
colour. Also run the Gateway's workspace validation tests you touched and give their counts. Run
everything in the foreground.

## Rules

- Do NOT open a pull request. When the work is committed and pushed to your branch, write
  `docs/missions/smart-director-restart-2026-09-19/proof-phase-1-task-1.md` (the command, the counts
  before and after, each new test in one plain sentence, the revert proofs, what you could not reach),
  commit it, push, and then `cc-devthrottle session report "<one paragraph>"`. I send your code to a
  Reviewer; you will get its findings back and answer every one in `review-phase-1-1-answers.md`.
- No fallback programming. Every public method logs entry, exit and errors with `FileLog.Write`.
- Nothing in the background. No sub-agents inside your session.
- Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", in any
  commit, file or comment. ASCII only in everything. Plain English, no abbreviations.
- Never destroy, deploy, or send anything outward. Never restart, drain, stop or message a session
  that is not yours. Never run any of this against a real Director; tests only.
- If something is undecidable inside this mandate: `cc-devthrottle session raise "<the question, with
  your recommendation>"` and carry on with the rest.
