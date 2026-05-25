# Personal Assistant - Architecture and Implementation Plan

Status: PLAN (brainstormed 2026-05-24, not yet started)
Scope: this doc/issue covers the ORCHESTRATION layer only (gateway queue hub, comm-queue
centralization, voice/email channels) that runs in cc-director.
Name: TBD (front-door umbrella name still open; skill referred to here as `personal-assistant`)

## Repository split (important)

The **personal assistant brain** - the `PA/` Karpathy wiki, the `personal-assistant`
skill, the `pa` library, and the ingestion of Soren's data - is implemented in the
**private repo** (`thefrederiksen/private`), tracked in **private issue #1**, with the
plan at `private/PA/docs/IMPLEMENTATION.md`. It is being built brain-first and proven by
talking to a session directly, with no infrastructure.

This cc-director doc and issue #139 cover only the **orchestration** that wraps that
brain once it works: the gateway queue hub, the centralized communication queue, and
the voice/email delivery channels. Where this plan references the wiki/skill, treat the
private repo as the source of truth for those pieces.

## Goal

A personal assistant that handles Soren's three life domains, consulting (Center
Consulting), mindzie (work, CTO), and health, reachable two ways:

- By voice, from the CC Director Client app.
- By email, at a dedicated mailbox the gateway watches.

It triages and (eventually) handles email, listens to voice recordings and
dictations, answers questions, and validates programming tasks. It must be able
to answer back.

## Guiding principle: the gateway is dumb about meaning

The gateway has no language model and must not try to be smart. It is the nervous
system: it senses inbound events, queues them, hands them to a session at a clean
boundary, and routes answers back out. All judgment lives in director sessions
(the brain). The gateway is mechanical only: watch, dedup, queue, dispatch, route.

## What already exists (build on this, do not rebuild)

- `private/assistant` - identity dispatcher (personal/consulting/work/swim),
  task playbooks, and `conventions.md` (approval/safety rules). The right shape
  for a front door.
- `MyHealthHelper` ("Jarvis") - the health brain, with goals/routines/tracking data.
- Coaches / LOS - six-domain coaching framework + `/coach`, `/assistant` skills.
- `cc-consult` / `CenterConsulting` - consulting tooling. mindzie repos - work.
- Communication Manager queue - drafts -> approval -> send. Today a local SQLite at
  `%LOCALAPPDATA%\cc-director\config\comm-queue` (14 pending, 100 posted at time of
  writing). Reached by the `cc-comm-queue` CLI, the Avalonia Comm Manager UI, and
  `CommQueueScheduler`.
- Channels: `cc-gmail`/`cc-outlook` (read), phone-recorder `/ingest` + dictation
  (voice in), CC Director Client (talk to a session), gateway session-spawn + `/chat`.

The pieces of an assistant exist but were never connected. This plan is the
connecting layer, not a new silo.

## Architecture

### The brain: one warm session + sub-agents

- A single always-warm director session runs the `personal-assistant` skill out of
  the `private` repo. Always warm = no per-request boot latency (good for voice).
- Each request is handled either inline (quick) or fanned out to a sub-agent.
  Sub-agents run in isolated context and return only a summary, so the parent
  session stays clean no matter how many requests flow through.
- The parent is a single-threaded coordinator: it can fan out a batch in parallel,
  but processes in waves. Fine for the expected volume (~100/day, rarely
  simultaneous). Growing to 2-3 sessions later is just adding consumers to the
  queue, not a redesign.

### Shared memory: an LLM wiki (Karpathy's LLM Wiki pattern)

This implements Andrej Karpathy's LLM Wiki (gist 442a6bf...): an LLM-owned,
interlinked markdown knowledge base that compounds over time, queried by the agent
rather than browsed by hand. Lives in a `PA/` subdirectory of the `private` repo.

Three layers (Karpathy's structure):
- **Raw sources (immutable):** the actual emails, transcripts/recordings, and the
  existing domain repos (MyHealthHelper data, cc-consult, mindzie). The assistant
  reads these and never rewrites them. They are the source of truth.
- **The wiki (`PA/`, LLM-owned):** markdown pages the assistant creates and maintains.
- **The schema (`PA/CLAUDE.md`):** the document that TEACHES the skill how the wiki is
  structured, the page conventions, and the ingest/query/lint workflows. This is the
  "teach the skill how to create wikis" piece.

Layout:

```
PA/
  CLAUDE.md        # schema: conventions + workflows that teach the skill
  index.md         # catalog of every page: one-line summary + category, updated each ingest
  log.md           # append-only history: "## [YYYY-MM-DD] action | title" (grep-friendly)
  people/          # entity pages (contacts, clients, colleagues)
  topics/          # concept pages (recurring decisions, how-to-handle-X, domain knowledge)
  sources/         # per-source summaries (an email thread, a recording, a dictation)
  domains/         # consulting.md, mindzie.md, health.md (or pointers to existing brains)
```

Three workflows (Karpathy's):
- **Ingest:** read a new source -> write a `sources/` summary -> update `index.md` ->
  revise the affected `people/` and `topics/` pages -> append a `log.md` entry -> commit.
- **Query:** search `index.md` + relevant pages (plus the vault semantic index) ->
  synthesize an answer with citations -> file a valuable answer back into the wiki.
- **Lint:** periodic health-check for contradictions, stale claims, orphan pages,
  missing cross-references, and gaps; propose fixes and new questions.

Discipline:
- Git commit on every ingest/query-filed/lint operation (full history of how
  understanding evolved). All writes go through the `pa` library (single safe writer).
- Wiki pages are indexed into the vault (`cc-vault docs`) for semantic retrieval,
  while the markdown stays the human-auditable source of truth. (Obsidian can view it.)
- Volatile facts carry a last-verified note and are re-verified rather than trusted
  blindly; durable how-to knowledge is trusted.
- The wiki is what lets the warm session survive context compaction (memory lives in
  git, not the session) and keeps sub-agents consistent. It is foundational.

### The gateway: a queue hub with two lanes

The gateway owns all queuing. Two symmetric lanes:

- Inbound work lane: events going TO sessions (voice utterances, email-triage
  tasks, scheduled scans).
- Outbound approval lane: drafts coming TO Soren for approval (the centralized
  Communication Manager queue).

Inbound work queue:
- Durable store on the gateway (sqlite or files) so a restart loses nothing.
- Priority lanes: interactive voice > direct asks > background scans.
- Idempotency keys (email id, transcript id) so redelivery never double-processes.
- Dispatch = the gateway watching session activity state (already detected for the
  Wingman). When the session is Idle and the queue is non-empty, push the
  top-priority item. When Working, push nothing. Work is only ever handed off at a
  clean boundary, so a running sub-agent is never interrupted. Background jobs are
  chunked small (triage 5 emails, not 50) so the idle boundary comes around fast
  and voice rarely waits more than one short item.

### Inbound channels

- Voice: CC Director Client gains an "Assistant" target. Tapping it routes the
  voice round-trip (utterance -> transcribe -> /chat -> native TTS) to the warm
  assistant session via the gateway, as a top-priority queue item.
- Email: the gateway polls the mailbox (read-only via cc-gmail/cc-outlook) and
  enqueues small triage items. The gateway does NOT judge what matters (no model);
  it just detects new mail and enqueues. The session does the reviewing.
- Dictation/recordings: new transcripts from the `/ingest` pipeline become inbound
  items the assistant can act on.

### Tooling split

- Gateway (thin, mechanical, no model): durable priority queue + idempotency,
  channel watchers (mailbox poll, voice endpoint, cron), idle-watcher dispatcher,
  output router, and session supervision (keep the assistant session alive).
- Session: the `personal-assistant` skill (persona + orchestration + `conventions.md`
  safety rules) in the `private` repo, plus a `pa` Python library/CLI that is the
  single safe writer for the deterministic plumbing: `wiki search/get/put`,
  `inbox fetch`, `draft queue`, `answer log`. Routing every wiki/git write through
  `pa` (with commit-retry/serialization) is what makes concurrent sub-agents safe
  and the discipline testable.

### Centralized Communication Queue (multi-machine)

- The gateway owns the one comm-queue database and exposes an API: enqueue, list,
  approve, reject.
- Directors lose direct DB access; that code is removed. Every client (any director
  on the network, the desktop Comm Manager UI, the `cc-comm-queue` CLI, the assistant
  sessions) goes through the gateway API. Any director anywhere can queue and approve.
- Two-path sending, executed on the always-on gateway, only ever on Approved items:
  - Deterministic sends: plain Python in the gateway sends directly (cc-gmail/cc-outlook).
  - Judgment-needed sends: the gateway dispatches to a session (the assistant) via
    the same queue/dispatch mechanism.
- Approval gate preserved: nothing sends without approval. Proposed exemption
  (Soren's call): a self-addressed digest to his own mailbox may auto-send, since a
  reply to himself carries none of the wrong-recipient risk the rule exists to prevent.

## Open decisions (not blockers)

- Front-door name (umbrella). Keep "Jarvis" as the health voice; give the front door
  its own name. "Life" matches existing LOS vocabulary.
- Which mailbox/domain for the assistant (recommend a personal, identity-neutral one).
- Email cadence (recommend a few scheduled scans/day, not constant polling).
- Digest delivery (recommend a single self-addressed summary, readable or spoken).
- Self-reply exemption to the no-direct-send rule (confirm yes/no).

## Phases (data migration is LAST on purpose)

Build the new system empty, prove each piece, then migrate live data in at the end
so development never risks the real data.

### Phase 1 - Gateway inbound queue hub
- Durable priority queue on the gateway with idempotency keys.
- Enqueue / claim / complete API.
- Idle-watcher dispatcher using existing session activity state (push top item only
  when the target session is Idle).

### Phase 2 - Gateway-owned Communication Queue API
- Add the gateway comm-queue API (enqueue/list/approve/reject) over a dev/empty DB.
- Repoint all clients (cc-comm-queue CLI, desktop Comm Manager UI, sessions) to the API.
- Remove directors' direct-DB-access code.
- Two-path sending scaffold (Python deterministic + session-assisted), Approved-only.
- Do NOT migrate the live 14 pending / 100 history yet (that is Phase 6).

### Phase 3 - Assistant brain (DELEGATED to the private repo)
- The brain (the `personal-assistant` skill, the `pa` library, the `PA/` Karpathy wiki,
  and the data ingestion) is built in the **private repo** under **private issue #1** -
  see `private/PA/docs/IMPLEMENTATION.md`. Not implemented here.
- The only orchestration piece that remains in cc-director: the gateway keeps one
  assistant session warm (spawn / supervise / restart) once the brain exists.

### Phase 4 - Email triage (the urgent slice)
- Gateway mailbox watcher (read-only) enqueues small triage chunks; cron a few/day.
- Assistant triages: categorize (urgent / needs-reply / FYI / noise), summarize,
  draft replies into the approval queue, produce a digest.
- Digest delivery per the self-reply decision.

### Phase 5 - Voice integration
- CC Director Client "Assistant" target routes the voice round-trip to the warm
  assistant session via the gateway, top-priority lane (no boot latency).

### Phase 6 - Data migration (LAST)
Migrate ALL existing data into the new system:
- Communication Manager: the live 14 pending + 100 history into the gateway-owned DB.
- Assistant knowledge into the wiki/vault: `MyHealthHelper` (Jarvis) data, coaches/LOS
  context, `private/assistant` identities + task playbooks, and relevant vault content.
- Verify counts and integrity after migration; nothing lost.

## Non-goals (for now)
- Unbounded concurrent assistants. Start with one session; the queue makes scaling a dial.
- Auth/security gating on the gateway APIs (single-user tailnet).
- Hands-free voice (wake word) - separate effort.
