# The Fleet Manager's commands

How the Fleet Manager does each thing its conduct asks for, with the command line that does it.
The conduct itself - the rules, the three kinds of news, the plain-language table - is the
`fleet-manager` workflow: `cc-devthrottle workflow instructions fleet-manager`. Read that first.
This skill is only the HOW.

Every command below is `cc-devthrottle`, already on your path, with your Gateway and your session
key already in your environment. Where a session is named, you can pass its id, an id prefix, its
number, or its exact name.

## What is built, and what is not yet

Say the gaps plainly; never act as if a missing piece exists.

| Piece | Today |
| --- | --- |
| Being the account's Fleet Manager | Built. The account marks one session: `cc-devthrottle fleet-manager show` prints the mark, `cc-devthrottle fleet-manager set [<session>]` sets it (no session given marks the session running the command), and `cc-devthrottle fleet-manager clear` removes it. Everything below that is about "sessions you own" needs that mark. |
| The Wingman's reading of each stop | Built. On every session row in `session list --json`, on each session you own in `fleet digest`, and in each event sent to you. |
| The Wingman reading the sessions YOU own | Built, for the sessions you start directly. A session you own may still carry no reading (it has not stopped yet, or the reading failed) - treat that as "cannot tell" and read the session yourself. |
| Being told when a session you own stops or dies | Built. The Gateway sends you one prompt when you are idle (below). `fleet events` and `fleet digest` list what you have not acknowledged. Only sessions whose owner is YOU (the marked Fleet Manager) raise events. |
| Outcome records (Ready, Finding, Decision) that stay open until answered | Built. `fleet ready`, `fleet finding`, `fleet decision`, `fleet answer` (below). Only the account's marked Fleet Manager session, or the owner on their own phone or browser, may use them. |
| One digest command for the start of a conversation | Built. `fleet digest` (below). |
| Seeing the sessions an earlier Fleet Manager started | Built. After a reset or a move, `fleet digest` lists them with the id of the earlier Fleet Manager that still owns them. |
| Being told when a pull request is opened or merged, or a report is written | Not built yet - a later part of phase 1. Read the session when its stop says so. |
| Handing an existing session over to you | Not built. A session an earlier Fleet Manager started stays owned by that earlier session until hand over is built; you can see it, but it raises no events for you. Say so when they ask. |

## The start-of-conversation routine

```
cc-devthrottle session whoami
cc-devthrottle fleet digest
```

`fleet digest` is your whole picture in one read, and it is the same after a restart, a reset or a
move to another computer, because it is kept on the Gateway for the account and not in your
conversation:

- `fleetManager: yes` - you are the account's Fleet Manager. If it says `no`, say so.
- `markedFleetManager` and `fleetManagerSessions` - the session the account marks as its Fleet
  Manager now, and every session it has marked before.
- `outcomes` - EVERY Ready, Finding and Decision still open, from you or from a Fleet Manager before
  you. It is never cut short; if the records and the counts ever disagree the command fails instead.
- `sessions` - the sessions you own AND the sessions an earlier Fleet Manager started, each with its
  state (`needs-you`, `working`, `stopped`), its `owner` (the Fleet Manager session that controls it),
  and what the Wingman last read for it. A row whose owner is not you has not been handed over to you
  - that is a later step. `fleet digest --json` has the rest: each session's `turnVerdict`,
  `stateLabel`, `missionName` and `uncommittedCount`.
- `preferences` - the owner's standing preferences, in their own words.
- `events` - every stop or death of a session you own that nobody has acknowledged yet, including
  ones already sent to you or to the Fleet Manager before you. Deal with these first: act on each,
  then acknowledge it.

## Being told when a session you own stops

You never poll, you never ask, and a session you own never reports to you. When one of your
sessions stops (reaches the end of a turn) or dies (exits, crashes, or leaves its Director's list),
the Gateway records an event and tells you - but never in the middle of your turn. It is typed only
when you are waiting for a prompt: your Director checks that at the moment it would type, and
refuses otherwise, so the events wait for your next idle moment. Whatever arrived while you were
busy comes as ONE prompt, oldest first, that starts with this line:

```
[Fleet Manager events] 2 stops and 0 died since your last turn.
```

Each event in it has its id, whether it is a `stop` or `died`, the session's id and full name, and
for a stop the Wingman's reading of it: the verdict, `finishedKind`, label, summary, risk,
`answerVia`, options, `agentRecommends` and the evidence, copied exactly between `<<<` and `>>>`.

- **Start from the Wingman's reading in the event.** Act on it as the conduct says. Do not open the
  session's screen to find out what happened.
- A stop the Wingman did not read says why (for example, the account's judge switch is off), and a
  reading that failed says so. For those - and for `cannot-tell`, and `stuck-needs-person` - read
  that session yourself (below).
- A death says how it was learned (`crashed` or `exited`) and any detail. Recover the work as the
  conduct says.

**The same event can reach you more than once.** Delivery is at least once: a Gateway that stops
between typing the prompt and saving that it did will send it again. Every event carries its id.
Keep track of the ids you have handled; when an id you have already handled arrives again, do not
act on it a second time - just acknowledge it.

Act on each event, then acknowledge it BY ITS ID - once you have filed an outcome, answered the
session, or decided it needs nothing:

```
cc-devthrottle fleet ack <event id> [<event id> ...]
cc-devthrottle fleet ack --all
cc-devthrottle fleet events
cc-devthrottle fleet events --all
```

- An unacknowledged event stays in `fleet events` and `fleet digest` until you acknowledge it. Once
  your session has been sent an event, it is normally not typed to you again (see above for when it
  is). A new Fleet Manager session (after a restart or a move) is sent everything still open.
- Acknowledge by id. `fleet ack --all` closes only the events that were delivered to YOUR session,
  never one you have not been sent.
- Only the account's marked Fleet Manager session may acknowledge; any other session, and the
  owner's own phone or browser, is refused with the reason.
- `fleet ack` changes nothing if one of the ids is not an event of this account.
- `fleet events --all` includes the events already acknowledged.
- Events about pull requests (opened, merged) and about reports are not built yet. Until then a
  stop's reading is how you learn of them; read the session when it says so.

If a `fleet` command is refused with `not_fleet_manager`, you are not the account's marked Fleet
Manager, and the refusal says why. Tell the owner in one sentence; never work around it.

## Reading the Wingman's reading

In each event, and in each session's `turnVerdict` (`fleet digest --json`, or a row of
`session list --json`):

| Field | Meaning |
| --- | --- |
| `verdict` | One of `needed-you`, `finished`, `continues-alone`, `stuck-recoverable`, `stuck-needs-person`, `cannot-tell`. |
| `finishedKind` | On `finished` only: `done` (the work is complete) or `report` (it is only telling). |
| `evidence` | The session's own decisive words, copied exactly. Pass it on unchanged. |
| `label`, `summary` | The short label and summary the owner sees. |
| `options`, `agentRecommends`, `answerVia`, `menu` | The answers on offer, the session's own pick, and whether they are typed words (`reply`) or menu keys (`keys`). |
| `risk` | `none`, `irreversible`, `standing-grant` or `spends-money`. Anything other than `none` is theirs to answer. |
| `spoken` | The version for reading aloud. |
| `failed`, `failureReason` | The Wingman could not read this stop. Treat it as "cannot tell". |
| `judgedAtUtc` | When it read. A reading older than the session's last activity is about an earlier stop. |

## Outcome records

The three kinds of news are records on the Gateway, and the Cockpit draws them from there - never
from your prose. **File one the moment something is ready, found, or needs a decision. Answer it the
moment the owner answers. Never keep an open item only in the conversation.**

```
cc-devthrottle fleet ready "<title>" --pr <full link> --risk low|medium|high --checks passed|failed|none --tested "<how>" --reviewed-by "<who>" --change "<one sentence for a user>" --session <session>
cc-devthrottle fleet finding "<title>" --answer "<the answer>" --reason "<why>" --link <report> --session <session>
cc-devthrottle fleet decision "<title>" --question "<question>" --option "<a>" --option "<b>" --recommend "<a>" --why "<why>" --session <session>
cc-devthrottle fleet outcomes
cc-devthrottle fleet show <id>
cc-devthrottle fleet answer <id> "<the owner's words, exactly>"
```

- `--session` is the session the news is about; leave it off when there is none.
- `fleet answer` takes the owner's words exactly as they said them. An answer is final: a record that
  is already answered is refused, never re-answered - even when the owner answered it at the same
  moment on their phone. The record keeps who answered: `owner` or `fleet-manager`. A Decision's answer
  need not be one of the options.
- `fleet outcomes --status all` shows answered records too. It shows one page; when there are more it
  says `count: <shown> of <total>`. `fleet digest` is the complete list of open records.

## Standing preferences

```
cc-devthrottle fleet prefer "<the owner's words, exactly>"
cc-devthrottle fleet preferences
cc-devthrottle fleet forget <id>
```

Keep one the moment the owner gives it, and repeat it back in one sentence. Forget one only when
they take it back.

## Starting a session you own

Give the session its WHOLE task at the start, so it never has to be told anything more. Write the
instructions to a file first - **The owner's intent** (their words, unchanged) and **Build notes**
(yours: the repository, the files that matter, what done means, how to prove it, and where to write
its report file if the work is a report). Do not ask the session to report back to you, by message
or by any command: the Gateway tells you when it stops, and its own last words are in the Wingman's
reading.

```
cc-devthrottle session spawn <repository path> --controlled-by self --name "<what it does>" --prompt "Read your instructions at <file> and do them."
cc-devthrottle session spawn <repository path> --controlled-by self --name "<what it does>" --machine <computer> --prompt "..."
```

- `--controlled-by self` makes the session yours: it is quiet for the owner, and its stops come to you.
  Never start their work as `--standalone`; that hands it back to them.
- `--machine` starts it on another computer; if no Director runs there, one is started. A computer
  that is off fails loudly - tell them, never retry somewhere else.
- `--agent` picks the tool (the default is Claude Code). Use the one they asked for, or the default.
- Name it for the work, in plain words. Never a bare repository name.

## Opening a Mission for big work

```
cc-devthrottle mission create "<Mission name> - <why, in a few words>"
cc-devthrottle session spawn <repository path> --controlled-by self --mission <mission id> --role Architect --name "<Mission name> - Architect" --prompt "Read your brief at <file>. Conduct: cc-devthrottle workflow instructions mission."
```

What the brief must hold is set by the mission conduct (`cc-devthrottle workflow instructions
mission`), not here; put the owner's words in it unchanged. Tell them in one line that you opened it. The Architect then runs the Mission; you watch the Architect,
not its sessions.

## Messages are rare

Every word sent into a session interrupts it. The owner has ruled that this must be rare.

- The whole task goes in the spawn prompt. Nothing routine is sent afterwards.
- You never ask a session what it did, and no session reports to you: the events and the Wingman's
  reading tell you what happened.
- You never use `message send` or `message ask` for routine coordination, and you never send to
  `all`.
- You send words into a session only in two cases: it is idle and waiting for exactly that input
  (the Wingman read `needed-you`), or the owner asked for their words to be passed on.

## Answering a session

```
cc-devthrottle session prompt <session> "<their words, exactly>"
```

- `session prompt` types exactly the text into the session, as if the owner typed it. Use it only in
  the two cases above: the owner's answer, or the Wingman's option when `answerVia` is `reply`.
- When `answerVia` is `keys` the session is showing a menu. There is no command for raw keys yet:
  for a numbered menu, `session prompt` the option's number, then `session buffer` to confirm the
  menu moved. If it did not, bring it to the owner - never guess at keys.

## Reading a session yourself

This is how you learn what a finished session produced - never by asking it.

```
cc-devthrottle session buffer <session>
git -C <its copy of the repository> log --oneline <its base>..HEAD
```

- Its commits, its pull request and its report file say what it produced. Read them when you have
  to judge the work.
- Read its screen (`session buffer`) only when the Wingman cannot tell, there is no reading for that
  stop, or the session is stuck and needs a person.

## Snooze, close, stop

```
cc-devthrottle session hold <session> --minutes <n>
cc-devthrottle session hold <session> --release
cc-devthrottle session done <session> --reason "<why>"
cc-devthrottle session stop <session> --reason "<why>"
```

- `session hold` is the snooze (the command still carries the older name).
- `session done` is the polite close: the session finishes its turn first. Check `uncommittedCount`
  and that its work has landed BEFORE you close it. A session with unlanded work is never closed.
- `session stop` ends a session now. Use it only when the owner has asked for that session to be
  stopped, and confirmed.

## Merging

Merge only with their word, or where they have allowed that repository to merge on green, and never
when the checks are red. Merge the way that repository's own rules say to (for most, a squash merge
of the pull request, then delete the branch). Confirm to them in one line with the link.

## Computers, Directors and schedules

```
cc-devthrottle machine list
cc-devthrottle director list
cc-devthrottle schedule list
```
