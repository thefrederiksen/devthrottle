# Mission: Alerts - one place for everything that needs the owner

Opened 24 October 2026. Architect: session cf5a8772. Seats: Delivery Lead (top-level, answers to the
owner), Developers, a Reviewer on a different agent (Codex, else Claude Code on Fable, else on Opus).

---

## 1. The mission

Replace the three approval cards that are stacked into the session list with **Alerts**: one shared
system, on the phone and in the Cockpit, where a red count in the header says how many things need the
owner, a tap opens the list, an item opens to be answered, and anything already handled can be cleared.

## 2. The why

On 24 October at 21:53 the owner opened the phone app and found a restart request he had **already
accepted** taking up the whole screen above his sessions. It could not be cleared, it overflowed the
right edge of the phone, and it was written in machine diagnostics. His words:

> "We can't have them lingering around like this. If we want anything, we need a messaging system that
> comes up and shows there are one, two, or three. And we can have alerts at the top, little red
> bubbles, showing there are things you can view and you can open them and see them. You need to be
> able to clear them. We can't have this shit staying around forever."

> "It needs to work on the mobile app, the phone app, and it also needs to work from the cockpit."

> "It needs to be a professionally done feature. Not this slapped together bullshit that looks like
> somebody duct taped an app together."

The review that led here (dev report 33bf61f3) found the cause is structural, not one bad card:

| Card | Added | Phone | Cockpit | Can it be cleared? | How long it stays |
|---|---|---|---|---|---|
| Director restart request (`client-core/src/restart`) | 7 Sep, #2768 | yes | yes | **never** | pending 30 min; accepted up to **3 h** (`RunningExpiry`); closed +30 min |
| Secret transfer (`client-core/src/secrets`) | 10 Oct, #3817 | yes | yes | only if answered on this device, until a reload (`answeredHere` is `useState`) | until answered |
| Message request (`client-core/src/fleet/LinkRequestsPanel.tsx`) | 23 Oct, #4376 | yes | **not mounted** | same as above | until answered |

Three components, three 5-second pollers, three CSS blocks copied into each app, three lifetime rules,
three clearing rules, and every one of them inline in the session list. Each feature followed the one
before ("mounted above the roster where Needs you lives") and nobody built the home these items needed.
The restart card also overflows (a 46-character launcher version with no break point, and no
`overflow-wrap` on `.restart-request-line` although the phone learned that rule in #838) and prints the
capability check's internal reasons verbatim, which contradict each other ("can be restarted" ... "will
refuse a restart"). A fourth feature would have added a fourth block. This mission ends that pattern.

## 3. The goal

All of these are true on origin/main, proven by the check in section 7:

1. **Nothing that asks the owner for something renders inside the session list**, on the phone or in
   the Cockpit. `RestartRequestsPanel`, `SecretTransfersPanel` and `LinkRequestsPanel` are no longer
   mounted in `apps/mobile/src/pages/Home.tsx` or `apps/cockpit/src/sessions/SessionRoster.tsx`, and a
   test fails if any panel of that kind is mounted there again.
2. **One bell in the header of both apps** (phone app bar; Cockpit left rail head), from ONE shared
   client-core component. It carries a red count of the alerts that **need an answer** - only those.
   No count when there are none.
3. **Tapping the bell opens the Alerts tray**: a bottom sheet on the phone, a popover anchored to the
   bell in the Cockpit. "Needs you" first, "Earlier" below, one line per alert, newest first.
4. **Opening an alert shows its detail and its actions** - Accept/Decline, Approve/Deny, Allow/Refuse -
   with the restart's existing two-step confirmation kept. Diagnostics (launcher version, capability
   check) sit behind a collapsed "Details", wrapped, never overflowing.
5. **Clearing works and is remembered on the Gateway.** Every alert in "Earlier" has an X; the tray has
   "Clear all". Clearing on the phone clears it in the Cockpit and survives a reload. An alert that
   still needs an answer cannot be cleared - it leaves when answered or when it expires.
6. **Answered means gone from the count at once.** An accepted restart moves to "Earlier" immediately
   as a status line ("Accepted - waiting for the Director"), never a red card, and never lingers in
   "Needs you" for 3 hours.
7. **Nothing overflows** at 360 px phone width or in a 280 px Cockpit popover, with the longest real
   strings (proved with a fixture holding the 46-character version string and a 300-character reason).
8. **Every existing feature behaviour survives**: the secret transfer's place-of-answer ("phone",
   "cockpit"), the message request's push notification and its close-on-answer, the restart's
   Gateway-side checks and `canAccept`. Their push notifications open the app straight onto that alert.
9. **Merged to origin/main and live** on the hosted Gateway (see section 9), with a QA report of
   screenshots at phone and desktop width: empty, one, three, an alert opened, an answer, a clear,
   clear-all, a failure to load, and the overflow fixture.

## 4. Decisions

Each in the owner's words where he gave them. The Delivery Lead takes its answers from here.

- **D1. Build it as one shared notification system for the phone and the Cockpit.** The owner, on the
  review: "That sounds good." (accepting: one shared inbox, a same-day stopgap first, run as a mission.)
- **D2. Shape: a red count at the top, open to view, clear when done.** "alerts at the top, little red
  bubbles, showing there are things you can view and you can open them and see them. You need to be able
  to clear them."
- **D3. Both surfaces.** "It needs to work on the mobile app, the phone app, and it also needs to work
  from the cockpit."
- **D4. Quality bar.** "It needs to be a professionally done feature."
- **D5. The owner is bothered only for specific questions, one at a time.** "only bother me again if you
  have specific questions and then ask them one at a time. Otherwise, just get this fucking done."
- **D6. The name is "Alerts"** - the owner's own word. Chosen by the Architect because "inbox" already
  means a session's fleet-message inbox (`cc-devthrottle message inbox`, the roster's `inboxLine`), and
  "notification" already means a push or desktop notification. One word per idea. "Alert" is used in the
  code only by `NetDiagAlertService` (an email), which does not collide on any screen.
- **D7. Deploy** - see section 9; recorded there in the owner's words.

## 5. Design

### 5.1 The principle: one source of truth per alert, one read model over them

An alert is not new data. A restart request, a secret transfer and a message request each already have
an authoritative record on the Gateway with its own state machine, expiry and audit trail. **Alerts
must not copy them into a second table**: a copied notification row drifts from its source the first
time a request is answered somewhere else (the cc-secrets window, a terminal, an agent's chat), and
then the bell lies. Instead:

- Each existing feature exposes its records as alerts through one small interface (`IAlertSource`).
- The Gateway composes them into one list per tenant at read time (`AlertsService`).
- The ONLY new stored state is what the owner has **seen** and **cleared**: one small table.

This is the pattern the professional tools use for an activity inbox over many domains (GitHub
notifications over issues/PRs/releases, Linear's inbox over issues/projects): typed sources, one feed,
per-user read and archive state.

### 5.2 Gateway

**Contract** (`CcDirector.Gateway.Contracts`, new `AlertDto`):

| Field | Meaning |
|---|---|
| `id` | Stable and globally unique: `"{kind}:{sourceId}"`, e.g. `restart-request:3f9c...` (the full kind word). |
| `kind` | `restart-request` \| `secret-transfer` \| `message-request`. A closed set the client switches on. |
| `needsAnswer` | true while the owner can still act (pending / waiting / `canAccept`). This alone drives the red count. |
| `title` | ONE line, written for the owner, under ~60 characters: "Restart the Director on mac-mini?" |
| `summary` | ONE plain sentence: who asked and why, or the outcome. "rob asks so the Mac Tester can run the v2.31.0 checks." |
| `status` | A short status word for Earlier rows: `Accepted`, `Declined`, `Expired`, `Delivered`, `Allowed`... Written by the source. |
| `createdAtUtc` / `updatedAtUtc` | For ordering and the "21:41" time on a row. |
| `expiresAtUtc` | Optional; shown as "Expires 22:11" on a needs-answer alert. |
| `seen` | The owner has opened the tray since this alert last changed. |
| `detail` | The kind-specific payload the detail view needs (the existing `DirectorRestartRequestDto`, `SecretTransfer`, `MessageLinkRequest`), so answering keeps using the existing, already-tested endpoints. |

**Sources.** `RestartRequestAlertSource`, `SecretTransferAlertSource`, `MessageRequestAlertSource`,
each beside its feature, each a pure mapping from that feature's existing store (no new queries
invented, no new state). Title and summary sentences are written HERE, by the source, from the
record's fields - the owner reads one sentence; the capability check's own `reason` /
`guardedRestartReason` go into `detail` for the collapsed Details only.

**Which records become alerts.** A record that needs an answer is always an alert. A record that is
closed (answered, expired, delivered, failed, completed) is an "Earlier" alert for **24 hours after it
last changed**, unless cleared. An accepted restart that is still running is an Earlier alert with
status `Accepted` and its latest `progress` sentence as the summary - it updates in place and turns into
its final state when the Director reports.

**Stored state - one new table, `alert_marks`** (EF entity + Postgres migration in
`CcDirector.Gateway.Migrations.Postgres`, following the existing tenant-keyed entity pattern):
`tenant_id`, `alert_id`, `seen_version` (the `updatedAtUtc` the owner last saw), `cleared_at_utc`.
Primary key `(tenant_id, alert_id)`. Rows older than the longest source retention are swept on read,
exactly like the restart store's own sweep. Marks are per **tenant (the account)**: every request today
is tenant-scoped and answered by the account owner; per-member marks for Teams are out of scope (5.6).

**Endpoints** (tenant-resolved like every other `/gateway/*` route):

- `GET  /gateway/alerts` -> `{ alerts: AlertDto[], needsAnswerCount: int }`. A body without the
  `alerts` array is an error, never an empty list (the rule already in `listRestartRequests`).
- `POST /gateway/alerts/seen` `{ ids }` -> marks those alerts seen at their current version.
- `POST /gateway/alerts/{id}/clear` -> refuses `409` with a sentence when the alert still needs an
  answer ("This still needs your answer; it leaves when you answer it or when it expires.").
- `POST /gateway/alerts/clear` -> clears every alert that does not need an answer. Returns how many.

Answering does NOT get a new endpoint: the detail view calls the existing accept/decline/answer routes,
which keep every check they have today. That is deliberate - the security-relevant paths are not
rewritten by this mission.

**Live updates.** Clients poll `GET /gateway/alerts` every 5 seconds while visible (one poll replacing
three) and immediately after any answer or clear. No new streaming channel: the roster itself polls, and
inventing a second transport for this would be the duct tape this mission removes.

**Push.** The existing per-feature push notifiers stay (they already carry the right copy and close on
answer). Each gains the alert `id` in its payload so the service worker opens the app onto
`?alert={id}`, which opens the tray on that alert. The app-icon dot keeps counting sessions that need
you (unchanged) - it is a separate signal and out of scope.

### 5.3 client-core (one implementation, both shells)

New folder `packages/client-core/src/alerts/`:

- `alertsClient.ts` - typed calls for the four routes.
- `AlertsProvider` + `useAlerts()` - the ONE poller and store, mounted once per shell at the app root,
  so the bell and the tray read the same state and nothing else polls.
- `AlertsBell` - the button: bell icon, red count badge (`--attention`), "9+" above nine,
  `aria-label="Alerts, 2 need you"`, no badge at zero. Opening the tray marks the visible alerts seen.
- `AlertsTray` - the list. Sections "Needs you" and "Earlier"; each row: unseen dot, title, one-line
  summary (ellipsised), time; X on Earlier rows; "Clear all" in the header when Earlier is non-empty; an
  empty state ("Nothing needs you."); a load-failure line that keeps the last-known list on screen.
  Keyboard: Escape closes, focus is trapped while open and returns to the bell on close; rows are
  buttons.
- `AlertDetail` - header (title, status), the summary, the kind's action block, collapsed "Details".
  The action blocks are the EXISTING card internals moved, not rewritten: the restart's two-step
  confirm, the secret transfer's place-aware answer, the message request's Allow/Refuse with its amount.
  Their existing tests move with them and keep passing.
- `alerts.css` - the ONE stylesheet, tokens only (`docs/CockpitVisualStyle.md`), every text line
  `overflow-wrap: anywhere; min-width: 0`. Shells may set only position/size variables
  (`--alerts-tray-width`, sheet vs popover), never copy rules.

The old panels (`RestartRequestsPanel`, `SecretTransfersPanel`, `LinkRequestsPanel`) and their copied
CSS blocks in both `styles.css` files are **deleted**, not left beside the new code. The amber
per-session icons in the Cockpit roster ("this session asked for a secret / to talk") stay: they are
row status, not banners. Their `waitingFrom` helper moves onto `useAlerts()`.

### 5.4 Phone (`apps/mobile`)

- Bell in the Home app bar, left of the filter button. Present on every screen that has the app bar.
- Tray is a bottom sheet (max 85% height, drag handle, scrolls inside), detail slides over it with a
  back arrow. Touch targets 44 px.
- `?alert={id}` deep link from a push opens the sheet on that alert.

### 5.5 Cockpit (`apps/cockpit`)

- Bell in the left rail head (`AppShell.tsx`, `.rail-head`); collapsed rail shows the bell with its
  badge. Tray is a popover anchored to it (width ~380 px, max-height 70vh), detail opens inside it.
- The browser tab title carries the count: `(2) DevThrottle`.
- The message request (phone-only today) appears in the Cockpit by construction - the tray lists every
  kind.

### 5.6 Out of scope

- The session "needs you" count, its app-icon dot and the roster's attention ordering - sessions are not
  alerts.
- Per-member read state for Teams accounts.
- New alert kinds. The design makes adding one a single `IAlertSource` plus one action block; adding one
  is a later mission.
- Changing what the restart / secret / message request flows do on the Gateway.

## 6. Phases

**Phase 1 - Stopgap (one Developer, hours, no Tech Lead).** The mess off the owner's screen today, before
the real thing lands. In `client-core/src/restart`: an accepted restart is no longer shown as a card
(`visibleRestartRequests` shows only `Pending`); `overflow-wrap: anywhere` + `min-width: 0` on every
card line in both apps; the capability sentences move behind a `<details>` "Details". Tests updated.
Merged and deployed on its own. This code is deleted again in Phase 4; it exists only so the owner is
not looking at the broken card for the length of the mission.

**Phase 2 - Gateway (one Developer).** `AlertDto`, the three sources, `AlertsService`, `alert_marks` +
migration, the four endpoints, push payloads carry the alert id. Unit tests per source (every state of
every record maps to the right `needsAnswer`/`status`/sentence), endpoint tests (tenant isolation,
clear refused while needs-answer, clear-all count, a malformed id), a database test for the migration.

**Phase 3 - client-core Alerts (one Developer, may start beside Phase 2 against the contract).**
`alertsClient`, `AlertsProvider`, `AlertsBell`, `AlertsTray`, `AlertDetail`, `alerts.css`; the three
action blocks moved in with their tests. Component tests for: zero/one/many, badge only counts
needs-answer, "9+", seen marking, clear, clear-all, clear refused, load failure keeps the list, the
overflow fixture, keyboard and focus.

**Phase 4 - Mount in both shells and remove the panels (one Developer, after 2 and 3).** Bell in the
phone app bar and the Cockpit rail head; deep link; tab title; delete the three panels and their CSS
from both apps; the roster guard test (5.3, goal 1). Then the QA run (section 7) on a local build
against a local Gateway with seeded alerts, and on the hosted Gateway after deploy.

A Tech Lead is not needed: four sequential, well-separated tasks. Phases 2 and 3 may run in parallel in
separate worktrees. Every phase's code goes to a Reviewer on a different agent before its pull request
merges; the Delivery Lead sends it.

## 7. The check

Proven means: the tests below pass, written for this mission and explained in the QA report, AND the
screenshots show every state at both widths with nothing overflowing.

Commands (repo root, in the mission worktree):

```bash
npm ci
npm run typecheck
npm test --workspace @devthrottle/client-core
npm test --workspace @devthrottle/mobile
npm test --workspace @devthrottle/cockpit
npm run lint
dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Alert"
dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Alert"
dotnet test src/CcDirector.Gateway.DatabaseTests --filter "FullyQualifiedName~Alert"
```

The roster guard (goal 1) must FAIL against origin/main as it stands today and PASS after Phase 4 - run
it both ways and record both in the QA report; a guard that has never failed has proved nothing.

QA screenshots (browser-harness, phone 390 x 844 and desktop 1440 x 900, both apps): bell at zero; bell
at 1 and at 12 ("9+"); tray with Needs you + Earlier; each of the three kinds opened; accept a restart
(two-step) and see it drop out of the count into Earlier; clear one; clear all; clear refused on a
needs-answer alert; Gateway unreachable (last-known list + the failure line); the overflow fixture; the
session list with NO cards in it. Then the same flow once on the hosted Gateway after deploy, from the
phone.

## 8. Merge plan

Merge each phase as soon as it is reviewed and green - four pull requests, each under a day old (trunk
rule 2). It is safe because each phase is additive until Phase 4: Phase 1 only narrows what a card
shows; Phase 2 adds routes and a table nothing reads yet; Phase 3 adds components nothing mounts yet;
Phase 4 switches the shells over and deletes the old panels in one pull request, so there is never a
build where both or neither are on screen.

## 9. Where it ends

Merged to origin/main **and deployed** to the hosted Gateway through the `deploy-hosted-gateway` workflow
(the Cockpit and the phone app ship in the Gateway image), after Phase 1 and after Phase 4. Never a
hand-rolled deploy.

Deploy grant (law 18), for THIS mission only, in the owner's words. Asked: "When the fix is merged, can
the mission deploy it to the hosted Gateway itself, so it reaches your phone without asking you? It would
deploy twice: once for the same-day stopgap, once for the finished Alerts." Answered 25 Oct 2026:
**"Yes, deploy both."**

Commit and merge grant: the owner asked for this mission to be built and finished - "create an amazing
mission document for this inbox. And then fucking implement it... just get this fucking done." Every
phase commits, opens a pull request, and merges to main without asking him again.

## 10. Questions

- **Q1. May this mission deploy to the hosted Gateway itself, after Phase 1 and after Phase 4?**
  Architect's recommendation: yes - the whole point is that the owner stops seeing the broken card on
  his phone, which only a deploy does, and the deploy workflow refuses any commit whose checks failed.
  Answer: _see section 9._

Everything else was decidable from the owner's words and the code, and is decided above. A Developer
who meets something genuinely undecidable asks the Delivery Lead; the Delivery Lead asks the owner one
question at a time, with a recommendation (D5).
