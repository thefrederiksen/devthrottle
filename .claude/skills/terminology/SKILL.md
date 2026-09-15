---
name: terminology
description: The words DevThrottle uses and what each one means - session, mission, task, workflow, run, team, the five roles, owner, parent, participation, snooze, agent. Use when writing an issue, brief, commit message, document or code comment, when unsure what to call something, or when you meet an older name in the code. Triggers on "/terminology", "what do we call", "what is the right word", "glossary", "vocabulary", "naming", "is it hold or snooze", "controller or supervisor".
---
# DevThrottle terminology - the words we use, and what they mean

One word per idea, one idea per word. These are the definitions everything else in DevThrottle
uses - the product, the documentation, the workflow conduct, and every agent working in this
fleet. If you are writing an issue, a brief, a commit message, a document, a code comment, or a
message to another session, use these words in these meanings.

The rule behind the list: we do not have to copy the industry, but we must not use a word that
already means something DIFFERENT in it.

## The work

- **Session** - one running coding agent: a process, on a machine, in a repository, that someone
  is talking to. The atom of the system. Never call a session an "agent".
- **Mission** - an undertaking: WHY we are doing something, WHO is working on it together, and the
  TASKS it breaks into. One session or many. A mission HAS a goal; it is not itself a goal. Every
  mission must state its why.
- **Task** - one piece of a mission, handed to one worker. A mission has several. (The word is
  settled; a Task record is not built yet, so do not write as though the system stores one.)
- **Workflow** - a reusable, versioned, published way of working. The HOW, where a mission is the
  WHAT. A mission may run any workflow. The one where an Architect settles the design, a Manager
  drives the phases and Workers build is the **Mission workflow** - always said with the word
  "workflow", which is what separates it from the Mission it runs.
- **Run** - one execution of a workflow, in service of a mission. It carries what the workflow
  promised and the evidence for each promise.
- **Team** - a session and every session beneath it: the session at the top, the sessions it owns,
  and theirs, all the way down. It is what the session list nests and what a collapsed row
  summarises ("3 under it: 2 working, 1 stopped"). A Team is made by OWNERSHIP, not by Mission and
  not by who started whom - a session whose owner is the user is a top-level row, and a session
  that starts one helper it owns is a Team of two.

Mission and Run are two records and that is deliberate. A Mission is a durable statement of
purpose that holds several tasks and can outlive any single execution; a Run is one mechanical
execution. Neither is a duplicate of the other.

## Roles - what a session is for

Five, and no others. Four are settable today; Reviewer is agreed but not yet recordable - see the
last section.

- **Standalone** - works alone, faces the human. The default.
- **Architect** - settles the design and writes the phases down. Must be declared; it cannot be
  inferred from who spawned whom.
- **Manager** - drives the phases and owns the workers.
- **Worker** - builds.
- **Reviewer** - a different session from the one that wrote the work, checking it before it lands.

An **Inspector** is a Reviewer carrying one extra requirement: it must be from a DIFFERENT AGENT
FAMILY to the people who did the work - if the fleet is Claude Code, the Inspector is Codex. Keep
using the word; it is not a sixth role, because the constraint is already proved by the agent kind
recorded on every session and every run participant.

## How sessions relate

- **Owner** - who a session ANSWERS TO: either another session, or the USER. Every session has
  exactly one, and no owner session means the user. It is the only relationship that decides
  anything. A session whose owner is a live session nests under it in the list, stays quiet, and
  reports there; a session whose owner is the user sits at the top level and goes red to ask him.
  When the owning session dies, the user becomes the owner again, and the session surfaces.
- **Parent** - the session that STARTED another. A historical fact, with no effect on display.

Owner and Parent carry the same id on an ordinary spawn and diverge when a session deliberately
starts a session for the user (`--standalone`): the parent is the session that started it, the
owner is the user. The list, the attention rule and every display verdict read the Owner, never
the Parent.

Said plainly, "the owner" with nothing after it means the USER, because the user is the owner of
every session nobody else owns. When you mean another session, say "the owning session" or "its
owner session". Do not say "supervisor" or "controller" for this relationship.

- **Participation** - a session's membership of a run, either active or ended. Do not say "seat":
  in any commercial context a seat is a paid licence.

A **Team** is the whole of what one session owns, read downwards - so Owner is the edge and Team is
the shape it makes. Team is NOT the broadcast boundary: `message send all` reaches the
sessions on your MISSION, and when you are on no mission, the sessions sharing your checkout on
this machine. Say Mission there, never team.

## State

- **Snooze** - suppressing a session's demand for attention, either now or as soon as it stops
  working. A snooze asked for while the agent is still working is a **snooze requested**; it lands
  when the work stops. Do not say "hold" or "parked".

## The machinery

- **Agent** - the coding agent TOOL: Claude Code, Codex, Gemini, Grok. It is what you run. A
  session is one running instance of it. This is the single most common word to get wrong.
- **Director** - the application on each machine that drives that machine's sessions.
- **Supervisor** - a component that WATCHES something running and steps in when it goes wrong: the
  Gateway's `SessionSupervisor` nudging a stuck session, the Launcher's `DirectorSupervisor`
  restarting a Director, the hosted agent's `BrainSupervisor`. This is the industry's meaning
  (Erlang, systemd) and it is the only meaning the word has here. A supervisor is machinery, never a
  session, and never a session's owner.
- **Gateway** - the cloud service that aggregates every machine and owns every display ruling. It
  is strictly a control plane rather than a gateway; describe it that way when it matters.

## Two known exceptions, stated on purpose

- **Worker** collides with Temporal and Kubernetes, where a worker is a PROCESS, not a role. We
  keep it anyway: it is in the shipped workflow steps, the role constant, the mission conduct and
  the command line, and it is a word we say out loud. Accepted, not overlooked.
- **Group** is retired. It was an older way of clustering sessions in the rail; Mission does that
  job. Do not use it in new work.
- **Crew** is retired. The session tree used it for an owning session and the sessions beneath it; that
  is a **Team**. The word belongs to CrewAI, whose signature word it is, and this rule's own test is
  that we do not take a word that already means something different in the industry. It is still in
  the code and is being swept out; do not write it in new work.

## Words in the code that have not caught up yet

The code still uses some older names. When you read them, translate; when you write NEW code or
prose, use the word on the left.

| Say this | The code may still say |
|---|---|
| Owner | `Controller`, `ControllerSessionId`, `HasLiveSupervisor`, `--controlled-by`, and "supervisor" wherever it means the owning session |
| Snooze | `HoldState`, `DeferredHold`, "parked" |
| Participation | "seat" |
| Team | `Crew`, `CrewSummary`, `crewAge`, `crew-*` in the stylesheets |
| Mission (in `message send all`) | "team" in the broadcast prose and warnings |
| Reviewer | nothing - the role does not exist yet |

Do not "fix" these opportunistically in unrelated work; each is a deliberate rename with its own
cost, and a half-applied rename is worse than either name.
