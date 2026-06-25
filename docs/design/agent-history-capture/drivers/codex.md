# Driver plan: Codex (history capture)

Implementation plan for capturing a Codex CLI session's conversation history into the
canonical model and surfacing it in the History tab. This is a PLAN (not yet built);
it is the source for the Codex GitHub issue.

Status: planned. Template for the JSONL-file driver shape (Codex, Pi, Grok).

---

## 1. Snapshot

| Property | Value |
| --- | --- |
| Terminal mode | Normal buffer (measured) - so the terminal scrollback also has the text, but the rollout file gives structured, full fidelity, so we prefer it |
| Store | `~/.codex/sessions/<yyyy>/<mm>/<dd>/rollout-<timestamp>-<uuid>.jsonl` |
| Format | JSON lines; `type` in { session_meta, response_item, event_msg, turn_context } |
| Fidelity | FULL - user + assistant messages, tool calls + outputs, reasoning |
| Pointer mechanism | Newest rollout whose `session_meta.cwd` matches the session repo and timestamp >= launch (no Claude-style hooks) |
| Launch wiring | None required (file is written automatically). Optional: Codex `notify` program as a future authoritative pointer |

---

## 2. Why a transcript driver

Codex renders in the normal terminal buffer, so unlike Claude its scrollback is not
empty - but the scrollback is raw text, not structured. Codex also writes its own
complete session log ("rollout") as JSON lines, which gives us structured user /
assistant / tool turns directly. We read that file and map it into the same canonical
`ConversationHistory` every other driver produces, so the History tab renders Codex
identically to Claude.

---

## 3. The store and its format

A rollout file is JSON lines. The relevant line types:

- `session_meta` - one per file; payload carries the session id, cwd, and start time.
  This is how we match a rollout to a Director session (by cwd) and order by start.
- `response_item` - the structured conversation:
  - `payload.type = "message"` with `role` user/assistant and content items
    `input_text` / `output_text`.
  - `payload.type = "function_call"` - a tool call (name + arguments).
  - `payload.type = "function_call_output"` - the tool result.
  - `payload.type = "reasoning"` - the model's reasoning (may be redacted/empty).
- `event_msg` - a parallel, already-cleaned stream (`user_message`, `agent_message`)
  useful as a fallback when a `response_item` is hard to render.
- `turn_context` - bookkeeping; skipped.

We parse `response_item` as the primary source (it has the tool structure) and ignore
`event_msg`/`turn_context` for the first cut.

---

## 4. The plan

### 4a. Pointer - find the current rollout

Codex has no hook that pushes the active session to us (Claude is special there). The
rollout file path is not known at launch. Resolution strategy:

1. When the History view asks for a Codex session's transcript, scan
   `~/.codex/sessions` for `rollout-*.jsonl` files.
2. Keep those whose `session_meta.cwd` equals the Director session's repo path and whose
   start time is at or after the session's launch time.
3. Pick the newest. Cache the resolved path on the session so we do not re-scan every
   poll; re-scan only if the cached file disappears.

This mirrors the existing `/claude-transcripts` newest-for-cwd fallback, generalized.
Edge cases to handle: a `/new` or context reset inside Codex may start a new rollout -
the newest-for-cwd rule naturally follows it. (Future: wire Codex's `notify` config to
POST the active rollout to a Control API endpoint, giving an authoritative pointer like
Claude's hook.)

### 4b. Reader - rollout JSONL to canonical

Add `CodexTranscriptReader.Read(path)` (in `Core/Codex/` or `Core/History/Providers/`),
mirroring `ClaudeTranscriptReader`: open with `FileShare.ReadWrite` (Codex appends
live), tolerate a truncated final line, and map each line:

| Codex line | Canonical |
| --- | --- |
| response_item / message (role=user, input_text) | ConversationMessage(User) + Text |
| response_item / message (role=assistant, output_text) | ConversationMessage(Assistant) + Text |
| response_item / function_call | ConversationPart(ToolUse) with name + arguments |
| response_item / function_call_output | ConversationPart(ToolResult) paired by call id |
| response_item / reasoning | ConversationPart(Thinking) (skip if empty) |
| session_meta, turn_context, event_msg | skipped |

### 4c. Wiring

Extend `SessionHistoryReader` so that for `AgentKind.Codex` it resolves the rollout
path (4a) and reads it with `CodexTranscriptReader` (4b). No launch-path change is
needed (no hooks). The History tab and the facade then work unchanged - the canonical
output is identical to Claude's.

---

## 5. Implementation steps

1. `CodexRolloutLocator` - newest-rollout-for-cwd resolution + per-session cache.
2. `CodexTranscriptReader.Read(path)` -> `ConversationHistory` (the mapping in 4b).
3. `SessionHistoryReader`: add the Codex branch (resolve + read).
4. Unit tests: a fixture rollout (message/function_call/function_call_output/reasoning
   + a truncated line) asserting the canonical mapping; a locator test (newest-for-cwd,
   ignores other cwds and older files); and a real-transcript smoke against a local
   rollout (not committed).
5. No UI change - the History tab already renders any `ConversationHistory`.

---

## 6. Capabilities and limitations

Captured: user prompts, assistant replies, tool calls (name + arguments) paired with
their outputs, reasoning when present, timestamps.

Limitations:
- Pointer is heuristic (newest-for-cwd), not authoritative like Claude's hook. Risk if
  two Codex sessions run in the same repo at once - mitigate by also matching the
  session start time window, and revisit with the `notify` hook later.
- Reasoning may be redacted/empty (like Claude thinking).
- In-Codex context reset (`/new`) starts a new rollout; the newest-for-cwd rule follows
  it but we should confirm the behavior in QA.

---

## 7. QA and acceptance (the HTML QA report must show)

1. Launch a Codex session through the Director in a known repo.
2. Send two prompts; the second must force a tool call (e.g. read a file).
3. Open the History tab; confirm the canonical thread shows: the two user prompts, the
   assistant replies, and the tool call + result, in order.
4. Verify continuity after an in-Codex context reset (the thread follows the new
   rollout).
5. Screenshot the History tab and produce the HTML QA report (screenshots + the
   asserted checks), the same format as the Claude proof.

Acceptance: the History tab renders a Codex conversation identical in shape to Claude's,
sourced from the rollout file, with the tool call/result paired.

---

## 8. Open questions / risks

- Exact `session_meta` field names for cwd and start time - confirm against a real
  rollout during implementation.
- Whether to prefer `response_item` vs `event_msg` for assistant text (structure vs
  pre-cleaned) - default to `response_item`.
- Multi-session-same-repo disambiguation (see limitations).
