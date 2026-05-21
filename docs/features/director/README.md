# Director feature specs

Spec docs for upcoming Director-side features. Each is small, has a clear acceptance bar, and can be implemented in isolation.

The three specs in this folder all came out of a single observation: when an agent stops to wait for you, the dashboard does not make that obvious enough, and when you do open the session, what the agent is asking is hard to find. These three features address that loop end-to-end.

| Spec | Status | Hook |
|---|---|---|
| [FEATURE_WAITING_FOR_INPUT.md](FEATURE_WAITING_FOR_INPUT.md) | PLANNED | Make it impossible to miss a waiting session at the dashboard level (banner, tab title, sort-to-top, optional browser notification). |
| [FEATURE_LIVE_SESSION_SUMMARY.md](FEATURE_LIVE_SESSION_SUMMARY.md) | PLANNED | Always-on "done so far / what's expected" pulse panel in the session detail view. Derived from local data; no LLM call. |
| [FEATURE_TERMINAL_QUESTIONS.md](FEATURE_TERMINAL_QUESTIONS.md) | PLANNED | Surface the actual question / plan / permission ask in the detail view with click-to-respond buttons. No more "open the raw terminal to find out what it wants." |

## Reading order

1. WAITING_FOR_INPUT - the dashboard-level signal.
2. LIVE_SESSION_SUMMARY - what you see immediately on click into a waiting session.
3. TERMINAL_QUESTIONS - the structured "respond here" surface that lives at the top of the detail view.

The three are not strictly dependent in code, but the user-experience story is most coherent in that order: notice -> understand -> respond.

## Where these live in the architecture

All three are Director-side. They do not require any Gateway changes - in the Phase 1 architecture, the Gateway is a thin directory page and the Director's `manager.html` is the canonical Manager UI. See [`../../architecture/gateway/GATEWAY_DIRECTOR_RESPONSIBILITIES.md`](../../architecture/gateway/GATEWAY_DIRECTOR_RESPONSIBILITIES.md).
