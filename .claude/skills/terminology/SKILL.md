---
name: terminology
description: The words DevThrottle uses and what each one means - session, mission, task, workflow, run, team, the five roles, owner, parent, participation, snooze, agent, Factory inbox, Your inbox, Secretary, the Boss decision words. Use when writing an issue, brief, commit message, document or code comment, when unsure what to call something, or when you meet an older name in the code. Triggers on "/terminology", "what do we call", "what is the right word", "glossary", "vocabulary", "naming", "is it hold or snooze", "controller or supervisor".
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

A **Fleet Manager** is a product name, not a role: the one session of an account that the owner talks
to. It sits above everything: it talks to the bosses of the factories and to the sessions that belong
to no factory. There is no Factory Manager; the Fleet Manager replaced it on 1 November 2026.

An **Inspector** is a Reviewer carrying one extra requirement: it must be from a DIFFERENT AGENT
FAMILY to the people who did the work - if the fleet is Claude Code, the Inspector is Codex. Keep
using the word; it is not a sixth role, because the constraint is already proved by the agent kind
recorded on every session and every run participant.

## The factories

- **Factory** - one business or one job that runs on its own, toward a goal the owner set, with its own
  seats, schedules, memory and activity record. The Factories screen lists them.
- **Seat** - one registered agent of a factory, named by its role (Scout, Sender, Mail Desk). A seat is
  not a session: a session is one run of a seat.
- **Boss** - the seat that runs a factory: it decides, hires the other seats, posts the goal number, and
  answers the owner's talk. One per factory, or none. The word is Boss, never CEO and never head: not
  every factory is a company, but every factory has a boss (the owner, 8 October 2026). **The boss has no
  name of its own.** It is "the boss of Website Business"; the product says "Talk to the boss" and "Latest
  from the boss", and a manifest that names the boss seat anything but Boss is refused. Its role word is
  Boss as well, and a manifest that gives the boss any other role word is refused in the same way.
  **A Boss never does the work** (the owner, 1 November 2026). Its main job is to delegate and to make sure the
  right resources are in place. **The Boss is a delegator** (the owner, 4 November 2026): it may do its own
  reviews and strategy, and everything else it hands on in one of two ways - it adds a **to-do** to be done
  later, or it starts a session to do it now - and it never does the work itself. A session it starts comes
  back to it when finished, and the Boss stays available to the owner meanwhile. **The Boss judges**: it
  decides whether what it is given is good or bad, and pushes back on the owner when the owner asks for
  something that is a mistake; once the owner has heard it and still says do it, it gets done. The Gateway gives every Boss this conduct on every start, so no seat
  file repeats it.
  A Boss hands work that belongs to another seat, or needs its grants, to that seat
  (`cc-devthrottle factory seat start --seat <seat> --name "<what>" --prompt "<the job>"`). The seat runs as the
  published definition says, owned by the Boss, and hands back when finished. A Boss chooses nothing else about it,
  cannot start a Boss seat, and a session that inherited the Boss's seat cannot start one at all. Each start is
  recorded on the factory's activity record.
- **Who makes a factory** - the owner, through a session. "Factories are created by agents as long as they
  have user approval" (the owner, 16 October 2026). A session in no factory that the owner tells what to
  make may create the factory from a folder (`cc-devthrottle factory definition create <folder>`), save
  drafts of it, see what changes (`factory definition diff`), publish and restore it; the owner's word in
  that session is the approval, and the Gateway asks for nothing more. THE BOSS IS THE BOSS OF ITS
  FACTORY (the owner, 21 October 2026): a factory's Boss session publishes and restores any change to its
  own factory on its own authority, and a change to the Boss's OWN seat or its authority (its seat file, which
  seat factory.yaml names as the boss, a grant that includes it, a rule set it names) needs the owner's approval, passed verbatim (`factory definition publish <factory> --version N --owner-approved "<the owner's words>"`).
  Every other session of a factory saves drafts only, and no factory's session creates a factory. Every
  publish records who did it - the person, or the session's id and name - and the owner's words when given.

## The inboxes

Two inboxes replace email between the owner and the fleet (the owner, 4 November 2026). On screen both sit
under ONE Inbox entry, with two tabs. They are not alerts (an alert asks for an answer now) and not dev
reports (a dev report is a document with questions).

- **Factory inbox** - the messages left IN a factory, for its Boss: typed or spoken by the owner, from the
  phone or the Cockpit, in seconds, without waiting for anything to start. Nobody but the factory reads them.
  Each message is logged and shows its trail: **Sent** (the Gateway has it), **Received** (the Secretary has
  filed it), **With the Boss** (handed over in a bundle), then the Boss's decision. Every factory with a Boss
  has one. Never call it a factory mailbox or a factory queue.
- **Your inbox** - the messages TO the owner, from the fleet: a Boss's Declined, Cannot be done and Pushed
  back, each factory's morning summary, and any session that has something to tell the owner. Kind of like
  email, checked in the morning, on the phone and on the Cockpit; each message is Unread, Read or Archived.
  "Email me" and "tell me" mean Your inbox; a real email goes out only when the owner names someone else or
  says "send me an email". Always "Your inbox", never "the user inbox" or "notifications".
- **Secretary** - the seat that deals with a Factory inbox, so the Boss does not have to be started for every
  message. The product gives one to every factory with a Boss; nobody defines it. It reads the new messages,
  merges duplicates, attaches what each is about, marks each Received, notes Good or Bad for the Boss, and
  hands ONE bundle to the Boss. It never judges, never decides and never starts work sessions, and it closes
  itself when the inbox is empty. The Secretary is a seat of the factory, not a role: the five roles above
  are unchanged.

**The decision words** - what a Boss decides about each message in a bundle. One word each, and only these:

- **Delegated** - handed to a session the Boss started to do it now (the session is named with it).
- **To do** - added to the factory's to-do list, to be done later.
- **Noted** - nothing to do; kept in the factory's memory.
- **Declined** - should not be done; the Boss says why.
- **Cannot be done** - cannot be done; the Boss says why.
- **Pushed back** - the Boss judged the request a mistake and says so to the owner, and why.

Only Declined, Cannot be done and Pushed back also write a message into Your inbox. Delegated, To do and
Noted are quiet: they show on the factory's thread and in its factory summary, and the owner is not told.

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
the shape it makes. `message send all` reaches only the sessions you started yourself - your
direct workers - never the rest of your mission and never the sessions sharing your checkout
(Message Load mission, 16 September 2026). Say workers there.

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
| Fleet Manager | "Factory Manager" in records and documents older than 1 November 2026 |
| Snooze | `HoldState`, `DeferredHold`, "parked" |
| Participation | "seat" |
| Team | `Crew`, `CrewSummary`, `crewAge`, `crew-*` in the stylesheets |
| Workers (in `message send all`) | "team" in older broadcast prose and warnings |
| Reviewer | nothing - the role does not exist yet |
| Boss | `CEO`, `ceoSeat`, `CeoSeat`, "head" in prose and code older than 8 October 2026 |

Do not "fix" these opportunistically in unrelated work; each is a deliberate rename with its own
cost, and a half-applied rename is worse than either name.
