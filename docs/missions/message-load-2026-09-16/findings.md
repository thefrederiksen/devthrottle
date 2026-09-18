# Message Load - what is true today (survey of origin/main at 66609c3c, 16 September 2026)

Written by the Architect before any design ruling. Every claim below was read on origin/main, not
the working tree. File references are on origin/main.

## The owner's words (16 September 2026, dictated, verbatim)

> "You have been sending out messages and you really need to stop doing that. Because sleep-wired
> messages are a pain in the ass and it needs to be more It needs to be much harder to do this.
> Messages interrupt what we do and it's really detrimental to the other sessions. ... We need to
> create a new a fully new session here that deals with this problem of way too many messages being
> sent. ... that needs to address this messaging and reduce messaging and probably also implement
> the queue like we already have in or that has been talked about in the Kuhn videos, or the tools
> from Kuhn that his first meet does not they accuse up the messages so i want a new session to deal
> with this because it's a big problem"

"Kuhn" is Kun Chen; "first meet" is his tool firstmate, which queues messages instead of
interrupting.

## How a message travels today

1. `cc-devthrottle message send <id> "text"` posts `{"text": ...}` to the Gateway route
   `POST /sessions/{sid}/message` (`tools/cc-devthrottle/src/session_ops.py:1056`). The sender is
   taken from the session key, never from the body (`GatewayEndpoints.cs:3153-3165`).
2. `message send all` posts to `POST /fleet/broadcast` (`session_ops.py:1046`); the Gateway keeps
   only targets inside the sender's team or Mission unless `--everyone` plus a human grant
   (`FleetBroadcastPolicy.cs`, `BroadcastGovernor.cs`: 5 broadcasts per 60 seconds per sender,
   grants live 10 minutes). This is what issue 1229 (now deleted on GitHub; commits #1231 and
   #1234 survive) built.
3. The Gateway runs `MessageSteward` (3 second duplicate window, 60 messages per minute per
   target, 10 broadcasts per minute; `MessageStewardOptions.cs:12-33`), frames the text as ONE line
   (`FleetMessaging.cs:40-65`, whitespace collapsed), and fires one `prompt` tunnel command at the
   owning Director (`GatewayEndpoints.cs:3089-3148`).
4. The Director checks only that the session has not exited (`SessionCommandExecutor.cs:201`) and
   types the text into the agent's composer as keystrokes, waits for the echo, presses Enter
   (`TerminalSubmit.cs:81-168`). It does not check whether the session is mid-turn. The session is
   flipped to Working (`Session.cs:2682`).
5. `message ask` is the same delivery with `waitForIdle`; the Gateway then polls the target's
   activity state every 750 ms until Idle or WaitingForInput (default 120 seconds) and returns a
   diff of the target's terminal buffer as "the answer" (`GatewayEndpoints.cs:3122-3136`). No
   correlation id, no reply channel.
6. `session report` (worker to supervisor) is just another message and its docstring says it
   interrupts deliberately, owner's ruling of 13 September 2026 (`session_ops.py:480-487`;
   `docs/new_architecture/sessions.html:488-493`). `session raise` is the pull-only flag
   (`sessions/{sid}/needs-manager`) and interrupts nobody.

## What the receiving agent experiences

Claude Code does not abort a turn when text and Enter arrive mid-turn: it queues the text and hands
it to the model as soon as the current tool calls finish, inside the same turn. So a fleet message
lands in the middle of whatever the session was doing and redirects it. That is the interruption.
Whether it clobbers text the owner had typed but not sent is unknown (open issue 2845; a 19-session
fleet stalled most of a day because a supervisor refused to risk it).

## What queueing exists today

None, anywhere in the path. Specifically:

- The Director has a `PromptQueue` (`Session.cs:834`) but it is the owner's "send later" list and
  never auto-sends. The auto-drain was deleted because it was gated on `ActivityState.Idle`, a state
  nothing ever assigns (`Session.cs:2967-2984`).
- The Gateway persists no message. The only trace is a `FileLog` line
  (`GatewayEndpoints.cs:3250`) and a `turn-submitted` row in `activity_events` with
  `route='fleet-message'` (recipient, time, content hash; no sender, no text). That table can give
  messages per recipient per day for the last 30 days.
- The Gateway events feed carries only `director.added` and `director.removed`.
- The Director installs only a `SessionStart` hook for Claude Code (`ClaudeHookInstaller.cs:110`);
  no Stop, UserPromptSubmit or Notification hook. The hook reads a preamble file the Director keeps
  current, but it fires only on startup, resume, clear and compact.

## The one deferral that does exist, and is the pattern to copy

Snooze. A hold asked for while the session is working is recorded as DEFERRED and lands on the
Working to settled edge (`SnoozeRegistry.cs:83-107`, `SnoozeLandingObserver.cs:129`). The
Gateway's `TurnEndWatcher` (`Briefing/TurnEndWatcher.cs:55`) already observes that edge for every
session in the fleet from Director state pushes plus a 15-second heartbeat.

## The known hazard

The settled edge is unreliable today: one in six blue flips is a repaint, and repaints kill snoozes
(open issue 2853; the turn-detection mission of 15 September is phase one of the fix, shipped behind
a switch that is OFF). A queue drained purely on the Gateway's edge could deliver into a session that
is mid-turn or blocked on a question. Delivery therefore has to be decided where the terminal can be
seen: the Director.

## What firstmate does (from devthrottle_internal/docs/research/firstmate/)

- A steer is written to a durable per-worker inbox file and a one-line "doorbell" is typed into the
  worker's terminal; the watcher rings again if the worker does not acknowledge.
- Wakes are durable: queued in `state/.wake-queue`, acknowledged only after handled; a first mate
  that dies mid-turn sees the wake again.
- A bash watcher costing no tokens decides what is actionable; benign events never reach the agent.
- "Scripts report facts, the mate reports judgement": outcomes with evidence are delivered by the
  script that recorded them, not by the model remembering to report.
- The research report's verdict on our messaging: "Worse. I found no queue and no acknowledgement."

## The firstmate handover (devthrottle_internal/docs/research/firstmate/handover-fleet-manager-and-message-load.html, 16 September 2026, 13:06)

Written by the firstmate research session for this Architect. The owner has read it and agrees with
the findings; they are findings, not rulings. What it adds to the picture above:

- Firstmate's send writes the message to a numbered per-worker inbox file first ("the durable record
  IS the delivery"); the terminal receives only one short, fixed doorbell line telling the worker to
  read its inbox. A doorbell cannot be cut at a newline, cannot be left half-typed, and a duplicate
  is harmless.
- Acknowledgement is an action by the recipient (it moves the file to handled/), not the fact of
  delivery. That catches "delivered but never seen".
- Unhandled after a grace period (90 seconds): ring again, once per period; a busy pane waits; a
  composer that visibly holds text skips the ring; after 3 unanswered rings, escalate to the owner
  as a stuck worker. A dead terminal skips the doorbell and goes to recovery.
- Workers never message each other and never message the owner; all steering flows through the one
  session that owns them. Workers report by appending sparse status lines (working, needs-decision,
  blocked, paused, done, failed) to a file that a watcher reads. "Never append working: merely to
  acknowledge receipt."
- A request that expects a reply gets a correlation id; the sender is told if the reply never comes,
  and the sender never blocks waiting.
- Control (interrupt, exit, relaunch) is kept apart from message text.
- The Fleet Manager mission (51c85570) will use this mission's inbox for its follow-up instructions
  to its sessions, so this queue is the one instruction channel, not one of two.
