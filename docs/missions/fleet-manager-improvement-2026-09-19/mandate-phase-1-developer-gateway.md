# Fleet Manager Improvement - phase 1, task 1: the Gateway - mandate for the Developer

20 September 2026. From the Tech Lead (session 51fbe18b). You report to the Tech Lead, never to the
owner and never to the fleet. This file is your whole mandate. Your seat is Developer.

## Read first, in this order

1. `cc-devthrottle skill get devthrottle-method` - the Developer section and the laws.
2. `MISSION.md` beside this file. It wins where anything disagrees. Your task is the first bullet of
   section 5 ("A raised session"); the owner's decisions are in section 4.
3. `HANDOVER.md` beside this file - what was verified on origin/main and where.
4. The repository's `CLAUDE.md` (rule zero, critical rules 2, 3, 4, 5 and 7, and the built-in skill
   rule) and `docs/CodingStyle.md`.
5. `cc-devthrottle skill get checks-that-fail-open` and
   `cc-devthrottle skill get proof-covers-the-wrong-thing` before you write a test.

## Where you work

Your own worktree `D:\ReposFred\devthrottle-fmi-p1-gateway`, branch
`fleet-manager-improvement/p1-gateway`, cut from origin/main. Never the shared checkout
`D:\ReposFred\devthrottle`, never the Tech Lead's worktree. Issue #3177.

## The task

Build, in the Gateway only:

1. **The list of raised sessions.** Per account (tenant). Durable: it survives a Gateway restart.
   Written only by (a) the owner's own device - a raise route and a lower route - and (b) setting up
   the Fleet Manager (`PUT /gateway/fleet-manager`): the marked session is raised; when the mark
   moves or clears, raised follows the mark. A raised entry ends with its session. Read
   `src/CcDirector.Gateway/Fleet/FleetManagerSessions.cs` for how the mark works and follow its
   pattern of one place that answers the question ("is this session raised?").
2. **The guard.** `AuthMiddleware` consults the list where it applies
   `src/CcDirector.Gateway/Util/SessionKeyGuard.cs` (two call sites today). A raised session key
   passes exactly two refusals it does not pass today: the agent input refusal (prompt, interrupt,
   escape, fan-out, answering a judged stop) and the owner-only Fleet Manager routes. This is a named
   widening, NOT a blanket allow: the guard is an allow list and stays one. Still refused to a raised
   key: the admission surface (devices, sign-in and sign-out, billing), raising or lowering any
   session (a raised session never raises another, and never itself), and anything only the developer
   of DevThrottle can do (shutting down the Gateway). Tenant binding is the session key's own and is
   not loosened - a raised key acting on another tenant's session is refused as today.
3. **The message exemption.** `src/CcDirector.Gateway/Messaging/FleetMessagePolicy.cs` gains a
   `FleetMessageExemption` for a raised sender that waives the relationship rule and both rates. The
   duplicate rule stays. The route that picks the exemption is in
   `src/CcDirector.Gateway/Api/GatewayEndpoints.cs`.
4. **The record.** Every action a raised key takes that an unraised key could not is recorded with
   the session that took it, and so are raise and lower themselves. Use the audit or event store the
   Gateway already has - find it, do not invent a second one. The record must be answerable by query,
   not only present in a log file.
5. **The words.** The shipped `fleet-manager` skill
   (`src/CcDirector.Gateway/Skills/Content/fleet-manager.skill.md`) and workflow
   (`src/CcDirector.Gateway/Workflows/Content/fleet-manager.instructions.md`) change in this same
   pull request to say what a raised Fleet Manager may now do and what it still may not. Those two
   files are the single sources; there is no `.claude/skills/fleet-manager` copy today - confirm that,
   and run `BuiltInSkillsHaveOneSourceTests` and `ShippedSkillsTeachOwnershipTests`. Also sweep
   `fleet-comms.skill.md` for a sentence the new grant makes false ("refused to every session") and
   correct it, regenerating `.claude/skills/fleet-comms/SKILL.md` from it as `CLAUDE.md` describes.

The raise and lower CONTROL in the clients is task 2 and is not yours. But the Gateway rules and the
client renders (critical rule 7): put on the session the client reads whatever task 2 will need to
draw the control - whether the session is raised, and whether raise or lower is offered - as finished
values, so task 2 adds no conditional that decides what a state means.

## The tests - the refusals are as much the feature as the grants

Known trap, from this repository's memory: adding a Gateway route does not add it to
`SessionKeyGuard` - verbs answer 403 while every test stays green. So your tests must drive the REAL
middleware with a REAL session key over the real request pipeline, never a hand-built verdict input.
A test that calls `SessionKeyGuard.Check` directly is welcome as well but does not count as this proof.

They must show:

- With a raised key: prompt, interrupt, escape, fan-out and answering a judged stop pass; the
  owner-only Fleet Manager routes pass.
- With a raised key: the admission surface refused; raising another session refused; lowering
  refused; shutting down the Gateway refused; another tenant's session refused.
- With a key that is NOT raised: every one of the above refused exactly as today.
- Raise and lower from the owner's device work; from a session key they are refused.
- Raised follows the Fleet Manager mark when it moves and when it clears; a raised entry is gone when
  its session ends; the list survives a restart of the store.
- Messages: a raised sender passes the relationship rule and both rates; an identical unread message
  is still dropped as a duplicate; a sender that is not raised is limited exactly as today.
- Every raised action leaves a record naming the session that took it.

Each pass condition is a specific presence (a status code and a body, a stored row), never an
absence. Name tests `MethodName_Scenario_ExpectedResult`. For at least the guard widening, prove the
test watches the code: commit first, remove the check, see the test go red, restore, rebuild (never
`--no-build` on the restore run), and say so in your proof.

## The check and the proof you owe

Run `.\scripts\test-local.ps1 -Parked` in your worktree (Docker is running on this machine; the
script builds its own throwaway database). It may run longer than ten minutes in the foreground; if
it cannot finish inside your foreground limit, say so in your report rather than hiding it in the
background - I run the same check myself in a separate worktree regardless. Commit
`docs/missions/fleet-manager-improvement-2026-09-19/proofs/phase-1-task-1-developer.md`: the command,
the counts per suite, in plain terms what your tests prove and what they do NOT cover.

## How it ends

Commit and push your branch and open ONE pull request against main that names issue #3177. Do NOT
merge it - I send it to a Reviewer running a different agent first, the findings come back to you,
and you answer every one, accepted or declined with the reason, in
`docs/missions/fleet-manager-improvement-2026-09-19/reviews/phase-1-task-1-answers.md`. I merge.

When the pull request is open, and again after each round of answers:
`cc-devthrottle session report "<what you did, and anything I must decide>"`. If something is
genuinely undecidable inside this mandate, `cc-devthrottle session raise "..."` and carry on with
everything else. Messages are limited; put news in reports.

## What you never do

Deploy the Gateway, release the Director, delete data, send anything outward, kill any running
process. No sub-agents and nothing in the background - everything in your visible foreground session.
No assistant's name on any commit, pull request, issue, comment or document - no trailer, no footer;
check for "Co-Authored-By" and "Generated with" before every commit and pull request. Plain English,
no abbreviations. Use the fleet's words: session, agent, owner, snooze, raise, lower.
