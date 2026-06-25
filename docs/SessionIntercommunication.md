# Session Intercommunication

How one running session talks to another running session, across the whole
fleet, by relaying through its own Director.

Status: design / proposed. This document is the specification for the task
breakdown in issue #705.

---

## 1. What this feature is

Today a session (one running Claude Code or other agent CLI inside a terminal)
is an island. It can drive its own machine, but it has no easy, documented way
to talk to another session, especially one running under a different Director on
a different machine.

This feature gives every session a simple way to:

- See every other session that is currently running anywhere in the fleet.
- Send a text message to one of them.
- Send the same message to all of them at once.
- Know who a message came from, so it can reply.

The point is that an agent can coordinate with another agent the way a person
would lean over and say "hey, run the tests while I write the docs." A human can
also tell one agent "go ask the docs session about the database schema," and the
agent has a real way to do it.

This was inspired by an agent-to-agent communication demo built on a
peer-to-peer networking library. We do not need the peer-to-peer part, because
every Director already connects to a central Gateway, and the Gateway is already
a message router. We are adding a thin, clear layer on top of machinery that
already exists. Section 8 compares the two approaches.

---

## 2. What already works today (and what does not)

This feature is deliberately small because most of it already exists. It is
important to be honest about the starting point.

A session is spawned with three environment values: its own identifier
(`CC_SESSION_ID`), its own Director's web address (`CC_DIRECTOR_API`), and its
own Director's identifier (`CC_DIRECTOR_ID`). It is given nothing about the
Gateway or any other Director.

What that means concretely:

- Talk to its own Director: yes, already. A session can call
  `GET $CC_DIRECTOR_API/sessions` to see its siblings and
  `POST $CC_DIRECTOR_API/sessions/{id}/prompt` to message one of them. So
  same-Director, session-to-session messaging already works today with no new
  code. It only needs to know it can.
- Reach the Gateway directly: no. The session has neither the Gateway address
  nor the fleet token.
- Reach another Director: no. The session knows only its own Director.

So the only real gap is crossing the Director boundary, and the transport for
that already exists at the Gateway. The job of this feature is to open a safe
path to it and to make the capability obvious to the agent.

---

## 3. The two identifiers

A session is referred to two ways. Keeping them separate matters.

1. Session identifier (the canonical identity)
   - A GUID, for example `7f3a1c20-...`.
   - Created by the Director when the session is created. Never changes.
   - Globally unique. Used for all routing.
   - For everyday use, a person or agent can type a short unique prefix of it
     (for example `7f3a`) as the handle. If a prefix is ambiguous, the tool
     refuses and lists the candidates.
   - This already exists today (`Session.Id`).

2. Session name (the human-friendly label)
   - Optional free text, for example "feature-work" or "docs".
   - This is the existing custom name. It defaults to the repository folder
     name. For human context only.
   - Not guaranteed unique, so it is a convenience, not the canonical address.
     A name is resolved to an identifier before anything is sent; an ambiguous
     name is refused with the list of matches.

There are no Gateway-assigned session numbers in this design. That was
considered and deferred (see scope). Addressing is by identifier (or a short
prefix of it), with name as a convenience.

---

## 4. The big picture

The fleet is hub and spoke. The Gateway is the hub. Each Director is a spoke.
Sessions live inside Directors. The important addition in this design is that a
session never talks to the Gateway itself; it talks only to its own Director,
and the Director relays.

```
                          +-------------------+
                          |     GATEWAY       |   <- the hub / router
                          |                   |       routes a message to the
                          |                   |       Director that owns the
                          +----+---------+----+       target session
                               ^         ^
              Director relays  |         |  Director relays
              using the fleet  |         |  using the fleet
              token it holds   |         |  token it holds
                               |         |
                  +------------+-----+   +-+----------------+
                  |   DIRECTOR A     |   |   DIRECTOR B     |
                  |   holds the      |   |   holds the      |
                  |   Gateway token  |   |   Gateway token  |
                  |                  |   |                  |
                  |  +-----------+   |   |  +-----------+   |
                  |  | session   |   |   |  | session   |   |
                  |  | (calls    |   |   |  | (receives |   |
                  |  |  its own  |   |   |  |  the       |   |
                  |  |  Director)|   |   |  |  message)  |   |
                  |  +-----------+   |   |  +-----------+   |
                  +------------------+   +------------------+
                          ^
                          | a session calls ONLY its own Director
                          | (CC_DIRECTOR_API). It never sees the
                          | Gateway address or the fleet token.
```

The security principle, stated plainly: the agent process is never given the
Gateway address or the fleet token. Those live only in the Director
(`GatewayConfig`), and only the Director uses them. A session asks its own
Director to send a message; the Director forwards it to the Gateway with the
token it already holds. The token boundary stays at the Director.

This matters because an agent process runs model-driven, partly untrusted code.
Handing it the fleet token would put a credential that can drive every session
in the fleet into an environment that could leak it. Relaying through the
Director avoids that entirely, and it needs no new credential at spawn.

---

## 5. End-to-end flow of one message

The complete path of "a session on machine A messages a session on machine B."

```
  Agent in session A (machine A) runs:
      cc-send 9b2f "run the tests"
            |
            |  calls its OWN Director only:
            |  POST $CC_DIRECTOR_API/fleet/send  { toSessionId: 9b2f..., text }
            v
      +------------------+
      |   DIRECTOR A     |  step 1: stamp the sender identity (this session)
      |  (has the token) |  step 2: forward to the Gateway using the fleet
      |                  |          token it already holds
      +--------+---------+
               |
               |  POST {gateway}/sessions/9b2f.../prompt   (token attached)
               v
      +------------------+
      |     GATEWAY      |  finds which Director owns 9b2f...  -> Director B
      +--------+---------+
               |
               |  POST /sessions/9b2f.../prompt  (Gateway dials Director B)
               v
      +------------------+
      |   DIRECTOR B     |  deliver into the target session as a prompt
      +--------+---------+
               |
               v
      Agent in the target session sees:
          [message from "feature-work" (machine-A), id 4c81]
          run the tests
          (to reply: cc-send 4c81 "<your reply>")
```

Every hop except the Director relay and the sender framing already exists in the
codebase. The Gateway routes by identifier already (this is how prompts,
broadcasts, and handovers work today). The new work is the small relay on the
Director and the message framing.

---

## 6. What a session can do (the methods)

Capabilities ship as small command-line tools on the path, so any agent in any
session can call them directly. Each one calls the session's own Director
(`CC_DIRECTOR_API`); the Director relays to the Gateway as needed.

### List the fleet

    cc-sessions

Shows every session running anywhere in the fleet:

    ID      NAME           MACHINE     REPOSITORY     STATUS
    4c81    feature-work   machine-A   devthrottle    working
    9b2f    docs           machine-B   mindzieWeb     idle
    a1d7    qa             machine-B   cc-director    working

Calls `GET $CC_DIRECTOR_API/fleet/sessions`, which the Director satisfies by
forwarding to the Gateway's existing fleet aggregator. If this Director has no
Gateway configured, it returns just its own sessions.

### Find out who you are

    cc-whoami

Prints this session's own short id, name, machine, and repository, plus the
one-line how-to for messaging others.

### Send a message to one session

    cc-send 9b2f "Can you run the integration tests on your branch?"
    cc-send docs "Please update the API page when you get a chance."

Address by short identifier (preferred) or by name. An ambiguous prefix or name
is refused with the list of candidates. Calls
`POST $CC_DIRECTOR_API/fleet/send`, which the Director relays to the Gateway's
existing prompt route.

### Send a message to everyone

    cc-send all "Heads up: I am about to merge to main in 5 minutes."

Calls `POST $CC_DIRECTOR_API/fleet/broadcast`, which the Director relays to the
Gateway's existing broadcast route.

---

## 7. What a received message looks like

When a session receives a message, it does not just see raw text. It sees a
framed message that tells it who is talking:

    [message from "feature-work" (machine-A), id 4c81]
    Can you run the integration tests on your branch?

    (to reply: cc-send 4c81 "<your reply>")

The sender identity is stamped by the relaying Director from the calling
session's own identifier; it is not trusted from the request body. The framing
is what turns one-way text injection into a real conversation: the recipient
always knows who to reply to.

Delivery is best effort. The message is delivered into the target by the same
mechanism a handover uses (typed in as a prompt). If the target session cannot
be reached, the sender gets a clear error. There is no store-and-forward queue
in this version.

---

## 8. The one genuinely new capability: ask and reply

Everything above is one-way: you send a message and it lands. The existing
prompt mechanism cannot return the other agent's answer to you. That
request-and-response loop is the one capability that is genuinely missing and
genuinely valuable, for example "ask the session that has repo B loaded what its
database schema is, and get the answer back."

This is scoped as a separate increment after the one-way messaging lands,
because it needs a reply-capture design (for example, read the target's output
after the question using the existing output-buffer cursor, or agree a
structured reply convention). It is called out here so the overall direction is
clear, but it is not part of the first version.

---

## 9. Comparison with the peer-to-peer library

The video built agent-to-agent communication on a peer-to-peer networking
library (each agent dials another by a public key, with a gossip swarm for
discovery and broadcast). Checking it capability by capability, our centralized
approach gives agents the same powers; the differences are all in the plumbing,
and our plumbing is simpler because we already have a hub and a private network.

| Capability                         | Peer-to-peer library (video)              | Our approach (relay through the Director)             | Same for the agent?            |
|------------------------------------|-------------------------------------------|-------------------------------------------------------|--------------------------------|
| Address another agent              | By public key (long, opaque)              | By session identifier (short prefix), or name         | Comparable                     |
| Discover who exists                | Join a gossip swarm on a topic            | cc-sessions, relayed to the Gateway directory         | Yes                            |
| Send a direct message              | Direct connection or gossip               | cc-send, relayed to the existing prompt route         | Yes                            |
| Broadcast to many                  | Gossip publish to a topic                 | cc-send all, relayed to the existing broadcast        | Yes                            |
| Know who a message is from         | Carried in the payload                    | Sender stamped by the relaying Director               | Yes                            |
| Reply to a sender                  | Dial the sender back                      | cc-send to the sender's id (one-way today)            | One-way now; ask-reply is next |
| Ask and get an answer back         | Open a stream and read the response       | Separate increment (section 8)                        | Not yet                        |
| Transfer large files               | Built-in resumable transfer               | Not in this version                                   | Not yet                        |
| Shared synced state                | Built-in synced documents                 | Not in this version                                   | Not yet                        |
| Network topology                   | Peer-to-peer mesh                         | Hub and spoke through the Gateway                     | Different plumbing, same result|
| Through firewalls / routers        | Hole punching + relay fallback            | Tailscale already does this                           | Solved either way              |
| Credential handling                | Each agent holds its own key              | Agent never holds the fleet token; Director relays    | Ours is safer                  |

Why we deliberately skip the peer-to-peer parts: the library exists because the
video has no central coordinator and must solve discovery, addressing, and
firewall traversal from scratch. We already have a coordinator (the Gateway) and
a private network that gets through firewalls (Tailscale). The hard parts of the
mesh approach are exactly what Tailscale and the Gateway already handle for us.
The easy, valuable part (an agent can list peers and message them) is what we
build.

---

## 10. Scope

### In this version

- Director-side relay endpoints: list the fleet, send to one, broadcast to all.
- The fleet token stays on the Director and is never exposed to a session.
- Command-line tools: cc-sessions, cc-whoami, cc-send (to one, and to all).
- Sender framing on every delivered message, stamped by the Director.
- A one-line reminder at spawn so each session knows the capability exists.
- Addressing by session identifier (short prefix) or name.

### Deliberately deferred

- Ask and reply: send a question and get the answer back (section 8). The next
  increment.
- Gateway-assigned session numbers and a number-reuse scheme. Not needed;
  identifiers and names cover addressing.
- A durable mailbox / store-and-forward for a target that is briefly offline.
  This version is best effort.
- Named groups beyond "all".
- Large file transfer and synced shared state.

---

## 11. Key files this design builds on

These already exist and are reused rather than replaced.

- `src/CcDirector.Core/Sessions/SessionManager.cs` - builds the session
  environment at spawn (`CC_SESSION_ID`, `CC_DIRECTOR_API`, `CC_DIRECTOR_ID`);
  the place for the one-line awareness note. No new credential is injected.
- `src/CcDirector.ControlApi/ControlEndpoints.cs` - the Director's own web
  service, including the existing prompt route. The new `/fleet/*` relay
  endpoints go here.
- `src/CcDirector.ControlApi/GatewayClient.cs` - the Director's authenticated
  client to the Gateway. Reused to forward relayed messages.
- `src/CcDirector.Core/Configuration/GatewayConfig.cs` - where the Gateway
  address and fleet token live. They stay here, server-side, never handed to a
  session.
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` - the Gateway routes that
  are reused as-is: the fleet session aggregator, the prompt route, and the
  broadcast route.
