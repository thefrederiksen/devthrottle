# Fleet Manager Improvement - phase 1, task 1 - mandate for the Reviewer

20 September 2026. From the Tech Lead (session 0cc96f78). You report to the Tech Lead, never to the
owner and never to the fleet. Your seat is Reviewer. You review; you do not build.

## What you are reviewing

Pull request 3198 on `thefrederiksen/devthrottle`, branch `fleet-manager-improvement/p1-gateway`,
head commit `bc47d00e4`. Issue #3177. It is checked out for you at this worktree in a detached head
at exactly that commit. Do not edit the product code, do not push, do not merge, do not touch the
Developer's worktree `D:\ReposFred\devthrottle-fmi-p1-gateway`.

The change adds "raised sessions" to the Gateway: a session the owner raises acts with the owner's
permissions inside the owner's own account.

## Read first, in this order

1. `docs/missions/fleet-manager-improvement-2026-09-19/MISSION.md` - it wins where anything
   disagrees. Section 4 holds the owner's decisions; section 5's first bullet is this task.
2. `docs/missions/fleet-manager-improvement-2026-09-19/mandate-phase-1-developer-gateway.md` - the
   task the Developer was given, including the list of tests it owes.
3. `docs/missions/fleet-manager-improvement-2026-09-19/proofs/phase-1-task-1-developer.md` - what
   the Developer says it proved and what it says it did not cover.
4. `CLAUDE.md` in the repository root - critical rules 2, 3, 4, 5, 7 and the built-in skill rule.
5. `docs/CodingStyle.md`.

## What to check, in priority order

1. **The guard is still an allow list.** `src/CcDirector.Gateway/Util/SessionKeyGuard.cs` and
   `src/CcDirector.Gateway/Util/AuthMiddleware.cs`. A raised session key must gain exactly two
   things: the agent input refusal (prompt, interrupt, escape, fan-out, answering a judged stop) and
   the owner-only Fleet Manager routes. Read every route the widening can reach and say whether any
   route is now reachable by a raised key that the mandate does not grant. This is the single most
   valuable thing you can do: a blanket allow dressed as a named widening is the failure mode.
2. **The refusals hold.** With a raised key: the admission surface (devices, sign-in, sign-out,
   billing) refused; raising or lowering any session refused; shutting the Gateway down refused;
   another tenant's session refused. Tenant binding must not be loosened anywhere.
3. **The tests drive the real middleware.** This repository's known trap is that adding a route does
   not add it to `SessionKeyGuard`, so verbs answer 403 while every test stays green. Check
   `src/CcDirector.Gateway.Tests/RaisedSessionHostTests.cs` really goes through the request pipeline
   with a real session key, and that each pass condition is a specific presence - a status code and a
   body, or a stored row - never an absence. Say which of the mandate's required test cases are
   genuinely covered and which only appear to be.
4. **The record.** Every action a raised key takes that an unraised key could not, and raise and
   lower themselves, must be recorded with the session that took it, answerable by query and not
   only present in a log file. Check it uses the audit store the Gateway already has and did not
   invent a second one.
5. **Messages.** `src/CcDirector.Gateway/Messaging/FleetMessagePolicy.cs`: a raised sender waives
   the relationship rule and both rates; the duplicate rule stays.
6. **The list itself.** `src/CcDirector.Gateway/Fleet/RaisedSessions.cs` and the migration: durable
   across a restart, per account, raised follows the Fleet Manager mark when it moves and when it
   clears, and a raised entry ends with its session.
7. **The client gets finished values (critical rule 7).** Whatever task 2 needs in order to draw the
   raise and lower control must already be on the session the client reads, as finished values, so
   task 2 adds no conditional that decides what a state means.
8. **The words.** `Skills/Content/fleet-manager.skill.md`, `Workflows/Content/fleet-manager.instructions.md`
   and `Skills/Content/fleet-comms.skill.md` must not now say something the change makes false, and
   `.claude/skills/fleet-comms/SKILL.md` must agree with its single source.

Also flag: any assistant's or vendor's name in a commit, comment or document; any abbreviation where
plain English belongs; any fallback that hides a failure instead of failing with a clear error.

## Three decisions the Developer made that the mandate did not settle - say whether each is right

- The Fleet Manager mark alone never raises: because any session key can already set the mark, the
  Developer made the mark raise only when the owner's own phone or browser sets it. A session that
  marks itself is marked and not raised.
- Marking a walkthrough record answered, snoozed or closed stores that the owner did it, so the
  Developer left those writes refused to a raised key.
- An earlier ruling banned shipped text from naming `cc-devthrottle session prompt`, on the premise
  that typing is refused to every session key. This change makes that premise false, so the Developer
  exempted the two Fleet Manager texts from that sweep.

## How to report

Write your review to
`docs/missions/fleet-manager-improvement-2026-09-19/reviews/phase-1-task-1-review-codex.md` in THIS
worktree. State your scope at the top: what you read and what you did not. Number every finding, and
for each one give the file and line, what is wrong, why it matters, and how sure you are. Separate
"this must change before merge" from "this could be better". If you find nothing in a section, say
so in a sentence - do not leave it silent, because a silence reads as a pass that never happened.

Say plainly what your review does NOT cover.

Do not commit and do not push - leave the file on disk and tell me it is there.

When you are done: `cc-devthrottle session report "<what you found, worst first>"`. If something is
genuinely undecidable, `cc-devthrottle session raise "..."` and carry on with everything else.

No assistant's name on anything you write - no trailer, no footer, no mention of any agent or vendor.
Plain English, no abbreviations. Use the fleet's words: session, agent, owner, snooze, raise, lower.
