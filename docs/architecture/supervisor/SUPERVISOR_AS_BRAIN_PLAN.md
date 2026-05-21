# SessionSupervisor as the Brain — Implementation Plan (Phase 4)

**Status:** DRAFT
**Date:** 2026-05-21
**Predecessor:** [GATEWAY_SESSION_VIEW_PLAN.md](../gateway/GATEWAY_SESSION_VIEW_PLAN.md) (Phase 2 + Phase 3 shipped commit `9545614`)

---

## Why this exists

Phase 3 made `Session.StatusColor` a first-class field and gave it a per-Director supervisor. That fixed the "who owns the color" question but left three real holes:

1. **The supervisor lies on the most common case.** `ActivityState=WaitingForInput` collapses two very different situations: "agent asked you a question mid-task" (genuinely needs you) vs. "agent finished its turn and is back at the idle prompt" (done, ready for next task). The Phase 3 fast path paints both red. So a session that just successfully answered "why is the sun yellow" sits at the prompt and the dot says RED. That trains the user to ignore red.

2. **Desktop and Gateway disagree.** The desktop sidebar paints a static per-session "identity color" (`Session.CustomColor`) that the user picked, which has nothing to do with status. The Gateway paints the supervisor color. So the same session shows green in one window and red in another. Color is supposed to mean "attention required"; right now it means two different things in two places.

3. **The supervisor is a black box.** You can't see what it decided, why, when, or what the last turn summary said. If the dot is wrong (as in case #1) you have no way to debug from the UI. The original Supervisor spec promised an Agent View and a Voice View; both exist as embryos but they're separate and neither shows the supervisor's reasoning.

Phase 4 closes all three.

---

## Principles (locked)

1. **Color = "does this need my attention", nothing else.** Not session identity. Not agent kind. Not user-assigned tint. One meaning, everywhere.
2. **The SessionSupervisor is the sole source of color.** Desktop and Gateway both read `Session.StatusColor` from the same field. No second path, no static override.
3. **No fallbacks. No UI-side derivation.** If the supervisor hasn't decided yet, the UI shows what the supervisor wrote (default green). It never invents.
4. **The supervisor must be observable.** Every decision lands in a per-session event log readable from the API. If the user can see the dot, they can see why.
5. **The supervisor reduces cognitive load — that's the job.** When a session needs the user, the supervisor produces a one-sentence distilled question. The merged Session View shows that, not the raw 800-word agent blob.

---

## Decisions recorded

- **`WaitingForInput` is no longer red by default.** The fast path treats it as Green ("ready, awaiting next prompt"). Red only when the supervisor has positive evidence of a pending question (either a turn-summary `needs_user != "no"` OR a buffer-scan heuristic for question markers like `❯ Do you want to`).
- **`Session.CustomColor` is repurposed, not removed.** Existing users have colored their sessions; we keep the field on the model but stop using it for the sidebar dot. It becomes a small accent (left border or tag) at most. Sidebar dot color = `Session.StatusColor`.
- **Agent View and Voice View merge into one "Session View"** tab in both Desktop and web. Becomes the default tab. Raw stays available; Source Control stays available.
- **Supervisor observability ships as `GET /sessions/{sid}/supervisor`** — a single endpoint that returns current color, current reason, last N transitions (timestamped), and the latest turn summary.

---

## Out of scope (Phase 5+)

- Supervisor-initiated actions ("auto-summarize commits before push", "auto-run review-code before commit"). Phase 4 is read-only and notification-only.
- Cross-session supervisor reasoning ("you have 3 reds in the same repo").
- Persisting the supervisor event log across Director restarts. In-memory ring buffer is fine for v1.
- Voice TTS integration polish (the merged view exposes the existing TTS controls; we don't rewrite the TTS engine).

---

## Slices

### 4a — Disambiguate "needs you" from "ready for next task"

**Why:** This is the bug that lies to the user most often. Fixing it alone makes the dot trustworthy.

**Touches:**
- `src/CcDirector.Core/Supervisor/SessionStatusSupervisor.cs` — `ColorFromActivityState`:
  - `WaitingForInput` → **Green**, reason `"ready, awaiting next prompt"`
  - `WaitingForPerm` → **Red**, reason `"waiting for permission"` (this one really is always a user gate)
- Add `PromotePendingQuestion(Session s, string detail)` — called by anyone with positive evidence of a real question (buffer scan, turn-summary slow path, agent's pending-interaction signal). Sets Red with the supplied detail.
- `ControlEndpoints.cs` `Map()` — no change; still reads `s.StatusColor`.
- `TurnSummaryCache` slow path (`ApplyTurnSummary`) — unchanged in shape, but the supervisor's slow-path rules are the canonical path that promotes a session to Red after a turn ends with `needs_user != "no"`.
- **Buffer heuristic (optional, additive):** check the last 4 KB of the session buffer on each activity-state change; if it contains a known question marker (`❯`, `[y/n]`, `(y/N)`, `Do you want to`, etc.) call `PromotePendingQuestion`. Cheap, regex-free string scan.

**Tests:**
- Existing tests update: `WaitingForInput_maps_to_red` becomes `WaitingForInput_maps_to_green_until_supervisor_promotes`.
- New: `PromotePendingQuestion_sets_red_with_detail`.
- New: `WaitingForPerm_still_red` (regression guard).
- New: turn summary `needs_user="question"` promotes to red even if activity state is Idle (timing edge case).

### 4b — Supervisor observability

**Why:** Make the supervisor's reasoning visible. If the dot is wrong, the user can see why and we can fix it.

**Touches:**
- `src/CcDirector.Core/Sessions/Session.cs` — add `SupervisorEventLog` (ring buffer, capacity 50). Every `SetStatusColor` writes `{ts, oldColor, newColor, reason, source}` where `source ∈ {"activity", "turn-summary", "promote", "init"}`.
- `src/CcDirector.Gateway.Contracts/SupervisorViewDto.cs` (NEW) — `{ currentColor, currentReason, since, events[], latestTurnSummary }`.
- `src/CcDirector.ControlApi/ControlEndpoints.cs` — new `GET /sessions/{sid}/supervisor` returns the DTO.
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` — new `GET /sessions/{sid}/supervisor` forwards to owning Director (read-through proxy).

**Tests:**
- `SupervisorEventLog_records_each_color_change`
- `SupervisorEventLog_ring_buffer_evicts_oldest_past_capacity`
- `GET /sessions/{sid}/supervisor` returns the latest entry first
- Gateway forwarder stamps machine fields on the response (consistent with Phase 2 stamping)

### 4c — Merge Agent View + Voice View into "Session View" (web + desktop)

**Why:** One view to live in. Replaces Raw as the default. Shows the supervisor's reasoning, the supervisor's distilled question, the turn summary, voice playback — everything you need to triage and respond without reading the terminal blob.

**Web side (Gateway `Web/`):**
- New: `Web/session.html` (or update existing `manager.html` / Director's per-session view) — single tab layout:
  - Top: supervisor banner (color dot, distilled reason, ago-time)
  - Center: **distilled question** when red, else last turn summary headline + bullets
  - Right rail: voice TTS controls (Speak / Stop / Read latest)
  - Bottom: supervisor event log (collapsed by default)
  - Side tabs: `Session | Raw | Source Control | Agent (legacy)` — Session is default
- Delete the standalone Voice tab; Voice controls move into Session.
- Agent tab marked legacy, removed in 4c.1 follow-up after the user is happy.

**Desktop side (Avalonia):**
- New: `Controls/SessionView/SessionView.axaml` — same layout primitives as web (banner + distilled / summary + log).
- Wire as a new tab in `MainWindow.axaml` alongside Terminal, Source Control, Agent.
- Default tab on session open changes from Terminal to Session (configurable).
- Voice tab in Avalonia merges into Session.

**Tests:**
- Web smoke: `/sessions/{sid}/view` renders the Session layout, queries supervisor + turn summary.
- Avalonia: covered by manual smoke (no UI unit tests on Avalonia today).

### 4d — Desktop reads supervisor color, drops static session color from sidebar

**Why:** End the green-vs-red disagreement between desktop and gateway. One field, one truth.

**Touches:**
- `src/CcDirector.Avalonia/Controls/SessionListItem.axaml` (or wherever the sidebar dot is) — bind the dot's brush to `Session.StatusColor` via a converter (`StatusColorToBrushConverter`).
- Remove the binding to `Session.CustomColor` from the sidebar dot.
- `Session.CustomColor` becomes a small accent (e.g. 2 px left border on the sidebar row) — preserves user customization without overloading the meaning of "color".
- Subscribe to `Session.OnStatusColorChanged` to repaint when the supervisor moves the color.

**Tests:**
- Avalonia change is mostly visual; covered by manual smoke. Add a small XAML converter unit test if `StatusColorToBrushConverter` is non-trivial.

### 4e — Supervisor's "distilled question" prompt

**Why:** When the supervisor says red, the user wants one crisp sentence — not 800 words of raw agent output. The supervisor's job is to be the editor.

**Touches:**
- `src/CcDirector.Core/Supervisor/SupervisorService.cs` — `BuildTurnSummaryPrompt` extends the JSON shape:
  ```json
  {
    "headline": "...",
    "needs_user": "no | question | error | permission | idle",
    "needs_user_short": "<one crisp sentence under 200 chars, present only when needs_user != 'no'>",
    "needs_user_detail": "<longer detail as today>",
    ...
  }
  ```
- `TurnSummary` DTO gains `NeedsUserShort`.
- `SessionStatusSupervisor.ApplyTurnSummary` uses `NeedsUserShort` (falls back to `NeedsUserDetail` if empty) as the `LastStatusReason` when promoting to red.
- The Session View's "distilled question" pane renders `NeedsUserShort` prominently; `NeedsUserDetail` is below it in a smaller block.

**Tests:**
- `ParseTurnSummaryJsonInto_extracts_needs_user_short`
- `ApplyTurnSummary_uses_needs_user_short_as_reason_when_present`
- Prompt-output snapshot test (loose): assert the Haiku prompt asks for `needs_user_short` and constrains it to <200 chars.

---

## File-by-file change list

| File | Change |
|---|---|
| `src/CcDirector.Core/Supervisor/SessionStatusSupervisor.cs` | EDIT — `WaitingForInput` -> Green; add `PromotePendingQuestion`; buffer-marker heuristic on activity-state change |
| `src/CcDirector.Core/Supervisor/SupervisorService.cs` | EDIT — extend turn-summary prompt + parser for `needs_user_short` |
| `src/CcDirector.Gateway.Contracts/TurnSummary.cs` | EDIT — add `NeedsUserShort` |
| `src/CcDirector.Core/Sessions/Session.cs` | EDIT — `SupervisorEventLog` ring buffer; record each color change |
| `src/CcDirector.Gateway.Contracts/SupervisorViewDto.cs` | NEW — `{ currentColor, currentReason, since, events[], latestTurnSummary }` |
| `src/CcDirector.ControlApi/ControlEndpoints.cs` | EDIT — new `GET /sessions/{sid}/supervisor` |
| `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` | EDIT — forward `GET /sessions/{sid}/supervisor` |
| `src/CcDirector.Gateway/Discovery/DirectorEndpointClient.cs` | EDIT — `GetSupervisorAsync` |
| `src/CcDirector.ControlApi/Web/session-view.html` (or update existing session view) | NEW/REWRITE — Session View web tab |
| `src/CcDirector.Avalonia/Controls/SessionView/SessionView.axaml(.cs)` | NEW — desktop Session View tab |
| `src/CcDirector.Avalonia/Controls/SessionListItem.axaml` | EDIT — sidebar dot binds to `Session.StatusColor`; `CustomColor` demoted to accent |
| `src/CcDirector.Avalonia/Converters/StatusColorToBrushConverter.cs` | NEW |
| `src/CcDirector.Avalonia/MainWindow.axaml(.cs)` | EDIT — Session tab added; default tab on open; subscribe to `OnStatusColorChanged` |
| `src/CcDirector.Core.Tests/Supervisor/SessionStatusSupervisorTests.cs` | EDIT — new disambiguation tests |
| `src/CcDirector.Core.Tests/Supervisor/SupervisorEventLogTests.cs` | NEW |
| `src/CcDirector.Gateway.Tests/SupervisorAggregationTests.cs` | NEW — endpoint forwarding + stamping |
| `docs/architecture/supervisor/SUPERVISOR_AS_BRAIN_PLAN.md` | NEW — this doc |

---

## Done criteria

1. The session in the screenshot (Claude just answered a question, sitting at the prompt) shows **green** within ~2s of finishing, NOT red.
2. When a Claude session asks a real question mid-turn (`needs_user != "no"`), the dot turns red within ~2s and the supervisor's `LastStatusReason` is the distilled one-sentence question.
3. Desktop sidebar dot and Gateway dot show the **same color** for the same session, sourced from `Session.StatusColor`. No session in any view shows a color from `CustomColor`.
4. `GET /sessions/{sid}/supervisor` returns current color, reason, last N events (with timestamps), and latest turn summary. Same endpoint works on the Gateway via the read-through proxy.
5. Session View tab exists in both Desktop and Gateway web. Default tab on session open. Voice tab is gone (controls live inside Session View). Raw is still reachable.
6. When the dot is red, the Session View shows the **distilled question** (`needs_user_short`) prominently. The raw agent output is one scroll away in Raw — not in front of you.
7. Existing tests pass. New tests: disambiguation, event log, distilled-question parsing, endpoint forwarding.

---

## Open questions

1. **Buffer heuristic vs. JSONL signal for the "is there really a pending question" check.** The Director already has JSONL stream parsing. The buffer scan is cheap but fuzzy. Preference: try the JSONL path first (look at the latest assistant message for question structure); fall back to buffer scan only if the session isn't linked to a JSONL.
2. **Voice TTS auto-play on red.** Today voice is manual. Should the supervisor auto-speak the distilled question when a session moves into red, if voice mode is on? Recommend: yes, behind a per-session toggle, default on.
3. **What happens to the existing Agent View?** Lazy answer: leave it as a deprecated tab for one more cycle, mark "(legacy)", remove after the user confirms Session View covers everything. Don't delete in 4c.

---

## Document history

| Date | Author | Change |
|---|---|---|
| 2026-05-21 | claude (cc-director assistant) | Initial PLANNED. Triggered by the green-desktop / red-gateway mismatch and the realisation that `WaitingForInput` -> Red is wrong for the most common case. |
