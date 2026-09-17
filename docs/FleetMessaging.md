# Fleet Messaging

Fleet messaging lets one DevThrottle session list, rename and open sessions, and send the rare
message to the few sessions it is allowed to reach. The session never needs a Gateway URL or the
account token: `cc-devthrottle` calls the Gateway with this session's own key (`CC_GATEWAY_URL` and
`CC_GATEWAY_SESSION_KEY`, both set when the session starts).

**Messages are rare, and they queue.** This page was rewritten on 17 September 2026 by the Message
Load mission. Before it, a message was typed straight into the receiving session and interrupted
whatever it was doing, and a blocking ask made the sender wait for an answer. Both are gone.

## Canonical Command

```bash
cc-devthrottle actions --json
cc-devthrottle session list
cc-devthrottle session whoami
cc-devthrottle session rename "Dev Throttle Review"
cc-devthrottle session rename 9b2f "Frontend Review"
cc-devthrottle session spawn D:\path\to\repo --controlled-by self --purpose "run the tests"
cc-devthrottle message inbox
cc-devthrottle message send 9b2f "Main is red - do not rebase until I say so."
cc-devthrottle message send 9b2f "Is the migration safe to run twice?" --reply-wanted
cc-devthrottle message reply <correlation-id> "Yes - it checks first."
cc-devthrottle message send all "Stop and commit what you have."
cc-devthrottle session report "Done: the API layer is merged."
cc-devthrottle session raise "Need a decision: keep or drop the old column?"
cc-devthrottle schedule list
cc-devthrottle setup status
```

## Commands

| Command | What it does |
|---------|--------------|
| `cc-devthrottle actions --json` | Lists agent-discoverable actions and exact command shapes. |
| `cc-devthrottle session list` | Lists every session in the fleet. |
| `cc-devthrottle session whoami` | Shows this session's id, name, machine, and repository. |
| `cc-devthrottle session rename "name"` | Renames the current session using `CC_SESSION_ID`. |
| `cc-devthrottle session rename <target> "name"` | Renames another session selected by id prefix or exact name. |
| `cc-devthrottle message send <target> "msg"` | Queues a message for the session that started you, or a session you started. Answers "queued", never "delivered". |
| `cc-devthrottle message send <target> "msg" --reply-wanted [--reply-by <minutes>]` | The same, asking for a reply. Prints a correlation id; nothing waits. |
| `cc-devthrottle message send all "msg"` | Queues one copy for each session you started, and nobody else. |
| `cc-devthrottle message reply <id> "answer"` | Answers a message that asked for a reply. The reply lands in the asker's inbox. |
| `cc-devthrottle message inbox [--all]` | Prints every unread message in full and marks it read. `--all` adds what you read in the last 24 hours. |
| `cc-devthrottle session report "what you did"` | Queues your end-of-turn report for the session that owns you. |
| `cc-devthrottle session raise "why"` | Puts your hand up to the session driving you when you are blocked. |
| `cc-devthrottle session spawn <repo>` | Opens a new session. From inside a session, say who owns it. |
| `cc-devthrottle schedule list` | Lists Gateway schedules. |
| `cc-devthrottle setup status` | Shows local setup status. |
| `cc-devthrottle selftest` | Windows only: spawns one throwaway worker and checks a message to it is queued. |

## Who may message whom

The Gateway decides, and refuses everything else with the reason:

- A session may message **the session that started it** (its owner) and **the sessions it started**
  (its workers). Nobody else: not a sibling worker, not another session on the same mission, not a
  session in the same checkout. A session with no owner and no workers can message nobody.
- **At most 6 messages an hour** from one session, and **1 to the same recipient every 10 minutes**.
  A report is exempt from the 10-minute spacing, and from nothing else.
- An **identical unread message** is dropped as a duplicate (same sender, recipient, kind, question
  it answers, reply request and text).
- Every refusal says **"put it in your report"**. The report at the end of your turn is where news
  belongs.
- A **reply** may always go back to whoever asked, once per question, and is not counted against
  the limits.
- A **whole-account broadcast** (`message send all --everyone --reason "..." --grant <id>`) needs a
  human-issued grant (issue #1229). Its copies are queued like any other message.
- **Typing into a session is the owner's alone.** `session prompt`, `session interrupt`,
  `session compact-continue` with a message, and answering a judged stop are refused to every
  session key. The owner, typing from his own screens, is never queued or gated.

## How a message reaches its reader

1. `message send` writes a record on the Gateway. That record IS the delivery. The text may span
   many lines and is never typed anywhere.
2. The Gateway asks the recipient's Director to ring. The Director rings only when the session is
   not working, its composer is empty, no menu is open and the owner is not dictating into it. It
   types one fixed line and nothing else:
   `[DevThrottle doorbell] 1 fleet message is waiting for you. To read, run: cc-devthrottle message inbox`
3. The session runs `cc-devthrottle message inbox`, which returns every unread message in full and
   marks them read. Reading is the acknowledgement.
4. An unread message is rung again after 5 minutes. After 3 unanswered rings it is marked **stuck**,
   and the sender receives a notice from the Gateway in its own inbox.
5. Every screen shows the waiting count on the recipient's row, as words the Gateway folds: "2
   messages waiting", "1 reply waiting", "1 message stuck, unread for 20 minutes".

A doorbell does not end a snooze (owner decision, 17 September 2026; see
`docs/new_architecture/sessions.html`).

**Limits, stated honestly.** Only Claude Code and Codex are rung today, because the Director can read
only their composers; a session running another agent sees its messages only when it runs
`message inbox` itself, and they never go stuck. A doorbell line that was typed but could not be
confirmed as submitted is left in the composer, never erased; the session is not rung again until the
owner clears or sends it.

## Questions and replies

```bash
cc-devthrottle message send 9b2f "Which schema is loaded?" --reply-wanted --reply-by 30
# ... carry on working; nothing waits ...
cc-devthrottle message inbox          # the reply arrives here, and you are rung for it
```

The recipient sees the question's correlation id and the exact command to answer it:

```bash
cc-devthrottle message reply <correlation-id> "Schema v42."
```

`--reply-by` is minutes, 1 to 1440, 60 when omitted. If nobody replies by then, a no-reply notice from
the Gateway arrives in the asker's inbox. A late reply still arrives, marked late.

## Targeting

A target can be a full session id, a unique id prefix, or an exact session name. If a target is
ambiguous, use a longer id prefix from `cc-devthrottle session list`.

## Notes

- `--reply-wanted` is one recipient only, never `all`.
- `message send all` exits 1 when you have no workers, or when nothing was queued and no identical
  message was already waiting.
- The command exits non-zero with a clear error when `CC_GATEWAY_URL` or `CC_GATEWAY_SESSION_KEY` is
  missing, a target is ambiguous, or a target cannot be found.
- The account token never enters the agent process; the session key is the session's own and ends
  with it.
