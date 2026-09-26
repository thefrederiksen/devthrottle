---
name: fleet-comms
description: Talk to other DevThrottle sessions across the fleet. Use when you want to list running sessions, rename this session, open a new session, or send one of the rare queued messages (to the session that started you or a session you started), read your inbox, or reply - from inside a session. Triggers on "/fleet-comms", "message another session", "talk to another session", "ask another session", "rename this session", "rename session", "list sessions", "what sessions are running", "open a session", "spawn a session", "cc-devthrottle", "fleet messaging", "session intercommunication".
---

# Fleet communication between sessions

DevThrottle lets a session talk to other sessions running anywhere in the fleet, meaning any
machine whose Director is attached to the same Gateway. Use the single `cc-devthrottle` command.
You never need the Gateway URL or any token; your own Director relays for you.

Every session is launched with the environment values the command relies on: `CC_GATEWAY_URL` (the
Gateway's address), `CC_GATEWAY_SESSION_KEY` (this session's own credential for it),
`CC_DIRECTOR_ID` (which Director you belong to) and `CC_SESSION_ID` (your own id).
`cc-devthrottle` reads them automatically. The Director itself listens on nothing - the
remove-the-network-port mission deleted its HTTP surface - so every command is a Gateway call.

## Discover actions

Use this first when mapping a user task to a command.

```
cc-devthrottle actions --json
```

## Sessions

```
cc-devthrottle session list
cc-devthrottle session whoami
cc-devthrottle session rename "Dev Throttle Review"
cc-devthrottle session rename 9b2f "Frontend Review"
cc-devthrottle session spawn D:\path\to\repo --controlled-by self --purpose "implement #799"
cc-devthrottle session spawn D:\path\to\repo --standalone --why "he asked me to open this for him" --name "Frontend review"
cc-devthrottle session spawn D:\path\to\repo --controlled-by self --purpose "run the test suite" --agent ClaudeCode --prompt "Run the tests and report failures."
cc-devthrottle session spawn D:\path\to\repo --controlled-by self --name "frontend" --agent RawCli --command cmd
cc-devthrottle director list
cc-devthrottle session spawn D:\path\to\repo --controlled-by self --name "build" --director "North build"
```

### WHEN YOU SPAWN, YOU MUST SAY WHO OWNS THE RESULT

**Every session answers to something.** Either another session is holding it - and gets told when its
turn ends - or it is the USER's, and it goes red and asks him. There is no third answer, and there is
no unowned session.

You are a session, so when you spawn there are two possible owners and no safe default between them.
**The spawn is REFUSED until you say which.** That is why every example above carries a declaration.

```
--controlled-by self           YOU own it. It stays quiet on the roster and reports back
                               to you when it finishes. For work you will collect.

--standalone --why "<reason>"  The USER owns it. It goes RED and asks HIM when it
                               finishes, and you will not hear from it.
```

You cannot give the new session to another session. The Gateway refuses a session that names any id
but its own as the owner, because the owner is one of the few sessions the new one may message.

**You may let go afterwards, and only that way.** If you own a session and the work turns out to be
the user's - it outlives you, it needs him, it was never yours to collect - release it:

```
cc-devthrottle session hand-over <session> --to owner
```

It answers to him from then on, goes red for him when it stops, and keeps everything it has done.
That is the change of owner a session makes on its own, and it is safe for the same reason
`--controlled-by self` is the only owner you may name at birth: giving work away puts it in front of
the person.

**And you may take one, when he tells you to.** If the user says to take a session over - to drive it,
to collect its work, to put it under you - take it to YOURSELF:

```
cc-devthrottle session hand-over <session> --to me
```

It answers to you from then on: it stops going red for him, it reports to you, and you answer for it. So
do this on his word, not on your own initiative. He can hand it back to himself whenever he likes. You
can only take a session that already answers to HIM - a session another running session owns is never
taken from it.

**The only owner you may ever name is yourself.** That is the whole rule: take to yourself, release to
him. There is no way to put a session under a third session, and no way to put yourself under another
session - `--to` takes a direction, never a session id. Handing a session to the Fleet Manager stays his
to direct. Anything else is refused with the reason.


**Reach for `--controlled-by self` by default.** You asked for the work; getting back with it is part
of doing it. `--standalone` is for the narrow case where the work is genuinely the user's - he asked
you to open it for him, or it needs his decision before anything else can run - and it requires
`--why` for exactly that reason: an agent that cannot say why the work is his should keep it.

This used to default silently to `self` whenever an environment variable happened to be set. That
default is gone: who a session answers to is too important to be decided by an environment variable.

*Version note: the `--why` requirement arrived in v2.1.3. An older Director accepts `--standalone`
without it. `--controlled-by self` behaves identically on every version, which is another reason to
reach for it first.*


`--machine <name>` starts the session on another COMPUTER; `--director <id-or-name>` starts it on ONE
named Director. They answer different questions: a computer runs several named Director instances, so
`--machine` lands on whichever is listed first. Name the Director when it has to be that one -
`director list` gives you the id, and a Director's toolbar Copy button hands out its name, id, and
machine for pasting. An unregistered or ambiguous name fails loudly and never falls back to another
Director.

Always name your session. On this fleet many sessions run in the SAME checkout, so a session with
no name displays as the bare folder name and is impossible to tell apart. Lead with `--name`
(an explicit display name) or `--purpose` (a short description of what the session is FOR, e.g.
`implement #799`); spawn warns when you give neither. A blank name, or a name equal to the bare
repository folder name, is rejected - pass something meaningful or a purpose.

### Spawning is a commitment, not a resource request

**When you spawn a session, it is YOUR worker and you own finishing it.** You do not hand it a
task and walk away. From the moment it exists it is your job to drive it to completion as quickly
as possible, and to get its work somewhere safe.

**A session is not complete when its work is written. It is complete when ALL of these are true:**

1. its code has been reviewed by a session OTHER than the one that wrote it;
2. its output is SAFE (see the two destinations below);
3. its worktree is gone, if it had its own;
4. the session itself is dead (`cc-devthrottle session done <target>`).

Any one of those missing means it is still open, still yours, and still costing something.

**"Safe" means one of two different things, and you choose which at spawn time:**

- **The child works in YOUR worktree.** Its output is safe once merged into your tree and you
  carry it forward. You are now responsible for that code.
- **The child has its OWN worktree.** Its output is safe ONLY on `origin/main`. That worktree
  will be deleted, and anything left in it - uncommitted changes, a branch never pushed, a patch
  on disk - dies with it.

Know which one you took on before you spawn; the obligation is different.

**Three questions you must be able to answer for every session you started:**

- when did it start?
- how long has it been open?
- what specifically has to happen to close it?

If you cannot answer the third, the session has no exit and will not acquire one by itself.

**This is not a limit on how many sessions you may run.** A hundred sessions is fine if every one
is being driven to done. Three is a mess if none of them are. What matters is closing, not
counting - so drive one thing all the way to dead-and-deleted before you pick up the next.

### If a command fails against an old Director

The remove-the-network-port mission ended the era of probing the Director's routes to date it -
there are no routes. Commands go to the Gateway, which reaches the owning Director over its
tunnel; a Director too old for a verb fails with the Gateway's own words naming the machine.

This is worth a beat of suspicion generally: a Director, a `cc-devthrottle`, and a checkout can
each be older than origin/main, and a stale one will contradict the code you just read. Verify
what is running before you conclude a feature is broken (issue #1514).

### Display-name convention (ratified by Soren, 2026-07-11)

Names are how the fleet sorts, so compose them so related sessions group together:

- A session on a Mission is named mission first, role second, joined with " - ":
  - `Gateway Connection - Architect`
  - `Gateway Connection - Manager`
  - `Gateway Connection - Worker - connect panel` (a Worker adds its task at the end)
- Sorting by name then puts every session of one Mission next to each other, Architect and
  Manager adjacent with the Workers under them.
- NEVER put the repository in the name - the session list already shows the repository in its
  own column. NEVER put session ids or numbers in the name.
- A solo session (no Mission) is named for the work itself ("Clean up stale branches"), again
  without the repository name.

`session rename "name"` renames the current session using `CC_SESSION_ID`.
`session rename <target> "name"` renames another session selected by id prefix or exact name.

### Phased missions: a fresh Manager each phase (the Architect's job)

When an implementation runs in phases, do NOT keep one Manager alive across all of them. At each
phase boundary the Architect **retires the current Manager and spawns a fresh one**, briefed on only
what is done and what this phase needs.

Why it is worth it: a long-lived Manager drags every earlier phase's context forward - it drifts,
answers worse (a fast model can even hallucinate that work is done), and burns tokens re-reading
stale history it no longer needs. The durable truth - decisions, what shipped, what is next - lives
in the mission document and memory, so a fresh Manager reads those and starts clean with nothing
important lost. Cheaper and sharper, every phase.

At each phase boundary:
1. Confirm the current Manager is stood down (its tree is clean, nothing in flight).
2. Reap it. To reap ANOTHER session (a Manager reaping a Worker, or you reaping the outgoing
   Manager), run `cc-devthrottle session stop <target> --reason "<why>"` (the Gateway routes it to
   whichever Director hosts the session over the tunnel), or have the user close its tab. A session reaps ITSELF with `cc-devthrottle session done`, which flags the current
   session (`CC_SESSION_ID`) for graceful removal without killing it mid-turn.
3. Spawn a fresh Manager with a tight brief: `session spawn <repo> --name "<Mission> - Manager"`,
   pointing it at the mission document, stating plainly what is DONE and only THIS phase's goal.

This only works because the mission document and memory hold the state - keep them current so a reset
never loses anything.

## Messages are rare, and they queue

**Most sessions can message nobody.** A session may message only two kinds of session: the session
that started it (its owner), and the sessions it started (its workers). Siblings, sessions in the same
mission, and sessions in the same repository are all refused by the Gateway. A session with no owner
and no workers cannot send a message at all.

**Six an hour.** The Gateway allows at most 6 messages an hour from one session, and 1 to the same
recipient every 10 minutes. An identical message that is still unread is dropped as a duplicate. Every
refusal says the same thing: put it in your report. `cc-devthrottle session handback` at the end of your
turn is where your news belongs; a message is for the rare thing that cannot wait for it.

**The one exception is a session the owner has raised.** A raised session acts with the owner's
permissions inside the owner's account: it may message any session of the account, and it is not held
to the six an hour or the ten minutes. The duplicate rule still applies to it, and every message those
limits would have refused is recorded against it. Only the owner raises a session, from his own phone
or browser - setting a session up as the Fleet Manager there raises it. No session can raise or lower
any session, itself included. Unless the owner raised you, none of this is about you.

**A message never interrupts.** Nothing is typed into the receiving session while it works. The
Gateway stores the message, and `message send` answers "queued" - never "delivered". When the
recipient is not working and its composer is empty, its Director types ONE fixed doorbell line:

```
[DevThrottle doorbell] 2 fleet messages are waiting for you. To read, run: cc-devthrottle message inbox
```

The doorbell carries no message text. **When you see that line, run the command.** The text may span
many lines and is never typed anywhere; you read it from your inbox:

```
cc-devthrottle message inbox          every unread message, in full; reading marks them read
cc-devthrottle message inbox --all    also what you read in the last 24 hours (at most 200)
```

Reading is the acknowledgement. An unread message is rung again after 5 minutes; after 3 unanswered
rings it is marked stuck, its sender gets a notice from the Gateway, and the recipient's row on every
screen says so ("1 message stuck, unread for 20 minutes"). While messages wait, the row says "2
messages waiting".

```
cc-devthrottle message send 4c810000 "The API layer is merged - start the frontend."
cc-devthrottle message send all "Stop: main is red, do not rebase until I say so."
```

`message send all` queues one copy for each session YOU started - your workers - and nobody else.

## Questions and replies - nobody waits

There is no blocking ask any more: the command that waited for an answer was removed. To ask a
question, send it with `--reply-wanted` and carry on with your work:

```
cc-devthrottle message send 9b2f "Which database schema is loaded in your checkout?" --reply-wanted
cc-devthrottle message send 9b2f "Is the migration safe to run twice?" --reply-wanted --reply-by 30
```

The answer prints a correlation id. `--reply-by` is minutes, 1 to 1440, 60 when omitted. The reply
arrives in YOUR inbox, and you are rung for it like any message. If nobody replies by the deadline, a
no-reply notice from the Gateway arrives instead.

The session that was asked answers with the id shown in its inbox:

```
cc-devthrottle message reply <correlation-id> "The schema is v42."
```

Only the session the question was sent to may answer, once, and only to whoever asked. A reply is not
held to the message limits, and a late reply still arrives, marked late.

## Raising your hand

If you are a worker and you are blocked on something you cannot decide inside your mandate - an
ambiguous requirement, an irreversible step, a real design fork - do not message. Put your hand up:

```
cc-devthrottle session raise "<what you are blocked on>"
cc-devthrottle session raise --clear
```

The session that started you sees it with `cc-devthrottle session workers`. Your hand lowers itself
when your turn ends.

## The whole fleet

`message send all --everyone` queues a copy for every session in the account. The Gateway refuses it
unless a human has issued a broadcast grant, and it requires a `--reason`:
`cc-devthrottle message send all "..." --everyone --reason "why" --grant <id>`. Almost nobody should
need this. If you think you do, ask the human for a grant - do not try to route around the Gateway (it
enforces the limit and also rate-limits repeated broadcasts). See issue #1229.

## Typing into a session: the sessions you own

You may type into a session YOU OWN - one you started with `--controlled-by self`, or were handed -
with `cc-devthrottle session prompt <session> "<text>"`, and rescue a stuck one with
`cc-devthrottle session compact-continue <session> "<text>"`. The Gateway types it only when that
session is waiting for a prompt, and never over words the owner typed into its composer and did not
send: then nothing is typed and the answer says why - try again later, or queue a message.

Every other session is refused: one the owner runs himself, one another session owns, and one owned
by a session you own (only the direct owner types). To reach those, queue a message. `session
interrupt` stays the owner's even for a session you own, because Ctrl+C clears his unsent words. The
owner types into any session from his own screens, and a session he has raised may type into any
session of his account - each time it does is recorded against it.

## Health check

```
cc-devthrottle selftest
```

On Windows, this spawns one throwaway worker, checks it is listed and that a message to it is
queued, then flags it for deletion. It does not prove the message was read.

## Related surfaces

The same binary also owns Gateway schedules and local setup diagnostics:

```
cc-devthrottle schedule list
cc-devthrottle setup status
```

## Rules

- Address a session by a short id prefix or by exact name.
- For a simple current-session rename, run `cc-devthrottle session rename "New Name"` directly.
- If a target is ambiguous, rerun with a longer id prefix.
- If a command says `CC_GATEWAY_URL` or `CC_GATEWAY_SESSION_KEY` is not set, you are outside a
  DevThrottle-launched session (or this machine has no Gateway - and no Gateway means no agent
  tooling, by design).
- The account-wide Gateway token never enters a session; your session key is yours alone and ends
  with the session.
