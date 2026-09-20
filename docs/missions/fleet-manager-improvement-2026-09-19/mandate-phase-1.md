# Fleet Manager Improvement - phase 1 mandate, for the Tech Lead

20 September 2026. From the Delivery Lead (session 4e6fa57d). You report to the Delivery Lead, never
to the owner and never to the fleet. This file is your whole mandate.

## Read first, in this order

1. `cc-devthrottle skill get devthrottle-method` - your seat is Tech Lead. You never write code.
2. `MISSION.md` beside this file. It wins where anything disagrees. Phase 1 is "Raise a session";
   its design is the first bullet of section 5 and its decisions are in section 4.
3. `HANDOVER.md` beside this file: what the Architect verified on origin/main and where, and what is
   NOT verified.
4. The repository's `CLAUDE.md` (rule zero: never read a stale tree), `docs/CodingStyle.md`.
5. `cc-devthrottle skill get fleet-comms` and `cc-devthrottle skill get fleet-naming` before you
   spawn anything.

## The phase

Issue #3177. Two Developer tasks, in this order, the second only after the first has merged:

1. **The Gateway.** A per-account list of raised sessions, written only from the owner's own device
   (raise, lower) and by setting up the Fleet Manager; it follows the Fleet Manager mark through a
   restart or a move and ends with the session. `AuthMiddleware` consults it where it applies
   `SessionKeyGuard`: a raised key passes the agent input refusal (prompt, interrupt, escape,
   fan-out, answering a judged stop) and the owner-only Fleet Manager routes. The admission surface
   (devices, sign-in and sign-out, billing) stays refused, and so does raising another session, and
   so does anything only the developer of DevThrottle can do. Tenant binding is not loosened.
   `FleetMessagePolicy` gains an exemption for a raised sender that waives the relationship rule and
   both rates; the duplicate rule stays. Every raised action is recorded with the session that took
   it. The shipped `fleet-manager` workflow and skill change their words in the SAME pull request -
   find where each one's single source lives before editing (the built-in skill rule in `CLAUDE.md`).
2. **The control.** Raise and lower in the shared client package (`packages/client-core`), mounted
   by both surfaces, following critical rules 7 and 8: the Gateway rules, the client renders.

Known trap, from this repository's memory: adding a Gateway route does not add it to
`SessionKeyGuard` - verbs answer 403 while every test stays green. The Developer's tests must drive
the real middleware with a real session key, raised and not raised, not a hand-built input.

The refusals are as much the feature as the grants. The tests must show, with a raised key: the
admission surface refused, raising another session refused, another tenant refused; and with a key
that is not raised: everything refused exactly as today.

## How you run it

- One Developer per task, each in its OWN worktree cut from origin/main
  (`git worktree add ../devthrottle-fmi-p1-<task> -b <branch> origin/main`), never the shared
  checkout `D:\ReposFred\devthrottle`, never this worktree. Spawn on the worktree path.
- Spawn with `--controlled-by self --mission 1be19189-9217-4167-91ae-f8a2d1e5d51f --role Worker`
  and the name `Fleet Manager Improvement - Developer - <what it does>`. Write each Developer a
  mandate file beside this one and point its prompt at it. After spawning, confirm the seat actually
  started work (read its state and screen) - a returned id is not a started seat.
- Everything runs in the foreground as a visible session. No background agents, no sub-agents.
- **The check, run by YOU, in a worktree of your own that no Developer is editing:**
  `.\scripts\test-local.ps1 -Parked` (needs Docker running; it says so if not). Task 2 also owes
  `npm test` in `packages/client-core`, `apps/cockpit` and `apps/mobile`. A run longer than the
  ten-minute foreground cap is not a reason to background it - tell me and I will decide.
- **Review before the pull request merges:** a Reviewer running a different agent, opened by you:
  `cc-devthrottle session spawn <worktree> --controlled-by self --agent Codex --mission 1be19189-9217-4167-91ae-f8a2d1e5d51f --role Worker --name "Fleet Manager Improvement - Reviewer - <what>" --prompt "<what to review; write the review to docs/missions/fleet-manager-improvement-2026-09-19/reviews/<file>.md, state your scope>"`.
  Findings go back to the Developer who built the work, who answers every one, accepted or declined
  with the reason, in the record.
- Then merge: `gh pr merge <number> --squash --delete-branch`. Nothing waits for continuous
  integration. One pull request per Developer task, merged the day it is opened.
- Shut down each Developer and Reviewer once its work is merged
  (`cc-devthrottle session stop <id> --reason "..."`). Before removing any worktree, run
  `git status` in it and move anything untracked that belongs in the record.

## What nobody on this phase does

Deploy the Gateway, release the Director, delete data, send anything outward. No assistant's name on
any commit, pull request, issue, comment or document - no trailer, no footer. Plain English, no
abbreviations. Use the fleet's words: session, agent, owner, snooze.

## What you owe me

The phase's proof committed on this worktree's branch (`fleet-manager-improvement/p1-record`) under
`docs/missions/fleet-manager-improvement-2026-09-19/`: `proofs/phase-1.md` (your own check run, the
command, the counts per suite, and in plain terms what the tests prove and what they do NOT cover),
the reviews, and the answers to findings. Push the branch; I land it and send the finished phase to
its own review.

Report with `cc-devthrottle session report "..."` when task 1 is merged, and when the phase is
done. If something is genuinely undecidable inside this mandate, `cc-devthrottle session raise` and
carry on with everything else. Messages are limited to six an hour; put news in reports.
