# Driver plan: Grok (history capture)

Implementation plan for capturing a Grok (Grok Build) CLI session's conversation history
into the canonical model and surfacing it in the History tab. PLAN (not yet built);
source for the Grok GitHub issue.

Status: planned. JSONL-file driver; Grok runs FULL screen so the transcript is the only path.

---

## 1. Snapshot

| Property | Value |
| --- | --- |
| Terminal mode | Full screen / alternate screen (measured) - terminal scrollback is empty, so a transcript is required |
| Store | `~/.grok/sessions/<encoded-cwd>/<session-id>/chat_history.jsonl` (plus events.jsonl, summary.json) |
| Format | JSON lines; entries: system / user / reasoning / assistant (content, tool_calls, model_id) / tool_result |
| Fidelity | FULL |
| Pointer | Encode the repo path the way Grok does, find `sessions/<encoded-cwd>/`, pick the newest `<session-id>` subdir |
| Launch wiring | None required |

---

## 2. Why a transcript driver

Grok runs full screen (it emits the alternate-screen sequence), so the terminal
scrollback is empty - we cannot scrape it. Grok writes its full conversation to
`chat_history.jsonl` per session, organized under a directory whose name is the
URL-encoded working directory. That file is the source of truth.

---

## 3. The store and its format

Layout: `~/.grok/sessions/<url-encoded cwd>/<session-id>/` contains `chat_history.jsonl`
(the conversation), plus `events.jsonl`, `summary.json`, etc. (ignored for the first cut).

`chat_history.jsonl` line roles:
- `system` - the system prompt (skipped or shown collapsed).
- `user` - the user prompt (content text, plus an injected user_info block).
- `reasoning` - the model's reasoning (encrypted/redacted content; treated like thinking).
- `assistant` - the reply: `content` text plus `tool_calls` (and `model_id`).
- `tool_result` - the tool output, linked to the call by id.

---

## 4. The plan

### 4a. Pointer - find the current Grok session
Resolve on demand: encode the Director session repo path exactly as Grok encodes the
directory name (URL-encoding of the absolute path - confirm the exact scheme), locate
`~/.grok/sessions/<encoded-cwd>/`, pick the newest `<session-id>` subdir (by mtime) at/after
launch, and read its `chat_history.jsonl`. Cache the resolved path; re-scan if it disappears.

### 4b. Reader - chat_history.jsonl to canonical
Add `GrokTranscriptReader.Read(path)` (FileShare.ReadWrite, tolerate truncation):

| Grok line | Canonical |
| --- | --- |
| user (content text) | ConversationMessage(User) + Text |
| assistant (content) | ConversationMessage(Assistant) + Text |
| assistant (tool_calls) | ConversationPart(ToolUse) per call (name + arguments) |
| tool_result | ConversationPart(ToolResult) paired by call id |
| reasoning | ConversationPart(Thinking) (skip if encrypted/empty) |
| system | skipped (first cut) |

### 4c. Wiring
Extend `SessionHistoryReader` with a Grok branch (encode cwd, resolve newest session, read).

---

## 5. Implementation steps
1. `GrokSessionLocator` - cwd-encoding + newest-session-dir resolution + cache.
2. `GrokTranscriptReader.Read(path)` -> `ConversationHistory` (mapping in 4b).
3. `SessionHistoryReader`: add the Grok branch.
4. Tests: a fixture chat_history.jsonl (system/user/reasoning/assistant+tool_calls/tool_result
   + truncated line); a locator test (the cwd-encoding round-trips, newest dir wins); a real-file smoke.
5. No UI change.

---

## 6. Capabilities and limitations
Captured: user prompts, assistant replies, tool calls + results, timestamps; reasoning
when not encrypted.
Limitations:
- The cwd-encoding must match Grok's exactly - the main risk; verify against a real
  sessions dir.
- Reasoning content is often encrypted/redacted (shown as empty thinking).
- Pointer heuristic (newest session dir for cwd).

---

## 7. QA and acceptance (HTML QA report must show)
1. Launch a Grok session through the Director in a known repo.
2. Send two prompts; the second forces a tool call.
3. Open the History tab; confirm the canonical thread shows prompts, replies, tool
   call + result, in order.
4. Screenshot and produce the HTML QA report.
Acceptance: a full-screen Grok session - whose terminal scrollback is empty - still shows
a complete, structured conversation in the History tab.

---

## 8. Open questions / risks
- Exact directory-name encoding Grok uses for the cwd (URL-encode of the absolute path?).
- `tool_calls` structure (name/arguments fields) and the call-to-result pairing key.
- Whether to surface `summary.json` as a header.
