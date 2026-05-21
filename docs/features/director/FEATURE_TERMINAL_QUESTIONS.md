# Feature: Terminal-Question Surfacing

**Status:** PLANNED
**Date:** 2026-05-19
**Owner:** Director-side
**Audience:** Whoever picks this up next

## Related documents

- [../../architecture/gateway/GATEWAY_DIRECTOR_RESPONSIBILITIES.md](../../architecture/gateway/GATEWAY_DIRECTOR_RESPONSIBILITIES.md)
- `FEATURE_WAITING_FOR_INPUT.md` - sibling spec; this feature is what shows up *inside* the detail view once the dashboard tells you someone is waiting
- `FEATURE_LIVE_SESSION_SUMMARY.md` - sibling spec; the pulse panel surfaces the pending question as its lead "What's expected of you" item
- `src/CcDirector.Core/Sessions/Session.cs` (`HandlePipeEvent`) - where hook events already drive `ActivityState`
- `src/CcDirector.Core/Pipes/PipeMessage.cs` - the hook payload shape
- `src/CcDirector.Core/Claude/WidgetBuilder.cs` - already handles `AskUserQuestion` and `ExitPlanMode` as historical widgets

---

## 1. Problem

When the agent asks the user a question, the question appears in the **raw terminal** but **not on the structured "Agent" view**. The user has to open the raw terminal tab to see what the agent is asking, then switch back to the Agent view to respond. This is real friction and is the most common cause of stale "Waiting for input" sessions that just sit there.

### Concrete cases this happens

| Trigger | Hook event | What the user has to read | Where it currently shows |
|---|---|---|---|
| `AskUserQuestion` tool use | `PreToolUse` with tool="AskUserQuestion" | The question text + option labels | Raw terminal only |
| `ExitPlanMode` tool use | `PreToolUse` with tool="ExitPlanMode" | The plan body + "approve?" prompt | Raw terminal only |
| Permission prompt for Bash/Edit/etc. | `Notification` with `notification_type=permission_prompt`, or `PermissionRequest` | "Allow this tool to run?" with the tool input | Raw terminal only |
| Free-form text question from agent | (none - the agent's reply happens to end with "?") | The question | Raw terminal AND historical Agent view (as `AssistantText` widget), but NOT marked as "waiting on you" |

The first three are the priority because they have a structured payload we can render. The fourth is harder (we'd have to detect "is this a question") and is out of scope here.

### Why this isn't fixed by today's plumbing

- `Session.HandlePipeEvent` already maps `PreToolUse`-with-interactive-tool, `PermissionRequest`, and `Notification:permission_prompt` to `WaitingForInput` / `WaitingForPerm`. So the activity state IS correct.
- `WidgetBuilder.cs` already builds historical widgets for `AskUserQuestion` ("Question - Claude needs your input") and `ExitPlanMode` ("Plan Ready - Waiting for your approval"). But this is post-hoc historical rendering of the JSONL, not real-time.
- The **live** pending question is not tracked. There's no `Session.PendingInteraction` property. There's no endpoint to fetch the current pending question. The detail view has no UI for "an interaction is waiting."

---

## 2. Goal

When the agent is waiting on the user for ANY structured reason, the Director's session detail view shows the **question and any options** prominently, with a one-click / one-text-input way to respond. The user should never need to open the raw terminal to know what's being asked.

Success looks like:

- I open a session that's `WaitingForInput`. Within the detail view, a "Question" card at the top shows the agent's question and (if applicable) the option buttons.
- I click an option (or type a reply and Send). The agent receives it. The card disappears. State transitions to `Working`.
- For `WaitingForPerm`, I see what the agent wants to run and an Allow / Deny pair.

---

## 3. The pending-interaction model

A new domain concept on the Session: a `PendingInteraction` is whatever question / approval the agent is currently waiting on. At most one per session at a time.

### 3.1 Kinds

| Kind | Source event | Payload extracted from |
|---|---|---|
| `Question` | `PreToolUse` with `ToolName == "AskUserQuestion"` | `ToolInput.question` (string), `ToolInput.options` (list of `{ label, description }`) |
| `Plan` | `PreToolUse` with `ToolName == "ExitPlanMode"` | `ToolInput.plan` (markdown string) |
| `Permission` | `PermissionRequest` event OR `Notification` with `notification_type=permission_prompt` | The tool name + tool input that triggered the permission ask |

Each kind has a different render in the UI but the same response model: "user clicks an option" or "user types text," resulting in a string written to the session's stdin (with Enter).

### 3.2 Lifecycle

- **Created** when one of the trigger events arrives in `Session.HandlePipeEvent`.
- **Cleared** when the activity state transitions out of `WaitingForInput` / `WaitingForPerm` (typically because the user replied and `UserPromptSubmit` fired, or the session exited).
- **At most one** pending interaction per session at a time. If a new one arrives while one is already pending (rare; would mean a stuck state), replace it.

### 3.3 New types

```csharp
namespace CcDirector.Core.Sessions;

public enum PendingInteractionKind
{
    Question,
    Plan,
    Permission,
}

public sealed class PendingInteractionOption
{
    public string Label { get; init; } = "";
    public string? Description { get; init; }
}

public sealed class PendingInteraction
{
    public required PendingInteractionKind Kind { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    // For Question / Plan / Permission - the text the user is being asked to respond to.
    public string Prompt { get; init; } = "";

    // For Question - the option list.
    public List<PendingInteractionOption> Options { get; init; } = new();

    // For Plan - the plan body (markdown).
    public string? PlanBody { get; init; }

    // For Permission - the tool name + truncated tool input.
    public string? ToolName { get; init; }
    public string? ToolInputSummary { get; init; }
}
```

Add `public PendingInteraction? PendingInteraction { get; private set; }` to `Session`.

---

## 4. Proposed UX

### 4.1 In the session detail view (top of the page)

When `session.pendingInteraction != null`, render an interaction card **above the Pulse panel** (from `FEATURE_LIVE_SESSION_SUMMARY.md`).

```
+--------------------------------------------------------+
| [WAITING ON YOU]   Question                            |
|                                                        |
|   Which approach do you prefer?                        |
|                                                        |
|   [ Approach A: Refactor first ]                       |
|   [ Approach B: Add tests first ]                      |
|   [ Approach C: Ship and iterate ]                     |
|                                                        |
|   Or type a custom reply:                              |
|   [____________________________]   [ Send ]            |
+--------------------------------------------------------+
```

Variations by kind:

- **Plan:** "Plan Ready" header, plan body rendered as markdown, two buttons `[ Approve ]` `[ Reject ]`. Custom-reply box still present.
- **Permission:** "Permission Request" header, "The agent wants to run `<tool>` with `<truncated args>`," two buttons `[ Allow ]` `[ Deny ]`. Custom-reply box still present.

### 4.2 On the card grid (less prominent)

The card already pulses with the yellow / orange border. Add a tiny `?` icon when `pendingInteraction != null` so you can see at a glance that the wait is for a structured question (vs. just a stale Idle).

### 4.3 In the Pulse panel

The "What's expected of you" lead bullet reads from the pending interaction:

- Question: `> Question: "Which approach do you prefer?"`
- Plan: `> Plan ready - awaiting approval`
- Permission: `> Permission ask: run Bash "<truncated command>"`

This is purely a derivation of the same `PendingInteraction` data.

---

## 5. API

### 5.1 Pending interaction is part of SessionDto

The simplest path: extend `SessionDto` with `PendingInteraction? PendingInteraction`. The cards list and the detail view both already poll `GET /sessions` / `GET /sessions/{sid}`; they get the data for free.

```json
{
  "sessionId": "...",
  "activityState": "WaitingForInput",
  "name": "my-cool-repo",
  "pendingInteraction": {
    "kind": "Question",
    "createdAt": "2026-05-19T13:24:55Z",
    "prompt": "Which approach do you prefer?",
    "options": [
      { "label": "Approach A: Refactor first" },
      { "label": "Approach B: Add tests first" }
    ]
  }
}
```

### 5.2 Responding

The existing `POST /sessions/{sid}/prompt` endpoint already accepts text + an `appendEnter` flag. Reuse it for all three kinds:

- Clicking an option button: POST the option label text (`"Approach A: Refactor first"`) with `appendEnter: true`.
- Typing a custom reply: same.
- Approve / Reject / Allow / Deny buttons: POST the exact response text the CLI expects. For Claude Code's plan-mode the convention is to reply something like `"approve"` / `"reject"` (verify against current Claude Code behavior - see open question).

No new endpoint needed. The client decides what text to send.

---

## 6. Implementation steps

### Step 1: Define the model

`src/CcDirector.Core/Sessions/PendingInteraction.cs` per section 3.3.

### Step 2: Capture pending interactions in `Session.HandlePipeEvent`

In `Session.cs`, in `HandlePipeEvent`:

- On `PreToolUse` with `ToolName == "AskUserQuestion"`, parse `msg.ToolInput` JSON and build a `PendingInteraction { Kind = Question, ... }`.
- On `PreToolUse` with `ToolName == "ExitPlanMode"`, build `{ Kind = Plan, ... }`.
- On `PermissionRequest` OR `Notification` with `notification_type=permission_prompt`, build `{ Kind = Permission, ... }`.
- Set `session.PendingInteraction = ...`.

When `ActivityState` transitions OUT of `WaitingForInput` / `WaitingForPerm` (in `SetActivityState`), clear `PendingInteraction` to null.

### Step 3: Surface in `SessionDto`

Add a `PendingInteraction?` field to `src/CcDirector.Gateway.Contracts/SessionDto.cs`. In `ControlEndpoints.Map(...)`, set it from `session.PendingInteraction`.

We need a contracts-side mirror type (it cannot reference `CcDirector.Core`). Add `PendingInteractionDto` + `PendingInteractionKindDto` to `CcDirector.Gateway.Contracts`. The Director's mapping function converts the Core type to the Contracts type.

### Step 4: UI in the Director's `manager.html`

Add an `#interactionPanel` above the existing `#detailHeader` panel inside the detail mode. On each `refresh()` tick when a session is selected, look at `selectedSessionDto.pendingInteraction` and:

- If null: hide the panel.
- If `kind == "Question"`: render the prompt + option buttons. Each button POSTs `/sessions/{sid}/prompt` with the option's label.
- If `kind == "Plan"`: render the plan body via the existing `renderRecapMarkdown` helper. Buttons: Approve / Reject.
- If `kind == "Permission"`: render the tool name + summary. Buttons: Allow / Deny.

Always include a free-text input + Send button. The free-text path is the same as the existing send-prompt panel; we just have it in two places when an interaction is pending.

### Step 5: Card icon

In `renderCard`, add a small `?` glyph next to the badge when the session has a pending interaction.

### Step 6: Wire the Pulse panel (after `FEATURE_LIVE_SESSION_SUMMARY.md` lands)

`PulseBuilder` reads `session.PendingInteraction` and produces the "What's expected of you" headline.

### Step 7: Tests

- Unit-test that `HandlePipeEvent` correctly creates / clears `PendingInteraction` for each trigger event.
- Unit-test the Question / Plan / Permission JSON parsing against sample `ToolInput` payloads (capture real ones from a Claude session and check them into `Core.Tests/TestData/`).
- A small JS test for the UI would be nice but is optional - this codebase doesn't have a JS test harness today.

### Step 8: Mobile-friendly buttons

Buttons should be `min-height: 44px` to match the rest of the touch-friendly UI (the existing CSS sets this for the main action buttons).

---

## 7. Edge cases and detail

- **Stale pending interactions.** If the agent crashes after firing `PreToolUse` but never reaches `Stop`, the interaction could linger. Mitigation: clear `PendingInteraction` when `ActivityState` leaves `Waiting*` for any reason, including `Exited` / `Failed`.
- **Multi-line plan bodies.** Use the existing markdown-ish rendering helper. Cap at ~4000 chars (the recap path already truncates to 4000).
- **Long option text.** Truncate option labels to ~120 chars in the UI; tooltip on hover shows the full text.
- **AskUserQuestion with `multiSelect: true`.** Out of scope for v1. Render the options as buttons but only allow one click (just send the picked label). The agent can re-ask if needed.
- **Permission prompts on the Bash tool with multi-line commands.** Show the first line of the command, "...", then a "Show full command" link that opens the tool input summary in a `<pre>`.
- **Notification fatigue.** This feature doesn't add new notifications. It just makes the EXISTING waiting state more useful when you're already on the detail view.
- **Reply timing.** The button-click POST is fire-and-forget. The activity state will transition to `Working` after the hook event for `UserPromptSubmit` fires. If the user double-clicks the button, the second POST hits a session that's already moved on - it gets the prompt and may treat it as the next user prompt. Mitigation: disable the buttons immediately on click; re-enable only when the activity state changes (or after 2 s timeout).

---

## 8. Open questions

1. **What's the exact reply text the CLI expects for Plan-mode Approve / Reject?** Need to verify against Claude Code today. Worst case, we send the option labels and let the agent handle whatever text it gets.
2. **For Permission prompts, what text causes Claude Code to allow / deny?** Same question. Today Claude Code uses `1` / `2` keystrokes in the terminal for permission choices. The exact translation depends on how Claude Code reads stdin in the current version.
3. **Should we have a "show me the raw terminal" link inside the interaction panel?** Useful escape hatch when the structured rendering doesn't capture everything. Recommend yes - a small `Open raw view` link at the bottom-right of the interaction panel.
4. **Should the Avalonia desktop UI also get this treatment?** Yes, eventually. Out of scope here (it's a separate code path). The HTML version is the priority since the user is moving toward the web manager.
5. **Persistence on Director restart.** A restarted Director re-reads its `SessionStateStore` to revive sessions. The pending interaction is not persisted (it's volatile state). After restart, the activity state is restored and the agent will likely fire the prompt again on its next turn. Recommend NOT persisting - keep volatile.

---

## 9. Out of scope

- Free-form question detection ("the agent's reply ends with a `?` so probably a question") - too speculative.
- Multi-select question support.
- A standalone `/sessions/{sid}/pending-interaction` endpoint - the field on `SessionDto` is enough.
- Avalonia desktop UI rendering - separate work.
- Replying via the Gateway (Phase 2+).
- Server-side rate-limiting of button presses (the UI disables the button; that's enough).

---

## 10. Acceptance

The feature is done when:

- [ ] When the agent fires `AskUserQuestion`, the Director's session detail view shows a Question card within ~1.5 s.
- [ ] Clicking an option sends the option label as the user's reply.
- [ ] Plan-mode and Permission-prompt cards render and respond correctly.
- [ ] Card disappears within ~1.5 s after the user replies.
- [ ] `SessionDto.pendingInteraction` is present in `GET /sessions` and `GET /sessions/{sid}` responses when applicable.
- [ ] Unit tests cover Question, Plan, Permission event handling and clearing.
- [ ] Mobile viewports render buttons large enough to tap (44 px min height).

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-05-19 | claude (cc-director assistant) | Initial spec. Defines `PendingInteraction` on the Session, surfaces via `SessionDto.pendingInteraction`, renders an interaction card in the Director's session detail view. |
