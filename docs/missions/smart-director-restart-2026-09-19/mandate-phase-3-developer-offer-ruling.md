# Mandate - Smart Director Restart - Developer: the way up offers a record whose sessions all ended without a handover

You are a Developer on the Smart Director Restart mission. You were opened by the Delivery Lead (session
number 150) and you report to it. Phase 3's Tech Lead has reported and gone; this is the one ruling it
asked for, now made. You have one task. You have no transcript; this file and the files it names are
your history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p3b`, branch `smart-restart/p3-offer-ruling`,
cut from `origin/main`.

## Finish before you stop

On this Director a session that has ended its turn is never woken (product issue 3186). Do EVERYTHING
below before your turn ends. Never `run_in_background`; keep any one command under nine minutes (split a
long test run by filter). Never run `rm` on a path built from a variable: a safety hook stops it and
asks the owner, which hangs you for good. Literal paths only. The account's weekly and monthly usage
limits are nearly spent; if your screen warns one is close, write where you are into
`docs/missions/smart-director-restart-2026-09-19/phase-3-status.md`, commit and push at once.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - "If you are a Developer", and laws 1, 4, 14, 17.
2. `docs/missions/smart-director-restart-2026-09-19/ruling-way-up-presence-check.md` in your worktree -
   THE RULING. It is uncommitted; commit it with your change. It says what to build and why.
3. `docs/missions/smart-director-restart-2026-09-19/mission.md` - sections 5.3 item 10, 10.3, 10.5. It
   wins over this file.
4. `docs/missions/smart-director-restart-2026-09-19/phase-3-proof.md` and `phase-3-status.md` - what
   phase 3 built, and what it could not reach.

## Your task

Widen `DirectorWayUp.IsOfferable` (`src/CcDirector.ControlApi/SmartRestart/DirectorWayUp.cs`, line 310)
exactly as the ruling says, and make every part of the way up agree with it - the record the start-up
offer builds, its counts and its words, and the history - so that a record whose sessions all ended
without a handover is offered at start-up with those seats listed, unticked, each with its reopen
button.

Read `BuildRecord`, `OwedSeats` and the window that renders the offer before you change anything: the
count a person sees must still be true, and an offer with nothing ticked must still make sense to read.

What must NOT change, and each needs a test that would fail if it did:

- an ignore-all record is never offered automatically;
- a cancelled record is never offered;
- a record with nothing left to act on is not offered and stays in the history;
- ended-without-a-handover seats stay unticked by default;
- nothing is brought back by itself;
- Codex stays noted only.

No fallback programming. If the ruling cannot be built as written - if something in the code makes it
wrong - STOP, write what you found and your recommendation into `phase-3-status.md`, push, and report.

## The check

Run these yourself, before and after, and put the counts in your proof. Read the COUNT, never the
colour:

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"
    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"

Phase 3's last run on main: 606 and 145, 0 failed. Prove each new test can fail.

## What you owe

`proof-way-up-offer-ruling.md` in `docs/missions/smart-director-restart-2026-09-19/`, committed beside
the code: what you changed, the counts before and after, what each new test proves in plain words, the
revert proofs, and what you could not reach. Then push, open the pull request against `main` naming
issue #3167, and squash merge it with the branch deleted. Do not wait for the hosted checks. If the
merge is refused, write why into `phase-3-status.md`, push, and stop - force nothing. Then
`cc-devthrottle session report "<one paragraph>"`.

Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", in any commit,
pull request, comment or file. ASCII only. Never deploy. Never run the feature against a real Director.
Never restart, drain, stop or message a session that is not yours.
