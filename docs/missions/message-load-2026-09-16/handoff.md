# Message Load - the running note

This is the note a fresh Manager is rebuilt from. Short on purpose. Everything the Architect knows is
here, in `brief.md`, or in `findings.md`, never only in a conversation.

## Where the rest lives

- The brief and rulings: `docs/missions/message-load-2026-09-16/brief.md` (this folder)
- What is true today, with file references: `findings.md`
- The owner-facing page he approved: `design-and-questions.html`
- Conduct: `cc-devthrottle workflow instructions mission`
- Firstmate handover (internal repository): `devthrottle_internal/docs/research/firstmate/handover-fleet-manager-and-message-load.html`

## Branch

`mission/message-load`, worktree `~/ReposFred/devthrottle-message-load` on devthrottle-mac-mini,
cut from `origin/main` at `66609c3c`. Rebase onto origin/main before each slice.

## Rules of this mission that are easy to forget

- NOBODY on this mission sends a fleet message, to anyone, for any reason. Report by writing this
  file, committing, pushing, then `cc-devthrottle session raise "<one line>"`.
- One slice at a time, in the brief's order. A slice is a pull request the Architect merges.
- Every guard is watched failing before it is called a guard.
- Mac tests: about 16 Core.Tests failures are pre-existing Mac-only failures. Name which ones you
  saw, do not "fix" them.
- Build to slot 5 or higher for any test Director; never touch the owner's running Directors.

## State

- Phase: 0, nothing built. Next: seat the Manager for slice 1 (the inbox and the gate).
- Done and pushed: brief, findings, owner page, this note.
- Open decisions: none. The owner answered "all recommended" to every question on 16 September.
