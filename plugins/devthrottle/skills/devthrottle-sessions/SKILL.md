---
name: devthrottle-sessions
description: "Talk to the other DevThrottle sessions running across your machines - list them, rename this one, send one of the rare queued messages, read the inbox, reply, open a new session, and close one down. Use when the task involves: message another session, ask another session, list sessions, what sessions are running, spawn a session, rename this session, close this session."
license: MIT
---

# Talking to other DevThrottle sessions

A **session** is one running coding agent. DevThrottle keeps every session your
machines are running attached to one gateway, and `cc-devthrottle` is how a session
reaches the others - on this computer or on any other computer attached to the same
gateway.

There is nothing to configure. Every session DevThrottle launches already carries the
gateway address and its own credential in its environment, so the commands below work
as written. If one of them says `CC_GATEWAY_URL` or `CC_GATEWAY_SESSION_KEY` is unset,
this shell is not inside a DevThrottle-launched session.

## Find out what exists before you act

```
cc-devthrottle actions --json     # the agent-discoverable actions this version exposes
cc-devthrottle session whoami     # this session's own id, name, machine and repository
cc-devthrottle session list       # every session across every attached computer
```

Read `actions --json` when mapping a request onto a command rather than guessing at a
flag: it describes the version that is actually installed, rather than the version you
remember. It is the agent-discoverable set, not an exhaustive list of every command -
`cc-devthrottle <group> --help` is the complete surface, and some commands documented
below appear only there.

## Address a session by id prefix or by name

Every target below accepts a short prefix of a session id, or the session's exact
display name. If a prefix is ambiguous the command fails and says so - rerun with more
characters. It never picks one for you.

## Name this session

```
cc-devthrottle session rename "Frontend review"
cc-devthrottle session rename 9b2f "API rewrite"
```

The first form renames the current session; the second renames another one. Name every
session for the work it is doing. Several sessions often run in the same checkout, and
an unnamed one shows up as the bare folder name, indistinguishable from its neighbours.
Leave the repository out of the name - the session list already has a column for it.

## Messages are rare, and they queue

Most sessions can message nobody. A session may message only the session that started it and
the sessions it started - never a sibling, never another session on the same piece of work,
never a session in the same checkout. The gateway allows at most six messages an hour, one to
the same recipient every ten minutes, and drops an identical message that is still unread.
Every refusal says: put it in your report.

```
cc-devthrottle message send 4c81 "Main is red - do not rebase until I say so."
cc-devthrottle message send all "Stop and commit what you have."
cc-devthrottle message inbox
```

**A message never interrupts.** Nothing is typed into a session while it works. The gateway
stores the message and `message send` answers "queued". When the recipient is not working and
its composer is empty, one fixed doorbell line tells it to run `cc-devthrottle message inbox`;
when you see that line, run it. Reading marks the messages read. An unread message is rung
again after five minutes and marked stuck after three rings, and its sender is told.

`message send all` queues one copy for each session you started, and nobody else.

A whole-fleet broadcast needs a grant from a person. If you believe you need one, ask the
person running the fleet; do not look for a way around the refusal.

## Questions and replies - nobody waits

```
cc-devthrottle message send 9b2f "Which database schema is loaded in your checkout?" --reply-wanted
cc-devthrottle message reply <correlation-id> "Schema v42."
```

There is no command that waits for an answer. Ask with `--reply-wanted` (optionally
`--reply-by <minutes>`, 60 by default) and carry on; the reply arrives in your inbox, or a
no-reply notice does if nobody answers in time. The session that was asked answers once with
`message reply` and the id its inbox shows.

**Delivery is not an answer.** "queued" says only that the message is stored. If you need to
know something, ask for a reply and read it.

When you finish, report with `cc-devthrottle session report "<what you did>"`. When you are
blocked on a decision, put your hand up with `cc-devthrottle session raise "<what you need>"`.

## Open a new session

```
cc-devthrottle session spawn /path/to/repo --controlled-by self --name "Frontend review"
cc-devthrottle session spawn /path/to/repo --controlled-by self --purpose "run the test suite" --agent ClaudeCode --prompt "Run the tests and report failures."
cc-devthrottle session spawn /path/to/repo --standalone --why "the person asked me to open it for them" --name "build" --machine other-computer
```

From inside a session, say who owns the new one: `--controlled-by self` (you own it, and it
reports back to you) or `--standalone --why "<reason>"` (the person owns it). The spawn is
refused until you say, and naming any other session as the owner is refused too.

Give every spawn a `--name` or a `--purpose`; a spawn with neither is warned about, and
a name equal to the bare folder name is rejected.

**Spawning is a commitment, not a resource request.** A session you started is yours to
drive to completion. Before you spawn one, be able to say what specifically has to
happen for it to be finished - a session with no stated exit does not acquire one on its
own. Decide up front where its output has to end up, because that decides whether its
work survives: output left in a throwaway checkout dies with that checkout.

## Close a session down

```
cc-devthrottle session done            # flag THIS session for removal
cc-devthrottle session done <target>   # flag another one
```

`session done` marks a session for graceful removal. It does not kill it mid-turn - the
session finishes what it is doing and is reaped shortly after. Use it on unattended runs
so a finished session does not sit idle.

## Check the plumbing

```
cc-devthrottle selftest
```

Windows only. Opens one throwaway worker, checks it is listed and that a message to it is
queued, then flags it for removal. It does not prove the message was read.
