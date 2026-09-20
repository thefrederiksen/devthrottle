# Mandate - Smart Director Restart - Developer: the shutdown must reach and account for EVERY session

You are a Developer on the Smart Director Restart mission. You were opened by the Delivery Lead
(session number 150) and you report to it. You have one task with two halves, both of the same shape:
a session the shutdown cannot ask must be handled by name, never silently and never fatally. You have
no transcript; this file and the files it names are your history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-verb`, branch `smart-restart/handover-verb`,
cut from `origin/main`. Rebase or merge it onto current `origin/main` before you start.

## Why this is the first thing being fixed

The owner ran the feature himself on 20 September and six of seven sessions came back "ended without a
handover". That is what these two defects look like on his screen. They are also why the run took the
whole five minutes: the shutdown ends as soon as every session is terminal
(`DirectorDrain.cs`, `if (seats.All(s => IsTerminal(s))) break;`), so a run where everyone hands over
takes about a minute - and a run where nobody can be asked takes the full time allowed.

## Finish before you stop

On this Director a session that has ended its turn is never woken (product issue 3186). Do EVERYTHING
below before your turn ends. Never `run_in_background`; keep any one command under nine minutes (split
a long test run by filter). Never run `rm` on a path built from a variable: a safety hook stops it and
asks the owner, which hangs you for good. Literal paths only. The account's weekly limit is nearly
spent; if your screen warns it is close, write where you are into
`docs/missions/smart-director-restart-2026-09-19/phase-5-status.md`, commit and push at once.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - "If you are a Developer", and laws 1, 4, 14, 17.
2. `docs/missions/smart-director-restart-2026-09-19/mission.md`, section 7 especially. It wins over
   this file.
3. The two issues in full: `gh issue view 3207` and `gh issue view 3235`
   (`-R thefrederiksen/devthrottle`), and the evidence they name.

## Half one - issue #3207: the two-thirds step never reaches a Pi session

At two thirds of the time allowed the shutdown interrupts whatever is still mid-turn and asks it to
hand over now. `POST /sessions/{sid}/interrupt` answers Conflict on a Pi session because
`PiDriver.InterruptAsync` throws, so that session is never told to hand over, runs to the limit, and
is ended without a handover.

Choose the verb from the driver's DECLARED CAPABILITIES before sending it: interrupt where the driver
declares it, escape where it does not. The issue records that escape stops a Pi session cleanly and it
then wrote its handover in 6.8 seconds. **Check every driver, not only Pi.** A driver that declares
neither must be reported BY NAME - in the record and on the progress screen - and still ended at the
limit as now. A silent skip is the defect, not the cure.

No fallback programming: never "try interrupt, and if it throws try escape". The verb is chosen from
what the driver declares, before it is sent. A `try` around a throw is exactly what law 1 forbids.

## Half two - issue #3235: one session that never starts a turn ends the WHOLE shutdown

A session that takes the words but never starts a turn threw `PromptNotSubmittedException` out of the
run, so the run stopped at the sixth of seven sessions, the seventh was never asked, every other
session was left running and nothing was handed over.

Mission section 7 requires the opposite, in the owner's own contract: "a wedged session that cannot
take a prompt: reported as such, **not waited on in silence**". So a session that cannot be asked is
recorded as such, named on the progress screen, and the shutdown CARRIES ON with everyone else. It is
ended at the limit like any other session that never handed over.

Find where the exception escapes and fix it where the decision belongs - per session, not by wrapping
the whole run in a catch. Wrapping the run would hide the next defect of this shape; the asking of one
session is what is allowed to fail.

## The check

Run these yourself, before and after, and put the counts in your proof. Read the COUNT, never the
colour:

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"
    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"

The Delivery Lead's own runs on `origin/main`: 637 and 147, 0 failed. Tests are always written: for
half one, one per driver shape (declares interrupt, declares escape only, declares neither); for half
two, a session that cannot take a prompt while others can, proving the others are still asked and the
run still finishes. Prove each new test can fail. Do not run this against a real Director or a real
session; tests only.

## What you owe

`proof-reach-every-session.md` in `docs/missions/smart-director-restart-2026-09-19/`, committed beside
the code: what you changed for each half, a table of which drivers declare what (read from the code),
the counts before and after, what each new test proves in plain words, the revert proofs, and what you
could not reach. Then push, open the pull request against `main` naming issues #3167, #3207 and #3235,
and squash merge it with the branch deleted. Do not wait for the hosted checks. If the merge is
refused, write why into `phase-5-status.md`, push, and stop - force nothing. Then
`cc-devthrottle session report "<one paragraph>"`.

Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", in any commit,
pull request, comment or file. ASCII only. Never deploy. Never restart, drain, stop or message a
session that is not yours.
