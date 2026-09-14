---
name: fleet-comms
description: Talk to other DevThrottle sessions across the fleet. Use when you want to list running sessions, rename this session, message another session, ask another session a question and get its answer, or open a new session - from inside a session. Triggers on "/fleet-comms", "message another session", "talk to another session", "ask another session", "rename this session", "rename session", "list sessions", "what sessions are running", "open a session", "spawn a session", "cc-devthrottle", "fleet messaging", "session intercommunication".
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
cc-devthrottle session spawn D:\path\to\repo --purpose "implement #799" --controlled-by self
cc-devthrottle session spawn D:\path\to\repo --name "Frontend review" --standalone
cc-devthrottle session spawn D:\path\to\repo --purpose "run the test suite" --controlled-by self --agent ClaudeCode --prompt "Run the tests and report failures."
cc-devthrottle session spawn D:\path\to\repo --name "frontend" --standalone --agent RawCli --command cmd
cc-devthrottle director list
cc-devthrottle session spawn D:\path\to\repo --name "build" --controlled-by self --director "North build"
```

**`--standalone` now states a reason.** From v2.1.3 an opt-out spawned from inside a session requires `--why "<reason>"`: it hands the work to the USER, so it goes red and asks him and you hear nothing back. It does not forbid the choice - it means an agent that cannot say why the work is his should keep it. `--controlled-by self` behaves the same on every version.


Every one of those says who will own the new session. From inside a session that is REQUIRED.
### WHO OWNS THE SESSION YOU OPEN - you must say

Every session has exactly one owner, and it is either another SESSION or the USER. There is no third
answer and there is no unowned session: no owner means the user. When you spawn from inside a session
there are two candidates and NO default between them, so the spawn is REFUSED until you say which:

```
--controlled-by self   YOU own it. It stays quiet on the roster and reports back to YOU when it
                       finishes. Use this for work you will collect.
--standalone           the USER owns it. It goes red and asks HIM when it finishes, and you will not
                       hear from it. Use this for work you are starting on his behalf.
--controlled-by <id>   another session owns it.
```

This used to default to `self` silently, and that default was the only way work could quietly stop
being the user's - a session answering to a machine because an environment variable happened to be set,
with nobody having chosen it. So choose deliberately: if you will not actually come back and read that
session's answer, it is not yours, and `--standalone` is the honest declaration.

A person spawning from the desktop, the Cockpit or the phone declares nothing. A session a person opens
is the user's, and there is no second candidate to tell it apart from.


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
   Manager), call the Gateway front door: `curl -X DELETE http://127.0.0.1:7878/sessions/<full-session-id>`
   (the Gateway routes it to whichever Director hosts the session over the tunnel), or have the user
   close its tab. A session reaps ITSELF with `cc-devthrottle session done`, which flags the current
   session (`CC_SESSION_ID`) for graceful removal without killing it mid-turn.
3. Spawn a fresh Manager with a tight brief: `session spawn <repo> --controlled-by self --name "<Mission> - Manager"
   --standalone` - a Manager answers to the USER, which is the whole point of the seat,
   pointing it at the mission document, stating plainly what is DONE and only THIS phase's goal.

This only works because the mission document and memory hold the state - keep them current so a reset
never loses anything.

## Messages

```
cc-devthrottle message send 4c810000 "I finished the API layer - you can start the frontend."
cc-devthrottle message send docs "Please update the API page when you get a chance."
cc-devthrottle message send all "Heads up team: I am about to rebase our shared branch."
cc-devthrottle message ask 9b2f "What database schema is loaded in your repo?"
cc-devthrottle message ask docs "What is the title of the API page?" --timeout-ms 60000
```

Send to the specific sessions that need to hear from you - by id prefix or name. `message ask` is
always single-target and waits for the target's answer.

## Who you may message, and the broadcast rule

Every incoming fleet message interrupts the receiving agent: it is typed into that agent's composer
and starts a turn. So a message you send is a demand on someone else's attention. Keep it scoped.

- Default scope is your own team: the sessions in your Mission, or - if you are a solo session -
  the sessions in the same repository on the same machine. A manager and its workers are one team.
  Message those sessions freely.
- `message send all` reaches ONLY your team, not the whole fleet. This is the everyday broadcast:
  use it for a heads-up your teammates need. It never touches sessions in other repositories or
  other missions, so it will not freeze the fleet.
- For git coordination on a shared working tree, `message send all` already reaches only the
  sessions that share your checkout - that is exactly who a shared-tree hold concerns.
- A WHOLE-FLEET broadcast (`message send all --everyone`) is different: it interrupts every session
  on every machine and repository. The Gateway Hub refuses it unless a human has issued a broadcast
  grant, and it requires a `--reason`:
  `cc-devthrottle message send all "..." --everyone --reason "why" --grant <id>`.
  Almost nobody should need this. If you think you do, ask the human for a grant - do not try to
  route around the Hub (it enforces the limit and also rate-limits repeated broadcasts). See issue #1229.

## Health check

```
cc-devthrottle selftest
```

This spawns two throwaway local sessions, proves list/send/ask works, then tears them down.

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
