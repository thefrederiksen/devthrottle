# Agent Mesh - Inter-Session Agent Communication

**Status:** DESIGN ONLY - nothing built. This document captures the design discussion
so the conventions can be reviewed before any code is written.

**Date:** 2026-06-06

---

## 1. The Idea

Every Claude Code session running under a Director has Bash, and Bash can `curl` the
Gateway. The Gateway already federates every Director on every machine over tailscale.
Therefore any agent can already:

- discover the whole fleet (`GET /sessions`)
- create a session on ANY machine (`POST /directors/{id}/sessions`)
- send a prompt to another agent (`POST /sessions/{sid}/prompt`)
- read what that agent did last turn (`GET /sessions/{sid}/summary`, `/turns`)
- watch its terminal if needed (`GET /sessions/{sid}/buffer`)

This means agents can talk to each other - on the same machine or across machines -
with zero new infrastructure. What is missing is not transport; it is **conventions**:
how a master knows a worker is done, how loops terminate, how sessions are addressed,
and which sessions are allowed to receive agent-issued prompts at all.

### Motivating scenarios

1. **QA loop (context isolation).** A QA agent that never fixes anything itself - it
   tests, files findings as prompts into a separate implementation agent's session,
   waits, re-tests, loops. The QA agent's context stays clean of implementation
   details, which is a real review-quality multiplier. Unlike the built-in Agent
   tool's sub-agents, both sides are persistent (context survives across rounds) and
   observable (both appear in the cockpit rail with briefs and terminals).

2. **Cross-machine reach (the Mac scenario).** A Windows Director's agent creates a
   session on the Mac's Director, prompts it "build and run the test suite, report
   failures", and reads the summary back. The Mac becomes a remote pair of hands.
   Same trick: platform-specific repros, parallel platform builds, soak tests on the
   laptop while desktop agents keep coding.

3. **Fanout with a collector.** `POST /fanout` exists today (one prompt to N
   sessions). The natural next step is a master that assigns shards and merges
   results.

---

## 2. What Already Exists (the transport layer)

All verified against `GatewayEndpoints.cs` / `ControlEndpoints.cs` as of 2026-06-06.

| Need | Endpoint | Notes |
|------|----------|-------|
| Fleet discovery | `GET /sessions` (Gateway) | filter by director, repo, state |
| Remote session create | `POST /directors/{id}/sessions` | the cross-machine primitive |
| Talk to an agent | `POST /sessions/{sid}/prompt` | see prompt-parking gotcha, sec. 4.4 |
| Read a turn result | `GET /sessions/{sid}/summary` | LastUserPrompt / LastAssistantText |
| Turn history | `GET /sessions/{sid}/turns` | |
| Raw terminal | `GET /sessions/{sid}/buffer` | last resort; prefer summary/turns |
| Third-party state assessment | wingman turn briefs (`/sessions/{sid}/brief`) | the "is it done / stuck / waiting" oracle |
| Broadcast | `POST /fanout` (Gateway), `POST /fanout-local` (Director) | |
| Move work with context | `POST /handover` | used by /move-session skill today |
| Event stream | `GET /events` (Gateway, SSE) | candidate for turn-end notification |
| Prompt queueing | `POST /sessions/{sid}/queue` (Director) | hold prompts without submitting |

Proof this works: the `/move-session` skill and the session-migration recipe already
drive sessions through these endpoints, agent-initiated.

Dependency: cross-machine reach requires the target Director to be reachable through
the Gateway (#197 self-serve, #223 two-way self-test). Same-machine and same-tailnet
cases work today.

---

## 3. Topology: informal master/worker, not a formal orchestrator

Decision (proposed): **do not build a Gateway-managed orchestrator first.** The
informal version is a skill (e.g. `/delegate`, `/qa-loop`) that any agent can run:

1. Find or create a worker session via the Gateway.
2. Send the task with a result convention (sec. 4.1).
3. Poll for turn completion (sec. 4.2).
4. Read the result, iterate or stop (sec. 4.3 loop guards).

All curl. Zero new C# code. The formal version (task queues, worker pools, dependency
graphs in the Gateway) is built only after the informal version shows which patterns
actually get used. Building it first is speculative architecture.

Topology rule: **trees only, never sideways.** Masters prompt workers; workers never
prompt their master or each other. A worker reports by finishing its turn (the master
reads the summary) or by writing to an agreed result file. This makes deadlock
(A waits on B waits on A) structurally impossible.

---

## 4. The Conventions (the actual design work)

### 4.1 Result signaling

The master's task prompt must end with an explicit result convention, one of:

- **DONE marker:** "When complete, end your reply with a line `MESH-RESULT: <one-line
  status>`." Master greps the summary for the marker.
- **Result file:** "Write your full report to `<path>` and reply DONE." For results
  too large for a turn summary. Path lives under the worker repo's `.temp/`.

The marker form is the default; the file form is for structured/large output.

### 4.2 Turn-completion detection

After prompting a worker, the master must know when the worker's turn has ended.
Raw Director state has known subtleties (the doorbell/assessedState work exists
because raw state lies). Layered approach, cheapest first:

1. **v1 - poll:** poll `GET /sessions/{sid}` state + `GET /sessions/{sid}/summary`
   until the turn ends AND the result convention (sec. 4.1) is satisfied. Poll
   interval 10-15s; this is what the doorbell-backed roster already refreshes at.
2. **v1 oracle:** when the state looks ambiguous (worker seems stuck or is asking a
   question), read the wingman brief - it already classifies "needs you" vs working
   vs all-clear, and it is infrastructure that is already trusted.
3. **v2 - subscribe:** masters listen on the Gateway `GET /events` SSE stream for the
   worker's turn-end instead of polling. Only worth building once v1 shows real use.

### 4.3 Loop guards

Unattended agent-to-agent loops are a token furnace and can oscillate. Mandatory in
any looping skill:

- **Round budget:** hard max-rounds (default 5). Exceeding it = stop and escalate to
  the user, never silently continue.
- **Convergence rule:** if the QA/reviewer side raises the same finding twice, stop
  and escalate. Repeat findings mean the loop is not converging.
- **Visible refereeing:** both sessions are normal cockpit sessions, so the user can
  watch the match in the rail and interrupt either side at any time. No hidden
  workers.

### 4.4 Prompt delivery (the parking gotcha)

Known trap: a seeded/sent prompt can land in the target composer UNSUBMITTED (parked).
An agent-to-agent loop that hits this stalls silently on round one. The delegation
skill must:

- send via `POST /sessions/{sid}/prompt` with explicit `appendEnter` semantics, and
- verify submission by checking that the worker entered a working state (or that
  `LastUserPrompt` now matches what was sent) before starting the completion poll;
  if parked, nudge with the known recovery (`{"text":"\r","appendEnter":false}`).

### 4.5 Addressing and roles

- Workers created by a master get a name convention: `[MESH:<role>] <task title>`
  (e.g. `[MESH:qa] verify #212 restore`), so topology is legible in the rail.
- This dovetails with Session Types (#211): a `QA`-type session's playbook already
  says "never fix, only report" - exactly the worker contract the QA loop needs.
  Session types are the natural enforcement point for role behavior.

### 4.6 The accepts-agent-prompts guard (safety boundary)

Scariest failure mode: a confused master prompts the WRONG session - including one of
the user's live working sessions. Proposed one-field guard:

- Sessions carry an `acceptsAgentPrompts` flag (default FALSE).
- Sessions created via the delegation path get it TRUE automatically.
- The delegation skill refuses to prompt any session without the flag. (Enforcement
  starts as a skill-level convention; if the mesh sees real use, the Director can
  enforce it server-side on `/prompt` with an agent-originated header.)

The user's own sessions are never prompt targets unless the user explicitly marks one.

---

## 5. Patterns

### 5.1 QA loop (two sessions, same or different machines)

```
master (QA agent, [MESH:qa])              worker (impl agent, [MESH:impl])
  |                                          |
  |-- create/find worker ------------------->|
  |-- POST prompt: "fix X; MESH-RESULT" ---->|
  |       (verify submitted, sec 4.4)        |  implements
  |-- poll summary until MESH-RESULT --------|
  |  re-test in OWN clean context            |
  |-- pass? stop. fail? round++ ------------>|  (budget + convergence, sec 4.3)
```

### 5.2 Remote hands (cross-machine)

```
desktop agent                              Mac Director (via Gateway)
  |-- GET /directors  (find mac id)          |
  |-- POST /directors/{mac}/sessions ------->|  session created on the Mac
  |-- POST /sessions/{sid}/prompt ---------->|  "build, run tests, report"
  |-- poll summary / read result file -------|
```

### 5.3 Fanout-collect

Master shards a task, `POST /fanout` (or N targeted prompts), polls each shard for
its MESH-RESULT, merges. Workers never see each other.

---

## 6. Risks (honest read)

| Risk | Mitigation |
|------|-----------|
| Token burn (two opus sessions ping-ponging) | round budgets are not optional (4.3) |
| Infinite/oscillating loops | convergence rule + escalate-to-user (4.3) |
| Deadlock | tree topology only, no sideways prompts (3) |
| Master acts on half-finished turn | result convention + wingman brief oracle (4.1, 4.2) |
| Prompt parked, loop stalls | submission verification + nudge (4.4) |
| Prompting the wrong session | acceptsAgentPrompts guard (4.6) |
| Worker asks a question nobody answers | wingman brief flags "needs you"; master escalates to user, never answers policy questions itself |

---

## 7. Benefits, ranked

1. **Context isolation** - the reviewer stays clean; persistent cross-session roles
   accumulate codebase knowledge across rounds, unlike throwaway sub-agents.
2. **Cross-machine reach** - Mac builds, platform verification, distributed heavy
   work; impossible from one box today.
3. **Coordinated parallelism** - fanout-with-collector.
4. **Watchability** - every agent is a full cockpit session with briefs, votes, and a
   terminal. The user can referee. SDK-based multi-agent frameworks cannot offer this.

---

## 8. Phasing

- **Phase 0 (no code):** this document; agree the conventions.
- **Phase 1 (skill only):** a `/delegate` (single task) and `/qa-loop` (bounded loop)
  skill implementing sections 4.1-4.5 via curl against the Gateway. Name-convention
  workers. acceptsAgentPrompts enforced at skill level.
- **Phase 2 (small platform help, only if Phase 1 sticks):** turn-end events consumed
  from `/events` instead of polling; `acceptsAgentPrompts` enforced server-side;
  `[MESH:*]` sessions visually grouped under their master in the cockpit rail.
- **Phase 3 (only with proven demand):** formal orchestration - Gateway task queue,
  worker pools, dependency graphs. Explicitly NOT designed here.

Out of scope for all phases: workers prompting masters, agent-to-agent chat outside
the prompt/summary cycle, autonomous loops without round budgets.

---

## 9. Open questions

1. Should worker sessions auto-close when the master's loop ends, or be left for
   post-mortem? (Lean: leave open, name them clearly; closing loses the terminal.)
2. Does the QA loop's "re-test" happen in the master's session or a third disposable
   session, to keep even test-run noise out of the QA context?
3. Where does the round budget live - skill prompt only, or also a Gateway-side
   circuit breaker counting agent-originated prompts per session pair per hour?
4. Cost control: should `[MESH:*]` workers default to a cheaper model than the
   master?
5. How does this interact with Hold state - should a master be able to prompt a held
   session? (Lean: no; Hold means hands off for agents too.)
