# Mandate - Smart Director Restart - Developer - the Smart shutdown dialog, review finding

You are a Developer on phase 2 of the Smart Director Restart mission. You were opened by the phase 2
Tech Lead (session c6c50eeb) and you report to it, never to the owner and never to the fleet. You have
no transcript; this file and the files it names are your history. The Developer who built the dialog is
gone; you answer the review of its work.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p2-dialog`, branch `smart-restart-p2-dialog`.
Work only there. The mission record lives in ANOTHER worktree,
`D:/ReposFred/devthrottle-smart-restart-p2`; read it there, never write to it.

## ONE TURN ONLY - read twice

Nobody can wake you once your turn ends. No message reaches a stopped session. Finish EVERYTHING below,
push it, and write your answers file before your turn ends. There is no second round. Run everything in
the foreground; nothing in the background; no sub-agents. Never run `rm` (or any delete) on a path
built from a variable: a safety hook stops the command and asks the owner, which hangs your seat for
good. Literal paths only.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Developer".
2. `D:/ReposFred/devthrottle-smart-restart-p2/docs/missions/smart-director-restart-2026-09-19/review-phase-2-1.md`
   - the review. One finding.
3. `mandate-phase-2-developer-dialog.md` in the same folder - what the first Developer was asked to build.
   Everything in its "Rules that do not bend" binds you too.

## Your one task

1. `git fetch origin` and rebase the branch onto `origin/main`. Nothing on main touches these files, so
   it should be clean.
2. Finding 1, ACCEPTED by the Tech Lead: the dialog's `SmartShutdownResult`
   (`src/CcDirector.Avalonia/SmartRestart/SmartShutdownResult.cs`) has the same name as the engine's
   `CcDirector.ControlApi.SmartRestart.SmartShutdownResult`, and the next task must use both in one
   file. Rename the dialog's type to `SmartShutdownChoice` (it is what the owner chose; the engine's is
   how a run ended), and its file, and any kind enumeration beside it if it carries the same clash.
   Rename every use, in the code and in the tests. Change nothing else about the dialog.
3. The reviewer's note about the null-forgiving operator in the tests is DECLINED by the Tech Lead: the
   same pattern is already on main in this test project and no harm was shown. Do not touch it.
4. Run, in the foreground, with a full build (never `--no-build`):
   - `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` - expect
     25 passed. Read the COUNT, never the colour: a filter that matches nothing exits green.
   - `dotnet test src/CcDirector.Avalonia.Tests` - the whole project; write the count down.
   - `dotnet build src/CcDirector.Avalonia` - 0 warnings, 0 errors.
5. Update `docs/missions/smart-director-restart-2026-09-19/attachments/phase-2/developer-dialog-proof.md`
   in your worktree: the new name, and the counts from step 4 with the commit of `origin/main` you
   rebased onto.
6. Write `docs/missions/smart-director-restart-2026-09-19/review-phase-2-1-answers.md` in YOUR worktree
   and commit it with the rest: each finding and note of the review, accepted or declined, with the
   reason and what was done. The decisions above are the Tech Lead's; say so.
7. Commit and push (`git push --force-with-lease`, because of the rebase). Then open the pull request
   against `main`:
   title `Smart Director Restart phase 2: the Smart shutdown dialog, a window nobody calls yet (#3167)`,
   a body that says in plain words what the window is, that nothing calls it yet, the counts, and that
   it was reviewed by a different agent with the review and the answers in the mission folder.
   Do NOT merge it: the Tech Lead runs the check and merges.
8. Last of all: `cc-devthrottle session report "<one paragraph: what you did, the counts, the pull request number>"`.

## Rules that do not bend

- Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", no robot
  emoji, in any commit, pull request, file or comment. Check the pull request body before you create
  it. ASCII only in everything. Plain English, no abbreviations.
- Never kill a running process. Never launch, restart, drain or stop a Director or a session.
- If something is undecidable inside this mandate, decide the smaller way, write down what you decided
  and why in the answers file, and carry on. Nobody can answer a question this turn.
