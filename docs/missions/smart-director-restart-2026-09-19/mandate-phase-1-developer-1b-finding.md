# Mandate - Smart Director Restart - Developer, phase 1, task 1: answer the one review finding

You are a Developer on the Smart Director Restart mission. You were opened by the Tech Lead of phase 1
(session 5a54dc21) and you report to it only. The Developer that built this work could not be reached
with the finding, so it has been closed and the finding is yours. You have no transcript; this file is
your history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p1-contracts`, branch
`smart-restart/p1-contracts`. Work only there. Never touch `D:/ReposFred/devthrottle`.

## Read first, all in `docs/missions/smart-director-restart-2026-09-19/` in your worktree

1. `mandate-phase-1-developer-1-contracts.md` - the task as it was given. Its rules bind you too.
2. `proof-phase-1-task-1.md` - what was built and proved.
3. `review-phase-1-1.md` - the review. It has ONE finding: the short hand-over-now message describes
   the closing block without the question and blocked-reason lines, although it is the only message
   some sessions will ever see.
4. `docs/CodingStyle.md`.

## Your one task

Answer the finding in `review-phase-1-1-answers.md`: accepted or declined, with the reason. A Reviewer
advises; it does not command. If you accept, fix it in `src/CcDirector.ControlApi/Drain/DrainMessages.cs`
with a test that would have caught it, keep the closing block exactly as `DrainReportBlock` parses it,
and add the new test and the new count to `proof-phase-1-task-1.md`.

Then run the check in the foreground and read the COUNT:

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

(475 passed, 0 failed before your change.) Commit everything in that folder - the review, your
answers, and the mandate files already placed there - push, and
`cc-devthrottle session report "<one paragraph>"`. Do NOT open a pull request.

Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with". ASCII only.
Plain English, no abbreviations. Nothing in the background, no sub-agents. Never restart, drain, stop
or message a session that is not yours.
