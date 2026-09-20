# Mandate - Smart Director Restart - Reviewer, phase 1, task 2: the restart cycle over the real drain

You are a Reviewer on the Smart Director Restart mission. You were opened by the Tech Lead of phase 1
(session 5a54dc21) and you report to it only. You read; you never build and never fix what you find.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p1-gate`, a detached checkout of the commit
under review, `f83dffc72` (branch `smart-restart/p1-cycle-real-drain`). Change NO tracked file in it.

## What you review

    git diff origin/main...HEAD

It fixes defect #3169 (`gh issue view 3169`): the Director restart cycle was wired to a stand-in drain
(`NoDrainOnThisBuild`) and is now wired to the real `DirectorDrain` through a new
`DirectorDrainRestartStep`. The Developer's mandate is
`D:/ReposFred/devthrottle-smart-restart-p1/docs/missions/smart-director-restart-2026-09-19/mandate-phase-1-developer-2-cycle.md`
and the mission document is `mission.md` in the same folder. The Developer's own proof is
`docs/missions/smart-director-restart-2026-09-19/proof-phase-1-task-2.md` in your worktree. Do not trust
it: it is self-testimony. Coding standards: `docs/CodingStyle.md` and the repository's `CLAUDE.md`.

Look hardest at:

- the verdict mapping, and whether any path lets the cycle ask the launcher for a restart when the
  drain did not finish ready;
- a drain that throws, or is cancelled, part way: what the cycle reports and whether the process-wide
  drain and cycle gates are always released;
- whether the tests watch the real cycle and the real drain on the rig, or a hand-built outcome that
  stays green if the wiring is reverted;
- the new xUnit collection: does it hide a real race in the product behind test ordering;
- the change to `src/CcDirector.Avalonia/DrainDirectorDialog.axaml.cs` and `Drain/DrainPaths.cs`: were
  they needed for this task;
- comments that still claim this build carries no drain.

You may run the check: `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"`
(the Tech Lead's own run on this commit: 428 passed, 0 failed). Foreground only, no sub-agents.

## What you write

One file, `docs/missions/smart-director-restart-2026-09-19/review-phase-1-2.md`, in your worktree, NOT
committed. It states your SCOPE first - what you read, what you ran, what you could not reach - and
then numbered findings. A finding must prove the harm: what breaks, for whom, and why it must change,
with the file and line. You may return nothing; "nothing found" within a stated scope is a valid
review. Do not list style preferences.

Never sign it: no agent or vendor name anywhere. ASCII only. Plain English, no abbreviations. Never
restart, drain, stop or message a session that is not yours. When the file is written:
`cc-devthrottle session report "<one paragraph: how many findings, the worst one>"`.
