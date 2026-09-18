# Mission: Message Load - fewer messages, and none that interrupt

**Status: FINISHED, 18 September 2026.** Merged to main as ddc78ac4 (pull request 3016) on top of slice 1 (a8fa8041, pull request 2970); post-merge fix 7b6afbdc; released as v2.6.0; the report is `report.html` beside this file. Started 16 September 2026. Worktree `~/ReposFred/devthrottle-message-load` on
devthrottle-mac-mini, branch `mission/message-load`, cut from `origin/main` at `66609c3c`. Mission
record on the Gateway: `7e6c9c04`. Conduct: `cc-devthrottle workflow instructions mission`.
Owner-facing version of this brief: `design-and-questions.html` beside this file. Survey of what is
true today: `findings.md` beside this file.

---

## The why

The owner, 16 September 2026, dictated:

> "You have been sending out messages and you really need to stop doing that. ... It needs to be
> much harder to do this. Messages interrupt what we do and it's really detrimental to the other
> sessions. ... reduce messaging and probably also implement the queue ... like Kun's firstmate
> does."

What is true today: a fleet message is typed straight into the receiving agent's terminal and Enter
is pressed, whatever the session is doing. Claude Code feeds it to the model as soon as the current
tool calls finish, so the session is redirected mid-task. Nobody knows whether it clobbers the
owner's unsent text (issue 2845). Messages are one line; long ones sit half-typed and are never
sent. The Gateway keeps no record of any message. Every session's preamble teaches messaging as an
ordinary tool.

When this mission is finished:

- A session can message only the session that started it and the sessions it started, a few times
  an hour. Everyone else is refused by the Gateway.
- A message never lands mid-work. It is a durable record on the Gateway. The Director rings a fixed
  one-line doorbell only when the session is not working and its composer is empty; the session
  reads the text from its inbox.
- Unread messages are chased and, after three rings, marked stuck and reported to the sender.
- No agent ever waits blocked for an answer. `message ask` is gone for agents; a reply arrives in the
  sender's inbox.
- Every screen shows "N messages waiting" on the session row, as a string the Gateway folds.
- The preamble and the fleet-comms skill say: messages are rare, they queue, most sessions cannot
  send one.

## Design rulings

All five questions in `design-and-questions.html` were answered "all recommended" by the owner on
16 September 2026. Everything below is therefore stated unless marked *inferred*.

1. **Who may send.** A session may message only its supervisor (the session that started it, the
   `--controlled-by` relationship) and its own workers (sessions it started). `send all` reaches
   only the sender's own workers. A session with no supervisor and no workers can message nobody.
   Same-Mission sends are refused. Stated.
2. **No agent ever interrupts.** There is no agent-side interrupt path, with or without a grant.
   Only the owner interrupts, by typing into a session from his own screens; the owner's input is
   never queued or gated. The 13 September ruling that `session report` interrupts deliberately is
   reversed: reports go through the queue. Stated.
3. **Limits.** 6 messages per hour per sender; 1 per recipient per 10 minutes; exact duplicates
   dropped; grace period before a re-ring 5 minutes; stuck after 3 unanswered rings. Refusals say
   "put it in your report". These are constants in one options class. Stated.
4. **Scope.** The six pieces below, the rewritten preamble and skill, and one line on the session
   row. Nothing more. Stated.
5. **Baseline.** Count messages per day from the Gateway's `activity_events` table
   (`route='fleet-message'`) if the Manager finds a way to query the hosted Gateway's data without
   new product surface; otherwise record "not measured". *Inferred* from "all recommended" on a
   question that had no recommendation.
6. **The record is the delivery.** `message send` succeeds when the Gateway has written the inbox
   record, and answers "queued", never "delivered". *Inferred* from firstmate; the owner agreed with
   the handover findings.
7. **The Director decides when it is safe.** The Gateway asks the Director to ring; the Director
   rings only if the session is not working and the composer is empty, and answers "rung" or
   "deferred, reason". The Gateway retries on the next settled edge and on its heartbeat. Nothing
   is typed into an exited session. *Inferred*: it follows from the unreliable turn-end signal
   (issue 2853) and the composer hazard (issue 2845).
   **Its limit (inspection 4, ruling 1, 17 September 2026):** the Director takes a last look - a third
   screen frame and its own activity state - immediately before the first byte, and defers if anything
   changed. What a terminal cannot close is the interval between that look and the first byte, and a turn
   the agent starts on its own inside it (a background task completing). The worst case there is one short
   fixed doorbell line queued behind the current tool call; it carries no message text, so nothing is lost.
8. **The doorbell is the only thing typed.** One fixed short line: how many messages wait and the
   command to read them. Message text may be multi-line and is never typed. A duplicate doorbell is
   harmless. Stated.
9. **Read is the acknowledgement.** A record is open until the recipient runs
   `cc-devthrottle message inbox`, which returns the full text and marks it read. Stated.
10. **Replies without blocking.** `message send --reply-wanted [--reply-by <minutes>]` gives the
    record a correlation id. `cc-devthrottle message reply <id> "text"` is allowed to whoever sent
    the original regardless of rule 1, and lands in the sender's inbox. If no reply arrives by the
    deadline, the Gateway queues a system notice to the sender. `message ask` is removed from the
    command line tool and the action catalogue. Stated.
11. **Stuck and system notices.** After 3 unanswered rings the record is marked stuck, the sender
    receives a system notice in its inbox, and the recipient's row line says so. System notices
    have the Gateway as sender and are exempt from rule 1 and rule 3. *Inferred*.
12. **The client is dumb.** The Gateway folds one string per session ("2 messages waiting", "1
    message stuck, unread for 20 minutes") onto the session the Director, Cockpit and phone already
    read. Clients render it verbatim. No client computes it. Stated (project rule 7).
13. **Whole-fleet broadcast.** The `--everyone` grant machinery from issue 1229 stays. Its messages
    enter the same queue; a grant is not an interrupt. *Inferred* from ruling 2.
14. **Agent-agnostic.** The doorbell is a typed line, so it works for every agent the Director runs.
    No agent hook is required. *Inferred*.
15. **Snooze.** A doorbell is agent-origin and does not cancel a snooze (already true). A snoozed
    session is still rung when safe. *Inferred*.
17. **Every agent-to-session input path is covered, not just `message send`.** `session prompt`,
    `session interrupt` and `session compact-continue` called with a session key (that is, by an
    agent) are refused by the Gateway with the reason; called by the owner from his own screens
    they are unchanged. An agent that wants to reach a session it supervises sends a queued message.
    *Inferred* from ruling 2; a gate with a side door is not a gate.
16. **Data.** The inbox is a tenant-scoped table in the Gateway database, both deployment shapes
    (Postgres hosted, SQLite self-host), EF migration in the same pull request as the model.
    Retention 30 days for read and stuck records, matching `activity_events`. *Inferred* from the
    existing pattern.

## The work, in the order it lands

One pull request per slice, each merged before the next builds on it. A fix and its regression test
are one unit. Every slice proves its guard can fail (revert, watch red, restore).

1. **The inbox and the gate (Gateway + command line tool).** Inbox table and migration. `POST
   /sessions/{sid}/message` writes a record and returns queued; policy for rule 1 and limits for
   rule 3 in one pure policy class with unit tests; `MessageSteward` and `BroadcastGovernor`
   reconciled, not duplicated. `GET` inbox endpoint that marks read. Command line: `message send`
   prints "queued" and the refusal reasons; `message inbox` prints the full text of unread messages
   (and `--all` for recent read ones); `message ask` removed; `session report` becomes a queued
   message. Action catalogue updated. The Gateway logs every send, refusal, ring and read.
2. **The doorbell (Gateway + Director).** A `ring` tunnel verb. The Director's safety check: not
   working, composer empty (say precisely how "composer empty" is decided from the screen, and what
   is NOT covered), not exited. The Gateway's ringing observer on the settled edge and heartbeat, the
   5-minute grace re-ring, the 3-ring stuck rule, the stuck system notice. Tests on the Director
   safety check with real screen rows, and on the Gateway's ring scheduling with a fake clock.
3. **Replies.** `--reply-wanted`, `--reply-by`, `message reply`, the no-reply system notice.
4. **The row line.** The Gateway folds the waiting/stuck string onto the session DTO; the Director,
   the Cockpit and the phone render it. One edit per client, no branching on meaning.
5. **Words.** `FleetPreambleTemplate.cs`, the fleet-comms skill (pull, edit, publish through
   `cc-devthrottle skill`), `docs/FleetMessaging.md`, `docs/new_architecture/sessions.html`
   (the 13 September ruling replaced, dated), the mission workflow's line about messages, the
   `session report` docstring. Every sentence that says a message interrupts is replaced.
6. **Baseline and record.** The per-day count if obtainable. The mission record (this folder)
   landed on main.

Then a release is cut so the owner's Directors pick up the doorbell.

## Explicitly out of scope

- Outcomes reported by scripts instead of the model (pull request opened, session died). Later
  mission, on this queue.
- Any new screen or inbox page on any client.
- Fixing turn detection itself (the turn-detection mission owns it).
- The Fleet Manager mission's own design; it will use this inbox.
- Changing how the owner's own typed input reaches a session.

## Proof the owner will read

- A worker mid-build receives a message: nothing is typed until its turn ends; then one line; the
  inbox shows the full multi-line text; the record is read.
- A session with owner text in its composer: the doorbell waits; evidence of the composer check.
- A session that never reads: three rings five minutes apart, then stuck, sender notified, row line.
- A sibling worker trying to message another: refused with the reason.
- A sender over its hourly limit: refused, "put it in your report".
- `message ask` no longer exists; `--reply-wanted` round trip works; no-reply notice arrives.
- The count of messages per day before, if measured.

## Conduct

`cc-devthrottle workflow instructions mission`. The Manager sends NO fleet messages to anyone,
including the Architect: the owner has ordered messaging stopped, and this mission is the fix.
The Manager reports by writing `handoff.md` in this folder, committing, pushing, and then running
`cc-devthrottle session raise "<one line>"`. The Architect reads the branch and the handoff.
Workers report to the Manager the same way. Inspector: Codex, seated by the Architect per slice.
