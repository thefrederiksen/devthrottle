# Handoff - phase 2: the Gateway report record and delivery into the session

Read first: `STATE.md` (rulings 1-8 bind you), issue #2936, the phase 1 contract
`packages/client-core/src/devreports/CONTRACT.md` (the item and message shapes you store are defined
there - do not invent a second shape), and `PHASE-1-REPORT.md`. Open a child issue of #2936 first.

## What this phase makes true

An agent publishes a report for its own session; the session's owner can list and read it, send notes
and answers, and see each item's state; the session receives ONE prompt per send, after its current turn
ends, naming exactly what was pointed at and chosen; the agent can post a reply the owner sees. Nobody
else can read or answer it. It survives a Gateway restart.

## Design (Architect rulings for this phase)

**Storage.** Gateway database tables, tenant-scoped like every other store: a report (tenant, session id,
owner, a report key so re-publishing the same file makes a new VERSION of the same report, title, status
from the shape check, HTML bytes, byte hash, version, published and updated times); its items (the
client item id from the contract - the idempotency key - kind note or answer, the anchor and question
fields as the contract defines them, text, and delivery state); and its replies. A model change carries
its migration in the same pull request, for every database provider the Gateway supports. Migrations are
serialised across the fleet - check `docs/` and recent history for the current rule before generating one.

**Endpoints** (names are yours; keep them with the existing session routes):
- Publish - SESSION key only, and only for that session. Runs `DevReportShapeCheck`; a failing report is
  refused with every error in the body. Over 10 MB is refused with a clear error (ruling 6).
- Reply - session key only, for its own report.
- List and read for the session (the tool will use these in phase 5).
- List, read (metadata, items with state, replies) and raw HTML for the OWNER, by device key.
- Send - owner only. Takes the batch the note script emits. Idempotent on client item id: a resend of an
  item already accepted returns its current state and never delivers twice.

**Owner-only.** Every owner route resolves the session's owner (there is a `SessionOwnerCache`) and
refuses anyone else with 404, so a report's existence does not leak. New routes must also be registered
with whatever guards session-key routes (a route added to the endpoints file is NOT automatically in the
session key guard - check, and test a session key calling an owner route and the reverse).

**Delivery - the Gateway owns it (ruling 4).** Item states: `queued` (accepted), `held` (session is
working - delivered when the turn ends), `delivered`, `refused` (with a reason). A send while the session
is working is held, never interrupts; when the session's turn ends, everything held for it goes as ONE
prompt. Reuse the idle wait that fleet message delivery already has (`WaitForIdle`) rather than writing a
second one; but held items live in the database, so a Gateway restart while items are held still delivers
them. The prompt text is composed in ONE Gateway place (a fold with tests): it names the report and
version, then each note with its quoted text and labels (table row and column, SVG part label), then each
answer with the question and the chosen option and comment, and tells the agent how to reply. It is the
owner's turn, not agent traffic. The owner's words go through verbatim.

**Ended sessions (ruling 3).** Reading still works. Send is refused with "this session has ended".

## Also in this phase: the smallest `cc-dev-reports`

`open <file>` (publish; print the shape-check errors or where to view it) and `reply "<text>"`, so an agent
can drive phase 3 end to end. Follow the packaging of `tools/cc-devthrottle` and `docs/axi-standard.md`
(unknown flag is an error, nothing waits forever). The rest of the tool is phase 5.

## Proof required

- Unit tests for the prompt fold, the state machine, idempotent resend, and the size limit.
- Integration tests on a real Gateway: publish -> owner sends while the session is working -> item held,
  no prompt yet -> turn ends -> exactly one prompt with the cell's row and column and the chosen option;
  a second user gets 404 on read and send; a session key cannot call owner routes; send to an ended session
  is refused; held items survive a Gateway restart. Revert each guard and watch its test go red.
- If a test needs a live Director session, use slot 5 or higher via the Task Scheduler launch in the repo
  instructions. Never stop or restart any other Director.
- `.\scripts\test-local.ps1` green, and `-Parked` too because this touches the Gateway.
- A reviewer session from a different agent family before the pull request.

## Done means

A pull request to main, not merged, a report at `docs/missions/dev-reports/PHASE-2-REPORT.md` (built,
proven and how, not proven), everything pushed, and ONE line to the Architect.
