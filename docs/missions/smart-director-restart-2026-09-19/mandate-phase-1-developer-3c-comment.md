# Mandate - Smart Director Restart - Developer, phase 1, task 3c: one comment that turns the default gate red

You are a Developer on the Smart Director Restart mission, opened by the Tech Lead of phase 1 (session
f42438de). You report to it only. This file is your whole history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p1-engine`, branch `smart-restart/p1-engine`.
Work only there.

You get ONE turn. A session that has stopped on this Director is never woken, so finish everything,
commit and PUSH before your turn ends. The Tech Lead polls the pushed branch, not a message.

## The defect

`src/CcDirector.Gateway.UnitTests/Drain/DrainMessagesSmartShutdownTests.cs` line 72, a comment written by
task 1 of this mission and already on `origin/main`, reads "... whichever message asked for it ...". The
repository-wide test
`CcDirector.Core.UnitTests.Skills.RetiredMessagingWordsTests.Nothing_in_the_repository_outside_the_named_history_uses_the_retired_messaging_words`
searches every file for the retired words, and "message asked" contains one of them, so the default
local gate is red on `origin/main` because of this mission.

## Your one task

Reword that comment so it says the same thing without those two words side by side (for example
"whichever request called for it"). Do NOT add an exemption to the test: the text is not a dated record,
it is just an unlucky phrase. Change nothing else.

## Prove it

    dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~RetiredMessagingWordsTests"

Run it BEFORE the change (it must be red and name that line; if it is not red, stop and say so in your
report) and AFTER, built from source, never `--no-build`. Foreground, never piped through `tail` or
`head`. Append four lines to `docs/missions/smart-director-restart-2026-09-19/proof-phase-1-task-3.md`
under a heading "Task 3c": the defect, the two counts, the new wording.

Commit, then `git push origin smart-restart/p1-engine`. No rebase, no force push, no pull request. Then
`cc-devthrottle session report "<one sentence>"`.

## Rules

Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with". ASCII only.
Plain English, no abbreviations. Nothing in the background, no sub-agents. Never run a delete on a path
built from a variable: a safety hook stops it and asks the owner, and the seat hangs for good. Never
touch a session that is not yours.
