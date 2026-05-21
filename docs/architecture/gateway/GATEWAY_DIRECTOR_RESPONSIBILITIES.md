# Gateway vs. Director: Division of Labor

**Status:** PLANNED (Phase 1 is the committed scope; Phase 2+ rows are a sketch, not a commitment)
**Date:** 2026-05-19
**Audience:** Anyone deciding "should I add this feature to the Gateway or the Director?" The default answer is in this doc.

## Related documents

- [GATEWAY_DIRECTOR_ARCHITECTURE.md](GATEWAY_DIRECTOR_ARCHITECTURE.md) - CURRENT state
- [GATEWAY_DIRECTOR_TARGET.md](GATEWAY_DIRECTOR_TARGET.md) - TARGET (Phase 1: thin Gateway, Director-canonical)

---

## 1. The rule

> **The Director is the canonical place where work happens.**
> **The Gateway is a thin receptionist that routes you to the right Director.**

Office metaphor: walk into the building, the receptionist (Gateway) sees who you are and shows you the door to the right department (Director). Inside that department you talk directly to the workers (sessions) and the department manager (Director's Manager UI). The receptionist does not run the meeting. They route, then get out of the way.

In Phase 1 the routing is even simpler than "relay" - it's "**deeplink**." The Gateway hands you a URL to the Director and your browser opens it directly. The Gateway never sees the conversation between you and the Director.

---

## 2. The decision matrix

Each feature has a **Phase 1 home** (committed) and a **Phase 2+ home** (sketch). Phase 1 is what we're building. Phase 2+ is what we might build later if the need is concrete.

### 2.1 Director-canonical (and that's the whole story for Phase 1)

These features live entirely on the Director. The Gateway does NOT have them in Phase 1.

| Feature | Phase 1 home | Phase 2+ home (sketch) |
|---|---|---|
| Manager UI (cards + detail view + new session modal) | Director only | Director (unchanged); Gateway might add an aggregated view |
| List sessions | Director only | Director; Gateway might aggregate read-only |
| Send prompt | Director only | Director; Gateway might add a proxy route for convenience |
| Interrupt | Director only | same |
| Rename session | Director only | same |
| Get buffer (ANSI-cleaned text) | Director only | same |
| Get summary (structured JSON) | Director only | same |
| Get / generate recap | Director only | same |
| Same-Director handover | Director only | same |
| Create a session | Director only | Director; Gateway might add an "on which Director?" wrapper |
| Kill a session | Director only | same |
| List repos available for a New Session | Director only | same |
| Per-session raw terminal view (`/sessions/{sid}/view`) | Director only | Director only (no reason to ever move) |
| Avalonia desktop UI | Director only | Director only |
| Source-control / git changes view | Director only | Director only |
| Workspaces dialog / history | Director only | Director only |

**Notable change vs. CURRENT:** today the Gateway has proxy routes for most of these (`PATCH /sessions/{sid}`, `POST /sessions/{sid}/prompt`, etc.). **In Phase 1 those proxy routes are removed from the Gateway.** They only exist on the Director.

### 2.2 Gateway-canonical (the only things the Gateway owns in Phase 1)

| Feature | Phase 1 endpoint | Why on the Gateway |
|---|---|---|
| Director registry (who is online) | `GET /directors`; populated by `POST /directors/register` + `POST /directors/{id}/heartbeat` | Gateway is the central rendezvous point |
| Directory page HTML | `GET /` | The user-facing entry point |
| Auth on the directory page | cookie / bearer middleware | Protects the front door |
| Health | `GET /healthz` | Operational liveness |

That is the **entire** Gateway surface in Phase 1.

### 2.3 Deferred to Phase 2+ (NOT in Phase 1)

These would justify Gateway smarts. They are deferred. **Do not implement them in Phase 1.**

| Feature | Why deferred | Likely Phase 2+ home |
|---|---|---|
| Aggregated session list across all Directors | Requires session tracking on the Gateway, not just Director tracking | Gateway |
| Live `/events` SSE with `session.*` events to browsers | Only valuable once aggregated views exist | Gateway |
| Fan-out to multiple sessions across multiple Directors | Rarely needed today; design once we have a concrete use | Gateway |
| Cross-Director handover orchestration | Same. Today's same-Director handover covers our actual usage | Gateway |
| Chat-with-the-receptionist interface ("talk to all my agents") | Needs aggregated state + event push first | Gateway |
| Remote Director spawn (start cc-director on another machine) | Adding a machine is a manual step in Phase 1 | Per-machine launcher agent + Gateway |
| Per-Director registration tokens | Shared bearer is fine for Phase 1 | Gateway |
| Per-user identity | Out of scope | Gateway + IDP |

### 2.4 Internal-only (not exposed via HTTP)

Director internals. Mentioned so we don't accidentally try to push them through the Gateway in any phase.

- `SessionManager` (in-memory map)
- `SessionStateStore`, `SessionHistoryStore`, `RepositoryRegistry`
- `HookInstaller` / `HookRelayScript` / `DirectorFileEventWatcher`
- `RecapCache` (in-process)
- ConPty / `ISessionBackend` lifecycle
- `InstanceRegistration` filesystem fallback

---

## 3. The Director-local Manager UI is the only Manager UI

The Director's `Web/manager.html` is canonical. It has:

- The cards list, scoped to this Director's sessions
- The detail view (editable name, badge, recap, send-prompt, interrupt, rename, open raw view)
- The New Session modal
- Everything else we built

The **Gateway's** `Web/manager.html` is replaced in Phase 1 with a simpler `Web/directory.html` (or equivalent) that just lists Directors with deeplinks. No cards, no detail view, no proxy logic.

There is no longer "two copies of the same UI to keep in sync." There is **one** Manager UI (on the Director) and a separate **directory** page (on the Gateway). They serve different purposes.

---

## 4. The four locked-in decisions, restated under the new framing

| Decision | Phase 1 choice | Implication |
|---|---|---|
| Director-local Manager UI scope | **Canonical, full-featured. The only Manager UI.** | The Gateway has no Manager UI of its own in Phase 1. |
| Per-session raw terminal view | Director-only; Gateway page deeplinks to Director URLs | Same as before. |
| Create / kill session lifecycle | **Director only.** No Gateway proxy route in Phase 1. | If you need to create a session in machine B, click into Director B from the Gateway directory and use Director B's New Session modal. |
| Spawn a new Director | Manual on each machine in Phase 1. Remote spawn deferred. | Adding a machine = install cc-director, set `gatewayUrl`, start it once. |

### Configuration (recommendation, still soft)

| Decision | Recommendation | Rationale |
|---|---|---|
| Where does each piece of configuration live? | Director-local files only (`cc-director.json` on each Director machine; `gateway.json` on the Gateway machine). No Gateway-to-Director config push. | Smallest viable scope. Add a config push channel later if it becomes painful. |

If anything in that recommendation is wrong, flag it and I'll fold it in.

---

## 5. How to use this doc when adding a feature

1. **Could a single Director do this on its own?**
   - **Yes** -> implement on the Director. Done. The Gateway adds nothing.
   - **No, it needs to span Directors** -> stop. Don't build it in Phase 1. Add it to section 2.3 (deferred). Revisit when the need is concrete.

2. **Is it about who's online across the fleet?**
   - **Yes** -> Gateway. This is the only category of Phase 1 Gateway work.

3. **Is it OS / process / desktop-UI level?**
   - **Yes** -> Director only. The Gateway has no surface for it in any phase.

If a feature feels like it belongs in two of the above, talk it out before implementing. The categories are designed to be mutually exclusive.

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-05-19 | claude (cc-director assistant) | Initial decision matrix with the "Gateway-primary, Director-mirrors" framing. |
| 2026-05-19 | claude (cc-director assistant) | **Reframed.** Director is now canonical; Gateway is a thin receptionist that deeplinks to Directors. All per-session features moved to "Director-canonical." Aggregated and cross-Director features moved to "Deferred to Phase 2+." Added Phase 1 vs Phase 2+ columns so scope is unambiguous. |
