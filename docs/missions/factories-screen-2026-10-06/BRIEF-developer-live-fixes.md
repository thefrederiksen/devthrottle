# Brief - Developer, two defects found in the live QA (phase E)

You are a Developer on the Factories screen mission. Read `MISSION.md`, `PLAN.md` and `BRIEF-developer-talk.md` in
this folder first. The Implementation Lead (session b496c54d) opened you and is who you report to. Work in your own
worktree cut from origin/main (everything of this mission is merged: phases A-D, live as commit 00a7ca4). Follow
CLAUDE.md, docs/CodingStyle.md. One pull request for both, each with a regression test that fails before your fix.

## Defect 1 - a talk session is not in its factory (must fix)

Live evidence, 6 Oct ~20:15 UTC: the owner's browser pressed "Talk to Nora Hale" on
`gateway.devthrottle.com/factories/warmforward`. The Gateway answered and session
`e1e8fac9-42a1-4546-b556-25e516c3ffe1` started on SOREN_NORTH, named "WarmForward - Nora Hale - talk with the
owner", in `D:\ReposFred\cc-consult\ideas\warmforward-factory`, no controller, no parent, origin human/cockpit -
all correct. But the session list shows `factory: null`, and the agent reported: "the Gateway says this session is
in no factory, so I can't read or write factory memory". So the talk's closing activity line and memory note cannot
work, which is the heart of mission item 5.

Find the ROOT CAUSE - do not guess and do not add a fallback. Trace the factory from `FactoryTalkEndpoints` through
`GatewayEndpoints.StartSessionOnDirectorAsync`, `SpawnFactory.TryEstablish`, the create the Gateway sends, the
Director that received it (which build is running on SOREN_NORTH, and does it read `factory` on a create?), and
the session's history row the memory commands read. The Gateway log and the Director log
(`%LOCALAPPDATA%\cc-director\logs\director\`) for that time are evidence. If the cause is on the Director and the
installed Director is older than the code on main, say so plainly with the version numbers - that changes what the
fix is, and the Lead decides. Prove the fix with a test that drives the real path a talk takes.

## Defect 2 - the Seats tab mixes two time zones (fix)

On `/factories/warmforward/seats` the CEO row reads "Daily 06:15 (America/Toronto)" and "Today 10:16 - started".
10:16 is the UTC time of the 06:15 Toronto run. A reader sees a run four hours after its schedule. Every time on
one row must be in one zone, and it must say which when it is not the account's. Decide the cleanest rule in the
fold (the account's zone for everything, or the schedule's own zone for the seat's row) and apply it to the Seats
tab and the factory page; test it with a non-UTC schedule.

## How you work

The local gate (`.\scripts\test-local.ps1`) plus the Gateway suites for your area (the machine is short of
memory - run what you need, say what you did not run). Commit `type(scope): description`, NO attribution. Push,
`gh pr create`, then `cc-devthrottle message send b496c54d "<one line>"`. The Lead arranges the review, merges and
deploys. Never deploy, never start a talk session on the live Gateway yourself, never touch a factory's schedules.
