# Feature: Waiting-for-Input Prominence

**Status:** PLANNED
**Date:** 2026-05-19
**Owner:** Director-side UI work
**Audience:** Whoever picks this up next

## Related documents

- [../../architecture/gateway/GATEWAY_DIRECTOR_RESPONSIBILITIES.md](../../architecture/gateway/GATEWAY_DIRECTOR_RESPONSIBILITIES.md) - confirms this lives on the Director, not the Gateway
- `FEATURE_TERMINAL_QUESTIONS.md` - sibling spec; this feature lights up the *count* visible at the dashboard level, the terminal-questions spec covers the *what* on the detail view

---

## 1. Problem

When an agent transitions into `WaitingForInput` or `WaitingForPerm`, the user has to **scan** the cards grid looking for a yellow badge. In a busy dashboard with 8-12 sessions, the badge is too easy to miss. The user has explicitly said this is the most common reason they don't notice an agent has stopped.

Today's signals:

- The card gets a yellow `WaitingForInput` badge and a slow pulsing border (`@keyframes pulse` in `manager.html`).
- That's it. No fleet-level signal. No tab-title change. No sort.

When the user is in another browser tab, or just looking at a different part of the page, they miss it entirely.

---

## 2. Goal

**Make it impossible to miss a session that's waiting for the user**, with no extra effort on the user's part.

Success looks like:

- I walk up to my screen and within 1 second I know whether anything is waiting on me.
- I can switch to the dashboard tab from any other tab and the browser tab title tells me how many are waiting.
- The waiting sessions are the first cards I see, not the eighth.

---

## 3. Proposed UX

### 3.1 Header banner (always-visible, on the Director's manager.html)

When `count > 0` of sessions in `WaitingForInput` OR `WaitingForPerm`, show a banner at the top of the page:

```
+---------------------------------------------------------+
|  3 agents are waiting for you                  [Show >] |
+---------------------------------------------------------+
```

- Yellow background (`var(--waiting)` for `WaitingForInput`-only; `var(--perm)` if any are `WaitingForPerm`).
- Pulsing border (same animation as the cards).
- Click "Show >" -> filter the cards grid to only waiting sessions.

Banner is **hidden when `count == 0`** (don't show a "0 waiting" banner).

### 3.2 Sort waiting sessions to top

The cards grid is implicitly ordered by Director. Change the order **within each Director's row** so that:

1. `WaitingForPerm` cards first
2. `WaitingForInput` cards next
3. `Working` cards
4. `Idle`, `Starting`, etc.
5. `Exited` / `Failed` last

Order within a state stays whatever it is today (creation time).

### 3.3 Browser tab title

When sessions are waiting, prefix the title:

- `(3) CC Director` - 3 waiting
- `(1) CC Director` - 1 waiting
- `CC Director` - 0 waiting

This is how the dashboard shows in a list of open tabs even when not focused.

### 3.4 Optional: browser notification

Once per state transition (idle/working -> waiting), fire a browser notification:

```
"Session 'my-cool-repo' is waiting for input"
```

- Requires `Notification.requestPermission()` consent the first time.
- Granular per-Director-tab (no centralized state, just the JS in `manager.html`).
- Off by default; an opt-in checkbox in the page footer ("Notify me when a session needs input").

### 3.5 Per-card visual: keep the existing pulse, no change

The existing yellow pulse on the card is fine. We don't need to make individual cards louder when the fleet-level signal exists.

---

## 4. Where the data already is

Everything we need is in the existing `SessionDto.ActivityState`:

- `Idle`
- `Working`
- `WaitingForInput`
- `WaitingForPerm`
- `Starting`
- `Exited`
- `Failed`

Source of truth: `Session.HandlePipeEvent` in `src/CcDirector.Core/Sessions/Session.cs` maps hook events to activity states. The activity state is already returned by `GET /sessions` and is already on the cards. We do not need any backend changes.

---

## 5. Implementation steps

The whole feature is a `manager.html` change, scoped to **the Director's** copy (`src/CcDirector.ControlApi/Web/manager.html`).

### Step 1: Compute the waiting count

After each `refresh()` call in the polling loop, derive:

```js
const waiting = sessList.filter(s =>
  s.activityState === 'WaitingForInput' ||
  s.activityState === 'WaitingForPerm'
);
const hasPerm = waiting.some(s => s.activityState === 'WaitingForPerm');
```

### Step 2: Add the header banner

A new `div#waitingBanner` in the page header section, sibling to `<header>`. Show / hide based on `waiting.length`. Use CSS variables already in the file:

```css
#waitingBanner {
  background: var(--waiting);
  color: #1e1e1e;
  padding: 12px 16px;
  font-weight: 700;
  display: none;
  align-items: center;
  justify-content: space-between;
  border-bottom: 1px solid var(--border);
  animation: pulse 1.6s ease-in-out infinite;
}
#waitingBanner.perm { background: var(--perm); }
```

Click "Show >" sets a `filterWaiting = true` JS flag that hides non-waiting cards.

### Step 3: Sort cards

In `renderCard`, change the parent insertion logic to insert in priority order. Or do the simpler thing: after every refresh, re-sort the children of each `.sessions` grid by a numeric `sortKey(activityState)`.

```js
function sortKey(state) {
  return { WaitingForPerm: 0, WaitingForInput: 1, Working: 2, Idle: 3,
           Starting: 4, Exited: 5, Failed: 5 }[state] ?? 9;
}
```

### Step 4: Tab title

```js
document.title = waiting.length > 0
  ? `(${waiting.length}) CC Director`
  : 'CC Director';
```

Set this inside `refresh()` so it updates every 1.5 s.

### Step 5: Optional browser notification

```js
// On state transition into waiting
const prevState = lastSeenState[s.sessionId];
if (prevState && prevState !== s.activityState
    && (s.activityState === 'WaitingForInput' || s.activityState === 'WaitingForPerm')
    && notificationsEnabled) {
  new Notification(`Session '${displayName(s)}' is waiting for input`);
}
lastSeenState[s.sessionId] = s.activityState;
```

Add a footer checkbox bound to `notificationsEnabled`. On checkbox-change, call `Notification.requestPermission()` if not granted yet.

### Step 6: Mirror to the Gateway directory page (later)

In Phase 2+ (or whenever the Gateway directory page lands), it can show a count per Director ("Director A: 3 waiting"). Out of scope for this spec.

---

## 6. Edge cases and detail

- **Tab not focused.** Tab title + browser notification both work when the tab is in the background.
- **Multiple Directors in view at once.** Each Director's row already groups its sessions. The banner counts ACROSS all visible Directors. (Today the Gateway aggregates; in Phase 1 it does not, so this banner only fires on the Director's own `manager.html`, counting that Director's sessions only.)
- **Audio cue.** Out of scope. Easy to add later if desired (single `<audio>` element + `play()` on transition).
- **Persistent indication that "this was acknowledged."** No - once the user sends input, the state transitions back to Working and the banner clears. No mute/dismiss button is needed.
- **Time-waiting display.** Optional addition: "3 agents waiting (oldest 4 min)". Skip for v1.

---

## 7. Open questions

1. **Default for browser notifications: off or on?** Recommend off (opt-in). Notifications are an interruption; the user can enable them if they want them.
2. **Should the banner remain "sticky" at the top while scrolling?** Yes - `position: sticky` so it stays visible. The page header already uses sticky positioning.
3. **Color: distinguish `WaitingForPerm` from `WaitingForInput`?** Yes, use the orange `--perm` color when any of the waiting sessions are `WaitingForPerm` (more urgent), otherwise yellow `--waiting`.
4. **Sort sessions to top: per-Director or globally?** Recommend per-Director. Today's grid groups by Director already, and re-sorting *across* Directors would scramble the visual ordering. Within a Director, waiting floats to the top.

---

## 8. Out of scope

- Audio cue (defer).
- Mobile push notifications (defer; would need a service worker).
- A "snooze" button (over-engineering).
- A persistent "last-seen waiting at X timestamp" badge.
- Multi-tab coordination (don't notify twice if the user has two tabs open). Each tab does its own thing.
- Changes to the Gateway's directory page (Phase 2+).

---

## 9. Acceptance

The feature is done when:

- [ ] Banner appears within ~1.5 s of a session entering `WaitingForInput` or `WaitingForPerm`.
- [ ] Banner disappears within ~1.5 s of the last waiting session leaving that state.
- [ ] Browser tab title reads `(N) CC Director` where N is the waiting count.
- [ ] Waiting cards appear at the top of their Director's grid.
- [ ] Optional notification fires on state transition INTO waiting (not on every refresh).
- [ ] All of the above works on mobile-width viewports (no layout breakage).

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-05-19 | claude (cc-director assistant) | Initial spec. Scoped to the Director's `manager.html`. |
