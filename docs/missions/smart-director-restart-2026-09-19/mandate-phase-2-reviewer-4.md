# Mandate - Smart Director Restart - Reviewer, phase 2, task 3: the swap of the two doors

You are a Reviewer on the Smart Director Restart mission. You were opened by the Tech Lead of phase 2
(session c6c50eeb) and you report to it only. You read; you never build and never fix what you find.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p2-review4`, a detached checkout of `52c5c2b54`
(branch `smart-restart-p2-swap`). Change NO tracked file in it.

## ONE TURN ONLY - read twice

Nobody can wake you once your turn ends. There is no second round. Finish everything and write the
review FILE before your turn ends. Run everything in the foreground; nothing in the background; no
sub-agents. Never run `rm` (or any delete) on a path built from a variable: a safety hook stops the
command and asks the owner, which hangs your seat for good. Literal paths only.

## What you review

    git diff 55870f183 HEAD

NOT the diff against main: commit `55870f183` merges the phase 1 engine branch into this branch, because
the engine is reviewed elsewhere and is not yet on main. You review only what comes after it. You may
READ the engine (`src/CcDirector.ControlApi/SmartRestart/`) to judge whether it is called correctly.

The Developer's mandate is
`D:/ReposFred/devthrottle-smart-restart-p2/docs/missions/smart-director-restart-2026-09-19/mandate-phase-2-developer-swap.md`;
`mission.md` beside it wins over it (sections 4.4, 5.3 items 1 to 9, 7, 10.4, 10.5). The Developer's
proof is `docs/missions/smart-director-restart-2026-09-19/attachments/phase-2/swap-proof.md` in your
worktree. Do not trust it: it is self-testimony. Coding standards: `docs/CodingStyle.md`,
`docs/VisualStyle.md` and the repository's `CLAUDE.md` (rules 0, 1, 3, 4, 7).

This is the change that goes live: after it, closing the main window and the File menu item reach the
new screens and the real engine. Look hardest at:

- `MainWindow.axaml.cs` `OnClosing`: every path through it. Can the window now close WITHOUT the owner
  being asked while sessions are running; can it become impossible to close (a cancelled close that
  nothing ever completes, a flag left set after a cancel, a failed or refused run, or an exception in
  the coordinator); is the close after `Emptied` really asked only once; does a close while a run is
  under way do nothing;
- the wiring in the main window that no test opens: read it line by line against the coordinator's
  constructor. The progress screen put in place of the session view and taken away again: is the session
  view truly back and usable after `Cancelled`;
- the operating system shutting down: no dialog, the record attempted with a five second limit, and the
  close carrying on. Can the wait block the interface thread or stop the close;
- one list for the decision and the count (the old close hook used a narrower rule);
- the engine's check in the dialog: opened at once, never blocking; the refusal words shown as given;
  ignore-all still working when the smart choice may not be used; what happens if the check itself
  throws or the dialog is closed before the answer arrives;
- ignore all: the interface thread is not blocked, the call is made once, and the File menu door does
  what the engine really does for the purpose `Restart` on that path;
- zero sessions from the File menu: the Developer says the engine cannot restart with nothing to shut
  down and shows one sentence instead. Is that true of the engine as built;
- tests that hand-build their input and would stay green if the flow were broken, and the source-reading
  test: can it fail;
- the two removed windows: is anything left in `src` or `tools` that names them or depended on them;
- anything that blocks the interface thread, swallows an error, or is a fallback.

You may run the check. Read the COUNT, never the colour (the Tech Lead's run: filter 87 passed, whole
project 686 passed, 0 failed):

    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"

## What you write

One file, `docs/missions/smart-director-restart-2026-09-19/review-phase-2-4.md`, in YOUR worktree
(`D:/ReposFred/devthrottle-smart-restart-p2-review4`), NOT committed. It states your SCOPE first - what
you read, what you ran, what you could not reach - and then numbered findings. A finding must prove the
harm: what breaks, for whom, and why it must change, with the file and line. You may return nothing;
"nothing found" within a stated scope is a valid review. Do not list style preferences.

The very last line of the file must be exactly `END OF REVIEW`, written only when the review is
complete. The Tech Lead waits for that line.

Never sign it: no agent or vendor name anywhere. ASCII only. Plain English, no abbreviations. Never
restart, drain, stop or message a session that is not yours. When the file is written:
`cc-devthrottle session report "<one paragraph: how many findings, the worst one>"`.
