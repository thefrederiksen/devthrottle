# Iroh - Repository Overview and Briefing

A primer on the `n0-computer/iroh` repository: what it is, what it does, and how
its pieces fit together. Written to hand to another agent as the basis for a
discussion about the project.

This is the library that powers the agent-to-agent communication shown in the
"Plans for Fable 5" video (the creator called it "Iron"; the real name is Iroh).
It is a candidate building block for peer-to-peer messaging, but the purpose of
this document is simply to explain accurately what the repository is and does.

---

## 1. At a glance

- Name: iroh
- Repository: https://github.com/n0-computer/iroh
- Maintainer: n0 (also written "number0"), a developer-infrastructure company
- Language: Rust (about 99.6 percent of the codebase)
- Status: version 1.0.0, released 15 June 2026 (stable)
- Scale signals: roughly 10.8k stars, about 2,500 commits on the main branch
- Documentation: https://docs.iroh.computer
- One-line tagline: "IP addresses break, dial keys instead."

In one sentence: iroh is a modular networking stack that lets one program open a
direct, encrypted connection to another program by its public key, no matter
where either one sits on the network, and falls back to relays when a direct
connection cannot be made.

---

## 2. The problem it solves

Normal networking forces you to know where another machine is: an IP address and
a port. That breaks constantly. Addresses change, machines move between networks,
home and office routers hide machines behind network address translation, and
firewalls block incoming connections. Building a program where two ordinary
computers talk directly is therefore hard, and most people give up and route
everything through a central server they have to run and pay for.

Iroh removes the central server from the connection itself. You address the other
side by a stable public key instead of an address. Iroh figures out where that
key currently is, punches a direct path through routers when it can, and only
uses a shared relay as a fallback. The connection is always end-to-end encrypted,
because the key you dial is also the identity you authenticate against.

---

## 3. The core mental model

These are the concepts a newcomer must hold to understand any iroh program.

- Endpoint
  The local networking interface a program creates. It is the thing that binds
  to the network and from which you make and accept connections. A program
  usually has exactly one.

- EndpointId (also seen as NodeId)
  The public key that identifies an endpoint. This is the "address" you dial.
  It is stable: it does not change when the machine moves networks. It is also
  the cryptographic identity, so dialing a key and trusting the peer are the
  same act. Built on Ed25519 public keys.

- Connection
  Established between two endpoints over QUIC. QUIC gives authenticated
  encryption, many concurrent streams without one stream blocking another, and
  datagram transport. Practically: once you have a connection, you have a
  reliable encrypted pipe (or several) to the peer.

- ALPN (Application Layer Protocol Negotiation)
  A short label that says which protocol a given connection is for. Because each
  connection carries an ALPN, a single endpoint can host many different
  protocols at once (file transfer, messaging, your own custom protocol) and
  route each incoming connection to the right handler.

- Router
  The dispatcher on the accepting side. You register handlers against ALPNs, and
  the router hands each incoming connection to the handler whose ALPN matches.

- Discovery
  How an EndpointId is turned into a current network location. Iroh ships
  discovery mechanisms (including a DNS-style public-key lookup service) so that
  dialing a key actually finds the peer. This is what lets you dial a key you
  have never contacted before.

- Relays and hole-punching
  Iroh always tries for a direct connection first, using NAT hole-punching.
  Reported success is high (around 95 percent in production conditions). When a
  direct path cannot be made, traffic falls back to public relay servers that n0
  runs and continuously performance-monitors. The relay only forwards encrypted
  bytes; it is not a trusted middleman.

How a connection actually happens, in order:
1. You call connect with the peer's EndpointId and an ALPN.
2. Discovery resolves where that key currently is.
3. Iroh attempts a direct hole-punched QUIC connection.
4. If that fails, it falls back to a relay, transparently.
5. The accepting side's Router dispatches the connection by its ALPN.

---

## 4. What is in the repository (workspace layout)

The repository is a Rust workspace (a monorepo of several crates):

- iroh
  The core library: endpoints, connections, hole-punching, relay communication.
  This is the crate most programs depend on directly.

- iroh-relay
  The production relay client and server implementation (the fallback path).

- iroh-base
  Common base types shared across the stack, such as EndpointId and relay URLs.

- iroh-dns-server
  The discovery service: a DNS and public-key (Pkarr-style) lookup server, run
  publicly as part of the iroh infrastructure.

Note that the higher-level protocols below (gossip, blobs, docs) live in their
own sibling repositories under the same n0-computer organization, not inside
this one. They are built on top of the core iroh crate.

---

## 5. The protocol ecosystem (what you build on top)

The core iroh crate gives you connections. By itself that is a transport. Most
real applications use one of n0's ready-made protocols layered on top, each in
its own repository:

- iroh-gossip
  A publish-subscribe overlay network. Peers interested in the same topic form a
  swarm and messages fan out across it. It is designed to need very little per
  peer, so it scales to many participants and tolerates peers joining and
  leaving. The underlying algorithms are HyParView (membership) and PlumTree
  (broadcast). This is the piece used for the agent-to-agent messaging in the
  video: agents subscribe to a shared topic and broadcast to each other.

- iroh-blobs
  Content-addressed file and data transfer using BLAKE3 hashing. Handles
  anything from kilobytes to terabytes, with resumable transfers. You ask for
  content by its hash and iroh-blobs fetches it from whoever has it.

- iroh-docs
  An eventually-consistent key-value store that syncs across peers, with a
  built-in synchronization protocol. Useful for shared mutable state without a
  central database. Values can reference blobs.

A simple way to remember the three: gossip is for messages, blobs are for files,
docs are for shared state.

---

## 6. Language bindings

Iroh is written in Rust, but it is not Rust-only. The `iroh-ffi` project provides
foreign-function-interface bindings so other languages can use it. The 1.0
release ships official bindings for Rust, Python, Node.js, Swift, and Kotlin, and
iroh also compiles to WebAssembly to run in browsers.

For a Python-based agent harness, the Python bindings are the practical entry
point; the API mirrors the Rust shape (create an endpoint, build the protocol,
subscribe or connect, send and receive).

---

## 7. A minimal example (gossip, the messaging use case)

The shape of a two-peer gossip program, the pattern an agent-to-agent messaging
feature would use. Rust API names shown; the Python binding mirrors them.

Peer that starts the swarm:
1. Bind an endpoint (this gives you your EndpointId, your address).
2. Build the gossip protocol on that endpoint.
3. Set up a router so the endpoint accepts gossip connections.
4. Choose a 32-byte topic id (the shared "room").
5. Subscribe to the topic with no bootstrap peers (you are the first node).
6. Split the subscription into a sender and a receiver.
7. Broadcast messages with the sender; read incoming messages from the receiver.

Second peer that joins:
- Same steps, except subscribe with the first peer's EndpointId as a bootstrap
  peer. That one piece of information pulls it into the existing swarm. After
  that, peer discovery spreads through the gossip layer itself.

The only out-of-band information the second peer needs is the first peer's
EndpointId (or an encoded "ticket" that bundles the topic plus bootstrap peers).
There is no central server address to configure.

---

## 8. How this maps to agent-to-agent communication

In the video, each agent is a program that:
- binds an endpoint (so it has a public-key identity),
- subscribes to a shared gossip topic,
- broadcasts messages to the topic and reads messages from it,
- and can fall back to or additionally use direct connections and blob transfer
  for larger payloads.

The appeal is that there is no central hub to run: agents on different machines,
behind different routers, find and message each other directly, addressed by key.
The trade-off is that you adopt a peer-to-peer mesh and its discovery and
membership machinery.

(For contrast, in a system that already has a central coordinator and a private
network, much of what iroh provides - discovery, addressing, firewall traversal -
is already handled, and a hub-and-spoke design can deliver the same agent-facing
capabilities without a mesh. That comparison is a separate discussion; this
document is about what iroh itself is.)

---

## 9. Glossary

- Endpoint: the local networking interface a program creates.
- EndpointId / NodeId: the public key that identifies and addresses an endpoint.
- ALPN: a label on a connection saying which protocol it carries.
- Router: dispatches incoming connections to handlers by their ALPN.
- Discovery: turning an EndpointId into a current network location.
- Hole-punching: establishing a direct connection through routers that normally
  block incoming traffic.
- Relay: a public fallback server that forwards encrypted bytes when a direct
  connection cannot be made.
- QUIC: the encrypted transport protocol iroh connections run on.
- Ticket: an encoded bundle (for example topic plus bootstrap peers) that lets a
  newcomer join without manual configuration.
- Swarm: the set of peers participating in a gossip topic.

---

## 10. Sources

- Repository: https://github.com/n0-computer/iroh
- Documentation: https://docs.iroh.computer
- Gossip protocol: https://github.com/n0-computer/iroh-gossip
- Blobs protocol: https://github.com/n0-computer/iroh-blobs
- Docs protocol: https://github.com/n0-computer/iroh-docs
- Foreign-function bindings: https://github.com/n0-computer/iroh-ffi

---

## 11. Suggested questions to explore with another agent

- Which of the three protocols (gossip, blobs, docs) does our use case actually
  need, and which are unnecessary?
- For our environment, what does iroh give us that a central coordinator plus a
  private network does not already provide?
- How would discovery work for our peers: the public iroh DNS service, or a
  self-hosted discovery and relay setup?
- What are the operational costs of running (or depending on) relay and discovery
  infrastructure?
- Through the Python bindings, how mature and complete is the gossip surface
  compared with the Rust crate?
- What is the failure behavior when a peer is offline, and does it match the
  delivery guarantees we need?
