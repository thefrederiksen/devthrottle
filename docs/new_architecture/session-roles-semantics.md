# Session Roles - Semantics and Attention Policy

Status: DRAFT (design only, no code). Date: 2026-07-09.
Owner: f33d (roles + lifecycle). Consumed by: the single Gateway color/triage/attention
fold (owned by the streaming worktree, c9f9a8e3), the desktop-rail role badge, and the
spawn/stop recipe.

LIFECYCLE MISSION SCOPE (routing is by SCOPE, not by name - Mission-doc rule, 2026-07-09): the
Lifecycle mission covers SESSION lifecycle - session roles (Manager/Worker/Architect/Standalone),
session naming, and start/stop speed. It does NOT cover the app's RELEASE / installer /
self-update / deploy lifecycle - that is separate cross-cutting infrastructure assigned by
Soren/Architect, and this mission DECLINES + escalates such homeless work rather than absorbing it.

Design authority: the Architect's `mission-as-first-class-unit-of-work.md` is the SINGLE design
source for the Mission + role model (Architect = c3df, Lifecycle mission). This doc is subordinate
to it - the implementation-facing behavior contract for the roles; where the two differ, the
Architect's mission doc wins. Cross-linked, not duplicated.

This document is the BEHAVIOR CONTRACT. It defines what each role MEANS. It computes no
color, names no hex, and writes no label strings - the Gateway fold derives all of that
from the rules below, keyed on the raw facts. It adds no raw facts itself.

## The raw facts (owned by the fold/streaming tree, NOT added here)

- `SessionRole { Standalone, Manager, Worker, Architect }` - a session's primary role.
  Standalone/Manager/Worker are auto-derived; Architect is EXPLICITLY set (see "How roles are
  assigned").
- `NeedsManager` (bool) - a Worker's explicit "raise my hand while still working" escalation.

Everything below is how the fold should interpret those two facts (plus the existing
`ActivityState` and `ControllerSessionId`).

## Two layers of attention (do not conflate)

`EffectiveColor` is ONE value per session, shown to ALL viewers - so "a worker glows toward ITS
manager" (which only that manager should see) cannot live in EffectiveColor. Attention therefore
splits into two layers:

1. GLOBAL / human-facing color + triage - owned by the single Gateway fold. One value everyone
   sees. For a Worker with a live manager this is quiet/receded with red suppressed. This is
   where "workers never nag the human" is enforced.
2. VIEWER-RELATIVE / manager-facing highlight - owned by each client rail, computed locally from
   the facts the fold exposes (`SessionRole`, `ControllerSessionId`, `ActivityState`,
   `NeedsManager`). Only the viewer who IS a given worker's manager sees that worker highlighted.
   This is my (the rail's) job, not the fold's.

The fold exposes the facts; the rail draws the manager-facing highlight. Neither re-derives the
other's layer.

## Role definitions

### Manager (Queen / coordinator)
- The human-facing seat. Its Waiting / needs-input state surfaces to the HUMAN exactly as
  sessions do today (red allowed).
- Owns zero or more Workers - it is their `ControllerSessionId`.
- Learns worker status by READING / SUMMARIZING its workers (the hub summarize tool), not by
  being messaged "notice me."
- Permissions: the human's chosen preset and model. Not forced.

### Worker
- NEVER surfaces to the human. All of its attention routes to its Manager.
- Controlled: `ControllerSessionId` = its Manager. A Worker with no live Manager is an error
  state - see the orphan policy.
- Attention model (this is the whole point of the role). It has TWO layers - see the
  "Two layers of attention" section; do not conflate them:
  - GLOBAL / human-facing (the single EffectiveColor, owned by the Gateway fold): a Worker whose
    manager is alive is always quiet / receded, red SUPPRESSED, never NeedsYou-to-human -
    regardless of Waiting or NeedsManager. (If its manager is DEAD, red is allowed - the fold
    surfaces it to the human. See escape hatch.)
  - VIEWER-RELATIVE / manager-facing highlight (owned by MY rail/client, computed per viewer):
    a "this worker wants me" highlight appears only for the viewer who IS the worker's manager,
    when `role == Worker AND controllerSessionId == this viewer AND (ActivityState == Waiting OR
    NeedsManager == true)`. Waiting means finished-or-blocked (manager looks, then summarizes);
    NeedsManager is the stronger "raise my hand while still working" case.
  - Worker + still Working + no flag => nobody's highlight (normal running).
- Permissions: the USER's choice, per session, exactly as they set up any session. The framework
  does NOT force or police permission posture. If a user wants an autonomous worker that never
  stalls on a prompt, they set it up with `--dangerously-skip-permissions` - that is their choice,
  not a framework rule.
- Reporting: actual CONTENT (the specific question / result / info) is surfaced by the Manager
  reading/summarizing the worker where possible; a fleet message is used only for content that
  summarizing cannot surface. Messages are NEVER used for "notice me."
- May itself be a Manager to sub-workers (nesting via `ControllerSessionId`). A session tags one
  primary role but can be a Worker to its parent and a Manager to its children.

### Standalone (default - today's behavior, unchanged)
- Human-facing, exactly as a normal single session behaves now. Red allowed, human's permission
  choice, no manager. Manager/Worker are opt-in; the default fleet stays Standalone so nothing
  about ordinary use changes.

### Architect (explicit, design-only)
- A fourth role, set EXPLICITLY (the human asks, or a session self-declares "make yourself an
  architect", or it is spawned as one) - NEVER auto-derived, because architecture cannot be
  inferred from the spawn graph.
- Mandate: it ONLY (a) recommends to the Manager and (b) maintains the design / architecture docs
  and diagrams. It does NOT drive implementation and does NOT implement code.
- Attention (SETTLED, Soren 2026-09-06 - this overturns the 2026-07-09 amendment recorded below):
  the Architect is a HUMAN-FACING seat. It surfaces to the owner, it counts in the needs-you total,
  and the wingman reads it aloud - exactly like a Manager or a Standalone. In his words:

  > "parking the architect seat is wrong. the architect is always the session i talk to."
  > "no the architect should push to me it is what i talk to."

  SUPERSEDED (Soren, 2026-07-09), kept because deleting it would let the next person re-derive it:
  the July amendment said the Architect does NOT push needs-you or status to the human - that is the
  Manager's job - and that like a Worker it never surfaces, the only difference being that the human
  may PULL it into a design conversation he initiates. That reached this document in July and reached
  the code on 2026-09-03 (commit 2a8679007, #2667). The owner then watched it running and reversed
  it: the seat he addresses cannot be the seat that is silenced.
- Runs ALONGSIDE managers without blocking them. When it changes an architectural document it
  NOTIFIES the relevant Manager(s) so they incorporate the change - a coordination behavior toward
  the MANAGER, not a human-facing push.
- Does not manage workers by default; it is an independent design seat.
- UI display and how it ties into the roster: DEFERRED (Soren, 2026-07-09) - design later.

## How roles are assigned (explicit set, else auto-derived)

Resolution precedence: an EXPLICITLY-set role wins; otherwise the role auto-derives from the spawn
graph. Order: explicit -> Worker (has a controller) -> Manager (controls a live session) ->
Standalone (confirmed with the hub/fold tree 2026-07-09).

- EXPLICIT set (the human sets it, or a session self-declares): the only way to become ARCHITECT
  (it can never be derived), and available to override any role. An explicit role is sticky -
  auto-derivation never overwrites it.

- WORKER = a raw birth fact from the controller link. A session spawned BY another session (it
  carries a `ControllerSessionId` = its spawner) is automatically a Worker. The fleet-spawn path
  auto-sets `ControllerSessionId` to the spawner when the create carries the spawner's
  `CC_SESSION_ID`; a human/desktop-created session (no `CC_SESSION_ID`) is never auto-made a
  worker. Two REQUIRED guards:
  - explicit OPT-OUT (`--standalone` / no-controller) so a session can deliberately spawn a
    peer/human-facing session (e.g. a manager spawning a peer manager);
  - the handover / move-session path is EXCLUDED - its target is a continuation/peer of the source
    (pre-created with `toSessionId`), not a subordinate; auto-worker must not fire there, or the
    continuation's red would be wrongly suppressed toward the human.
- MANAGER vs STANDALONE = DERIVED from the spawn graph, not a birth fact. A non-worker,
  non-architect that controls >= 1 live session is a Manager; otherwise Standalone. Computed at the Gateway
  aggregation (it needs the fleet view). Rationale: Manager and Standalone are behaviorally
  identical toward the human (both surface, both may go red); the only difference is "has a live
  worker to supervise," so a session becomes a Manager the instant it has one and reverts to
  Standalone when the last exits.
- NESTING: a Worker that itself spawns sub-workers keeps the raw Worker label (the dominant
  attention fact - its red still routes to ITS manager) and is NOT relabeled Manager. It still
  RECEIVES the manager-facing highlight for its own children, because that highlight is
  viewer-relative (`row.ControllerSessionId == me`), independent of the label. Arbitrary nesting
  works; a mid-tree session simply shows the Worker badge, not a crown (acceptable for v1).

## Auto-naming and the IsAutoNamed flag

Auto-names are role-flavored: a Worker is named from its task/purpose; a Manager/Standalone from
its repo. An `IsAutoNamed` flag records that a name was auto-generated, so a later user (or self)
rename overrides it gracefully - an explicit name always wins and is never overwritten by a later
auto-name. This is the mechanism the naming policy in `fleet-identity-naming-and-addressing.md`
requires.

## One axis only: management role (job-type removed)

DECIDED (Soren, 2026-07-09): the fleet uses ONLY the management axis - `SessionRole`
{ Standalone, Manager, Worker }. There is NO separate job-type axis.

The CLI's dead `--type` option (Developer / Implementation / Discuss / Product / QA / Support) -
sent by `cc-devthrottle` but silently dropped server-side (the server has no such field) - is to
be REMOVED, not wired. It lives only in the CLI: `tools/cc-devthrottle/src/cli.py` (the `--type`
option) and `tools/cc-devthrottle/src/session_ops.py` (the `session_type` -> `body["type"]`
passthrough); nothing server-side reads it. Removal is coordinated with the roles worker because
those same CLI files carry the `controlled_by` spawn option it is editing.

## When does a Worker set NeedsManager? (the escalation policy - my lane)

Set `NeedsManager = true` when ALL of these hold:
- the worker is still actively Working (has NOT idled to Waiting), AND
- it has reached a point where continuing without a manager/human decision would be wrong or
  wasteful - an ambiguous requirement, a destructive or irreversible step, a real design fork,
  or an authorization/policy it does not hold, AND
- the worker cannot resolve the decision itself within its mandate.

Clear `NeedsManager = false` as soon as the blocking decision is answered, OR as soon as the
worker idles to Waiting (Waiting already carries the "look at me" cue, so the explicit flag is
redundant once idle).

Do NOT set it for: routine progress, "I finished" (idling to Waiting covers that), or anything
the worker can decide itself. The bar is: "I would be red-to-human if I were Standalone, and I
am still mid-turn so idling will not express it."

## AMENDED 2026-09-02 - the owner widened this, and it is now BUILT

Two things changed, and both are in the code and in
`docs/new_architecture/session-state.html` (the authority for the ladder):

1. **Suppression became PARKING.** A suppressed session used to be recoloured and left sitting in the
   middle of the roster. The owner's ruling - *"supervised still show up in Director and Cockpit,
   session should go to onhold when not working"* - makes it sink into the parked bucket instead. It
   is still fully visible and readable on every screen; it just stops being in his queue.
2. **The rule covers two kinds, not one.** It was Worker-only. It became every SUPERVISED session: a
   **Worker** (live supervisor) and a **scheduled run** (`OriginKind == "schedule"`).

   It briefly covered THREE. The **Architect** was added on 2026-09-03, implementing the 2026-07-09
   amendment which had reached this document and never reached the code; the owner removed it again
   on 2026-09-06 - "the architect is always the session i talk to".

   **SUPERSEDED 2026-09-13 - see the next section. No ROLE is supervised any more, and neither is a
   scheduled run.** Both of those inputs are stamped at birth, and asking about them is what let the
   fleet quieten sessions nobody was holding. The rule now asks one question about the present.

Unchanged, and load-bearing: **nothing outranks working**, and an **exited or crashed** session never
hides behind a snoozed label.

## AMENDED 2026-09-13 - the rule asks about the PRESENT, not about the session's type

The owner's ruling: *"I think it is wrong that somebody without a parent can automatically be put on
snooze because nobody knows they existed."*

**When a turn finishes, a session asks exactly one question: is there a supervisor alive right now?**

- **No** - it goes RED and asks the owner. Its seat and its origin have no vote.
- **Yes** - it stays grey, and it tells the supervisor itself, because the supervisor asked it to do
  something and getting back to them is part of doing it.

What this replaced, and why neither old arm survives as a special case:

- **The Worker seat** was a PROXY for live supervision, and a leaky one. The role is derived as
  "controlled AND the controller is alive", so it carried the right answer - until a seat was stamped
  by hand, which short-circuits the derivation entirely (`FleetRoleResolver` returns on
  `ExplicitRole` before it reaches the liveness check). An explicitly stamped Worker kept the seat,
  and the quiet, long after its supervisor had exited. The orphan escape hatch this document promised
  could not fire for exactly the sessions that needed it. Reading the liveness answer directly removes
  the proxy rather than patching it.
- **The schedule origin** silenced every cron session on the grounds that it escalates by email
  instead. A cron firing has no supervisor, so under the new rule it SURFACES. The owner settled the
  reversal on 2026-09-13: let them go red. An email path that nothing on the roster can attest to is
  not a supervisor.

Measured on the live fleet the morning this was written: six sessions grey and labelled "Snoozed"
with `snoozeUntil` empty and `onHold` false - nobody had snoozed any of them. Five of the six had
nobody who would ever come for them.

The answer is `SessionDto.HasLiveSupervisor`, resolved against the whole fleet by `FleetRoleResolver`
on every pass and never stamped at birth: a supervisor alive at spawn can be gone by three in the
morning. It defaults to false, so a fold path that forgets to resolve the fleet first surfaces the
session to the owner. The failure mode is a session asking for him when somebody had it - never
silence over a session nobody had.

### The other half: ownership is DECLARED at spawn, never inferred

The rule above reads an answer. This is where the answer comes from, and the two shipped together
because half of this is not worth having.

**Every session has exactly one owner, and it is either another SESSION or the USER.** There is no
third answer and there is no unowned session: no owner means the user. That is the whole model.

A session-initiated spawn must now SAY which (owner's ruling, 2026-09-13 - *"I think it is clear if
you have to state ownership intent when you start a session"*):

| Declaration | Owner | What happens when it stops |
|---|---|---|
| `--controlled-by self` | the spawning session | quiet on the roster; it reports back to its owner |
| `--controlled-by <id>` | that session | the same, to that session |
| `--standalone` (= `--controlled-by none`) | the USER | red, and in his queue |
| nothing, from inside a session | REFUSED | the spawn does not happen |
| nothing, from a person or a schedule | the USER | red, and in his queue |

**Why the refusal.** `cc-devthrottle session spawn` used to read:

```
elif cc_session: controller_session_id = cc_session
```

so a session that spawned without saying anything took ownership of what it spawned, because
`CC_SESSION_ID` happened to be set in its environment. Nobody chose it and nothing recorded that a
choice had been made. That default was the ONLY way work could stop being the user's: a person
spawning and a schedule firing both land on him already, so an agent spawning an agent is the single
path by which something leaves his queue. It is now the single path that has to declare itself.

**A human spawn declares nothing, and that is not an exemption.** There is nothing to state - a
session a person opens from the desktop, the Cockpit or the phone is the user's, and there is no
second candidate to tell it apart from. The requirement lands exactly where the ambiguity is.

**Breaking free is the point of `--standalone`.** A session started by another session - or by a
scheduled run - must be able to answer to the user instead of to whatever opened it. That flag has
existed for a long time; what is new is that the attention rule finally honours it, because the rule
now reads the live relationship rather than the seat, and a seat stamped at spawn could previously
overrule a session that had deliberately broken free.

3. **The wingman no longer enrols a supervised session into voice mode.** `VoiceModeAllSweep.Plan` resolves
   the roles itself and skips them - it has to resolve, because the push store nulls the role at ingest, so a
   check that merely read the field would answer "not supervised" for every session on the fleet and narrate
   exactly as before. Measured, not asserted: with the resolution removed and the skip left in, all three
   supervised cases fail.

   INCOMPLETE, and stated plainly: this stops NEW enrolment only. A session already marked as a voice session
   before this change stays marked and is still narrated, because the sweep is deliberately one-directional
   and never switches voice OFF. Un-enrolling the ones already on is not built.

4. **A supervised session can raise its hand to its supervisor** - issue #2662, BUILT. `NeedsManager` was
   declared on the wire from July with zero writers and zero readers; it now has a Gateway-owned registry, an
   endpoint (`POST /sessions/{sid}/needs-manager`), a fold stamp on both the roster and the display-push
   paths, and two commands: `cc-devthrottle session raise "<what I need>"` and `cc-devthrottle session
   workers`. The hand LOWERS ITSELF when the session stops working - derived, not swept - because stopping
   already cues the supervisor and a latch somebody had to clear is the one still up next week. The reason is
   required: a hand with no words is the "notice me" ping this design rejects.

   The owner never sees it. It is not read by the colour, the label or the triage bucket, and a test asserts a
   raised hand changes none of the three - at both ends of the ladder.

STILL NOT BUILT, and this document should not be read as claiming otherwise: no client draws the
viewer-relative manager-facing HIGHLIGHT described below - no client knows its own session id, so none can
ask "am I this row's supervisor?". The manager-facing surface built for #2662 is the COMMAND LINE
(`session workers`), which is where managers actually live on this fleet. A rail highlight remains open.

## The supervision table - MACHINE-CHECKED, and the one to change

This table is the written half of `SessionOrdering.IsSupervised`, and it is not decoration.
`SupervisionRuleMatchesTheDesignDocumentTests` (in `CcDirector.Gateway.UnitTests`) parses the rows
below and fails when the document and the code disagree about any case, in either direction. It also
fails when a row is missing: the table must name every combination of the four roles, the two
origins and the two supervisor states, so it cannot pass by saying nothing.

**The seat and the origin columns are here precisely BECAUSE they no longer decide anything.** A
two-row table would state the rule correctly and prove nothing about the thing that went wrong: for
two years the answer turned on a category. Sixteen rows, with the verdict constant down each block,
is the claim itself - *neither the seat nor the origin changes the answer* - written where the
machine can check it. A guard has fired here the moment anyone makes a role matter again.

**Why the guard exists.** The 2026-07-09 amendment sat in this document, unimplemented, for two
months. Every test of the day asserted the SHIPPED behaviour in the present tense, so a document
saying the opposite could not make anything go red - the divergence was invisible to the machine and
was only ever going to be found by a person reading both halves side by side. Writing the rule down
twice, once here and once in code, with nothing tying the two together, is what cost the two months.

The role is the one the Gateway RESOLVED across the whole fleet, not the one a Director pushed - and
so is the live-supervisor answer. Both are recomputed on every pass by `FleetRoleResolver`; neither
is a birth fact. "Live supervisor = yes" means this session is controlled, names its supervisor, and
that supervisor is present and not Exited in the fleet the pass resolved from.

<!-- SUPERVISION-TABLE-BEGIN -->

| Resolved role | Origin kind | Live supervisor | Verdict |
|---|---|---|---|
| Standalone | (none) | no | HUMAN-FACING |
| Manager | (none) | no | HUMAN-FACING |
| Architect | (none) | no | HUMAN-FACING |
| Worker | (none) | no | HUMAN-FACING |
| Standalone | schedule | no | HUMAN-FACING |
| Manager | schedule | no | HUMAN-FACING |
| Architect | schedule | no | HUMAN-FACING |
| Worker | schedule | no | HUMAN-FACING |
| Standalone | (none) | yes | SUPERVISED |
| Manager | (none) | yes | SUPERVISED |
| Architect | (none) | yes | SUPERVISED |
| Worker | (none) | yes | SUPERVISED |
| Standalone | schedule | yes | SUPERVISED |
| Manager | schedule | yes | SUPERVISED |
| Architect | schedule | yes | SUPERVISED |
| Worker | schedule | yes | SUPERVISED |

<!-- SUPERVISION-TABLE-END -->

HUMAN-FACING means all three of: the row may go red and reach the owner, it counts in the needs-you
total, and the wingman reads it aloud. SUPERVISED means it parks as "Snoozed" when it stops working -
still fully visible and readable on every screen, just out of his queue - and the wingman leaves it
alone. Nothing outranks WORKING: a supervised session mid-turn is still blue, and an exited or
crashed one never hides behind a snoozed label.

The verdict is constant down each block: every row with no live supervisor is HUMAN-FACING and every
row with one is SUPERVISED, whatever the seat and whatever started it. A scheduled run has no
supervisor, so it reads off the "no" block and goes red like anything else nobody is holding.

## Attention routing table

| Live supervisor | Signal | Layer | Who sees it |
|---|---|---|---|
| No (never had one) | Waiting / needs-perm | global color (fold) | human, red allowed |
| No (supervisor died) | Waiting / needs-perm | global color (fold) | human, red allowed |
| No (scheduled run) | Waiting / needs-perm | global color (fold) | human, red allowed (Soren, 2026-09-13: let them go red) |
| Yes | Waiting / blocked | global color (fold) | everyone: quiet/receded, red suppressed |
| Yes | Waiting OR NeedsManager | supervisor-facing highlight (my rail) | ONLY its supervisor, never human |
| Any | Working | - | everyone: blue. Nothing outranks working. |
| Any | Exited / Crashed | global color (fold) | human. Never hidden behind a snoozed label. |

The seat does not appear in this table any more, and that is the change. A Worker whose supervisor is
alive is held; the same Worker an hour after its supervisor died is the owner's, and nothing has to
notice or repair that - the answer is recomputed every pass.

## Attention hard rules (settled 2026-07-09, Rule 1 amended 2026-09-06; the Mission doc is authoritative)

Mirror of the "Attention hard rules" section in the Architect's `mission-as-first-class-unit-of-work.md`
(the authority). Summarized here for the roles implementation:

- Rule 1 - The human-facing PUSH channel is MANAGER + STANDALONE + ARCHITECT (settled 2026-09-06). A
  WORKER never surfaces to the human in NORMAL operation (structural - the fold suppresses worker
  red); the ONE deliberate exception is Rule 3 (dead manager + blocked worker), because an exception
  always involves the operator. An ARCHITECT pushes exactly like a Manager - it is the seat the owner
  addresses, so it surfaces, it counts in the needs-you total, and the wingman reads it aloud.
  *Amended 2026-07-09 to put the Architect on the worker's side of this line, built 2026-09-03, and
  reversed by the owner on 2026-09-06 once he saw it running. Recorded rather than overwritten,
  because the round trip is the interesting part.*
- Rule 2 - MANAGER / Standalone / Architect are NEVER auto-muted BY THEIR SEAT. A manager raises
  "need you" by its OWN judgment - to get a decision OR simply to report an update - on the SAME
  single "need you". Involving the operator when the manager judges it worthwhile is the point of a
  manager, not clutter. *The one thing that quietens any of the three is ORIGIN, not seat: a run a
  SCHEDULE started is supervised whatever seat it occupies, because nobody was at a keyboard for it.
  See the supervision table above, which states all eight cases.*
- Rule 2a - NO NEW STATES. "Need you" covers both "I need a decision" and "here is an update".
  Auto-hold vs stay is QUEUE ROUTING over the existing Waiting + pending-ask signal, not a new
  user-facing state.
- AUTO-HOLD is for WORKERS only (as an automatic action): a done/idle worker drops from its
  manager's highlight and any queue. For a manager, On Hold is a post-update TIDY only - after the
  operator has been told and nothing is left to act on (operator-driven by default, or an auto
  "nothing-left" cleanup ONLY once the operator has actually seen the update); NEVER a muzzle on an
  un-surfaced update or a pending ask. A manager's DELIBERATE surface - a chosen question OR a
  chosen report - keeps it in the operator's queue until the operator has SEEN it; only a manager
  that went idle having surfaced NOTHING is eligible for the nothing-left tidy.
- Rule 3 - ORPHAN (dead manager = an EXCEPTION; Soren, 2026-07-09, veto of the earlier
  reassign-to-Hub proposal). Soren's rule for exceptions: ALWAYS involve the operator.
  - Worker DONE -> auto-hold (no exception).
  - Worker BLOCKED -> ESCALATE TO THE OPERATOR with a CLEAR EXPLANATION surfaced by the fold/brain
    ("the Manager <name> you were working for is gone; here is the task; here is where you are
    stuck"); the operator cleans up (resume / reassign / hand off / stop).
  - Do NOT auto-reassign to a Hub Manager - that masks the problem and needs always-on machinery.
  - This is the ONE deliberate exception to Rule 1: Rule 1 governs NORMAL operation; a dead manager
    is not normal, so it reaches the operator.
- DESIGN GOAL (minimize orphaning): managers should be durable/resumable, and a manager shutting
  down CLEANLY deals with its workers first (finish / hand off / stop) - so orphaning only ever
  comes from a true crash.

Ownership: the fold + brain (c9f9a8e3) implement worker suppression, the auto-hold split, and the
dead-manager-blocked escalation surface. This doc mirrors; the Mission doc governs.

## Permissions and safety posture (the framework does NOT police)

DECIDED (Soren, 2026-07-09): permission/safety posture is the USER's choice, per session - exactly
how they set up any Claude Code session. The framework does NOT force, gate, or nag about it. A
user may run a session wide-open (`--dangerously-skip-permissions`) or locked-down; that is their
call, for every role including Worker. It is not our job to police it (we are not a seatbelt
chime).

## My rail: badge + manager-facing highlight (Layer 2 design)

Both pieces are mine; both read ONLY the four exposed facts (`SessionRole`,
`ControllerSessionId`, `ActivityState`, `NeedsManager`); neither computes global color.

- Role badge (non-color, one per row): BUILT as small grey letters - Manager = M, Worker = W,
  Architect = A, Standalone = none (no clutter). Non-color so it never competes with the status
  color; tooltip names the role. Marker STYLE = grey letters, FINAL (Soren, 2026-07-09;
  crown/gear/triangle icons dropped as over-design). The rail stays FLAT + hand-ordered, one marker per row, NO
  grouping/indent (honors never-auto-reorder-the-rail; the rail answers "which session do I
  click"). Relationships are NOT shown on the rail - that is the separate Mission MAP (container
  boxes; Architect+Manager peers on top, Workers under the Manager, nested sub-Mission boxes,
  status color on nodes, Mission-to-Mission arrows), defined in the Architect's Mission doc.
- Manager-facing highlight (viewer-relative): for the viewer whose OWN session id ==
  a row's `ControllerSessionId`, highlight that worker row when
  (`ActivityState == Waiting` OR `NeedsManager == true`). Waiting = subtle ("look when you can");
  NeedsManager = stronger ("hand raised"). Never shown to non-manager viewers; never a human red.
- Seams: desktop = `SessionViewModel` + `MainWindow.axaml` (a new binding from my session id +
  the row's facts); mobile/cockpit = the same rule in `packages/client-core` so every client
  agrees without re-deriving color.

## Validated against prior art (2026-07-09 survey)

Survey of CrewAI, AutoGen, LangGraph, OpenAI Swarm / Agents SDK, MetaGPT, Anthropic
orchestrator-worker + Claude Code subagents, claude-flow Queen/worker, and cmux. Findings that
confirm or sharpen this design:

- Enforce in the TRANSPORT, not the prompt. The only system that reliably keeps workers from
  interrupting the human (Claude Code) does it by DENYING the tool, not by instructing. So our
  worker red-suppression must stay structural (the fold) and never be a prompt request - a worker
  has no channel to the human by construction.
- Report UP, never hand OFF. Swarm's clean split: agent-as-tool returns a result to the caller
  who keeps control, vs a handoff that transfers the human-facing turn away. A worker must be
  agent-as-tool - it reports to its manager and never inherits the human conversation.
- Prefer manager PULL over worker PUSH. AutoGen's route-everything-through-the-manager broadcast
  is quadratic chatter at scale. Our "manager reads/summarizes the worker" (pull) is the right
  shape; messages stay for content only, never for "notice me".
- Give workers a status, not a raw dump. Recommended worker->manager shape is a status
  { done | blocked | needs-decision }. Ours already encodes it: Waiting = done-or-blocked
  (manager summarizes to learn which), NeedsManager = needs-decision.
- Blocked workers must be PERSISTED + resumable (not answer-in-RAM), resume idempotent.
  DevThrottle already has session persistence + crash journals; the orphan/escape-hatch path
  relies on it.
- Make role VISIBLE and route worker attention to the MANAGER's view, not the human roster. cmux
  (no role model) proves the failure mode: N panes all yelling at the human. Our two-layer split
  (human roster = managers/standalone only; worker attention = manager-facing highlight) is
  exactly the gap cmux leaves and the model Claude Code validates.

Manager discipline (pitfalls to design against): give each worker an explicit objective, output
format, and task boundaries at spawn (prevents the documented duplicate-work failure); only spawn
workers for high-value tasks (multi-agent runs cost roughly 15x the tokens of a single chat).

## What this doc deliberately does NOT do

- No color, no hex, no label strings - the Gateway fold owns all of that, keyed on these rules.
- No per-app color/state logic (the apps no longer decide color; that seam was deleted).
- Does not add or edit the raw facts - `SessionRole` and `NeedsManager` live on
  `Session` -> `SessionDto` -> `NewSessionRequest` in the streaming/fold tree.

## Open items for Soren

- (RESOLVED 2026-07-09) Permission/safety posture is the user's per-session choice; the framework
  does not police it. See "Permissions and safety posture" above.
- (RESOLVED 2026-07-09) Architect IS a fourth role - explicit, human-facing, design-only. See the
  Architect definition and "How roles are assigned" above. Open sub-item: UI display + how it ties
  into the roster (deferred by Soren; my lane, later).
- HUMAN-TO-FLEET ROUTING (raised 2026-07-09): Soren reports "sometimes I'm talking to the worker
  and sometimes the manager." Hypothesis: there is no single designated top seat, so the human
  juggles multiple sessions with no clear hierarchy. Intended fix = the HUB MANAGER (one top seat)
  plus the rule "the human addresses Managers/Standalone; Workers are reached through their
  manager." To investigate and design.

Resolved: orphan policy = promote-to-human (default); roles = Standalone / Manager / Worker /
Architect (ratified; Architect is explicit + design-only); the job-type axis is removed.
