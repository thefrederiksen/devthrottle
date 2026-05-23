# Remote Experience - Working Plan (intent and decisions)

Companion to `remote_experience.html` (the surface-by-surface UX assessment).
That file is the assessment; this file is the intent and the decisions we have
made. All future work on the remote experience - the user's browser/phone view
of CC Director, served over Tailscale - is tracked here.

Last reconciled 2026-05-23, after point-by-point reviews of an earlier draft of
this plan and a batch of agent suggestions, against the `remote_experience.html`
vision. Filter applied to every candidate: add it only if it changes a decision
or prevents wasted work; describe-the-current-pixels items were dropped.

## What "remote experience" means

The away-from-keyboard view of a CC Director session: a phone or laptop browser
reaching the Gateway, a Director, or a single session's HTML view over the
tailnet (HTTPS via Tailscale Serve, no plain HTTP). The goal is that a user who
is not at their machine can understand what a session is doing and act on it,
with the same trust they would have sitting in front of the desktop app.

## The spine: three modes off one session

The remote experience is not one responsive page. It is three context-shaped
modes off the same session, surfaced as tabs and auto-selected by device. The
supervisor is what makes the non-terminal modes possible: it translates the raw
terminal firehose into what a human can consume in that context.

1. Desktop view - Terminal. Big screen, full attention. Show the raw terminal
   undigested; you can read the firehose, so summarizing it only gets in the way.
2. Mobile view - mobile-friendly typing + session supervisor. Small screen,
   glanceable, cannot read the raw view. Show a simple "is it working" status
   plus the supervisor's plain-language explanation of the latest turn,
   mobile-friendly text input, and tap-able view-links for any file the agent
   references. No raw turn-by-turn detail.
3. Voice mode - hands-free car mode. Hands and eyes unavailable. Voice in, voice
   out, talking to the session supervisor. The explanation is rendered as
   speakable text (real sentences, no code, no file paths, no symbols) so
   text-to-speech reads it cleanly. Hard requirement: the microphone API
   (`navigator.mediaDevices`) only exists on a secure origin, so voice mode is
   HTTPS-via-Tailscale-Serve or no mic at all - this makes "no plain-HTTP remote
   path" a functional requirement, not just security. (localhost is a secure
   context, so it works in dev; the failure only appears on a non-HTTPS remote
   path.)

The mechanism that makes mobile and voice instant: the supervisor "explain" runs
automatically the moment a turn finishes and is cached on the session - not on
demand when the page opens. So when you open your phone, the explanation is
already there, like glancing at a notification. This is the existing supervisor
`Mode == "explain"` work, run proactively per turn instead of only when asked.

## Priority work

### P1 - core experience

1. Supervisor digest fidelity. [DONE this session] The digest must reflect the
   session's true state and must never narrate a highlighted/pending menu option
   as a completed user action (it was caught reporting the user typed "Yes, go
   ahead with the revised plan" while the session was still WaitingForInput - the
   worst failure mode: telling you that you answered when you did not). The fix
   teaches the smart model to read the terminal screen in plain language (boxed
   text with a cursor in front = the agent's own suggestion, not a user action)
   as judgment guidance, not a hard rule. Implemented in
   `SupervisorService.AppendSessionContext`, regression test in
   `SupervisorAskTests`. This is the trust floor under the mobile and voice
   modes; keep it as a permanent guardrail, not a one-off fix. A second fidelity
   rule belongs here: restate the agent's actual question verbatim - clarify only
   in parentheses when it is ambiguous out of context, never reword or soften the
   decision being put to the user (paraphrasing "delete all files?" into "asking
   about cleanup" silently changes the decision you are answering).

2. Proactive explain + cache (gated, decision-point). Generate the supervisor
   explanation with the strong model (Opus) and cache it on the session so mobile
   and voice are instant on open - no spinner after the page loads. Decisions:
   - Gated to mobile-mode sessions only. Mobile mode is an explicit per-session
     flag, surfaced by a small mobile icon on the session in both the gateway
     directory and the session view, so you can see and control which sessions
     incur the Opus cost. Cost accepted for now; the gate keeps it off the whole
     fleet.
   - Regenerate on decision-point transitions (WaitingForInput / red / done), NOT
     on every turn. The "is it working" status stays always-live and cheap; the
     Opus briefing only refreshes at the moments you would actually pick up the
     phone. Regenerating every intermediate working turn is mostly wasted spend.
   - It is a background job - nobody is waiting on it - so it does not inherit the
     user-facing on-demand timeouts. On generation failure or timeout, preserve
     the last good cached briefing; never blank it, or you recreate the empty
     screen on a huge-context turn.
   - Groundwork already exists: the on-demand Explain button and its
     prompt/contract are built. P1.2 adds the trigger, the gating, and the cache -
     not the prompt from scratch.
   - Open question: should mobile mode auto-enable (and stay sticky) the first
     time a session is opened from a phone, so you do not have to remember to flag
     it before leaving?

3. Three modes as tabs. Terminal / mobile-supervisor / voice, auto-selected by
   device with a manual override.

4. Supervisor view-link minting. Give the session supervisor the ability to turn
   a file path into a Tailscale HTTPS URL the user can open in the browser. Rule:
   paths stay file paths by default (the agent and copy-path workflows are
   unchanged); the supervisor mints a URL only when (a) the user asks, or (b) we
   are in mobile view, where a raw `D:\...` path is useless and file references
   should auto-render as tap-able view-links. See Security posture for the
   serving decision.

### P2 - high-value features

5. Screenshots in the web viewer. Bring the desktop app's Screenshots panel to
   the web viewer. One data source, presentation diverges by viewport: desktop =
   right-side rail; mobile = a "Shots" tab/drawer. API: `GET /screenshots`
   (newest ~50 list) + `GET /screenshots/{id}` (bytes) + `?thumb` (see Media
   delivery), reusing the desktop's existing screenshots-directory resolution.
   Tap opens a full-res lightbox. Screenshots are how a remote user sees what the
   agent saw; the remote surface is blind without them.

6. Agent artifacts and the report library. The agent already produces rich
   self-contained HTML/PDF to communicate (this plan's companion doc, and the
   nav-report prototype). Make those first-class: a per-session artifacts folder,
   listed and served by the Director, opened in the same tab (HTML renders; PDF
   served inline), browser Back returns to the session. Built on the same
   view-link minting (P1.4) and the in-tab viewer. Because a report is served
   same-origin with the Control API, its own links and buttons can act on the
   live system (open a session, re-run a test) - a report becomes a small control
   surface, not just a snapshot.

7. Cross-surface navigation (no `gateway.url` dependency). The session view is
   served by the Director, so the Director button is just relative `/`. The
   Gateway link is derived client-side from the request host - same tailnet host,
   no port (port 443 is always the gateway, per the auto-provisioner convention) -
   so it needs no `gateway.url` config and never silently hides (the old failure
   mode). Two header buttons (Director / Gateway) ARE the navigation: there are
   only three fixed levels and you are always at the bottom, so a separate
   breadcrumb component is chrome without capability. This closes the
   "Gateway > Director > Session breadcrumb" idea as satisfied by the two buttons.
   (Loopback/desktop is the only case where the gateway port differs, and that is
   not the remote scenario.)

## Standing rules / doctrine

These are not features; they are rules every remote feature obeys.

- Responsive doctrine. Where the underlying data is the same (screenshots, files,
  diffs): one endpoint, one data source, presentation diverges by viewport
  (right-rail / tab / spoken) via a single breakpoint (~900px) - no second
  implementation per surface. Where the human's need differs by context (terminal
  vs digest vs spoken): the content itself is intentionally different, produced
  by the supervisor - that is a feature, not a violation. (Amended from the
  original "presentation diverges" rule so it does not flatten the three-mode
  content split.)

- Interaction-translation. Desktop affordances do not auto-port to touch. Every
  remote feature needs a deliberate touch-native equivalent (tap -> lightbox,
  long-press -> menu). And if a desktop gesture has no sensible touch equivalent,
  the control does not render on mobile at all - a hidden control beats a dead
  one (drag-to-attach, right-click, open-in-tab).

- Media delivery. The Control API serves a `?thumb` variant for lists (generated
  on the first request and cached to a thumbs dir keyed by source file +
  modified-time; a straight disk read after that) and full resolution on tap,
  with lazy-load + browser cache on top. Thumbnails are in from the start, not
  deferred: the consumer is mobile, and a list can only show small images anyway,
  so shipping full-res just to downscale in the browser wastes the phone's data
  plan. The one prerequisite is an image-resize dependency in the Control API
  project (none today; the Director is Windows-only so SkiaSharp / ImageSharp /
  System.Drawing all work).

- File view-links. The session supervisor mints Tailscale HTTPS links for files
  on demand or in mobile view (P1.4). There is no sandbox / allowed-roots
  restriction on what can be minted - see Security posture for why that is
  acceptable today and when to revisit.

- Preview / validate. Any remote surface can be rendered at phone size without a
  physical phone using DevTools device-metrics emulation over CDP. Reference
  viewports: desktop 1440x900, mobile 390x844. This is how mobile layout is
  verified before shipping (it caught the mobile composer overlap), and it
  doubles as the screenshot-capture method that feeds agent reports.

## Security posture

- Today the remote surfaces have no login. Security rests entirely on the
  Tailscale boundary. `tailscale serve` is tailnet-only (not Funnel): it is not
  on the public internet and is not reachable from other tailnets.
- Decision: the file view-link / serving endpoints have no sandbox or
  allowed-roots restriction. The supervisor can mint a link for any path. This is
  acceptable because the tailnet is solo - only the owner's own devices are on it
  - so "reachable over the tailnet" means "reachable by the owner's own devices."
- Deferred: real authentication. Until it lands, do not treat the tailnet
  boundary as more than it is.
- Revisit trigger: the moment a device that is not the owner's, a shared node, or
  a second user joins the tailnet, this decision must be revisited - add
  authentication before that exposure exists. The cheap first step discussed was
  signed capability links (an HMAC over the path with a per-Director secret, so a
  minted link grants access to exactly one file and nothing else can be
  requested) - it removes the open arbitrary-read without a full auth system.
- Transport: HTTPS-only via Tailscale Serve; there is no plain-HTTP remote path,
  ever. Voice mode depends on this directly (secure origin for the mic), so it is
  a functional rule, not only a security one.
- Known divergence / task: the intended posture is "Kestrel bound to loopback,
  the only remote path is Tailscale Serve" - but the code currently binds
  `IPAddress.Any` (0.0.0.0) in both `ControlApiHost` and `GatewayHost`, exposing
  the plain-HTTP port on every interface. Tailscale Serve proxies to localhost, so
  loopback is sufficient; binding to loopback is an open task, not a done fact.

## Status

- Item 1 (digest fidelity): implemented and tested this session.
- Groundwork already shipped: on-demand Explain button + prompt/contract; the
  legacy "Agent (legacy)" tab removed; Director/Gateway nav buttons added (the two
  done items struck from the report's ideas table).
- Quick standalone fix available now, independent of the rest: bind Control API +
  Gateway Kestrel to loopback (currently 0.0.0.0) to match the HTTPS-only posture.
- Everything else: planned, not started. P1.2 through P1.4 are the next build
  targets, in that order.
