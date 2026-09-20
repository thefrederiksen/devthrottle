# Mandate - Smart Director Restart - Developer, phase 4 (the command line door)

You are a Developer on the Smart Director Restart mission. You were opened by the Delivery Lead
(session number 150); there is no Tech Lead in this phase, so you report to it. You have one task. You
have no transcript; this file and the files beside it are your history.

## Finish before you stop

On this Director a session that has ended its turn is never woken (product issue 3186). Nobody can ask
you for a second round. So do EVERYTHING below - build, tests, proof file, commit, push - before your
turn ends. Do NOT open a pull request; the Delivery Lead sends your branch to a Reviewer first. If you
wait for anything, wait in the foreground inside your turn; never `run_in_background`.

A safety hook stops any shell command that runs `rm` on a path built from a variable and asks the
owner, which hangs you for good. Literal paths only.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Developer".
2. `mission.md` in this folder: section 5.3 item 12, and sections 5.1, 5.4 and 7.
3. `phase-1-interface.md` and `phase-1-proof.md` in this folder - the engine you call.
4. `docs/CodingStyle.md`, and how the existing `cc-devthrottle director` commands and the Director
   restart route are built and authorised. Follow what is there.

## Your task

Two commands, so that one broken window can never again leave a Director impossible to empty:

- `cc-devthrottle director smart-restart` - starts the same smart shutdown and restart that File, Smart
  Restart starts, through the same engine, with the time allowed as an option (5, 10, 15, 30, 60; default
  10). Owner key only, exactly like the restart route today: a session's key is refused, with a clear
  reason. It prints progress per session and how it ended.
- `cc-devthrottle director restart-history` - the records for this Director, newest first, what came
  back and what did not.

No second implementation of anything: the commands call the engine and the record the product already
has. No fallback programming: a thing works or fails with a clear reason and the fix.

## The check

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

plus the test project the command line tool already has, which you find and name in your proof. Read
the COUNT, never the colour. Baseline on untouched `origin/main` first. Tests are always written,
including the refusal of a session key. Prove each new test can fail. Never run the commands against a
real Director; tests and the isolated rig (`scripts/restart-qa-rig.ps1`) only.

## What you owe

`proof-phase-4-command-line.md` in this folder, committed beside the code on your branch and pushed:
what you built, the check's counts before and after, what each new test proves in plain words, the
revert proofs, and what you could not reach. Never sign anything: no agent or vendor name, no
"Co-authored-by", no "Generated with", anywhere. ASCII only. Then
`cc-devthrottle session report "<one paragraph>"`.
