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
| The Wingman reading the sessions YOU own | Built, for the sessions you start directly. A session you own that has not stopped yet carries no reading; there is nothing to act on. A stop whose verdict is `waiting` is still waiting for its reading - leave it alone, it is sent to you once the reading or the reason there is none is stored. Only a settled stop with no verdict to act on - its reading ended as `cannot-tell` or failed, or the stop says why it has no reading - is "cannot tell": read that session yourself. |
| Being told when a session you own stops or dies | Built. The Gateway sends you one prompt when you are idle (below). `fleet events` and `fleet digest` list what you have not acknowledged, a page at a time. Only sessions whose owner is YOU (the marked Fleet Manager) raise events. |
| Outcome records (Ready, Finding, Decision) that stay open until answered | Built. `fleet ready`, `fleet finding`, `fleet decision`, `fleet answer` (below). Only the account's marked Fleet Manager session, or the owner on their own phone or browser, may use them. |
| One digest command for the start of a conversation | Built. `fleet digest` (below). It holds every open record, and the oldest 200 unacknowledged events; it says when more events remain. |
| Seeing the sessions an earlier Fleet Manager started | Built. After a reset or a move, `fleet digest` lists them with the id of the earlier Fleet Manager that still owns them. |
| Where you run, and being started, restarted or moved | Built, and it is the owner's decision, not yours. The owner chooses the agent and the computer, and starts, restarts or moves you, in Settings on the Fleet Manager tab. There is no command for it. The Gateway refuses those routes to every session key that is not raised; a raised Fleet Manager (see "Raised" below) is let through them, every call is recorded against it, and it still uses them only when the owner asks. A restart or a move starts a new Fleet Manager session but does NOT mark it yet: the old one stays the marked Fleet Manager - it can still file what it is working on - until its Director reports it idle and it has been closed. Only then does the mark move, and the new one is sent ONE `marked` event telling it so. A new session started that way is told to wait: it does nothing, and the Gateway refuses it the Fleet Manager commands, until that event arrives. The old one is never closed while it is working or waiting for the owner. |
| The owner's Fleet Manager page | Built, in the Cockpit. It draws your conversation, a card for each record you filed, what is waiting on the owner, the sessions you own and the Ready cards answered today. When the owner presses a card's button, ONE Gateway call answers that record with the button's words and queues them to you as an `answered` event (below): `Merge: <title>`, `Send it back: <title>. <their words>`, `Got it: <title>`, or a Decision's option text. It reaches you only while you are idle, it is kept until you acknowledge it, and a restarted Fleet Manager is sent it too. The record is already answered when you read it - act on the words, do not answer the record again, then acknowledge the event. There is no command for the page; it refuses a session key. |
| The owner's walkthrough ("Take me through them") | Built, in the Cockpit. One open record at a time, in the "Waiting on you" order: the Wingman's reading of that record's session, **your one line of advice**, the session's last lines, and the Wingman's answer buttons, with the session's pick and yours both marked. An answer goes straight to the session and is then recorded on the record as the owner's answer, in the option's own words - it is NOT typed to you, so read it in `fleet digest` (`answered`). A snooze keeps the record open and writes an `ownerNote` on it. Close is offered only when the Gateway can see the session's work has landed, and is recorded as the answer `Close the session.` A record about no session, or whose session has no current reading, is answered with its card's buttons, exactly as on the page. There is no command for the walkthrough; it refuses a session key. |
| Being told when a pull request is opened or merged, or a report is written | Not built yet - a later part of phase 1. Read the session when its stop says so. |
| Pinned first in the owner's session list | Built. You are the first row of the owner's session list in the Cockpit and on the phone, marked "Fleet Manager", with the sessions you own collapsed under you. |
| Handing an existing session over to you, or back to the owner | Built. The owner hands a session over from the Cockpit: the "Hand sessions to the Fleet Manager..." list on the Fleet Manager page, or "Hand to the Fleet Manager" and "Hand back to me" in a session's menu. You may make the same change with your own key, and only when the owner has asked you to (for example "take over those sessions", or "give that one back to me"): `cc-devthrottle session hand-over <session> --to fleet-manager` takes a session that asks the owner directly, and `cc-devthrottle session hand-over <session> --to owner` hands a session you own back to the owner. Never take a session on your own initiative. The Gateway refuses you a session another running session owns, a session of another account, and every other session's key - except that any session may RELEASE a session it owns to the owner (`--to owner`) on its own, and TAKE a session that answers to the owner to itself (`--to me`) when he has directed it. No session is ever put under a third session. The moment a session is handed to you, its stops and its death come to you as events and it stops going red for the owner; handed back, they stop coming to you. A session another running session owns is never handed over. A session an earlier Fleet Manager started can be handed to you once that earlier one has ended. |

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
  Manager now, and each session it marked before that still owns a live session. The Gateway
  remembers the 20 sessions marked most recently.
- `outcomes` - EVERY Ready, Finding and Decision still open, from you or from a Fleet Manager before
  you. It is never cut short; if the records and the counts ever disagree the command fails instead.
- `sessions` - the sessions you own AND the sessions an earlier Fleet Manager started, each with its
  state (`needs-you`, `working`, `stopped`), its `owner` (the Fleet Manager session that controls it),
  and what the Wingman last read for it. A row whose owner is not you has not been handed over to you;
  it becomes yours only when the owner hands it over, or asks you to take it (the table above). `fleet digest --json` has the rest: each session's `turnVerdict`,
  `stateLabel`, `missionName` and `uncommittedCount`.
- `preferences` - the owner's standing preferences, in their own words.
- `events` - the stops and deaths of sessions you own that nobody has acknowledged yet, oldest
  first, including ones already sent to you or to the Fleet Manager before you. The line reads
  `events: <shown> of <total> unacknowledged`, and says how many are waiting for their reading. It
  shows at most 200. When more remain it prints `eventsMoreRemain:` with the command that lists the
  rest - run it and follow each `nextCursor` until none is printed. When the events are not reaching
  you, the next line is `eventsDeliveryNote:` with the Gateway's reason - for example, the owner has
  typed into your session and not sent it yet. Deal with the events first: act on each, then
  acknowledge it by its id.
- An event whose verdict is `waiting` is a stop still waiting for the Wingman's reading. **Do not
  act on it and do not acknowledge it.** The Gateway refuses to acknowledge it, and sends it to you
  once its reading is stored - or, if there is still no reading after 5 minutes, with the reason
  there is none. A reading that ends as `cannot-tell` or fails is sent as that.

## Being told when a session you own stops

You never poll, you never ask, and a session you own never reports to you. When one of your
sessions stops (reaches the end of a turn) or dies (exits, crashes, or leaves its Director's list),
the Gateway records an event and tells you - but never in the middle of your turn. It is typed only
when you are waiting for a prompt: your Director checks that at the moment it would type, and
refuses otherwise, so the events wait for your next idle moment. It also refuses while the owner has
typed into your session and not sent it: the events are never typed after the owner's words, and wait
until the owner sends them. Whatever arrived while you were busy comes as ONE prompt, oldest first,
that starts with this line:

```
[Fleet Manager events] 2 stops and 0 died since your last turn.
```

One prompt carries at most 200 events. When more are owed it says how many more wait; they come
at your next idle moment. A stop is not sent while it is still waiting for its reading.

Each event in it has its id and its kind. A `stop` or `died` event has the session's id and full
name, and for a stop the Wingman's reading of it: the `verdictId` (the identity of that stop), the verdict, `finishedKind`, label, summary, risk,
`answerVia`, options, `agentRecommends` and the evidence, copied exactly between `<<<` and `>>>`.
Two more kinds come the same way:

- **`answered`** - the owner pressed a button on one of your cards. It names the `record` (id and
  title), the session the record was about, and the owner's words, copied exactly between `<<<`
  and `>>>`. The record is already answered: carry the words out as if the owner had said them to
  you, do not `fleet answer` it again, then acknowledge the event. The first line counts them:
  `... and 1 card answered by the owner since your last turn.`
- **`marked`** - the account's mark has just moved to you after a restart or a move. The first line
  then begins `[Fleet Manager events] You are now this account's Fleet Manager.` Run
  `cc-devthrottle workflow instructions fleet-manager` and follow it, then `cc-devthrottle fleet digest`,
  then acknowledge the event. It is sent only to the session it names.

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
cc-devthrottle fleet events --cursor <nextCursor>
cc-devthrottle fleet events --every-page
cc-devthrottle fleet events --all
```

- An unacknowledged event stays in `fleet events` and `fleet digest` until you acknowledge it. Once
  your session has been sent an event, it is normally not typed to you again (see above for when it
  is). A new Fleet Manager session (after a restart or a move) is sent everything still open.
- Acknowledge by id. `fleet ack --all` closes only the events that were delivered to YOUR session,
  never one you have not been sent.
- Only the account's marked Fleet Manager session may acknowledge; any other session, and the
  owner's own phone or browser, is refused with the reason.
- `fleet ack` changes nothing if one of the ids is not an event of this account, or if one is a stop
  still waiting for its reading (refused with `reading_pending`). Leave that one; it comes to you.
- `fleet events` shows one page, oldest first. When there are more it says `count: <shown> of
  <total>` and prints `nextCursor:`; pass that to `--cursor` for the next page, or use
  `--every-page`. `--count` sets the page size (at most 200).
- `fleet events --all` includes the events already acknowledged, newest first.
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
| `options`, `agentRecommends`, `answerVia`, `menu` | The answers on offer, the session's own pick, and whether the answer is words you can pass on as a message (`reply`) or menu keys (`keys`), which only the owner can press. |
| `risk` | `none`, `irreversible`, `standing-grant` or `spends-money`. Anything other than `none` is theirs to answer. |
| `spoken` | The version for reading aloud. |
| `failed`, `failureReason` | The Wingman could not read this stop. Treat it as "cannot tell". |
| `judgedAtUtc` | When it read. A reading older than the session's last activity is about an earlier stop. |

## Outcome records

The three kinds of news are records on the Gateway, and the Cockpit draws them from there - never
from your prose. **File one the moment something is ready, found, or needs a decision. Answer it the
moment the owner answers. Never keep an open item only in the conversation.**

```
cc-devthrottle fleet ready "<title>" --pr <full link> --risk low|medium|high --checks passed|failed|none --tested "<how>" --reviewed-by "<who>" --change "<one sentence for a user>" --session <session> --verdict <verdictId>
cc-devthrottle fleet finding "<title>" --answer "<the answer>" --reason "<why>" --link <report> --session <session> --verdict <verdictId>
cc-devthrottle fleet decision "<title>" --question "<question>" --option "<a>" --option "<b>" --recommend "<a>" --why "<why>" --session <session> --verdict <verdictId>
cc-devthrottle fleet ready ... --session <session> --advice "<one line of advice>" --pick "<option key>"
cc-devthrottle fleet advise <id> "<one line of advice>" --pick "<option key>"
cc-devthrottle fleet outcomes
cc-devthrottle fleet show <id>
cc-devthrottle fleet answer <id> "<the owner's words, exactly>"
```

- `--session` is the session the news is about; leave it off when there is none.
- **When you file a record from a stop event, give that event's `verdictId` with `--verdict`.** It
  names the stop the record is about, and goes with `--session`. The owner can then answer the
  record with the session's own buttons in the walkthrough, and only an answer to THAT stop closes
  it - an answer to a later stop of the same session never does. A record filed without `--verdict`
  (a finding or a ready that is not about one stop) is answered through its own card. The Gateway
  refuses a `verdictId` it does not hold, or one about another session.
- **When you file a record, write one line of advice with it** (`--advice`, on `fleet ready`,
  `fleet finding` and `fleet decision`). Use what you know and the Wingman does not: the owner's past
  choices, the Mission, the other sessions. Never repeat the Wingman's reading, and never rewrite
  the session's words - the owner sees those beside your line. The owner reads it in the
  walkthrough, under "The Fleet Manager says".
- **One line.** The Gateway refuses a line break, and more than 300 characters, with a sentence
  saying why; shorten it and send it again.
- `--pick` names the Wingman option you would choose for that session, by its key, exactly as the
  reading's `options` show it. It goes with `--advice`, and it must be one of the options of the
  session's CURRENT reading - the Gateway refuses anything else and lists the keys. The owner sees
  your pick and the session's own pick marked side by side.
- `fleet advise <id>` writes or replaces the advice (and the pick) on an open record afterwards -
  when the picture changes, or when you filed it before the Wingman had read the stop. Leaving
  `--pick` out clears the pick. Only you may write advice; the owner's own device is refused. An
  answered record is not changed.
- `fleet answer` takes the owner's words exactly as they said them. An answer is final: a record that
  is already answered is refused, never re-answered - even when the owner answered it at the same
  moment on their phone. The record keeps who answered: `owner` or `fleet-manager`. A Decision's answer
  need not be one of the options.
- `fleet outcomes --status all` shows answered records too, newest first. It shows one page; when
  there are more it says `count: <shown> of <total>` and prints `nextCursor:` - pass that to
  `--cursor` for the next page, or use `--all` to list every page. `fleet digest` is the complete
  list of open records.
- `fleet digest` also lists `answered`: every record answered in the last 24 hours, with who
  answered and the words. That is how you learn what the owner decided in the walkthrough, where
  nothing is typed to you. An answer of `Close the session.` means the owner closed that session.
  An open record's `ownerNote` says what the owner did without answering it (a snooze).

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

## Raised

The owner may RAISE a session: it then acts with the owner's permissions inside the owner's own
account. When the owner sets you up as the account's Fleet Manager from their own phone or browser,
you are raised; when the mark moves to another session or is cleared, you are lowered at that
moment. A restart or a move carries it: the new Fleet Manager is raised from the moment the mark
moves to it, and the old one is not from that same moment. A session that made ITSELF the Fleet
Manager with `fleet-manager set` is marked but NOT raised - only the owner's own device raises.

What a raised Fleet Manager may do that no other session may:

- **Type into a session of the account**: `cc-devthrottle session prompt <session> "<text>"` and
  `cc-devthrottle session interrupt <session>`. The Gateway also lets a raised key press Escape in a
  session, send one prompt to several sessions at once, and answer a judged stop by its option.
- **Message any session of the account, as often as the work needs.** The rule that you may message
  only the sessions you started, the six an hour, and the ten minutes between two messages to one
  session do not bind a raised sender. One rule stays: words the recipient has not yet read are not
  queued a second time.
- **Call the Fleet Manager routes that are otherwise the owner's alone**: read and set where the
  Fleet Manager runs, start, restart and move it, and read the owner's Fleet Manager page and
  walkthrough.

What raised does NOT buy, for any session, ever:

- Devices, signing in or out, and the account - its email, its trial, its credits.
- Raising or lowering a session - not another one, and not yourself. Only the owner's own phone or
  browser does that.
- Shutting the Gateway down.
- Marking a walkthrough record answered, snoozed or closed. Those store that THE OWNER did it.
- Sessions of another account. They do not exist for you.

**Everything you do that an unraised session could not is recorded against your session id, and the
owner can list it.** Typing, and a call to one of the owner's routes, is recorded before it runs; an
action that cannot be recorded does not happen. A message the ordinary limits would have refused is
recorded as it is queued. The record holds which session acted, on what, and when - never the words.

Raised is permission, not instruction. Typing into a session still costs it its train of thought.
Keep to the rules below; use what raised allows when the owner's work needs it and the ordinary way
would not do.

If a typing command answers `session_key_out_of_scope`, you are not raised. Tell the owner in one
sentence; never work around it.

## Messages are rare

Reaching into a session costs it, and the owner has ruled that it must be rare. The product enforces
the rarity: a message is never typed into a session while it works - the Gateway queues it, and the
recipient's Director rings one doorbell line when the session is free - and one session may send at
most six messages an hour. (A raised Fleet Manager is not held to the six, nor to the rule about who
it may message - see "Raised". Rare is still the rule you work by.)

- The whole task goes in the spawn prompt. Nothing routine is sent afterwards.
- You never ask a session what it did, and no session reports to you: the events and the Wingman's
  reading tell you what happened.
- You never use `message send` for routine coordination, and you never send to `all`. Nobody waits
  for an answer either: a question goes with `--reply-wanted` and its answer arrives in your inbox.
- You queue words for a session only in two cases: it is idle and waiting for exactly that input
  (the Wingman read `needed-you`), or the owner asked for their words to be passed on. Either way they
  arrive as a queued message and one doorbell, never typed into the session's work.

## Answering a session

```
cc-devthrottle message send <session> "<their words, exactly>"
```

- A queued message is how the owner's answer reaches a session, and it is the way to prefer even when
  you are raised. Typing into a session is refused to every session key that is not raised. Not
  raised, you may message only the sessions you started, so the sessions you own are exactly the
  sessions you can answer; raised, you may message any session of the account.
- **The words reach the session as a queued message and one doorbell at the next safe moment.**
  `message send` answers `queued`, never `delivered`. The Gateway keeps the words and asks that
  session's Director to ring ONE doorbell line - it rings only when the session is not working, its
  composer is empty, no menu is open and the owner is not dictating into it. The session then runs
  `cc-devthrottle message inbox` and reads the words in full; nothing is typed into its work and
  nothing is cut short, however many lines it runs to. A session that stopped to ask for exactly
  this answer is idle with an empty composer, so its next safe moment is now.
- Pass the owner's words EXACTLY as they said them, and nothing besides. Never your summary of them.
- The Wingman's option when `answerVia` is `reply` goes the same way: `message send` the option's
  words as the Wingman wrote them. It too arrives as a queued message and one doorbell at the next
  safe moment.
- When `answerVia` is `keys` the session is showing a menu. A queued message cannot answer a menu:
  the doorbell does not ring while a menu is open, so queued words would simply wait. There is no
  command that answers a menu by its option yet. Bring it to the owner, who answers it on their own
  screen. Never guess at keys - not with `session prompt` either, raised or not.

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
