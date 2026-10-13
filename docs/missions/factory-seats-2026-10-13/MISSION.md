# Mission document - Factory Seats: a seat you can open and define without a schedule

Date: 2026-10-13. Owner: Soren. Driver: the Implementation Lead (session "Factory Seats - Implementation Lead").
Issue: thefrederiksen/devthrottle#3950. Status: the owner said go on 2026-10-13 ("implement what you've marked up").

The two dev reports this mission was settled in:

- How a factory works: https://gateway.devthrottle.com/r/4b57d266-b165-4b4c-8370-850dc5805935
- A seat without a schedule, with the mock-up: https://gateway.devthrottle.com/r/06e0c753-1ba5-43af-bba6-3ce9c165471c

This mission builds a slice of an older, approved architecture and must not contradict it: the Teams, factory
agents and policy engine record (`devthrottle_internal`, `docs/missions/teams-and-agents-2026-09-19/DESIGN.md`,
epic #2177). That record already rules that a factory agent's definition lives on the Gateway, beside skills and
workflows, with draft, publish, versions kept and roll back (R9, R20, section 5.2). Its build mission 1 was never
started. This mission does not start it. It takes one part - the seat's brief held on the Gateway - because the
owner's screen cannot work without it. Dispatch, preflight, the policy seam, calls between factory agents and the
Fleet Manager as a definition all stay in that mission.

## 1. The mission

Every seat of a factory has a page of its own, whether or not anything is scheduled, and what the seat is told to
do is held on the Gateway where the owner can read it and change it.

## 2. The why

On 13 October 2026 the owner took the schedule off the boss seat of one factory and renamed it. Two things broke.

The screen lost the seat. The only way into a seat on the Factories screen is the Edit schedule button of its
schedule; the seat's instructions are a tab inside the schedule editor. The owner:

> "When we now turn the schedule off for an agent, it seems I can't define the agent anymore. It seems that our UI
> has entangled the scheduling with the agent itself. I might be able to do all this using an agent to control what
> the factory agent does but the user interface doesn't show me."

> "The boss session should not be scheduled. It should have the schedule turned off. So we should be able to have
> the agent has no scheduling."

The rename took four changes in four places: the registration, a merged pull request for the brief in a
repository, two replacement schedules because a prompt named the seat, and a records folder. The owner:

> "Why do we need to merge to change the names of things in a factory. The definitions of the factory should be in
> the factory and then the document should be maybe in the repo."

## 3. The goal

True at the end, each shown in the QA report with the flow and the failure cases:

1. Every seat row on the Seats tab opens that seat's page, for a seat with a schedule and for one with none.
2. The seat page shows who the seat is, what it is told to do, how it starts, the factory's goal and number, and
   what it has left behind. A seat with no schedule shows all of it.
3. A seat with nothing scheduled reads "Talk only", and a factory with nothing scheduled is not shown as PAUSED.
4. A schedule can be taken off a seat from the page and from the command line.
5. A seat's brief can be held on the Gateway: read, edited, every version kept, an older version brought back.
6. A talk and a scheduled run of a seat whose brief is on the Gateway are given that brief by the Gateway, and are
   told who they are by the Gateway. Renaming such a seat is one registration and nothing else.
7. Nothing that works today stops working for a seat whose brief is still a file in a repository.

## 4. Decisions

The owner's, in his words or by his tap, 13 October 2026:

- **D1. The seat page.** "Yes. Every seat has its own page, with or without a schedule. Instructions, how it
  starts, goal and records are all there." (answer to the mock-up report, value `yes-seat-page`)
- **D2. Documents on the Gateway.** "We should store all documents about the factory on the gateway, not in the
  repo. This way the gateway has full control of the factory and run again 0, 1 or more repos."
- **D3. One issue for both reports**, for the sessions working on factories (value `one-issue`). Filed as #3950.
- **D4. The boss is the Boss.** "She's called the CFO but she shouldn't be. She should just be called boss and she
  shouldn't have a name."
- **D5. A seat may have no schedule.** Quoted in section 2.

- **D6. Merged and released.** "We should get this merged and released." (13 October 2026, after the build
  started.) This replaces section 9's "not released" for this mission only. The release itself is made by the
  repository's own release procedure, never by a Developer.
- **D7. The existing factories are not converted.** "I really don't care that much about converting my factories,
  I can recreate all of them. Almost rather turn them all off and then recreate them one by one. And I don't know
  if any other tenants have created factories." So no tool is built to move a brief file onto the Gateway. A seat
  gets a brief on the Gateway by being given one. Because another account may have factories, a seat whose brief is
  still a file keeps working exactly as it does today (P5).

The Implementation Lead's, where the owner has not spoken. Each is a PROPOSAL the owner can replace; the final
report lists them:

- **P1. The word is "brief".** The registration already says `briefFile` and a talk already says "your brief". The
  mock-up's "What this seat does" is the heading of the brief's panel. One word, one idea.
- **P2. Draft, then published (R20).** A save makes a new draft version. Publish makes one version the one runs
  and talks use. Bringing back an older version publishes a copy of it as the newest version.
- **P3. Who may write (R7).** A person in the Cockpit, or a session of that factory, may save a draft. Only a
  person publishes. A session that tries to publish is refused with that sentence.
- **P4. The status word for a factory with nothing scheduled is TALK ONLY**, with the line "Nothing scheduled. You
  run it by talking to it." It is not a problem state and never counts toward FAILING or NEEDS YOU.
- **P5. A brief file in a repository keeps working.** A seat with no brief on the Gateway is told the path of its
  file, exactly as today, and its page says where that file is. Nothing offers to move it (D7). This is the
  state every existing seat starts in, not a fallback for a failure.
- **P6. Phase 3 is designed before it is built.** D2 in full - every document on the Gateway, a factory working
  in no repository, one or several - touches where tools and permission rules live. It gets its own short design
  and the owner's answer before any code.

## 5. Design

**The seat page.** `/factories/<factory>/seats/<seat>`, built the way the factory page is: the Gateway composes
every word, the Cockpit draws it. Panels, in order:

| Panel | Holds | Source |
|---|---|---|
| Head | Name, role, factory, computer, a chip for how it starts, Talk | The registration |
| What this seat does | The brief: the published text, its version and date, Edit, History. Or, for a seat whose brief is a file: the path and the computer | Phase 2 store; the registration |
| How it starts | Talk only, or each schedule with when it runs, Edit schedule, Pause, Take off this seat; each trigger that starts it | Schedules, triggers |
| Goal | The factory's goal and its newest number | The registration, goal numbers |
| What it has left behind | Its last talk, its last run and result, its recent activity rows, the count of memory notes with a link to Memory | Activity, run results, memory |

The older read-only page at `/factories/<factory>/agents/<agent>` is not extended. When the seat page has what it
shows, it redirects to the seat page and is removed in the same pull request.

**The brief on the Gateway.** A new store beside factory memory, shaped the same way and for the same reasons:
append-only rows keyed by account, factory, seat and version; a writer says which version it read and is refused
with the current one if it is stale; current text capped; versions kept. Each version records who wrote it (a
person, or which session), when, and whether it is a draft or was published. Routes for read, history, save draft,
publish and bring back; the same in `cc-devthrottle factory brief`. A brief may be the YAML file a seat has today,
taken whole, so R9 holds: one YAML file, with the character as prose inside it.

**The Gateway opens every run.** `FactoryTalkSeed` already writes who the seat is from the registration. For a seat
with a published brief it writes the brief's text in place of the path. A scheduled run of a seat with a published
brief is opened the same way by the Gateway, ahead of the schedule's own prompt, in the place
`ScheduledRunClosingInstruction` already adds the closing words. The schedule's prompt is then only what is
particular to the unattended run.

**Taking a schedule off a seat.** One route and one command. The schedule stays, switched as it was, belonging to
no factory. Guarded by the rule every factory schedule change already obeys (`FactoryNaming.TryAct`).

**Out.** Everything in epic #2177 not named above. Documents other than the brief, and factories over several
repositories (phase 3, designed first). Moving the existing factories' briefs (D7). 

## 6. Phases

| Phase | What is built | Tracks | Tech Lead |
|---|---|---|---|
| 1. A seat you can open | The seat page from what the Gateway holds today; Open seat on every row; TALK ONLY; take a schedule off a seat (route, command, button); the old agent page redirected | One Developer | No - the Implementation Lead checks it |
| 2. The brief on the Gateway | 2a the store, routes and command, no screen. 2b the brief panel: read, edit, history, publish, bring back. 2c the Gateway opens talks and scheduled runs with the brief | 2a runs beside phase 1; 2b and 2c after both | No |
| 3. Documents and repositories | Design only, then the owner's answer | - | - |

## 7. The check

Run by the seat that receives the work, from a worktree of the branch, on Linux:

```
dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory|FullyQualifiedName~Cron|FullyQualifiedName~Schedule"
dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Factory"
npm run typecheck
npm test --workspace @devthrottle/client-core
npm test --workspace apps/cockpit        (the workspace name as package.json gives it)
python3 -m pytest tools/cc-devthrottle/tests -q -k "factory or schedule"
```

Every phase adds its tests to what those commands reach. Then the QA report for the phase, committed under
`docs/qa/factory-seats/<phase>/` with desktop and phone screenshots taken from the Cockpit against a Gateway
holding a fixture factory, the way `docs/qa/factory-control/shifts/capture.py` does it:

- Phase 1: a seat with a schedule; a seat with none; a seat whose schedule is missing from the Gateway; a factory
  with nothing scheduled on the cards; a schedule taken off a seat, before and after; a session outside the
  factory refused when it tries to take one off, with the refusal's words.
- Phase 2: a brief saved as a draft, published, a second version published, the first brought back; a stale save
  refused with the current version; a session's publish refused; a talk seed and a scheduled-run opening for a seat
  with a brief and for one without, side by side.

A refusal is proven by the response that carries it, quoted in the report. "No error appeared" proves nothing.

## 8. Merge plan

Trunk-based, as the repository requires: one worktree per workstream off `origin/main`, small pull requests,
merged on green. Other sessions merged into the factory code several times on 13 October, so every branch is
rebased onto `origin/main` before its check is run. Phase 2a adds one table by an Entity Framework migration, with
its Postgres migration, the way the factory email and run result tables were added that week; nothing existing
reads it. Each Developer's work is read by a Reviewer on a different agent (Codex first) before its pull request
merges. The Implementation Lead sends it; the Developer never picks the Reviewer.

## 9. Where it ends

Merged to main with this record and the QA reports beside the code, then released by the repository's release
procedure on the owner's word of 13 October (D6). The mission is over when the owner can open a seat with no
schedule in his own Cockpit.

## 10. Questions

None block phases 1 and 2. For the final report: P2, P3 and P4 above, and the shape of phase 3.
