# Mandate - Smart Director Restart - Developer, phase 1, task 2: wire the restart cycle to the real drain (defect #3169)

You are a Developer on the Smart Director Restart mission. You were opened by the Tech Lead of phase 1
(session 5a54dc21) and you report to it, never to the owner and never to the fleet. You have no
transcript; this file is your history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p1-cycle`, branch
`smart-restart/p1-cycle-real-drain`, cut from `origin/main`. Work only there. Never touch
`D:/ReposFred/devthrottle`.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Developer".
2. `docs/missions/smart-director-restart-2026-09-19/mission.md`, sections 5.1, 5.5 and 8.
3. Issue #3169: `gh issue view 3169`.
4. `docs/CodingStyle.md`.

## Your one task

`ControlApiHost.RestartDrainStep()` (`src/CcDirector.ControlApi/ControlApiHost.cs`) returns
`new Restart.NoDrainOnThisBuild()`, a stand-in. So a restart asked for through the restart cycle
(`src/CcDirector.ControlApi/Restart/DirectorRestartCycle.cs`) never drains anything. The real engine,
`src/CcDirector.ControlApi/Drain/DirectorDrain.cs`, exists, has tests, and `ControlApiHost.CreateDrain`
already builds it.

1. Write the real `IRestartCycleDrain` over the real drain, in `src/CcDirector.ControlApi/Restart/`.
   - `Availability`: available when this Director has a Gateway client; when it has none, not
     available, with the reason in plain words (the record must live off the machine). It is asked
     BEFORE the owner is shown a request, so it must be cheap and must change nothing.
   - `RunAsync`: runs the drain with the order's reason written into the record, passes each progress
     change on as one sentence, and maps the result: ready to restart becomes `Drained` with the
     workspace id; not ready becomes `Blocked` with the workspace id and the drain's own reason; a
     drain already running becomes `Blocked` with that reason. Read `DirectorRestartCycle.RunStepsAsync`
     for what the cycle does with each verdict before you choose.
   - It takes what it needs through its constructor (a factory for the drain), so the test can hand it
     the `DrainTestRig`'s drain. `RestartDrainStep()` and `JudgeRestartEligibility()` are static today
     and the real step needs the instance's Gateway client: make them instance members, and keep the
     change to `ControlApiHost.cs` as small as you can - other missions edit that file.
2. Delete `NoDrainOnThisBuild`, and sweep for the CLAIM, not only the symbol: comments in
   `ControlApiHost.cs`, `DirectorRestartCycle.cs`, the contracts (`DrainAvailable`, `DrainReason`) and
   anywhere else that say this build carries no drain. After your change they must say what is true.
   Keep the `Unavailable` verdict: a Director with no Gateway client still answers with it.
3. Do not change what the drain itself does. The smart shutdown's new behaviour (time allowed, two
   stages, ending at the limit) is another Developer's task in another worktree; this cycle keeps the
   drain's existing options for now.

## Tests

Always written. In `src/CcDirector.Gateway.UnitTests/Restart/`, on the existing `DrainTestRig` and the
existing cycle test fakes. Names: `MethodName_Scenario_ExpectedResult`. The test issue #3169 asks for:
a cycle with sessions running drains them BEFORE it asks the launcher to restart - assert the ORDER,
through the real cycle and the real drain on the rig, not through a hand-built outcome. Also: a drain
that ends not ready stops the cycle and the launcher is never asked; no Gateway client means
`Unavailable` and nothing touched. For each new test write one plain sentence saying what it shows. Do
one revert proof: put the stand-in back, rebuild, and show which of your tests go red; then restore,
REBUILD, and run again. Commit before you mutate.

## The check

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

Baseline on untouched `origin/main` at `2092941b6`: 418 passed, 0 failed. Read the COUNT, not the
colour. Run everything in the foreground.

## Rules

- Do NOT open a pull request. When the work is committed and pushed to your branch, write
  `docs/missions/smart-director-restart-2026-09-19/proof-phase-1-task-2.md` (the command, the counts
  before and after, each new test in one plain sentence, the revert proof, what you could not reach),
  commit it, push, and then `cc-devthrottle session report "<one paragraph>"`. I send your code to a
  Reviewer; you will get its findings back and answer every one in `review-phase-1-2-answers.md`.
- No fallback programming. Every public method logs entry, exit and errors with `FileLog.Write`.
- Nothing in the background. No sub-agents inside your session.
- Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", in any
  commit, file or comment. ASCII only in everything. Plain English, no abbreviations.
- Never destroy, deploy, or send anything outward. Never restart, drain, stop or message a session
  that is not yours. Never run any of this against a real Director; tests only.
- If something is undecidable inside this mandate: `cc-devthrottle session raise "<the question, with
  your recommendation>"` and carry on with the rest.
