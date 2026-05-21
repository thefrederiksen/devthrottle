# Feature: Live Current-Session Summary

**Status:** PLANNED
**Date:** 2026-05-19
**Owner:** Director-side
**Audience:** Whoever picks this up next

## Related documents

- [../../architecture/gateway/GATEWAY_DIRECTOR_RESPONSIBILITIES.md](../../architecture/gateway/GATEWAY_DIRECTOR_RESPONSIBILITIES.md)
- `FEATURE_WAITING_FOR_INPUT.md` - sibling spec
- `FEATURE_TERMINAL_QUESTIONS.md` - sibling spec
- `src/CcDirector.Core/Claude/SummaryBuilder.cs` - existing structured-summary builder we extend
- `src/CcDirector.Core/Claude/TurnAccumulator.cs` - per-turn live data already captured by SessionManager
- `src/CcDirector.Gateway.Contracts/SessionSummaryDto.cs` - existing summary DTO returned by `GET /sessions/{sid}/summary`

---

## 1. Problem

The detail view on the session card answers "what's the current state" but not **"what has this session done for me, and what is it about to do next?"**.

Today's two close-by features:

- **Recap** (`POST /sessions/{sid}/recap`) - good, but it's a manual button press that spawns a side `claude --print --bare --model haiku`. ~10-60 s. Snapshot, regenerated on click. Cached per-session, in-process, lost on Director restart.
- **Summary** (`GET /sessions/{sid}/summary`) - returns structured data (last user prompt, last assistant text, files touched, recent commands, open TODOs) but is not surfaced in the UI today.

The user wants a **continuously-updated** view of:

- **What has been done** - the last few user prompts, characterized in one line each; key files touched; key commands run.
- **What is expected of me / what's next** - open TODOs from the most recent `TodoWrite`; the last user prompt if the agent hasn't replied yet; whether the agent is waiting for a question / permission.

This should be:

- **Always visible** at the top of the session detail view (above Recap, above Send Prompt).
- **Updated live** as turns happen, with no button press, no LLM call.
- **Derived entirely from local data** - the session's JSONL transcript and its hook-event state. No `claude --print` spend.

The Recap feature stays - it's the LLM-quality version. The live summary is the cheap, always-on version.

---

## 2. Goal

A new panel in the session detail view that answers "what has been done / what's expected" purely from local data, refreshed every couple of seconds as long as the detail view is open.

Success looks like:

- I click into a session and immediately read a tight ~5-line "Done so far" plus ~3-line "Next" without waiting for an LLM call.
- The panel updates as the session advances. New turn lands -> the panel reflects it within 2 s.
- No API key spend.

---

## 3. Data sources (all already on the Director)

### 3.1 `Session.TurnAccumulator` (per-turn live)

`src/CcDirector.Core/Claude/TurnAccumulator.cs` captures every turn:

- `UserPrompt`
- `ToolsUsed` (list of tool names)
- `FilesTouched` (from Read / Edit / Write / Glob tool args)
- `BashCommands` (from Bash tool args)
- `StartedAt`
- `IsActive`

A turn starts on `UserPromptSubmit` and ends on `Stop`. `OnTurnCompleted` event fires.

**We can keep a rolling list of the last N completed turns on the `Session`.** Currently the accumulator only holds the active turn and forgets on completion. We'll add a `RecentTurns: Queue<TurnData>` capped at ~5-10.

### 3.2 `SummaryBuilder` (JSONL-derived snapshot)

`src/CcDirector.Core/Claude/SummaryBuilder.cs` reads the JSONL and produces a `SessionSummaryDto`:

- `LastUserPrompt`, `LastAssistantText`
- `FilesTouched: List<FileTouch>` (Path + Read/Write/Edit)
- `RecentCommands: List<string>`
- `OpenTodos: List<TodoItem>` (Status + Content)
- `TurnCount`

Already exposed at `GET /sessions/{sid}/summary` on both the Director and the Gateway. **Not displayed today.**

### 3.3 `Session.ActivityState`

Tells us whether the agent is mid-turn (`Working`), idle (`Idle`), or waiting on us (`WaitingForInput` / `WaitingForPerm`).

### 3.4 Pending interaction (FEATURE_TERMINAL_QUESTIONS.md)

Once the sibling spec lands, the Session will expose whatever question / plan / permission is currently pending. The live summary will surface it as the lead item under "What's expected of me."

---

## 4. Proposed UX

A new "**Pulse**" panel in the session detail view, positioned **above** the Recap panel. Always-visible when the detail view is open.

### 4.1 Layout

```
+-----------------------------------------------------------+
|  PULSE                                  refreshed 2 s ago |
|                                                           |
|  Done so far                                              |
|    - Renamed Session.cs and added Verify() method         |
|    - Ran  dotnet test  (passed)                           |
|    - Wrote test in SessionTests.cs                        |
|    - Edited 3 files in src/CcDirector.Core/Sessions/      |
|                                                           |
|  What's expected of you                                   |
|    > Agent is WAITING for input (4 min)                   |
|    Open TODOs:                                            |
|      * pending - Wire up the new endpoint                 |
|      * pending - Update the docs                          |
|    Last prompt awaiting a reply:                          |
|      "Add a test for the empty case"                      |
+-----------------------------------------------------------+
```

### 4.2 "Done so far" derivation rules

Pick at most 5 bullets, in order of priority:

1. If the session has 0 turns yet: show "Session just started; no completed turns."
2. Otherwise, for the last 3-5 completed turns: characterize each in ONE line:
   - Prefer: "Edited `<file basename>` and ran `<command>`" if both happened.
   - Else: "Edited `<file basename>` (+ N more)" if files were touched.
   - Else: "Ran `<command>` (+ N more)" if commands were run.
   - Else: "Used tools: <comma-sep tool names>" as last resort.
3. Truncate each bullet to 80 chars with ellipsis.

This is pattern-matching, NOT an LLM call. The output is "OK-quality" not "great" - the Recap feature is what you click for the great version.

### 4.3 "What's expected of you" derivation rules

In order:

1. If the session has a `PendingInteraction` (FEATURE_TERMINAL_QUESTIONS), lead with `> Agent is WAITING: <question summary>`.
2. Else if `ActivityState == WaitingForInput`, show `> Agent is WAITING for input (Xm)` where X is minutes since the state transition.
3. Else if `ActivityState == WaitingForPerm`, show `> Agent is WAITING for permission (Xm)`.
4. Else if `ActivityState == Working`, show `> Agent is working (Xm)`.
5. Else if `ActivityState == Idle`, show `> Session is idle.`

After the headline, regardless of state:

6. Show open TODOs (status `pending` or `in_progress`) up to ~5.
7. If there's a `LastUserPrompt` and the `Stop` event hasn't fired since (the agent hasn't replied), show "Last prompt awaiting a reply: ...".

### 4.4 Refresh model

- The panel refreshes when the cards list refreshes (today: 1.5 s setInterval). Same `refresh()` tick.
- The panel data comes from one new call per detail-view tick: `GET /sessions/{sid}/pulse` (see API below).
- If the detail view is not open for a given session, we don't fetch its pulse. Each tick fetches only the currently-displayed pulse.

---

## 5. API

### 5.1 New endpoint on the Director

```
GET /sessions/{sid}/pulse
```

Returns:

```json
{
  "sessionId": "...",
  "directorId": "...",
  "activityState": "WaitingForInput",
  "activityStateAgeSeconds": 245,
  "turnCount": 14,
  "doneSoFar": [
    "Edited Session.cs and ran dotnet test",
    "Wrote test in SessionTests.cs",
    "Ran git status"
  ],
  "whatNext": {
    "headline": "Agent is WAITING for input",
    "headlineKind": "WaitingForInput",     // or WaitingForPerm / Working / Idle
    "headlineSinceSeconds": 245,
    "pendingInteraction": null,            // or { kind, summary, ... } from FEATURE_TERMINAL_QUESTIONS
    "openTodos": [
      { "status": "pending", "content": "Wire up the new endpoint" },
      { "status": "pending", "content": "Update the docs" }
    ],
    "lastPromptAwaitingReply": null
  },
  "generatedAt": "2026-05-19T13:24:55Z"
}
```

This is cheap to compute on every request (no LLM, no heavy JSONL re-parse - we can keep the parsed `SessionSummaryDto` cached for ~2 s per session if profiling shows it matters).

### 5.2 Same shape on the Gateway in Phase 2+ (not now)

In Phase 1, the Gateway has no `/sessions/{sid}/pulse` route. The Director's manager.html calls its own Director directly. When the aggregated Gateway view lands later, it can proxy.

---

## 6. Implementation steps

### Step 1: Extend `Session` with `RecentTurns`

In `src/CcDirector.Core/Sessions/Session.cs`:

- Add `Queue<TurnData> RecentTurns` (capped at 10).
- In the existing `OnTurnCompleted` handler path, push the completed `TurnData` into the queue, dropping the oldest if size > 10.

### Step 2: Track activity-state transition timestamps

Add `DateTime ActivityStateChangedAt` to `Session`, updated in `SetActivityState`. Used for the "WAITING for Xm" duration.

### Step 3: Add `PulseBuilder`

New class `src/CcDirector.Core/Claude/PulseBuilder.cs`:

```csharp
public static class PulseBuilder
{
    public static SessionPulseDto Build(Session session)
    {
        // 1. Read SummaryBuilder.Build(...) (or cached snapshot)
        // 2. Combine with session.RecentTurns to compute doneSoFar bullets
        // 3. Combine with session.ActivityState + pending interaction (when wired)
        //    to compute whatNext
        // 4. Return SessionPulseDto
    }
}
```

Pure function. Easily unit-testable. No HTTP, no IO except reading the JSONL via existing helpers.

### Step 4: Add `SessionPulseDto`

In `src/CcDirector.Gateway.Contracts/SessionPulseDto.cs`. Shape per section 5.1.

### Step 5: Add `GET /sessions/{sid}/pulse` to ControlEndpoints

Light wrapper that resolves the session and calls `PulseBuilder.Build`.

### Step 6: Render in `manager.html`

In the session detail view, between the header panel and the Recap panel:

```html
<div class="pulse-panel">
  <h2>Pulse</h2>
  <div id="pulseBody">Loading...</div>
</div>
```

In JS, on every `refresh()` tick when a session is selected, fetch `/sessions/{sid}/pulse` and re-render.

Mirror the change to the Gateway's `manager.html`... wait. Per the new architecture decisions, the Gateway's `manager.html` becomes a thin directory page in Phase 1. So the only `manager.html` to update is the Director's.

### Step 7: Unit tests

- `PulseBuilder` tests that exercise different combinations: empty session, mid-turn, idle, waiting, with TODOs, without TODOs.
- Tests should use small fixture JSONL files (there are already test fixtures under `src/CcDirector.Core.Tests/TestData/`).

---

## 7. Edge cases and detail

- **Brand new session (0 turns).** "Session just started; no completed turns." in Done; "Session is idle." in What next.
- **TODOs out of date.** The `OpenTodos` list is whatever `TodoWrite` most recently wrote. If the agent stopped using `TodoWrite`, the list will be stale. That's OK - it's the same caveat as today's summary endpoint.
- **Very long bullets.** Truncate to ~80 chars.
- **Multi-file edits.** Group: "Edited 3 files (Session.cs, SessionTests.cs, ControlEndpoints.cs)" - up to 3 names, then "+ N more".
- **No JSONL yet.** Show "Session is starting (no transcript yet)" and skip the heavy fields.
- **JSONL parse failure.** Return a pulse DTO with status fields populated and `doneSoFar: []`. Don't 500.

---

## 8. Open questions

1. **Cache the parsed `SessionSummaryDto` for ~2 s?** Probably yes. Each pulse call parses the JSONL today; at 1.5 s polling per open session, that's 30+ JSONL parses per minute per open session. Cheap as it is, caching the parsed result for the polling interval is free win. Recommend a tiny per-session cache `(sid -> (dto, parsedAt))` with a 2 s TTL.
2. **Show "Done" bullets even when the session is brand new?** Recommend: if there's a partial first turn in flight, show "Started: <user's first prompt>" as the only bullet under Done so the panel isn't empty. Otherwise empty + a placeholder.
3. **Should the pulse REPLACE the cards' compact view info?** No. Cards stay as-is (name, badge, elapsed). The pulse is detail-view only.
4. **Auto-refresh independently of the cards list?** Recommend no for v1 - piggyback on the same 1.5 s tick the cards list uses. If lag becomes noticeable when a turn finishes, we can add SSE later.
5. **Do we move Recap below the Pulse panel, or keep Pulse at the top?** Recommend Pulse at the top (always-on), Recap below (on-demand). The pulse is the answer to "what's going on right now"; the recap is "give me a polished summary".

---

## 9. Out of scope

- LLM-based summarization (that's the existing Recap feature).
- Multi-session "fleet pulse" view on the Gateway (Phase 2+).
- Activity timeline / graph visualization.
- Surfacing in the Avalonia desktop UI (separate work item).
- Persistence of the pulse data across Director restarts. (The underlying data persists; the pulse is always derived.)

---

## 10. Acceptance

The feature is done when:

- [ ] `GET /sessions/{sid}/pulse` returns a populated `SessionPulseDto` for an active session within ~50 ms.
- [ ] The detail view shows a Pulse panel above the Recap panel.
- [ ] After the agent finishes a turn, the next dashboard tick reflects the change in the Pulse panel.
- [ ] When `ActivityState == WaitingForInput`, the headline reads "Agent is WAITING for input (Xm)".
- [ ] Open TODOs from the most recent `TodoWrite` are listed.
- [ ] Unit tests cover empty session, mid-turn, idle, waiting, with TODOs, no TODOs.
- [ ] Works on mobile viewports (no layout breakage).

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-05-19 | claude (cc-director assistant) | Initial spec. Adds the Pulse panel to the Director's session detail view, derived from local data only. |
