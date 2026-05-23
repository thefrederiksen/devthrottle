# Remote Experience - Agent Suggestions

Source: things learned while building the mobile session UI changes (commit `ebafdba`:
removed the legacy Agent tab, added the Explain button, added Director/Gateway nav
buttons). These are candidate additions to `remote_experience.html`. Nothing here is
auto-applied except where marked DONE/DECIDED.

Status legend:
- DONE        = already shipped in code (commit ebafdba)
- DECIDED     = decision made, already written into remote_experience.html
- OPEN        = needs a decision before it goes in the report

---

## A. Current-state corrections (the report describes things we already changed)

The report's "current state" sections (esp. section 3, Session view) are now partly stale:

- **DONE** - Session view tabs no longer include "Agent (legacy)". (Was P3 "hide the
  deprecated Agent (legacy) tab".)
- **DONE** - Session view top bar no longer has a bare back arrow. It now has two nav
  buttons: **Director** (this Director's home, relative `/`) and **Gateway**
  (cross-Director directory, absolute `gateway.url`). Partial down-payment on the P3
  "Gateway > Director > Session breadcrumb".
- **DONE (groundwork)** - The supervisor "Explain" now exists as an on-demand button
  (Opus). The P1 vision (proactive at turn-end + cached) is still open, but the
  prompt and contract now exist to build on.

Suggested action: update section 3 wireframe/components to match, and move the two
done P3 items out of the "ideas" table into a "shipped" note.

---

## 1. Record the two hard-won Explain prompt rules  (OPEN -> recommend adding)

Non-obvious requirements that are easy to regress, so they belong next to the P1
"explain" item as hard rules:

- Restate the agent's question **verbatim**. Clarify only in parentheses when the bare
  question is ambiguous out of context. Do not reword, soften, or summarize.
- A highlighted / boxed menu option in the terminal buffer is the **agent's suggestion**,
  NOT a user answer. The briefing must not confabulate that the user already responded
  (regression seen: it once narrated a highlighted "Yes, go ahead" as if the user typed
  it while the session was still WaitingForInput).

Both are now encoded in `BuildExplainPrompt` and covered by tests, but the report should
state them as design requirements so a rewrite does not lose them.

---

## 2. Model / cost stance for proactive Explain  (DECIDED - already in report)

Decision (now written into section 5 Mobile panel and the "How is the mode chosen?"
finding):

- Proactive **Opus** "Explain" runs at **every turn-end**, but **only for sessions in
  "mobile mode"**.
- **Mobile mode is an explicit per-session flag**, surfaced by a small **mobile icon**
  on the session in both the gateway directory and the session view.
- Cost is **accepted for now** (the experience win dominates); it is gated to mobile-mode
  sessions only. Optimize later if warranted (regenerate only on red/waiting, or fall
  back to the Haiku turn-summary).

Still to build in code: the per-session mobile-mode flag, the mobile icon, and the
turn-end Opus generation + cache for mobile-mode sessions.

---

## 3. Cross-surface navigation depends on `gateway.url`  (OPEN - needs decision)

- The session view is **served by the Director**, not the gateway. So `/` is relative to
  the Director (the Director button works anywhere), but any link **up to the gateway**
  needs an **absolute URL**, which the Director only has if `gateway.url` is configured.
  On this machine it was unset, so the **Gateway button silently hides**.
- This is the concrete blocker behind the section 4 "movement is ad hoc / port hop is
  visible" finding and the P3 "Gateway > Director > Session breadcrumb" idea: a reliable
  breadcrumb is impossible until every Director knows its gateway address.

Open decisions:
- (a) How does a Director learn its gateway URL? Manual `gateway.url` config (easy to
  forget -> button hides) vs. **learn it automatically from the registration handshake**
  (the Director already registers/heartbeats with the gateway). Recommend the automatic
  path to kill the silent-hide failure mode.
- (b) Is the target a full clickable breadcrumb (Gateway > Director > Session), or are the
  two header buttons (Director / Gateway) the intended end state?

---

## 4. Voice / car mode hard-requires HTTPS  (OPEN -> recommend adding as a constraint)

`navigator.mediaDevices` (the microphone) only exists on a **secure origin**. Plain http
over a tailnet IP strips the mic API entirely. So the car experience is not "nice to have
HTTPS", it is **HTTPS via Tailscale Serve or no mic at all**. The session view already
preflights this and prints the `tailscale serve --bg --https=443 http://localhost:<port>`
fix. Record it as a hard constraint on the section 5 "Car / voice" target, not an
optional nicety.

---

## 5. Known timeout ceilings  (OPEN -> recommend adding)

On-demand Explain rides the `/supervisor/ask` path:
- **45s** gateway forward timeout (DirectorEndpointClient).
- **60s** supervisor process timeout (SupervisorService.ProcessTimeout).

This bounds how large a context the on-demand path can chew before it fails, and is
another argument for the P1 **cached** version (the phone reads the cache instantly
instead of waiting on a live Opus call behind a spinner).

---

## 6. Auth / HTTPS posture is effectively decided  (OPEN -> recommend folding into P1 security)

In practice the stance is already settled, even though the report still frames auth as an
open P1 question:
- Tailnet boundary + **HTTPS-only via Tailscale Serve**.
- Kestrel bound to loopback; the only remote path is Tailscale Serve.
- No plain-HTTP-from-phone state should exist.

Suggested action: record this as the chosen posture under the P1 "decide the auth posture"
item, rather than leaving it open.

---

## Quick triage table

| # | Topic | Status | Action |
|---|-------|--------|--------|
| A | Stale current-state (legacy tab, nav buttons, explain exists) | DONE | update section 3 + ideas table |
| 1 | Two Explain prompt rules (verbatim question; menu != user answer) | OPEN | add as design requirements |
| 2 | Per-turn Opus explain gated on mobile-mode flag + mobile icon | DECIDED | in report; build the flag/icon/cache |
| 3 | Cross-surface nav needs gateway.url (breadcrumb blocker) | OPEN | decide config vs auto-discovery |
| 4 | Voice mode hard-requires HTTPS (secure origin for mic) | OPEN | add as hard constraint |
| 5 | Timeout ceilings (45s forward / 60s process) | OPEN | add as known limits |
| 6 | Auth/HTTPS posture already decided in practice | OPEN | fold into P1 security item |
