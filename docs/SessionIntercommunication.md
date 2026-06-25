# Session Intercommunication

How one running session talks to another running session, across the whole
fleet, through the Gateway.

Status: design / proposed. This document describes the intended architecture
and the capabilities each session will have. It is the reference for the
implementation work.

---

## 1. What this feature is

Today a session (one running Claude Code or other agent CLI inside a terminal)
is an island. It can drive its own machine, but it has no way to talk to
another session, and certainly not to a session running under a different
Director on a different machine.

This feature gives every session a simple, human-friendly way to:

- See every other session that is currently running anywhere in the fleet.
- Send a text message to one of them.
- Send the same message to all of them at once.
- Know who a message came from, so it can reply.

The whole point is that an agent can coordinate with another agent the same way
a person would lean over and say "hey, session 3, can you run the tests while I
write the docs." A human can also tell one agent "go ask session 7 about the
database schema," and the agent has a real way to do it.

This was inspired by an agent-to-agent communication demo that used a
peer-to-peer networking library. We do not need the peer-to-peer part, because
every Director in our fleet already connects to a central Gateway. The Gateway
is already a message router. We are adding a small, clear layer on top of
machinery that already exists. Section 7 compares the two approaches in detail.

---

## 2. The three identifiers (and why there are three)

Every session has three different ways to be referred to. Keeping them separate
is the heart of the design, so they are listed first.

1. Session identifier (the canonical identity)
   - A GUID, for example `7f3a1c20-...`.
   - Created by the Director when the session is created. Never changes.
   - Globally unique by construction (a random GUID).
   - Used for all internal routing. A human never sees it or types it.
   - This already exists today (`Session.Id`).

2. Session number (the human-friendly address)
   - A small integer, for example `7`.
   - Handed out by the Gateway, unique across the whole fleet.
   - This is what a person says out loud and what an agent types into a
     command: "send to 7."
   - It is reusable. When a session closes, its number returns to a pool and a
     future session can be given that number again. This keeps the numbers
     small enough for a human to actually use.
   - This is new.

3. Session name (the human-friendly label)
   - Optional free text, for example "feature-work" or "docs".
   - This is the existing `CustomName`. It defaults to the repository folder
     name. It is for human context only.
   - It is not guaranteed unique, so it is never the primary address. You can
     ask to send by name as a convenience, but it is resolved to a number, then
     to a GUID.

The golden rule that makes everything safe:

    A human or agent addresses a message by NUMBER or NAME.
    The Gateway resolves that to a GUID at the moment of sending.
    From that point on, the message travels by GUID only.

Because the message is bound to a GUID the instant it is sent, a number that
gets recycled a second later can never cause an already-sent message to be
delivered to the wrong session.

---

## 3. The big picture

The fleet is hub and spoke. The Gateway is the hub. Each Director is a spoke.
Sessions live inside Directors.

```
                          +-------------------+
                          |                   |
                          |     GATEWAY       |   <- the hub / router
                          |                   |       - knows every Director
                          | session number    |       - hands out session
                          |   <-> GUID map     |         numbers
                          |                   |       - resolves number to
                          +----+---------+----+         GUID, then routes
                               |         |
              dials back into  |         |  dials back into
              the Director's   |         |  the Director's
              own web service  |         |  own web service
                               v         v
                  +------------------+   +------------------+
                  |   DIRECTOR A     |   |   DIRECTOR B     |
                  |   (machine A)    |   |   (machine B)    |
                  |                  |   |                  |
                  |  +-----------+   |   |  +-----------+   |
                  |  | session 3 |   |   |  | session 7 |   |
                  |  +-----------+   |   |  +-----------+   |
                  |  +-----------+   |   |  +-----------+   |
                  |  | session 4 |   |   |  | session 8 |   |
                  |  +-----------+   |   |  +-----------+   |
                  +------------------+   +------------------+
```

Important fact about the wiring: a Director does not hold a permanent open
socket to the Gateway. Instead there are two web services that call each other
over the private Tailscale network:

- Director to Gateway: short messages going up. Every 15 seconds a Director
  sends a heartbeat that includes a snapshot of all its sessions. It also rings
  a "doorbell" whenever a session is created or changes state.

- Gateway to Director: when the Gateway needs to act on a specific session, it
  makes a fresh call back into that Director's own web service (the Control
  Application Interface, default port 7879), at the address the Director gave it
  when it registered.

This matters because it means the Gateway can already reach any online session
on any machine. We are not inventing a delivery channel. We are reusing the one
that already drives prompts, interrupts, and handovers today.

---

## 4. How a session gets its number

The Gateway is the only component that can see the whole fleet, so the Gateway
is the only component that can hand out numbers that are unique across the
whole fleet. The good news is that it already receives everything it needs.

```
  Director A                         Gateway
  ----------                         -------
  creates session (gets a GUID)
        |
        |  rings doorbell: "session-created, GUID=7f3a..."
        +-------------------------------------->  receives doorbell (fast path)
        |                                              |
        |  heartbeat every 15s, lists all sessions     |  reconciles the list,
        +-------------------------------------->       |  assigns the lowest
        |                                              |  free number to any
        |                                              |  session that does not
        |   heartbeat response carries the numbers     |  have one yet
        <-------------------------------------+        |
        |  now knows: GUID 7f3a... is number 7         |
        |  shows "#7" in the user interface            |
```

Rules for number assignment:

- The Gateway keeps a map of session number to GUID, and a pool of free
  numbers.
- When it first sees a session without a number, it assigns the lowest
  available number.
- When a session disappears from the fleet (it exited, or its Director went
  away for good), its number goes back into the free pool.
- Numbers are reused, lowest first, so a normal fleet stays in the single and
  low double digits.

What happens when the Gateway is down: nothing breaks locally. The Director
still creates and runs the session; it simply has no fleet number yet. The
number arrives within about 15 seconds of the Gateway coming back. This is
acceptable because the number only matters for cross-session messaging, and
cross-session messaging needs the Gateway anyway.

---

## 5. What a session can do (the methods)

These are the capabilities exposed to a running agent. They are delivered as
small command-line tools that ship on the path, so any agent in any session can
call them directly. Under each tool is the Gateway route it uses, for
implementers.

### 5.1 List the fleet

    cc-sessions

Shows every session running anywhere in the fleet:

    NUMBER  NAME             MACHINE     REPOSITORY            STATUS
    3       planner          machine-A   devthrottle          idle
    4       feature-work     machine-A   devthrottle          working
    7       docs             machine-B   mindzieWeb           idle
    8       qa               machine-B   cc-director           working

This is how an agent discovers who exists before talking to them. It is also
how a name like "docs" gets turned into a number, and then a GUID.

Under the hood: the Gateway's existing fleet aggregator (`GET /sessions`),
which already polls every Director and stamps the owning machine onto each row.
We add the session number column.

### 5.2 Find out who you are

    cc-whoami

Prints this session's own number, name, machine, and repository:

    You are session #4 ("feature-work") on machine-A, repo devthrottle.
    To message another session:  cc-send <number> "<message>"
    To see all sessions:         cc-sessions

Every session also receives its own number at startup in an environment
variable (`CC_SESSION_NUMBER`) and a one-line reminder of how to message other
sessions, so the agent knows the capability exists without being told.

### 5.3 Send a message to one session

    cc-send 7 "Can you run the integration tests on your branch?"

Sends the text to session 7. You may address by number (preferred) or by name:

    cc-send docs "Please update the API page when you get a chance."

If a name matches more than one session, the tool refuses and lists the
candidates with their numbers, so you can pick one.

Under the hood: the Gateway resolves the number or name to a GUID, then uses
the existing prompt route (`POST /sessions/{guid}/prompt`), which already finds
the owning Director and delivers the text. The message is wrapped with a sender
header (see 5.5).

### 5.4 Send a message to everyone (broadcast)

    cc-send all "Heads up: I am about to merge to main in 5 minutes."

Sends the same message to every other session in the fleet. You may also target
a group later (a named subset), but the first version supports "all."

Under the hood: the existing broadcast route (`POST /fanout`), which already
sends one piece of text to many sessions across many Directors in parallel.

### 5.5 What a received message looks like

When session 7 receives a message from session 4, it does not just see raw
text. It sees a framed message that tells it who is talking:

    [message from session #4 "feature-work" (machine-A)]
    Can you run the integration tests on your branch?

    (to reply: cc-send 4 "<your reply>")

The framing is what turns one-way text injection into a real conversation. The
recipient always knows who to reply to.

### 5.6 Delivery timing (busy versus idle)

A message is delivered into the target session by typing it in as a prompt, the
same way a handover works today. There are two cases:

- Target is idle: the message is delivered immediately.
- Target is busy (mid-turn): the message is placed in that session's existing
  prompt queue and delivered the moment the agent finishes its current turn.

This means a message never corrupts an agent that is in the middle of thinking;
it simply waits its turn.

---

## 6. End-to-end flow of a single message

The complete path of "session 4 on machine A messages session 7 on machine B."

```
  Agent in session 4 runs:
      cc-send 7 "run the tests"
            |
            | HTTP call to its own Director, or straight to the Gateway,
            | carrying { from: 4, to: 7, text: "run the tests" }
            v
      +-----------+
      |  GATEWAY  |   step 1: look up number 7        -> GUID 7f3a...
      |           |   step 2: look up number 4        -> "feature-work"
      |           |   step 3: find which Director owns 7f3a...  (Director B)
      |           |   step 4: wrap the text with the sender header
      +-----+-----+
            |
            | HTTP POST  /sessions/7f3a.../prompt
            | (the Gateway dials Director B's own web service)
            v
      +------------+
      | DIRECTOR B |   is session 7 idle?
      |            |     yes -> type the message in now
      |            |     no  -> add to session 7's prompt queue
      +-----+------+
            |
            v
      Agent in session 7 sees:
          [message from session #4 "feature-work" (machine-A)]
          run the tests
          (to reply: cc-send 4 "<your reply>")
```

Every step except number assignment and the sender wrapping already exists in
the codebase today. The new work is the number map, the small set of tools, and
the message framing.

---

## 7. Comparison with the peer-to-peer library from the video

The video built agent-to-agent communication on a peer-to-peer networking
library (each agent dials another agent directly by a public key, with a gossip
swarm for discovery and broadcast). It is worth checking, capability by
capability, whether our centralized approach gives the agents the same powers.

The short answer: for the actual messaging capabilities an agent cares about,
yes, we match or exceed it. The differences are all in the plumbing underneath,
and our plumbing is simpler because we already have a hub and a private network.

| Capability                         | Peer-to-peer library (video)              | Our Gateway approach                                  | Same for the agent?            |
|------------------------------------|-------------------------------------------|-------------------------------------------------------|--------------------------------|
| Address another agent              | By public key (a long opaque identity)    | By session number (small, human-friendly), then GUID  | Ours is friendlier             |
| Discover who exists                | Join a gossip swarm on a shared topic     | cc-sessions: ask the Gateway for the fleet directory  | Yes, both can list peers       |
| Send a direct message              | Open a direct connection or gossip message| cc-send: routed prompt through the Gateway            | Yes                            |
| Broadcast to many                  | Gossip publish to a topic                 | cc-send all: existing fanout                          | Yes                            |
| Know who a message is from         | Carried in the message payload            | Sender header wrapped onto every delivered message    | Yes                            |
| Reply to a sender                  | Dial the sender back                      | cc-send <their number>                                | Yes                            |
| Transfer large files               | Built-in resumable file transfer (blobs)  | Not in version 1 (we have file endpoints to build on) | Not yet, planned fast-follow   |
| Shared synced state across agents  | Built-in conflict-free synced documents   | Not in version 1 (no demand identified)               | Not yet                        |
| Network topology                   | Peer-to-peer mesh, no central server      | Hub and spoke through the Gateway                     | Different plumbing, same result|
| Getting through firewalls / routers| Library does hole punching, relay fallback| Tailscale already does this for the whole fleet       | Solved either way              |
| Works if the hub is down           | No hub, so not applicable                 | Cross-session messaging pauses; local sessions fine   | Acceptable tradeoff            |
| Delivery if target is offline      | Best effort                               | Best effort in version 1; durable mailbox fast-follow | Same in version 1              |

Why we deliberately do not copy the peer-to-peer parts:

- The whole reason the video needs a peer-to-peer library is that it has no
  central coordinator and has to solve discovery, addressing, and getting
  through firewalls from scratch. We already have a coordinator (the Gateway)
  and we already have a private network that gets through firewalls
  (Tailscale). Re-implementing a mesh on top of that would add complexity for
  no benefit.

- The hard parts of the video's approach (hole punching, relay fallback, gossip
  membership) are exactly the parts Tailscale and the Gateway already handle for
  us. The easy and valuable part (an agent can list peers and message them) is
  what we are building.

The two genuine capabilities the library has that we do not, large file
transfer and synced shared documents, are noted as possible follow-ups. Nothing
in this design blocks adding them later, because the same Gateway routing would
carry them.

---

## 8. Scope

### In version 1

- Gateway assigns and reuses fleet-unique session numbers, bound to GUID at
  send time.
- cc-sessions, cc-whoami, cc-send (to one, and to all).
- Sender header framing on every delivered message.
- Busy-aware delivery using the existing prompt queue.
- Each session learns its own number at startup and is reminded how to message
  others.
- Session number shown in the desktop user interface next to each session.

### Deliberately deferred (fast-follow)

- Durable mailbox: hold a message for a session whose machine is briefly
  offline and deliver it when the machine returns. Version 1 is best effort and
  returns a clear error if the target cannot be reached.
- Ask-and-wait: send a question and block for a structured reply, instead of
  fire-and-forget.
- Named groups: address a named subset instead of just "all."
- Large file transfer and synced shared state (the two peer-to-peer library
  features above), if a real need appears.

---

## 9. Key files this design builds on

For implementers. These already exist and are reused rather than replaced.

- `src/CcDirector.Core/Sessions/Session.cs` - the session model and
  `SendTextAsync` (how a message is typed into a session).
- `src/CcDirector.Core/Sessions/SessionManager.cs` - the per-Director session
  registry, keyed by GUID.
- `src/CcDirector.ControlApi/ControlEndpoints.cs` - the Director's own web
  service, including the prompt route that delivers text into a session and the
  per-session prompt queue.
- `src/CcDirector.ControlApi/GatewayClient.cs` - the Director-to-Gateway client
  (register, heartbeat, doorbell). The heartbeat already carries the per-session
  snapshot the Gateway needs to assign numbers.
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` - the Gateway routes,
  including the fleet session aggregator, the prompt proxy, the broadcast
  (fanout) route, and the resolver that finds which Director owns a session.
- `src/CcDirector.Gateway/Discovery/DirectorRegistry.cs` - the live list of
  Directors and the natural home for the new session-number map and free pool.
- `src/CcDirector.Gateway/Discovery/DirectorEndpointClient.cs` - the
  Gateway-to-Director client used to dial a specific session.
- `src/CcDirector.Gateway.Contracts/` - the shared data shapes; new message and
  directory shapes go here.
